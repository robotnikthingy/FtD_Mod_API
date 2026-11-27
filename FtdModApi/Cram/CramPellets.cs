using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using BrilliantSkies.Modding;
using BrilliantSkies.Ui.Tips;
using FtdModApi.Patching;
using UnityEngine;

namespace FtdModApi
{
    /// <summary>
    /// A new CRAM pellet: a copy of one of the game's pellet blocks with its own strength.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The block itself is ordinary game content in your mod folder: usually an <c>.itemduplicateandmodify</c>
    /// file in an <c>ItemDup</c> folder that duplicates a game pellet (e.g. "High Explosive Pellets") under a
    /// new guid, name and look. It keeps the game's pellet class, so it packs, connects and shows in the
    /// gun's tooltip exactly like the pellet it copies. This definition then tells the API how much stronger
    /// or weaker it is than that pellet.
    /// </para>
    /// <para>Multipliers are per pellet and relative to the copied pellet: 1 = the same, 1.5 = 50% more.</para>
    /// </remarks>
    public sealed class CramPelletDefinition
    {
        /// <summary>Id used in logs, reports and <see cref="CramShellContext.ModdedPelletCount"/>, e.g. "MyMod.DenseHePellets".</summary>
        public string Id { get; set; }

        /// <summary>The pellet block: its guid (preferred) or internal name.</summary>
        public string Block { get; set; }

        /// <summary>Optional line added to the block's tooltip, e.g. "Denser filling: +30% HE power, -20% shell health".</summary>
        public string Description { get; set; }

        /// <summary>
        /// Strength of the pellet's main effect: HE power for HE pellets, frag power for frag, EMP damage for EMP,
        /// fuel and oxidizer for incendiary. Hardener pellets use <see cref="ArmourPiercing"/> instead.
        /// </summary>
        public float Payload { get; set; } = 1f;

        /// <summary>The pellet's share of the shell's kinetic damage.</summary>
        public float KineticDamage { get; set; } = 1f;

        /// <summary>The pellet's share of the shell's armour piercing (hardener-based pellets only).</summary>
        public float ArmourPiercing { get; set; } = 1f;

        /// <summary>The pellet's share of the shell's health.</summary>
        public float Health { get; set; } = 1f;
    }

    /// <summary>A registered modded pellet.</summary>
    public sealed class CramPelletInfo
    {
        internal CramPelletInfo(string modId, CramPelletDefinition definition)
        {
            ModId = modId;
            Definition = definition;
        }

        public string ModId { get; }

        public string Id => Definition.Id;

        /// <summary>The block's guid, once found (Guid.Empty until the game has built its content).</summary>
        public Guid BlockGuid { get; internal set; }

        /// <summary>Which game pellet the block is a copy of (null until found, or if it is a custom pellet class).</summary>
        public CramPelletKind? BasedOn { get; internal set; }

        /// <summary>What happened when the API looked for the block, for reports.</summary>
        public string Status { get; internal set; } = "waiting for the game to build its content";

        internal CramPelletDefinition Definition { get; }

        internal string Describe()
        {
            var d = Definition;
            var parts = new List<string>();
            if (d.Payload != 1f) parts.Add($"payload x{d.Payload:0.##}");
            if (d.KineticDamage != 1f) parts.Add($"kinetic x{d.KineticDamage:0.##}");
            if (d.ArmourPiercing != 1f) parts.Add($"AP x{d.ArmourPiercing:0.##}");
            if (d.Health != 1f) parts.Add($"health x{d.Health:0.##}");
            return parts.Count == 0 ? "same as the game pellet" : string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Add new CRAM pellet blocks. Feature id: <see cref="Features.CramPellets"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// CramPellets.Register("MyMod", new CramPelletDefinition
    /// {
    ///     Id = "MyMod.DenseHePellets",
    ///     Block = "6f1c3d2a-....",          // guid of your duplicated HE pellet block
    ///     Payload = 1.3f,                   // +30% HE power per pellet
    ///     Health = 0.8f
    /// });
    /// </code>
    /// Or with no code, in FtdModApi.content.json:
    /// <code>
    /// "cramPellets": [ { "id": "MyMod.DenseHePellets", "block": "6f1c3d2a-....", "payload": 1.3, "health": 0.8 } ]
    /// </code>
    /// </example>
    public static class CramPellets
    {
        private static readonly object _gate = new object();
        private static readonly List<CramPelletInfo> _pellets = new List<CramPelletInfo>();
        private static volatile Dictionary<Guid, CramPelletInfo> _byGuid = new Dictionary<Guid, CramPelletInfo>();

        private static readonly ConditionalWeakTable<CramConnectedTypeInfo, Tally> _tallies = new ConditionalWeakTable<CramConnectedTypeInfo, Tally>();
        private static readonly ConditionalWeakTable<RailwayGunStats, GunPellets> _guns = new ConditionalWeakTable<RailwayGunStats, GunPellets>();

        /// <summary>Registers a pellet. Returns null if the definition is invalid (the reason is logged).</summary>
        public static CramPelletInfo Register(string modId, CramPelletDefinition definition)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var log = FtdApi.GetLog(modId);

            if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Block))
            {
                log.Error("CramPellets.Register: Id and Block are required.");
                return null;
            }
            foreach (var (name, value) in new[] { ("Payload", definition.Payload), ("KineticDamage", definition.KineticDamage), ("ArmourPiercing", definition.ArmourPiercing), ("Health", definition.Health) })
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                {
                    log.Error($"CramPellets.Register('{definition.Id}'): {name} must be 0 or more.");
                    return null;
                }
            }

            var info = new CramPelletInfo(modId, definition);
            lock (_gate)
            {
                var existing = _pellets.FirstOrDefault(p => string.Equals(p.Id, definition.Id, StringComparison.Ordinal));
                if (existing != null)
                {
                    log.Warning($"CRAM pellet '{definition.Id}' is already registered.");
                    return existing;
                }
                _pellets.Add(info);
            }

            if (Guid.TryParse(definition.Block.Trim(), out var guid))
            {
                Publish(info, guid);
            }
            if (Configured.i != null) Resolve(info); // registered after the game built its content

            log.Info($"Registered CRAM pellet '{definition.Id}' (block {definition.Block}: {info.Describe()}).");
            return info;
        }

        /// <summary>All registered modded pellets.</summary>
        public static IReadOnlyList<CramPelletInfo> All
        {
            get { lock (_gate) return _pellets.ToList(); }
        }

        /// <summary>The modded pellet with this id, or null.</summary>
        public static CramPelletInfo Find(string id)
        {
            lock (_gate) return _pellets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        }

        /// <summary>Modded pellets connected to a gun (empty when none). For your own patches.</summary>
        public static IReadOnlyList<CramPelletCount> On(RailwayGunStats gun)
            => gun != null && _guns.TryGetValue(gun, out var g) ? g.Counts : CramPelletCount.None;

        // ------------------------------------------------------------------------------------------
        // Finding the blocks
        // ------------------------------------------------------------------------------------------

        private static void Publish(CramPelletInfo info, Guid guid)
        {
            lock (_gate)
            {
                if (_byGuid.TryGetValue(guid, out var other) && !ReferenceEquals(other, info))
                {
                    info.Status = $"block {guid} is already used by pellet '{other.Id}' ({other.ModId})";
                    FtdApi.GetLog(info.ModId).Error($"CRAM pellet '{info.Id}': {info.Status}.");
                    return;
                }
                info.BlockGuid = guid;
                _byGuid = new Dictionary<Guid, CramPelletInfo>(_byGuid) { [guid] = info };
            }
        }

        private static void Unpublish(CramPelletInfo info)
        {
            lock (_gate)
            {
                if (info.BlockGuid == Guid.Empty) return;
                if (_byGuid.TryGetValue(info.BlockGuid, out var current) && ReferenceEquals(current, info))
                {
                    var copy = new Dictionary<Guid, CramPelletInfo>(_byGuid);
                    copy.Remove(info.BlockGuid);
                    _byGuid = copy;
                }
                info.BlockGuid = Guid.Empty;
            }
        }

        /// <summary>Called once the game has built its content (main menu), and for late registrations.</summary>
        internal static void ResolveAll()
        {
            foreach (var p in All) Resolve(p);
        }

        private static void Resolve(CramPelletInfo info)
        {
            var log = FtdApi.GetLog(info.ModId);
            var item = Content.FindItem(info.Definition.Block.Trim());
            if (item == null)
            {
                Unpublish(info);
                info.Status = "block not found (is the block's mod enabled? check FtdModApi_blocks.txt)";
                log.Error($"CRAM pellet '{info.Id}': block '{info.Definition.Block}' was not found.");
                return;
            }

            Type cls = null;
            try
            {
                cls = item.Definition.GetBlockClassType();
            }
            catch
            {
                // Reported below.
            }
            if (cls == null || !typeof(CannonPackerAddon).IsAssignableFrom(cls) || typeof(CannonAmmoBox).IsAssignableFrom(cls))
            {
                Unpublish(info);
                info.Status = $"block '{item.Name}' is not a CRAM pellet (class '{item.ClassName}')";
                log.Error($"CRAM pellet '{info.Id}': {info.Status}. Duplicate one of the game's pellet blocks.");
                return;
            }

            info.BasedOn = KindOfClass(cls);
            if (info.BlockGuid != item.Guid) Publish(info, item.Guid);
            if (info.BlockGuid == item.Guid)
            {
                info.Status = $"active: block '{item.Name}' ({item.Guid}), based on {(info.BasedOn?.ToString() ?? cls.Name)}";
                log.Verbose(() => $"CRAM pellet '{info.Id}' resolved: {info.Status}.");
            }
        }

        private static CramPelletKind? KindOfClass(Type cls)
        {
            if (typeof(CannonArmourPiercingWarhead).IsAssignableFrom(cls)) return CramPelletKind.Hardener;
            if (typeof(CannonExplosiveWarhead).IsAssignableFrom(cls)) return CramPelletKind.Explosive;
            if (typeof(CannonFragWarhead).IsAssignableFrom(cls)) return CramPelletKind.Fragmentation;
            if (typeof(CannonEmpWarhead).IsAssignableFrom(cls)) return CramPelletKind.Emp;
            if (typeof(CannonIncendiaryWarhead).IsAssignableFrom(cls)) return CramPelletKind.Incendiary;
            return null; // A custom pellet class: what it counts as is measured when it connects.
        }

        // ------------------------------------------------------------------------------------------
        // Counting connected pellets
        // ------------------------------------------------------------------------------------------

        /// <summary>Modded pellets counted during one packer's connection pass.</summary>
        private sealed class Tally
        {
            public readonly Dictionary<CramPelletInfo, int[]> ByPellet = new Dictionary<CramPelletInfo, int[]>();
        }

        /// <summary>Modded pellets on one gun. Replaced, never changed in place, so worker threads can read it.</summary>
        private sealed class GunPellets
        {
            public volatile CramPelletCount[] Counts = CramPelletCount.None;
        }

        /// <summary>The game's pellet counters before a pellet adds itself.</summary>
        internal struct Counters
        {
            public int Hard, Exp, Frag, Emp, Fire;
        }

        // Prefix on CannonPackerAddon.UpdateConnectedTypeInfo: remember the counters.
        private static void UpdateConnectedTypeInfo_Prefix(CramConnectedTypeInfo __0, out Counters __state)
        {
            __state = new Counters
            {
                Hard = __0.hardenerMaterials,
                Exp = __0.expMaterials,
                Frag = __0.fragMaterials,
                Emp = __0.empMaterials,
                Fire = __0.fireMaterials
            };
        }

        // Postfix: if this is a modded pellet, record what it added.
        private static void UpdateConnectedTypeInfo_Postfix(CannonPackerAddon __instance, CramConnectedTypeInfo __0, Counters __state)
        {
            var map = _byGuid;
            if (map.Count == 0) return;
            var id = __instance.item?.ComponentId;
            if (id == null || !map.TryGetValue(id.Guid, out var pellet)) return;

            var tally = _tallies.GetOrCreateValue(__0);
            if (!tally.ByPellet.TryGetValue(pellet, out var byKind))
            {
                byKind = new int[6]; // 5 kinds + total
                tally.ByPellet.Add(pellet, byKind);
            }
            byKind[(int)CramPelletKind.Hardener] += __0.hardenerMaterials - __state.Hard;
            byKind[(int)CramPelletKind.Explosive] += __0.expMaterials - __state.Exp;
            byKind[(int)CramPelletKind.Fragmentation] += __0.fragMaterials - __state.Frag;
            byKind[(int)CramPelletKind.Emp] += __0.empMaterials - __state.Emp;
            byKind[(int)CramPelletKind.Incendiary] += __0.fireMaterials - __state.Fire;
            byKind[5]++;
        }

        // Postfix on CannonAutoLoader.SeeConnections: the packer adds its pellets to the gun.
        private static void SeeConnections_Postfix(CannonAutoLoader __instance, CramConnectedTypeInfo __0)
        {
            if (!_tallies.TryGetValue(__0, out var tally) || tally.ByPellet.Count == 0) return;
            var stats = __instance.Node?.Stats;
            if (stats == null) return;

            var gun = _guns.GetOrCreateValue(stats);
            var merged = gun.Counts.ToDictionary(c => c.Pellet, c => (int[])c.ByKind.Clone());
            foreach (var kv in tally.ByPellet)
            {
                if (!merged.TryGetValue(kv.Key, out var into)) merged[kv.Key] = into = new int[6];
                for (int i = 0; i < 6; i++) into[i] += kv.Value[i];
            }
            gun.Counts = merged.Select(kv => new CramPelletCount(kv.Key, kv.Value[5], kv.Value)).ToArray();
            foreach (var kv in tally.ByPellet)
            {
                var pellet = kv.Key;
                FtdApi.GetLog(pellet.ModId).VerboseOnce("cram-pellet-connected:" + pellet.Id,
                    () => $"CRAM pellet '{pellet.Id}' connected to a gun for the first time ({kv.Value[5]} connection(s) via one packer).");
            }
        }

        // Postfix on CramNode.PriorToSendingOutFeelers: the gun recounts everything.
        private static void PriorToSendingOutFeelers_Postfix(CramNode __instance)
        {
            var stats = __instance.Stats;
            if (stats != null && _guns.TryGetValue(stats, out var gun)) gun.Counts = CramPelletCount.None;
        }

        // ------------------------------------------------------------------------------------------
        // Shell stats
        // ------------------------------------------------------------------------------------------

        // Postfix on RailwayGunStats.GetStatsOfShell: pellet multipliers, then CramApi.ShellStats modifiers.
        private static void GetStatsOfShell_Postfix(RailwayGunStats __instance, float __0, RailwayGunShellStats __result)
        {
            if (__result == null) return;
            var pellets = _guns.TryGetValue(__instance, out var gun) ? gun.Counts : CramPelletCount.None;
            if (pellets.Length > 0 && _pelletsActive)
            {
                try
                {
                    ApplyPellets(__instance, __result, pellets);
                }
                catch (Exception e)
                {
                    FtdApi.Log.ErrorOnce("cram-pellets", "Applying modded CRAM pellets failed.", e);
                }
            }

            var pipeline = CramApi.ShellStats;
            if (pipeline.IsEmpty) return;
            var ctx = CramShellContext.Rent(__instance, __result, Mathf.Clamp(__0, 0f, __instance.MaxPackTime), pellets);
            try
            {
                pipeline.Run(ctx);
                ctx.WriteBack(__result);
            }
            finally
            {
                ctx.Release();
            }
        }

        private static volatile bool _pelletsActive;

        private static float Ratio(int count, float extra) => count > 0 ? Mathf.Max(0f, (count + extra) / count) : 1f;

        /// <summary>
        /// The game's shell formulas are linear in each pellet count, so a pellet worth x1.3 is the same as
        /// 0.3 extra pellets of its kind for that one stat. Scale each stat by (counted + extra) / counted.
        /// </summary>
        internal static void ApplyPellets(RailwayGunStats s, RailwayGunShellStats r, CramPelletCount[] pellets)
        {
            float exp = 0f, frag = 0f, emp = 0f, fire = 0f, ap = 0f, kinSoft = 0f, kinHard = 0f, hpSoft = 0f, hpHard = 0f;
            foreach (var p in pellets)
            {
                var d = p.Pellet.Definition;
                var k = p.ByKind;
                int hard = k[(int)CramPelletKind.Hardener];
                int soft = k[(int)CramPelletKind.Explosive] + k[(int)CramPelletKind.Fragmentation] + k[(int)CramPelletKind.Emp] + k[(int)CramPelletKind.Incendiary];
                exp += k[(int)CramPelletKind.Explosive] * (d.Payload - 1f);
                frag += k[(int)CramPelletKind.Fragmentation] * (d.Payload - 1f);
                emp += k[(int)CramPelletKind.Emp] * (d.Payload - 1f);
                fire += k[(int)CramPelletKind.Incendiary] * (d.Payload - 1f);
                ap += hard * (d.ArmourPiercing - 1f);
                kinSoft += soft * (d.KineticDamage - 1f);
                kinHard += hard * (d.KineticDamage - 1f);
                hpSoft += soft * (d.Health - 1f);
                hpHard += hard * (d.Health - 1f);
            }

            r.HePower *= Ratio(s.ExpPerSec, exp);
            r.FragPower *= Ratio(s.FragPerSec, frag);
            r.EmpDamage *= Ratio(s.EmpPerSec, emp);
            float fireRatio = Ratio(s.FirePerSec, fire);
            r.FireFuel *= fireRatio;
            r.FireOxidizer *= fireRatio;
            r.ArmourPiercing *= Ratio(s.HardPerSec, ap);

            int softCount = s.ExpPerSec + s.FragPerSec + s.EmpPerSec + s.FirePerSec;
            float kinBase = CramConstants.KineticPerPayload * softCount + CramConstants.HardenerKineticPerPayload * s.HardPerSec;
            if (kinBase > 0f)
            {
                float kinExtra = CramConstants.KineticPerPayload * kinSoft + CramConstants.HardenerKineticPerPayload * kinHard;
                r.KineticDamage *= Mathf.Max(0f, (kinBase + kinExtra) / kinBase);
            }
            float hpBase = CramConstants.HealthPerPayload * softCount + CramConstants.HardenerHealthPerPayload * s.HardPerSec;
            if (hpBase > 0f)
            {
                float hpExtra = CramConstants.HealthPerPayload * hpSoft + CramConstants.HardenerHealthPerPayload * hpHard;
                r.Health *= Mathf.Max(0f, (hpBase + hpExtra) / hpBase);
            }
        }

        // Postfix on CannonPackerAddon.AppendToolTip: say what the modded pellet does.
        private static void AppendToolTip_Postfix(CannonPackerAddon __instance, ProTip tip)
        {
            var map = _byGuid;
            if (map.Count == 0 || tip == null) return;
            var id = __instance.item?.ComponentId;
            if (id == null || !map.TryGetValue(id.Guid, out var pellet)) return;
            var text = string.IsNullOrEmpty(pellet.Definition.Description) ? pellet.Describe() : pellet.Definition.Description;
            tip.Add(Position.Middle, $"Modded pellet ({pellet.ModId}): {text}");
        }

        // ------------------------------------------------------------------------------------------
        // Patches and report
        // ------------------------------------------------------------------------------------------

        internal static IEnumerable<PatchSpec> Specs()
        {
            yield return new PatchSpec
            {
                Id = "RailwayGunStats.GetStatsOfShell: CRAM shell stats",
                FeatureId = Features.CramShellStats,
                Target = () => PatchSpec.Method(typeof(RailwayGunStats), nameof(RailwayGunStats.GetStatsOfShell), typeof(float)),
                Postfix = PatchSpec.Hook(typeof(CramPellets), nameof(GetStatsOfShell_Postfix))
            };
            yield return new PatchSpec
            {
                Id = "CannonPackerAddon.UpdateConnectedTypeInfo: count modded pellets",
                FeatureId = Features.CramPellets,
                Target = () => PatchSpec.Method(typeof(CannonPackerAddon), nameof(CannonPackerAddon.UpdateConnectedTypeInfo), typeof(CramConnectedTypeInfo)),
                Prefix = PatchSpec.Hook(typeof(CramPellets), nameof(UpdateConnectedTypeInfo_Prefix)),
                Postfix = PatchSpec.Hook(typeof(CramPellets), nameof(UpdateConnectedTypeInfo_Postfix))
            };
            yield return new PatchSpec
            {
                Id = "CannonAutoLoader.SeeConnections: add modded pellets to the gun",
                FeatureId = Features.CramPellets,
                Target = () => PatchSpec.Method(typeof(CannonAutoLoader), nameof(CannonAutoLoader.SeeConnections), typeof(CramConnectedTypeInfo)),
                Postfix = PatchSpec.Hook(typeof(CramPellets), nameof(SeeConnections_Postfix))
            };
            yield return new PatchSpec
            {
                Id = "CramNode.PriorToSendingOutFeelers: recount modded pellets",
                FeatureId = Features.CramPellets,
                Target = () => PatchSpec.Method(typeof(CramNode), nameof(CramNode.PriorToSendingOutFeelers)),
                Postfix = PatchSpec.Hook(typeof(CramPellets), nameof(PriorToSendingOutFeelers_Postfix))
            };
            yield return new PatchSpec
            {
                Id = "CannonPackerAddon.AppendToolTip: modded pellet tooltip",
                FeatureId = Features.CramPellets,
                Required = false,
                Target = () => PatchSpec.Method(typeof(CannonPackerAddon), "AppendToolTip", typeof(ProTip)),
                Postfix = PatchSpec.Hook(typeof(CramPellets), nameof(AppendToolTip_Postfix))
            };
        }

        /// <summary>Called after patching: pellets need both their own patches and the shell stats hook.</summary>
        internal static void AfterPatching()
        {
            if (!Features.IsAvailable(Features.CramShellStats) && Features.IsAvailable(Features.CramPellets))
            {
                Features.SetState(Features.CramPellets, FeatureState.Unavailable, "needs " + Features.CramShellStats + ", which is unavailable");
            }
            _pelletsActive = Features.IsAvailable(Features.CramPellets);
        }

        internal static void AppendReport(StringBuilder sb)
        {
            sb.AppendLine("CRAM PELLETS  (modded pellets; game pellet blocks are listed under Core_CRAM in FtdModApi_blocks.txt)");
            var all = All;
            if (all.Count == 0) sb.AppendLine("  None registered.");
            foreach (var p in all)
            {
                sb.AppendLine($"  {p.Id}  [{p.ModId}]  {p.Describe()}");
                sb.AppendLine($"      {p.Status}");
            }
            sb.AppendLine();
        }
    }
}
