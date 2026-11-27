using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using FtdModApi.Patching;
using HarmonyLib;

namespace FtdModApi
{
    /// <summary>
    /// The game's read-only constants (one-line getters such as <c>ExplosionConstants.AirBleedPower =&gt; 0.35f</c>),
    /// made tunable. Mono can inline those getters, so patching them is unreliable; instead every game method that
    /// reads one is rewritten to read a value the API controls. Once done they are ordinary tunables
    /// ("Explosion.AirBleedPower", ...), listed in FtdModApi_tunables.txt and changed with Tunables.Set/Add/Multiply
    /// or FtdModApi.tunables.json. Feature id: <see cref="Features.ReadOnlyTunables"/>.
    /// </summary>
    internal static class ReadOnlyTunables
    {
        private sealed class Def
        {
            public readonly string Group;
            public readonly string Name;
            public readonly TunableKind Kind;
            public readonly string[] Readers;
            public int Slot;
            public TunableValue Value;
            public readonly List<MethodInfo> Getters = new List<MethodInfo>();
            public int Rewrites;

            public Def(string group, string name, TunableKind kind, params string[] readers)
            {
                Group = group;
                Name = name;
                Kind = kind;
                Readers = readers;
            }
        }

        /// <summary>
        /// Value, kind and the game classes that read it (from the 4.3.4 code). Groups: Explosion, Detection,
        /// SharedWeapon, FuelRefinery. Values with no readers in 4.3.4 (Detection.SteamTemperaturePerMaterial,
        /// SharedWeapon.DamageAt0Health) and the Map/Game/Fuel/Shell groups are not included.
        /// </summary>
        private static readonly Def[] Defs =
        {
            new Def("Explosion", "MaxExplosionRadius", TunableKind.Int, "CustomExplosion"),
            new Def("Explosion", "AirBleedPower", TunableKind.Float, "CustomExplosion"),
            new Def("Explosion", "AirBleedRadius", TunableKind.Float, "CustomExplosion"),
            new Def("Explosion", "AirExplosionPowerReduction", TunableKind.Float, "CustomExplosion", "ExplosionDamageDescription"),
            new Def("Explosion", "PowerToApplyToAC", TunableKind.Float, "CustomExplosion", "ExplosionDamageDescription"),
            new Def("Explosion", "NonDestroyedBlocksBlastPercent", TunableKind.Float, "CustomExplosion"),
            new Def("Explosion", "DamagedThresholdPercent", TunableKind.Float, "CustomExplosion", "ExplosionDamageDescription"),
            new Def("Explosion", "DamagedThresholdMaxDamage", TunableKind.Float, "CustomExplosion", "ExplosionDamageDescription"),
            new Def("Explosion", "ExtraEnergyLostOnBlockDestroyed", TunableKind.Float, "CustomExplosion"),
            new Def("Explosion", "NonDamagedBlocksBlastPercent", TunableKind.Float, "CustomExplosion"),
            new Def("Explosion", "ExplosionBoost", TunableKind.Float, "CustomExplosion"),
            new Def("Explosion", "IsSortBlocksToDamage", TunableKind.Bool, "CustomExplosion"),
            new Def("Explosion", "IsHitBlocksSeveralTimes", TunableKind.Bool, "CustomExplosion"),
            new Def("Detection", "SmokeStrengthMultiplier", TunableKind.Float, "DetectionParametersAndMaths"),
            new Def("Detection", "SmokeVisibility", TunableKind.Float, "DetectionParametersAndMaths"),
            new Def("Detection", "ChaffStrengthMultiplier", TunableKind.Float, "ConstructableChaffManager"),
            new Def("Detection", "SonarSignaturePerForce", TunableKind.Float, "MainConstruct"),
            new Def("Detection", "SonarSignaturePerPower", TunableKind.Float, "FuelEngineNode"),
            new Def("Detection", "SoundEventDecayConstant", TunableKind.Float, "PassiveSignatureManager"),
            new Def("Detection", "SoundEventMinimumDecayPerSecond", TunableKind.Float, "PassiveSignatureManager"),
            new Def("Detection", "MissilePassiveSignatureCoefficient", TunableKind.Float, "MissileTube"),
            new Def("Detection", "WirelessSignaturePerSharingDistance", TunableKind.Float, "MainConstruct"),
            new Def("Detection", "WirelessSignaturePerBuoyDistance", TunableKind.Float, "BuoyHolder"),
            new Def("Detection", "DragHeatCoefficient", TunableKind.Float, "AeroHeating"),
            new Def("Detection", "ShieldHeatCoefficient", TunableKind.Float, "ShieldProjector"),
            new Def("Detection", "MaxECMSeedChangeInterval", TunableKind.Float, "MainConstruct"),
            new Def("Detection", "AimPointDetectionGracePeriod", TunableKind.Float, "Track"),
            new Def("Detection", "WarpVelocityCompensationPeriod", TunableKind.Float, "Track"),
            new Def("SharedWeapon", "ProjectileArmor", TunableKind.Float, "CannonBarrelCommon", "CannonFiringPiece", "MissileInterceptor", "Projectile", "StatPanel", "StatsTab"),
            new Def("SharedWeapon", "HoldFireSecsAfterInit", TunableKind.Float, "ConstructableWeapon"),
            new Def("SharedWeapon", "RecoilForce", TunableKind.Float, "AdvCannonFiringPiece", "CannonFiringPiece", "ShellModel_Propellant"),
            new Def("SharedWeapon", "HeatHeshDirectDamagePenaltyFactor", TunableKind.Float, "HighEnergyPenetrator", "SpallingGenerator"),
            new Def("SharedWeapon", "FragAP", TunableKind.Float, "AdvPooledProjectile", "CannonFiringPiece", "MissileExplosionHandler", "PooledCramProjectile", "ShellRackExplosions", "ShellRackPhysicalContainer", "StatPanel", "StatsTab"),
            new Def("SharedWeapon", "HeatFragFraction", TunableKind.Float, "MissileWarheads", "ShellModel_ExplosiveCharges"),
            new Def("SharedWeapon", "HeatAP", TunableKind.Float, "StatPanel", "StatsTab", "WeaponConfigurations"),
            new Def("SharedWeapon", "BaseHeatMetric", TunableKind.Float, "PayloadDerivedValues"),
            new Def("SharedWeapon", "HeatFragAngle", TunableKind.Float, "AdvPooledProjectile", "HighEnergyPenetrator", "StatPanel", "StatsTab"),
            new Def("SharedWeapon", "SquashFragFraction", TunableKind.Float, "PayloadDerivedValues"),
            new Def("SharedWeapon", "SquashExponent", TunableKind.Float, "SpallingGenerator"),
            new Def("SharedWeapon", "BaseSquashAp", TunableKind.Float, "SpallingGenerator"),
            new Def("SharedWeapon", "SquashFragAngle", TunableKind.Float, "AdvPooledProjectile", "SpallingGenerator", "StatsTab"),
            new Def("FuelRefinery", "BaseEfficiency", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "MaxEfficiencyBonus", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "CrackerMaterialsPerSec", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "MaxOptimalAltitude", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "MaxAltitude", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "CrackerGasCapacity", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "CokerGasCapacity", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "CrackerGasPerSec", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "CokerGasPerSec", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "FlareGasPerSec", TunableKind.Float, "FuelRefineryNode"),
            new Def("FuelRefinery", "FlareMaxTemperature", TunableKind.Float, "FuelRefineryFlarestack"),
            new Def("FuelRefinery", "FlareStackCoolingPerSec", TunableKind.Float, "FuelRefineryFlarestack"),
            new Def("FuelRefinery", "DesalterGasFactor", TunableKind.Float, "FuelRefineryDesalter", "FuelRefineryNode"),
            new Def("FuelRefinery", "DesalterBuildupHeMultiplier", TunableKind.Float, "FuelRefineryStill"),
            new Def("FuelRefinery", "ExplosionDamagePerGasPerSec", TunableKind.Float, "FuelRefineryStill"),
            new Def("FuelRefinery", "ExplosionDamagePerGas", TunableKind.Float, "FuelRefineryStill"),        };

        /// <summary>Wrapper getters that pass a constant through (CustomExplosion.AirBleedPower => ExplosionConstants.AirBleedPower).</summary>
        private static readonly Dictionary<string, string> AliasClasses = new Dictionary<string, string> { { "Explosion", "CustomExplosion" } };

        private static TunableValue[] _slots = new TunableValue[0];
        private static Dictionary<MethodInfo, Def> _byGetter;
        private static readonly Dictionary<string, TunableSystem> _groups = new Dictionary<string, TunableSystem>(StringComparer.Ordinal);

        // ---- Read by the rewritten game code ----

        /// <summary>Called from patched game code in place of a float constant getter.</summary>
        public static float GetFloat(int slot) => (float)_slots[slot].Raw;

        /// <summary>Called from patched game code in place of an int constant getter.</summary>
        public static int GetInt(int slot) => (int)_slots[slot].Raw;

        /// <summary>Called from patched game code in place of a bool constant getter.</summary>
        public static bool GetBool(int slot) => _slots[slot].Raw != 0;

        private static readonly MethodInfo GetFloatMethod = AccessTools.Method(typeof(ReadOnlyTunables), nameof(GetFloat));
        private static readonly MethodInfo GetIntMethod = AccessTools.Method(typeof(ReadOnlyTunables), nameof(GetInt));
        private static readonly MethodInfo GetBoolMethod = AccessTools.Method(typeof(ReadOnlyTunables), nameof(GetBool));

        // ---- Setup ----

        private static Assembly GameAssembly => typeof(AdvCannonFiringPiece).Assembly;

        /// <summary>Finds each getter, reads the game's value and creates its slot. Runs before patching.</summary>
        private static void Prepare()
        {
            if (_byGetter != null) return;
            _byGetter = new Dictionary<MethodInfo, Def>();
            var slots = new List<TunableValue>();
            foreach (var d in Defs)
            {
                var constants = AccessTools.TypeByName(d.Group + "Constants");
                var getter = constants == null ? null : AccessTools.PropertyGetter(constants, d.Name);
                if (getter == null || !getter.IsStatic) continue;

                double value;
                try
                {
                    var raw = getter.Invoke(null, null);
                    value = raw is bool b ? (b ? 1 : 0) : Convert.ToDouble(raw);
                }
                catch
                {
                    continue;
                }

                d.Getters.Add(getter);
                if (AliasClasses.TryGetValue(d.Group, out var aliasName))
                {
                    var alias = AccessTools.TypeByName(aliasName);
                    var aliasGetter = alias == null ? null : AccessTools.PropertyGetter(alias, d.Name);
                    if (aliasGetter != null && aliasGetter.IsStatic && aliasGetter.ReturnType == getter.ReturnType) d.Getters.Add(aliasGetter);
                }

                if (!_groups.TryGetValue(d.Group, out var group)) _groups[d.Group] = group = new TunableSystem(Tunables.GameOwner, d.Group, string.Empty);
                d.Value = MakeValue(group, d.Name, d.Kind, value);
                d.Slot = slots.Count;
                slots.Add(d.Value);
                foreach (var g in d.Getters) _byGetter[g] = d;
            }
            _slots = slots.ToArray();
        }

        private static TunableValue MakeValue(TunableSystem group, string name, TunableKind kind, double value)
        {
            switch (kind)
            {
                case TunableKind.Int: return new TunableInt(group, name, (int)value);
                case TunableKind.Bool: return new TunableBool(group, name, value != 0);
                default: return new TunableFloat(group, name, (float)value);
            }
        }

        /// <summary>The game methods (in the reader classes, including lambdas) that call any of this group's getters.</summary>
        private static IEnumerable<MethodBase> ReadingMethods(string group)
        {
            Prepare();
            var defs = Defs.Where(d => d.Group == group && d.Value != null).ToList();
            if (defs.Count == 0) throw new MissingMemberException(group + "Constants", "read-only values");

            var tokens = new HashSet<int>(defs.SelectMany(d => d.Getters).Where(g => g.Module == GameAssembly.ManifestModule).Select(g => g.MetadataToken));
            var readerNames = new HashSet<string>(defs.SelectMany(d => d.Readers), StringComparer.Ordinal);
            if (AliasClasses.TryGetValue(group, out var alias)) readerNames.Add(alias);

            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type[] types;
            try
            {
                types = GameAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }

            var found = new List<MethodBase>();
            foreach (var top in types.Where(t => readerNames.Contains(t.Name)))
            {
                var scan = new List<Type> { top };
                for (int i = 0; i < scan.Count; i++) scan.AddRange(scan[i].GetNestedTypes(all));
                foreach (var t in scan)
                {
                    if (t.IsGenericTypeDefinition) continue;
                    foreach (var m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
                    {
                        if (m.IsAbstract || m.IsGenericMethodDefinition) continue;
                        if (CallsAny(m, tokens)) found.Add(m);
                    }
                }
            }
            return found.Distinct().ToList();
        }

        /// <summary>Raw IL scan for call/callvirt to one of the getter tokens (same approach as ShellParts).</summary>
        private static bool CallsAny(MethodBase m, HashSet<int> tokens)
        {
            try
            {
                if (m.Module != GameAssembly.ManifestModule) return false;
                var il = m.GetMethodBody()?.GetILAsByteArray();
                if (il == null) return false;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x28 && il[i] != 0x6F) continue;
                    int token = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);
                    if (tokens.Contains(token)) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Replaces "call XConstants.get_Name()" with "ldc.i4 slot; call GetFloat/GetInt/GetBool(slot)".</summary>
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand is MethodInfo mi && _byGetter.TryGetValue(mi, out var d))
                {
                    // Turn the call itself into "ldc.i4 slot" so any jump target or exception block stays on it,
                    // then add the read call after it. (Labels are not touched: their type is not visible to this project.)
                    var read = new CodeInstruction(OpCodes.Call, d.Kind == TunableKind.Bool ? GetBoolMethod : d.Kind == TunableKind.Int ? GetIntMethod : GetFloatMethod);
                    ins.opcode = OpCodes.Ldc_I4;
                    ins.operand = d.Slot;
                    d.Rewrites++;
                    yield return ins;
                    yield return read;
                    continue;
                }
                yield return ins;
            }
        }

        private static string SpecId(string group) => $"{group}Constants read-only values: redirect the game code that reads them";

        internal static IEnumerable<PatchSpec> Specs()
        {
            foreach (var group in Defs.Select(d => d.Group).Distinct())
            {
                var g = group;
                yield return new PatchSpec
                {
                    Id = SpecId(g),
                    FeatureId = Features.ReadOnlyTunables,
                    Required = false,
                    Targets = () => ReadingMethods(g),
                    Transpiler = PatchSpec.Hook(typeof(ReadOnlyTunables), nameof(Transpiler)),
                    Verify = () =>
                    {
                        return Defs.Any(d => d.Group == g && d.Rewrites > 0)
                            ? null
                            : "no reads of " + g + " values were found to redirect";
                    }
                };
            }
        }

        /// <summary>
        /// After patching: adds every value whose readers were redirected to the tunables table. Values whose readers
        /// could not be patched are left out, so changing them is never silently ignored.
        /// </summary>
        internal static int Register()
        {
            // A group whose patch failed was rolled back, even if some of its methods were rewritten first.
            var patched = new HashSet<string>(PatchManager.Results.Where(r => r.Success).Select(r => r.Id), StringComparer.Ordinal);
            int count = 0;
            foreach (var d in Defs)
            {
                if (d.Value == null) continue;
                if (!patched.Contains(SpecId(d.Group))) d.Rewrites = 0;
                if (d.Rewrites == 0)
                {
                    FtdApi.Log.Warning($"Read-only value {d.Group}.{d.Name}: no game code reading it was redirected, so it stays read-only.");
                    continue;
                }
                var note = $"Read-only in the game; {d.Rewrites} read(s) redirected by FtdModApi.";
                if (Tunables.RegisterGameGetter(d.Value, note)) count++;
                var def = d;
                FtdApi.Log.Verbose(() => $"Read-only value {def.Group}.{def.Name} = {def.Value.AsDouble} is tunable ({def.Rewrites} read(s) redirected, slot {def.Slot}).");
            }
            return count;
        }
    }
}
