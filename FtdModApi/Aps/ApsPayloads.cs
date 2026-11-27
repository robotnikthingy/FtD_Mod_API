using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BrilliantSkies.Blocks.AdvCannonComponents.Gui.AmmoCustomising;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Id;
using BrilliantSkies.Core.Types;
using BrilliantSkies.Ftd.AdvancedCannons;
using BrilliantSkies.Ftd.Constructs.Modules.All.StandardExplosion;
using BrilliantSkies.Ftd.DamageModels;
using BrilliantSkies.Ftd.Game.Pools;
using BrilliantSkies.Ftd.Game.Pools.SpecialExplosionEffects;
using BrilliantSkies.GridCasts;
using FtdModApi.Patching;
using HarmonyLib;
using UnityEngine;

namespace FtdModApi
{
    /// <summary>
    /// A payload of your own for an APS shell part: code that runs when the shell detonates. Set it as
    /// <see cref="ShellPartDefinition.Payload"/>. Feature: <see cref="Features.ApsPayloads"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Detonate"/> runs on the host only (the host decides all damage in FtD), once per shell, when the
    /// shell's fuse or impact sets it off, the same moment as the game's own HE, frag and EMP go off. A shell whose
    /// only payload is yours stops at that point, as an HE shell does. It runs on a game worker thread: use the
    /// helpers on <see cref="ApsPayloadHit"/> (explosion, EMP, fire, fragments) or
    /// <see cref="ApsPayloadHit.OnMainThread"/> for anything else.
    /// </para>
    /// <para>
    /// Strength follows the game's rules: <see cref="ApsPayloadShell.Strength"/> is what the game would multiply
    /// by its base damage if the parts were one of its own payload bodies (bigger and longer shells hit harder,
    /// sabot heads weaken it, <see cref="ApsApi.ShellStats"/> payload multipliers apply). The static helpers on
    /// <see cref="ApsPayloads"/> turn a strength into game-equivalent damage.
    /// </para>
    /// </remarks>
    public abstract class ApsPayload
    {
        /// <summary>Runs when a shell carrying this part detonates (host only, worker thread).</summary>
        public abstract void Detonate(ApsPayloadHit hit);

        /// <summary>
        /// Rows shown for this part in the ammo customiser's stats tab, for the inspected shell. Keep it fast:
        /// it runs every time the tab draws. Default: one row with the strength.
        /// </summary>
        public virtual IEnumerable<ApsPayloadStat> Describe(ApsPayloadShell shell)
        {
            yield return new ApsPayloadStat("Payload strength", shell.Strength.ToString("0.##"),
                "How strong this payload is in this shell. Grows with the shell's gauge and the number of these parts.");
        }
    }

    /// <summary>One row in the ammo customiser's stats tab.</summary>
    public sealed class ApsPayloadStat
    {
        public ApsPayloadStat(string name, string value, string toolTip = null)
        {
            Name = name ?? string.Empty;
            Value = value ?? string.Empty;
            ToolTip = toolTip ?? string.Empty;
        }

        public string Name { get; }
        public string Value { get; }
        public string ToolTip { get; }
    }

    /// <summary>A shell design that carries a modded payload part.</summary>
    public class ApsPayloadShell
    {
        internal ApsPayloadShell(ShellModel shell, ShellPartInfo part, int count, float strength)
        {
            Shell = shell;
            Part = part;
            Count = count;
            Strength = strength;
        }

        /// <summary>The modded part this payload belongs to.</summary>
        public ShellPartInfo Part { get; }

        /// <summary>How many of these parts the shell has.</summary>
        public int Count { get; }

        /// <summary>
        /// Payload strength of all these parts together, in the game's units: the value the game multiplies by
        /// e.g. <see cref="AdvCannonConstants.BaseExplosiveDamage"/> for HE bodies. See <see cref="ApsPayloads"/>.
        /// </summary>
        public float Strength { get; }

        /// <summary>Shell diameter in metres.</summary>
        public float Diameter => Shell.Diameter;

        /// <summary>Raw game shell model. Unstable.</summary>
        public ShellModel Shell { get; }
    }

    /// <summary>A detonating shell. Valid only during <see cref="ApsPayload.Detonate"/>.</summary>
    public sealed class ApsPayloadHit : ApsPayloadShell
    {
        private readonly AdvPooledProjectile _projectile;
        private readonly GridCastHit _hit;

        internal ApsPayloadHit(ShellModel shell, ShellPartInfo part, int count, float strength, AdvPooledProjectile projectile,
            IAllConstructBlock construct, Block block, Vector3 position, ExplosionReason reason, GridCastHit hit, float damageFactor)
            : base(shell, part, count, strength)
        {
            _projectile = projectile;
            _hit = hit;
            Construct = construct;
            Block = block;
            Position = position;
            ReasonValue = reason;
            DamageFactor = damageFactor;
        }

        /// <summary>Where the shell detonates (world position).</summary>
        public Vector3 Position { get; }

        /// <summary>The shell's velocity this frame.</summary>
        public Vector3 Velocity => _projectile._pState?.FrameStartVelocity ?? _projectile.Velocity;

        /// <summary>The shell's direction of travel.</summary>
        public Vector3 Direction => Velocity.sqrMagnitude > 0f ? Velocity.normalized : _projectile.Forward;

        /// <summary>
        /// 0–1: how intact the shell is (damaged shells deliver less, as the game's payloads do). Multiply your
        /// damage by it; the helpers below already do.
        /// </summary>
        public float DamageFactor { get; }

        /// <summary>
        /// Why it detonated, as the game names it: "inertial_fuse", "soft_head", "penetration_depth_fuse",
        /// "ground_hit", "shell_destroyed", "run_out_of_kinetic_energy", and so on.
        /// </summary>
        public string Reason => ReasonValue.ToString();

        /// <summary>Raw game reason. Unstable.</summary>
        public ExplosionReason ReasonValue { get; }

        /// <summary>The craft hit (or the one the shell was inside for delayed fuses), or null in the open.</summary>
        public MainConstruct Craft => Construct?.iMain as MainConstruct;

        /// <summary>The construct (craft or turret/spin-block part of it) hit, or null. Raw game type.</summary>
        public IAllConstructBlock Construct { get; }

        /// <summary>The block hit, or null (fuses and proximity detonations have none).</summary>
        public Block Block { get; }

        /// <summary>True when the shell detonated in contact with (or inside) a craft.</summary>
        public bool HitCraft => Construct != null;

        /// <summary>The faction that fired the shell.</summary>
        public ObjectId Team => _projectile.Team;

        /// <summary>
        /// An explosion like the game's HE (damage and radius as the game uses them; damage is multiplied by
        /// <see cref="DamageFactor"/>). Other mods' explosion modifiers apply to it.
        /// </summary>
        public void Explosion(float damage, float radius, bool flak = false) => ExplosionAt(ExplosionPoint(), damage, radius, flak);

        /// <summary>As <see cref="Explosion(float, float, bool)"/>, at another world position (e.g. a little ahead of the shell).</summary>
        public void ExplosionAt(Vector3 position, float damage, float radius, bool flak = false)
        {
            if (damage <= 0f || radius <= 0f) return;
            var dd = new ExplosionDamageDescription(_projectile.Gunner, damage * DamageFactor, radius, position) { IsFlak = flak };
            ExplosionCreator.Explosion(Construct, dd);
        }

        /// <summary>
        /// An HE explosion of the given strength, exactly as the game makes one for HE bodies of that strength.
        /// </summary>
        public void HeExplosion(float strength)
        {
            if (strength <= 0f) return;
            float damage = ApsPayloads.HeDamage(strength * DamageFactor);
            var dd = new ExplosionDamageDescription(_projectile.Gunner, damage, ApsPayloads.ExplosionRadius(damage), ExplosionPoint());
            ExplosionCreator.Explosion(Construct, dd);
        }

        /// <summary>EMP damage to the craft hit (spreads through its electronics as the game's EMP does). No effect in the open.</summary>
        public void Emp(float damage)
        {
            if (damage <= 0f || Construct == null) return;
            ProjectileContactEffectHandler.ApplyEmp(damage * DamageFactor, Construct, Block?.GameWorldPosition ?? Position, _projectile.Gunner, Block);
        }

        /// <summary>Starts a fire in the craft hit, as incendiary shells do. No effect in the open.</summary>
        public void Fire(float fuel, float intensity, float oxidizer = 0f)
        {
            if (fuel <= 0f || intensity <= 0f || Construct == null) return;
            var cell = _hit != null ? _hit.LocalVoxelPosition : (Vector3i)Construct.SafeGlobalToLocal(Position);
            Construct.iMain.FireRestricted.DelayedCreateFire(_projectile.Gunner, Construct, cell, fuel * DamageFactor, intensity, oxidizer * DamageFactor);
        }

        /// <summary>
        /// Fragments flying on in the shell's direction, as frag warheads make them. <paramref name="coneAngle"/>
        /// in degrees (game frag bodies default to 60).
        /// </summary>
        public void Fragments(int count, float damagePerFragment, float coneAngle = 60f, float armourPiercing = -1f)
        {
            if (count <= 0 || damagePerFragment <= 0f) return;
            var config = new WeaponConfig
            {
                KineticDamage = damagePerFragment * DamageFactor,
                ArmourPiercing = armourPiercing >= 0f ? armourPiercing : SharedWeaponConstants.FragAP,
                Speed = ShellConstants.FragVelocity,
                SpeedError = 0.1f
            };
            var velocity = Velocity;
            var rotation = Quaternion.LookRotation(velocity.sqrMagnitude > 0f ? velocity : _projectile.Forward, Vector3.up);
            var generator = new FragGenerator(Position, rotation, velocity, _projectile.UniqueId, _projectile.Gunner, Construct)
            {
                InteractWithMissiles = _projectile.InteractWithMissiles
            };
            generator.CreateFrags(config, count, coneAngle, 0f);
        }

        /// <summary>Runs <paramref name="action"/> on the game's main thread soon (e.g. for visuals or spawning things).</summary>
        public void OnMainThread(Action action)
        {
            if (action == null) return;
            var log = FtdApi.GetLog(Part.ModId);
            MainThread.PostLater(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    log.ErrorOnce("payload-main:" + Part.Id, $"APS payload '{Part.Id}' threw on the main thread.", e);
                }
            });
        }

        private Vector3 ExplosionPoint()
        {
            if (_hit != null && _hit.FindLocalCellForExplosion(out _, out Vector3 global)) return global;
            return Position;
        }
    }

    /// <summary>Game-equivalent numbers for payload strengths, and the payload hooks.</summary>
    public static class ApsPayloads
    {
        /// <summary>HE damage the game would give HE bodies of this strength.</summary>
        public static float HeDamage(float strength) => PayloadDerivedValues.GetScaledExplosionDamage(strength * AdvCannonConstants.BaseExplosiveDamage);

        /// <summary>Explosion radius the game uses for this explosion damage.</summary>
        public static float ExplosionRadius(float explosionDamage) => PayloadDerivedValues.GetExplosionRadius(explosionDamage);

        /// <summary>Flak (anti-munition) damage the game would give flak bodies of this strength.</summary>
        public static float FlakDamage(float strength) => PayloadDerivedValues.GetScaledExplosionDamage(strength * AdvCannonConstants.BaseFlakDamage);

        /// <summary>EMP damage the game would give EMP bodies of this strength.</summary>
        public static float EmpDamage(float strength) => strength * AdvCannonConstants.BaseEmpDamage;

        /// <summary>Total fragment damage the game would give frag bodies of this strength.</summary>
        public static float FragmentDamage(float strength) => strength * AdvCannonConstants.BaseFragDamage;

        /// <summary>Number of fragments the game uses for this total fragment damage.</summary>
        public static int FragmentCount(float totalFragmentDamage) => Mathf.Max(1, PayloadDerivedValues.GetFragmentCount(totalFragmentDamage));

        // ------------------------------------------------------------------------------------------
        // Per shell design: which payload parts it has and how strong they are
        // ------------------------------------------------------------------------------------------

        private sealed class Entry
        {
            public ShellPartInfo Part;
            public int Count;
            public float Strength;
        }

        private sealed class Plan
        {
            public Entry[] Entries;
        }

        private static readonly ConditionalWeakTable<ShellModel, Plan> _plans = new ConditionalWeakTable<ShellModel, Plan>();
        private static readonly Plan Empty = new Plan { Entries = new Entry[0] };

        private static FieldInfo _volumeModifierField;
        private static FieldInfo _explosiveModifierField;

        private static Entry[] EntriesOf(ShellModel shell)
        {
            if (shell == null || ShellParts.PayloadParts.Length == 0) return Empty.Entries;
            return _plans.GetValue(shell, BuildPlan).Entries;
        }

        private static Plan BuildPlan(ShellModel shell)
        {
            var payloadParts = ShellParts.PayloadParts;
            var parts = shell.PartsAndMesh?.ShellParts;
            if (parts == null || parts.Count == 0) return Empty;

            List<Entry> entries = null;
            foreach (var part in payloadParts)
            {
                int count = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i] != null && parts[i].Type == part.Type) count++;
                }
                if (count == 0) continue;
                if (entries == null) entries = new List<Entry>();
                var entry = new Entry { Part = part, Count = count, Strength = PayloadModifier(shell) * count };
                entries.Add(entry);
                FtdApi.GetLog(part.ModId).VerboseOnce("payload-plan:" + part.Id,
                    () => $"First shell design with APS payload '{part.Id}': {count} part(s), strength {entry.Strength:0.###} at {shell.Diameter * 1000f:0} mm.");
            }
            return entries == null ? Empty : new Plan { Entries = entries.ToArray() };
        }

        // The game's per-part payload scale for this shell: module volume (gauge and part length) times the
        // payload modifier (sabot and similar parts, and ApsApi.ShellStats payload multipliers).
        private static float PayloadModifier(ShellModel shell)
        {
            float volume = 1f, explosive = 1f;
            try
            {
                if (shell.ExplosiveCharges is ShellModel_ExplosiveCharges charges)
                {
                    _volumeModifierField = _volumeModifierField ?? AccessTools.Field(typeof(ShellModel_ExplosiveCharges), "_moduleVolumeModifier");
                    _explosiveModifierField = _explosiveModifierField ?? AccessTools.Field(typeof(ShellModel_ExplosiveCharges), "_explosiveModifier");
                    if (_volumeModifierField != null) volume = (float)_volumeModifierField.GetValue(charges);
                    if (_explosiveModifierField != null) explosive = (float)_explosiveModifierField.GetValue(charges);
                }
                else
                {
                    volume = ShellConstants.VolumeToModifier(shell.Propellant.ModuleVolume);
                }
            }
            catch (Exception e)
            {
                FtdApi.Log.WarningOnce("ApsPayloads.Modifier", "Could not read the shell's payload scale (" + e.Message + "); modded payloads use 1.");
            }
            return volume * explosive;
        }

        // ------------------------------------------------------------------------------------------
        // Patches
        // ------------------------------------------------------------------------------------------

        private static FieldInfo _shellModelField;
        private static FieldInfo _explodedField;

        internal static IEnumerable<PatchSpec> Specs()
        {
            yield return new PatchSpec
            {
                Id = "AdvPooledProjectile.Explode: modded APS payloads go off",
                FeatureId = Features.ApsPayloads,
                Target = () =>
                {
                    _shellModelField = AccessTools.Field(typeof(AdvPooledProjectile), "_shellModel") ?? throw new MissingFieldException(nameof(AdvPooledProjectile), "_shellModel");
                    _explodedField = AccessTools.Field(typeof(AdvPooledProjectile), "_explodedAlready") ?? throw new MissingFieldException(nameof(AdvPooledProjectile), "_explodedAlready");
                    if (_shellModelField.FieldType != typeof(ShellModel) || _explodedField.FieldType != typeof(bool))
                        throw new MissingFieldException("AdvPooledProjectile fields changed type");
                    return EventHelpers.Declared(typeof(AdvPooledProjectile), "Explode");
                },
                Prefix = PatchSpec.Hook(typeof(ApsPayloads), nameof(Explode_Prefix)),
                Postfix = PatchSpec.Hook(typeof(ApsPayloads), nameof(Explode_Postfix))
            };
            yield return new PatchSpec
            {
                Id = "StatsTab.Build: show modded APS payloads in the ammo customiser",
                FeatureId = Features.ApsPayloads,
                Required = false,
                Target = () =>
                {
                    _getSegment = AccessTools.Method(typeof(StatsTab), "GetNewTableSegment") ?? throw new MissingMethodException(nameof(StatsTab), "GetNewTableSegment");
                    _addRow = AccessTools.Method(typeof(StatsTab), "AddTableRow") ?? throw new MissingMethodException(nameof(StatsTab), "AddTableRow");
                    return AccessTools.Method(typeof(StatsTab), nameof(StatsTab.Build));
                },
                Postfix = PatchSpec.Hook(typeof(ApsPayloads), nameof(StatsBuild_Postfix))
            };
        }

        private static void Explode_Prefix(AdvPooledProjectile __instance, out bool __state)
        {
            __state = true;
            try
            {
                __state = ShellParts.PayloadParts.Length == 0 || (bool)_explodedField.GetValue(__instance);
            }
            catch
            {
                // Leave __state true: do nothing.
            }
        }

        private static void Explode_Postfix(AdvPooledProjectile __instance, bool __state, ref bool __result,
            IAllConstructBlock C, Block B, Func<Vector3> dynamicPoint, ExplosionReason reason, GridCastHit hit)
        {
            if (__state || Net.IsClient) return; // exploded before this call, no payload parts, or not the host
            Entry[] entries;
            ShellModel shell;
            try
            {
                shell = _shellModelField.GetValue(__instance) as ShellModel;
                entries = EntriesOf(shell);
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("ApsPayloads.Explode", "Reading a detonating shell failed; modded payloads were skipped.", e);
                return;
            }
            if (entries.Length == 0) return;

            Vector3 position;
            try
            {
                position = dynamicPoint != null ? dynamicPoint() : __instance.Position;
            }
            catch
            {
                position = __instance.Position;
            }
            float damageFactor = 1f;
            try
            {
                if (shell.Health > 0f) damageFactor = Mathf.Clamp01(SharedWeaponConstants.HealthDamageFactor(__instance.Health / shell.Health));
            }
            catch
            {
                // Full strength.
            }
            // Delayed fuses detonate after leaving the hit; the game's EMP and fire then use the craft last hit.
            var construct = C;
            var fusing = __instance._pState?.Fusing;
            if (construct == null && fusing != null && (fusing.UsePenetrationDepth || fusing.UseTimeFromFirstSurface)) construct = fusing.LastBlockHit;

            foreach (var entry in entries)
            {
                var payloadHit = new ApsPayloadHit(shell, entry.Part, entry.Count, entry.Strength, __instance, construct, B, position, reason, hit, damageFactor);
                try
                {
                    var part = entry.Part;
                    FtdApi.GetLog(part.ModId).VerboseOnce("payload-detonate:" + part.Id,
                        () => $"APS payload '{part.Id}' detonated for the first time ({reason}, strength {payloadHit.Strength:0.###}, {(construct != null ? "on a craft" : "in the open")}).");
                    part.Payload.Detonate(payloadHit);
                }
                catch (Exception e)
                {
                    FtdApi.GetLog(entry.Part.ModId).ErrorOnce("payload:" + entry.Part.Id, $"APS payload '{entry.Part.Id}' threw when a shell detonated.", e);
                }
            }

            // A shell whose only payload is modded has still detonated: stop it, as the game does for HE shells.
            try
            {
                _explodedField.SetValue(__instance, true);
            }
            catch
            {
                // The game will only skip a second detonation check.
            }
            __result = true;
        }

        // ------------------------------------------------------------------------------------------
        // Ammo customiser stats tab
        // ------------------------------------------------------------------------------------------

        private static MethodInfo _getSegment;
        private static MethodInfo _addRow;

        private static ShellModel InspectedShell(AmmoCustomise focus)
        {
            try
            {
                var controller = focus?.Controller;
                return controller?.GetShellAtDiameter(controller.UiData.InspectionDiameter.Us * 0.001f);
            }
            catch
            {
                return null;
            }
        }

        private static List<ApsPayloadStat> StatsFor(ShellModel shell, ShellPartInfo part)
        {
            var entry = EntriesOf(shell).FirstOrDefault(e => e.Part == part);
            if (entry == null) return null;
            try
            {
                return part.Payload.Describe(new ApsPayloadShell(shell, entry.Part, entry.Count, entry.Strength))?.Where(s => s != null).ToList()
                       ?? new List<ApsPayloadStat>();
            }
            catch (Exception e)
            {
                FtdApi.GetLog(part.ModId).ErrorOnce("payload-describe:" + part.Id, $"APS payload '{part.Id}' threw in Describe.", e);
                return new List<ApsPayloadStat>();
            }
        }

        private static void StatsBuild_Postfix(StatsTab __instance)
        {
            try
            {
                var focus = __instance._focus;
                var shell = InspectedShell(focus);
                if (shell == null) return;
                foreach (var entry in EntriesOf(shell))
                {
                    var part = entry.Part;
                    var stats = StatsFor(shell, part);
                    if (stats == null || stats.Count == 0) continue;

                    Func<bool> shown = () => StatsFor(InspectedShell(focus), part) != null;
                    var table = _getSegment.Invoke(__instance, new object[] { part.Definition.Name, stats.Count, shown });
                    for (int row = 0; row < stats.Count; row++)
                    {
                        int r = row;
                        Func<AmmoCustomise, string> value = f =>
                        {
                            var now = StatsFor(InspectedShell(f), part);
                            return now != null && r < now.Count ? now[r].Value : "-";
                        };
                        _addRow.Invoke(__instance, new object[] { table, r, stats[r].Name, stats[r].ToolTip, value, null });
                    }
                }
            }
            catch (Exception e)
            {
                FtdApi.Log.WarningOnce("ApsPayloads.Stats", "Could not add modded payloads to the ammo customiser stats (" + e.Message + ").");
            }
        }
    }
}
