using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FtdModApi.Patching;
using HarmonyLib;
using UnityEngine;

namespace FtdModApi
{
    /// <summary>Which kind of APS loader a <see cref="ApsLoaderContext"/> is about.</summary>
    public enum ApsLoaderKind
    {
        /// <summary>Standard autoloaders (1m to 8m).</summary>
        Autoloader,
        /// <summary>Belt feed autoloaders.</summary>
        BeltFeed
    }

    /// <summary>
    /// Hooks into the advanced cannon (APS). Feature ids: <see cref="Features.ApsShellStats"/> and
    /// <see cref="Features.ApsLoaderTiming"/>.
    /// </summary>
    public static class ApsApi
    {
        /// <summary>
        /// An APS shell's stats, worked out once when the game builds the shell from its design (ammo customiser,
        /// loading a ship, changing gauge). Change the context's values to change every shell of that design:
        /// reload, cooldown, speed, kinetic damage, armour piercing, payload strength and shell health.
        /// Feature id: <see cref="Features.ApsShellStats"/>.
        /// </summary>
        /// <remarks>
        /// The ammo customiser shows the changed values. Shells already built keep their stats until the game
        /// rebuilds them, so register modifiers in OnLoad. May run on a worker thread: keep it a pure calculation.
        /// </remarks>
        public static ModifierPipeline<ApsShellContext> ShellStats { get; } =
            new ModifierPipeline<ApsShellContext>(Features.ApsShellStats);

        /// <summary>
        /// How fast one APS loader loads its shells, as a factor on the shell's reload time (lower = faster).
        /// Runs whenever the game recalculates it: when the loader is placed, clips are attached or removed, or the
        /// gauge changes. Feature id: <see cref="Features.ApsLoaderTiming"/>.
        /// </summary>
        public static ModifierPipeline<ApsLoaderContext> LoaderTiming { get; } =
            new ModifierPipeline<ApsLoaderContext>(Features.ApsLoaderTiming);

        /// <summary>
        /// Convenience: multiply the loading time of one loader block (guid or name, e.g. "Autoloader (8m shells)").
        /// 0.8 = 20% faster. Works for copies made with the game's item-duplicate files too.
        /// </summary>
        public static Registration SetLoaderTimeFactor(string modId, string blockGuidOrName, float factor)
        {
            if (string.IsNullOrWhiteSpace(blockGuidOrName)) throw new ArgumentException("A block guid or name is required.", nameof(blockGuidOrName));
            if (float.IsNaN(factor) || factor <= 0f) throw new ArgumentOutOfRangeException(nameof(factor), "Must be more than 0.");
            var key = blockGuidOrName.Trim();
            bool isGuid = Guid.TryParse(key, out var guid);
            return LoaderTiming.Add(modId, ctx =>
            {
                if (isGuid ? ctx.BlockGuid == guid : string.Equals(ctx.BlockName, key, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.LoadingTimeFactor *= factor;
                }
            }, Priority.Multiply, $"{key} loading time x{factor}");
        }

        // ------------------------------------------------------------------------------------------
        // Shell stats
        // ------------------------------------------------------------------------------------------

        private static readonly MethodInfo SetReloadTime = AccessTools.PropertySetter(typeof(ShellModel_Propellant), nameof(ShellModel_Propellant.ReloadTime));
        private static readonly MethodInfo SetIntakeTime = AccessTools.PropertySetter(typeof(ShellModel_Propellant), nameof(ShellModel_Propellant.IntakeTime));
        private static readonly MethodInfo SetCooldownTime = AccessTools.PropertySetter(typeof(ShellModel_Propellant), nameof(ShellModel_Propellant.CooldownTime));
        private static readonly MethodInfo SetSpeedModifier = AccessTools.PropertySetter(typeof(ShellModel_Propellant), nameof(ShellModel_Propellant.SpeedModifier));
        private static readonly MethodInfo SetKineticModifier = AccessTools.PropertySetter(typeof(ShellModel_KineticDamage), nameof(ShellModel_KineticDamage.KineticDamageModifier));
        private static readonly MethodInfo SetApModifier = AccessTools.PropertySetter(typeof(ShellModel_ArmourPierce), nameof(ShellModel_ArmourPierce.ArmourPiercingModifier));
        private static readonly FieldInfo HealthField = AccessTools.Field(typeof(ShellModel), "<Health>k__BackingField");
        private static readonly FieldInfo ExplosiveModifierField = AccessTools.Field(typeof(ShellModel_ExplosiveCharges), "_explosiveModifier");
        private static readonly MethodInfo DeriveStatistics = AccessTools.Method(typeof(ShellModel_ExplosiveCharges), "DeriveStatistics");

        private static void Set(MethodInfo setter, object target, float value, float old)
        {
            if (setter != null && target != null && value != old) setter.Invoke(target, new object[] { value });
        }

        // Postfix on the ShellModel constructor: the shell's stats are final at this point.
        private static void ShellModel_Postfix(ShellModel __instance)
        {
            var pipeline = ShellStats;
            if (pipeline.IsEmpty) return;

            var propellant = __instance.Propellant as ShellModel_Propellant;
            var kinetic = __instance.KineticDamage as ShellModel_KineticDamage;
            var ap = __instance.ArmourPierce as ShellModel_ArmourPierce;
            if (propellant == null || kinetic == null || ap == null) return;

            var ctx = ApsShellContext.Rent(__instance, propellant, kinetic, ap);
            try
            {
                pipeline.Run(ctx);

                Set(SetReloadTime, propellant, Mathf.Max(0.01f, ctx.ReloadTime), propellant.ReloadTime);
                Set(SetIntakeTime, propellant, Mathf.Max(0.01f, ctx.IntakeTime), propellant.IntakeTime);
                Set(SetCooldownTime, propellant, Mathf.Max(0f, ctx.CooldownTime), propellant.CooldownTime);
                Set(SetSpeedModifier, propellant, Mathf.Max(0f, ctx.SpeedModifier), propellant.SpeedModifier);
                Set(SetKineticModifier, kinetic, Mathf.Max(0f, ctx.KineticDamageModifier), kinetic.KineticDamageModifier);
                Set(SetApModifier, ap, Mathf.Max(0f, ctx.ArmourPiercingModifier), ap.ArmourPiercingModifier);

                float health = Mathf.Round(Mathf.Max(1f, ctx.Health));
                if (HealthField != null && health != __instance.Health) HealthField.SetValue(__instance, health);

                float payload = Mathf.Max(0f, ctx.PayloadMultiplier);
                if (payload != 1f && __instance.ExplosiveCharges is ShellModel_ExplosiveCharges charges && ExplosiveModifierField != null && DeriveStatistics != null)
                {
                    ExplosiveModifierField.SetValue(charges, (float)ExplosiveModifierField.GetValue(charges) * payload);
                    DeriveStatistics.Invoke(charges, null);
                }
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("aps-shell-stats", "Applying APS shell stat modifiers failed.", e);
            }
            finally
            {
                ctx.Release();
            }
        }

        // ------------------------------------------------------------------------------------------
        // Loader timing
        // ------------------------------------------------------------------------------------------

        private sealed class LoaderRef
        {
            public AdvCannonAutoloader Block;
        }

        private static readonly ConditionalWeakTable<ShellRackModelAbstract, LoaderRef> _loaders = new ConditionalWeakTable<ShellRackModelAbstract, LoaderRef>();
        private static readonly FieldInfo RackField = AccessTools.Field(typeof(AdvCannonAutoloader), "ShellRackModel");
        private static readonly MethodInfo SetLoadingTimeFactor = AccessTools.PropertySetter(typeof(ShellRackModelAbstract), nameof(ShellRackModelAbstract.LoadingTimeFactor));
        private static readonly MethodInfo CalculateLoadingTimeFactor = AccessTools.Method(typeof(ShellRackModelAbstract), "CalculateLoadingTimeFactor");

        [ThreadStatic] private static bool _applying;

        // Postfix on AdvCannonAutoloader.ComponentStart (and the belt feed's override): remember which block owns the rack.
        private static void ComponentStart_Postfix(AdvCannonAutoloader __instance)
        {
            if (!(RackField?.GetValue(__instance) is ShellRackModelAbstract rack)) return;
            _loaders.Remove(rack);
            _loaders.Add(rack, new LoaderRef { Block = __instance });
            // The rack worked out its factor in its constructor, before we knew its block. Belt feeds redo it when
            // their gauge is set; for the others, recalculate now.
            if (!(rack is BeltFeedShellRackModel) && !LoaderTiming.IsEmpty)
            {
                try
                {
                    CalculateLoadingTimeFactor?.Invoke(rack, null);
                }
                catch (Exception e)
                {
                    FtdApi.Log.ErrorOnce("aps-loader-recalc", "Recalculating an APS loader's timing failed.", e);
                }
            }
        }

        // Postfix on ShellRackModelAbstract.CalculateLoadingTimeFactor.
        private static void CalculateLoadingTimeFactor_Postfix(ShellRackModelAbstract __instance) => ApplyLoaderTiming(__instance, updateIntakes: false);

        // Postfix on BeltFeedShellRackModel.SetDiameter (belt feeds set their factor from the gauge).
        private static void BeltSetDiameter_Postfix(BeltFeedShellRackModel __instance) => ApplyLoaderTiming(__instance, updateIntakes: true);

        private static void ApplyLoaderTiming(ShellRackModelAbstract rack, bool updateIntakes)
        {
            if (_applying || LoaderTiming.IsEmpty) return;
            if (!_loaders.TryGetValue(rack, out var owner) || owner.Block == null) return;

            _applying = true;
            var ctx = ApsLoaderContext.Rent(owner.Block, rack);
            try
            {
                LoaderTiming.Run(ctx);
                float factor = Mathf.Max(0.001f, ctx.LoadingTimeFactor);
                if (factor != rack.LoadingTimeFactor)
                {
                    SetLoadingTimeFactor.Invoke(rack, new object[] { factor });
                    var name = ctx.BlockName;
                    var original = ctx.OriginalLoadingTimeFactor;
                    FtdApi.Log.VerboseOnce("aps-loader:" + name, () => $"APS loader '{name}': loading time factor {original:0.###} -> {factor:0.###} (first change for this block type).");
                    if (updateIntakes)
                    {
                        foreach (var intake in rack.Intakes) intake.LoadingTimeFactor = factor;
                    }
                }
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("aps-loader-timing", "Applying APS loader timing modifiers failed.", e);
            }
            finally
            {
                ctx.Release();
                _applying = false;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Patches
        // ------------------------------------------------------------------------------------------

        internal static IEnumerable<PatchSpec> Specs()
        {
            var self = typeof(ApsApi);

            yield return new PatchSpec
            {
                Id = "ShellModel constructor: APS shell stats",
                FeatureId = Features.ApsShellStats,
                Target = () => AccessTools.Constructor(typeof(ShellModel), new[] { typeof(IShellModelPartsAndMesh), typeof(List<ShellModuleParams>), typeof(float) }),
                Postfix = PatchSpec.Hook(self, nameof(ShellModel_Postfix)),
                Verify = () => SetReloadTime == null || SetCooldownTime == null || SetSpeedModifier == null || SetKineticModifier == null || SetApModifier == null
                    ? "a shell stat setter was not found"
                    : (HealthField == null || ExplosiveModifierField == null || DeriveStatistics == null ? "shell health or payload fields not found" : null)
            };

            yield return new PatchSpec
            {
                Id = "AdvCannonAutoloader.ComponentStart: remember each loader's rack",
                FeatureId = Features.ApsLoaderTiming,
                Targets = () => new MethodBase[]
                {
                    AccessTools.DeclaredMethod(typeof(AdvCannonAutoloader), nameof(AdvCannonAutoloader.ComponentStart)),
                    AccessTools.DeclaredMethod(typeof(AdvCannonBeltFeedAutoloader), nameof(AdvCannonBeltFeedAutoloader.ComponentStart))
                }.Where(m => m != null).ToList(),
                Postfix = PatchSpec.Hook(self, nameof(ComponentStart_Postfix)),
                Verify = () => RackField == null || SetLoadingTimeFactor == null ? "loader rack field or LoadingTimeFactor setter not found" : null
            };

            yield return new PatchSpec
            {
                Id = "ShellRackModelAbstract.CalculateLoadingTimeFactor: loader timing",
                FeatureId = Features.ApsLoaderTiming,
                Target = () => CalculateLoadingTimeFactor ?? throw new MissingMethodException("ShellRackModelAbstract", "CalculateLoadingTimeFactor"),
                Postfix = PatchSpec.Hook(self, nameof(CalculateLoadingTimeFactor_Postfix))
            };

            yield return new PatchSpec
            {
                Id = "BeltFeedShellRackModel.SetDiameter: belt feed timing",
                FeatureId = Features.ApsLoaderTiming,
                Target = () => PatchSpec.Method(typeof(BeltFeedShellRackModel), nameof(BeltFeedShellRackModel.SetDiameter), typeof(float)),
                Postfix = PatchSpec.Hook(self, nameof(BeltSetDiameter_Postfix))
            };
        }
    }

    /// <summary>
    /// One APS shell design's stats while modifiers run. Only the settable values change the shell.
    /// Do not keep a reference to the context after your modifier returns (it is reused).
    /// </summary>
    public sealed class ApsShellContext
    {
        [ThreadStatic] private static ApsShellContext _cached;

        private bool _inUse;
        private readonly List<string> _parts = new List<string>();

        private ApsShellContext()
        {
        }

        internal static ApsShellContext Rent(ShellModel shell, ShellModel_Propellant propellant, ShellModel_KineticDamage kinetic, ShellModel_ArmourPierce ap)
        {
            var c = _cached != null && !_cached._inUse ? _cached : (_cached == null ? (_cached = new ApsShellContext()) : new ApsShellContext());
            c._inUse = true;
            c.GameShell = shell;
            c.Diameter = shell.Diameter;
            c._parts.Clear();
            foreach (var part in shell.PartsAndMesh.ShellParts) c._parts.Add(ShellParts.Describe(part.Type));
            c.PropellantAmount = propellant.PropellantAmount;
            c.ShellLength = propellant.LengthOfShell;
            c.TotalLength = propellant.TotalLength;
            c.ReloadTime = propellant.ReloadTime;
            c.IntakeTime = propellant.IntakeTime;
            c.CooldownTime = propellant.CooldownTime;
            c.SpeedModifier = propellant.SpeedModifier;
            c.KineticDamageModifier = kinetic.KineticDamageModifier;
            c.ArmourPiercingModifier = ap.ArmourPiercingModifier;
            c.Health = shell.Health;
            c.PayloadMultiplier = 1f;
            return c;
        }

        internal void Release()
        {
            _inUse = false;
            GameShell = null;
        }

        // ---- Read-only inputs ----

        /// <summary>Shell diameter (gauge) in metres.</summary>
        public float Diameter { get; private set; }

        /// <summary>Part names in the shell (game names such as "HeWarhead", or a modded part's id), base first.</summary>
        public IReadOnlyList<string> Parts => _parts;

        /// <summary>How many of a part (game name or modded id) the shell has.</summary>
        public int CountOf(string part) => _parts.Count(p => string.Equals(p, part, StringComparison.OrdinalIgnoreCase));

        /// <summary>Propellant in the casings (gunpowder plus railgun casings equivalent).</summary>
        public float PropellantAmount { get; private set; }

        /// <summary>Length of the shell without casings, in metres.</summary>
        public float ShellLength { get; private set; }

        /// <summary>Total length including casings, in metres.</summary>
        public float TotalLength { get; private set; }

        /// <summary>The game's shell object. Raw game type: may change between game versions.</summary>
        public ShellModel GameShell { get; private set; }

        // ---- Values you can change ----

        /// <summary>Seconds a loader takes to load this shell (before the loader's own factor).</summary>
        public float ReloadTime { get; set; }

        /// <summary>Seconds an ammo intake takes to take this shell in.</summary>
        public float IntakeTime { get; set; }

        /// <summary>Barrel cooldown time after firing this shell.</summary>
        public float CooldownTime { get; set; }

        /// <summary>Muzzle velocity multiplier from the shell's parts (also scales railgun speed).</summary>
        public float SpeedModifier { get; set; }

        /// <summary>Kinetic damage multiplier from the shell's parts.</summary>
        public float KineticDamageModifier { get; set; }

        /// <summary>Armour piercing multiplier from the shell's parts.</summary>
        public float ArmourPiercingModifier { get; set; }

        /// <summary>Shell health (minimum 1).</summary>
        public float Health { get; set; }

        /// <summary>
        /// Multiplier on every payload: HE, frag, flak, EMP, incendiary, HEAT/HESH. 1 = unchanged.
        /// </summary>
        public float PayloadMultiplier { get; set; }
    }

    /// <summary>One APS loader's timing while modifiers run. Do not keep a reference to the context (it is reused).</summary>
    public sealed class ApsLoaderContext
    {
        private static readonly ApsLoaderContext Shared = new ApsLoaderContext();
        private bool _inUse;

        private ApsLoaderContext()
        {
        }

        internal static ApsLoaderContext Rent(AdvCannonAutoloader block, ShellRackModelAbstract rack)
        {
            var c = !Shared._inUse ? Shared : new ApsLoaderContext();
            c._inUse = true;
            c.Block = block;
            c.Kind = block is AdvCannonBeltFeedAutoloader ? ApsLoaderKind.BeltFeed : ApsLoaderKind.Autoloader;
            c.BlockGuid = block.item?.ComponentId?.Guid ?? Guid.Empty;
            c.BlockName = block.item?.ComponentId?.Name ?? string.Empty;
            c.LengthCapacity = rack.LengthCapacity;
            c.Diameter = rack.Diameter;
            c.ClipCount = rack.ContainerCount;
            c.OriginalLoadingTimeFactor = rack.LoadingTimeFactor;
            c.LoadingTimeFactor = rack.LoadingTimeFactor;
            return c;
        }

        internal void Release()
        {
            _inUse = false;
            Block = null;
        }

        /// <summary>Autoloader or belt feed.</summary>
        public ApsLoaderKind Kind { get; private set; }

        /// <summary>The loader block's guid (works for duplicated blocks too).</summary>
        public Guid BlockGuid { get; private set; }

        /// <summary>The loader block's internal name, e.g. "Autoloader (8m shells)".</summary>
        public string BlockName { get; private set; }

        /// <summary>Longest shell the loader can take, in metres.</summary>
        public float LengthCapacity { get; private set; }

        /// <summary>Gauge of the cannon it feeds, in metres (0 until known).</summary>
        public float Diameter { get; private set; }

        /// <summary>Ammo clips attached.</summary>
        public int ClipCount { get; private set; }

        /// <summary>The game's factor before any modifier.</summary>
        public float OriginalLoadingTimeFactor { get; private set; }

        /// <summary>Loading time = shell reload time x this. Lower is faster. Change this.</summary>
        public float LoadingTimeFactor { get; set; }

        /// <summary>The game's loader block. Raw game type: may change between game versions.</summary>
        public AdvCannonAutoloader Block { get; private set; }
    }
}
