using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BrilliantSkies.Blocks.AdvCannonComponents.Gui.AmmoCustomising;
using BrilliantSkies.Blocks.Weapons.Uis;
using BrilliantSkies.Core.Help;
using BrilliantSkies.DataManagement.Attributes;
using BrilliantSkies.DataManagement.Saving;
using BrilliantSkies.DataManagement.Vars;
using FtdModApi.Patching;
using HarmonyLib;

namespace FtdModApi
{
    /// <summary>
    /// The APS gauge cap (500 mm in the game). Feature id: <see cref="Features.ApsGaugeCap"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mods request a higher cap with <see cref="RequestMaxGauge"/> (or <c>"apsMaxGaugeMm"</c> in
    /// FtdModApi.content.json). The highest request wins, up to <see cref="AbsoluteMaxGaugeMm"/>. With no requests
    /// the game behaves exactly as normal.
    /// </para>
    /// <para>
    /// Above 500 mm the game's own formulas continue: each extra gauge increaser adds a little less, so 1000 mm needs
    /// about 19 gauge increasers on a single barrel. Multi-barrel guns scale the cap as usual (2 barrels: half).
    /// </para>
    /// <para>
    /// Request the cap in OnLoad. Blueprints with guns above 500 mm load clamped to 500 mm on installs without a mod
    /// that raises the cap, and the smaller gauge is saved if the design is saved there.
    /// </para>
    /// </remarks>
    public static class ApsGauge
    {
        /// <summary>The game's own cap, in mm.</summary>
        public const float GameMaxGaugeMm = 500f;

        /// <summary>
        /// Highest cap the API allows, in mm. The game's gauge increaser formula approaches about 3060 mm with
        /// unlimited increasers, so higher caps could never be reached.
        /// </summary>
        public const float AbsoluteMaxGaugeMm = 3000f;

        private static readonly object _gate = new object();
        private static readonly List<KeyValuePair<string, float>> _requests = new List<KeyValuePair<string, float>>();
        private static volatile float _maxGauge = GameMaxGaugeMm / 1000f;
        private static volatile int _maxIncreasers = 8;

        /// <summary>The cap in effect, in mm.</summary>
        public static float MaxGaugeMm => _maxGauge * 1000f;

        /// <summary>The cap in effect, in metres (the unit the game uses internally).</summary>
        public static float MaxGauge => _maxGauge;

        /// <summary>Gauge increasers a single-barrel gun needs to reach the cap.</summary>
        public static int MaxGaugeIncreasers => _maxIncreasers;

        /// <summary>Who asked for what, for reports.</summary>
        public static IReadOnlyList<KeyValuePair<string, float>> Requests
        {
            get { lock (_gate) return _requests.ToList(); }
        }

        /// <summary>
        /// Ask for the APS gauge cap to be at least <paramref name="maxGaugeMm"/> (e.g. 1000). The highest request
        /// wins. Dispose the registration to withdraw it.
        /// </summary>
        public static Registration RequestMaxGauge(string modId, float maxGaugeMm)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (float.IsNaN(maxGaugeMm) || maxGaugeMm < GameMaxGaugeMm)
            {
                FtdApi.GetLog(modId).Warning($"ApsGauge.RequestMaxGauge({maxGaugeMm}): only caps above the game's {GameMaxGaugeMm} mm do anything.");
            }
            if (maxGaugeMm > AbsoluteMaxGaugeMm)
            {
                FtdApi.GetLog(modId).Warning($"ApsGauge.RequestMaxGauge({maxGaugeMm}): limited to {AbsoluteMaxGaugeMm} mm.");
                maxGaugeMm = AbsoluteMaxGaugeMm;
            }

            var entry = new KeyValuePair<string, float>(modId, maxGaugeMm);
            lock (_gate) _requests.Add(entry);
            Recalculate();
            FtdApi.GetLog(modId).Info($"Requested an APS gauge cap of {maxGaugeMm:0} mm (now {MaxGaugeMm:0} mm).");
            return new Registration(() =>
            {
                lock (_gate) _requests.Remove(entry);
                Recalculate();
            });
        }

        /// <summary>The tunable key that holds the cap, so players see and change it in the settings menu.</summary>
        public const string TunableKey = "FtdModApi.ApsMaxGaugeMm";

        private static TunableFloat _tunable;
        private static Registration _requestContribution;

        /// <summary>
        /// Publishes the cap as a tunable in the API's own settings. Mod requests feed it (the highest one, as a Set);
        /// a player value in the settings menu overrides them.
        /// </summary>
        internal static void RegisterTunable(TunableSystem settings)
        {
            if (settings == null) return;
            if (!Features.IsAvailable(Features.ApsGaugeCap))
            {
                FtdApi.Log.Verbose("APS gauge cap patches are not active; FtdModApi.ApsMaxGaugeMm is not registered.");
                return;
            }
            _tunable = settings.Float("ApsMaxGaugeMm", GameMaxGaugeMm, min: 18f, max: AbsoluteMaxGaugeMm,
                description: "Largest APS shell gauge a gun can reach, in mm (the game's normal limit is 500). Above 500 mm each extra gauge increaser adds a little less. Mods can raise it; a value you set here overrides them.",
                units: "mm");
            _tunable.Changed += v => Apply(((TunableFloat)v).Value);
            Recalculate();
        }

        private static void Recalculate()
        {
            KeyValuePair<string, float> top;
            bool any;
            lock (_gate)
            {
                any = _requests.Count > 0;
                top = any ? _requests.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).First() : default;
            }

            if (_tunable == null)
            {
                Apply(any ? Math.Max(GameMaxGaugeMm, top.Value) : GameMaxGaugeMm);
                return;
            }

            // One contribution, from the mod with the highest request; the tunable's Changed event applies it.
            _requestContribution?.Dispose();
            _requestContribution = any && top.Value > GameMaxGaugeMm ? Tunables.Set(top.Key, TunableKey, top.Value, Priority.Override) : null;
            Apply(_tunable.Value);
        }

        private static void Apply(float mm)
        {
            mm = Math.Max(18f, Math.Min(AbsoluteMaxGaugeMm, mm));
            if (Math.Abs(_maxGauge * 1000f - mm) < 0.001f && _slidersFound) return;
            _maxGauge = mm / 1000f;
            _maxIncreasers = IncreasersFor(_maxGauge);
            ApplySliderLimits();
            FtdApi.Log.Verbose(() => $"APS gauge cap is now {mm:0} mm ({_maxIncreasers} gauge increasers on one barrel); slider limits {(_slidersFound ? "set" : "not found")}.");
        }

        /// <summary>Gauge increasers needed for a single-barrel gun to reach a gauge (the game's own formula).</summary>
        internal static int IncreasersFor(float gauge)
        {
            for (int i = 0; i <= 200; i++)
            {
                if (Adjustments.BiAM(0.06f, i, 0.06f, 0.98f) >= gauge - 0.0001f) return Math.Max(8, i);
            }
            return 200;
        }

        // ------------------------------------------------------------------------------------------
        // Slider limits: the game clamps saved values to the slider attribute's range when it loads a design,
        // and builds the ammo customiser's slider from it. The attributes are cached per class, so raising the
        // cached copies raises the limit everywhere.
        // ------------------------------------------------------------------------------------------

        private static readonly FieldInfo SliderMax = AccessTools.Field(typeof(SliderAttribute), "<Max>k__BackingField");
        private static readonly FieldInfo SliderMiddle = AccessTools.Field(typeof(SliderAttribute), "<Middle>k__BackingField");
        private static bool _slidersFound;

        internal static void ApplySliderLimits()
        {
            try
            {
                bool a = SetSliderMax(typeof(AdvCannonData), nameof(AdvCannonData.DesiredGauge), _maxGauge);
                bool b = SetSliderMax(typeof(ApsShellUiData), nameof(ApsShellUiData.InspectionDiameter), _maxGauge * 1000f);
                _slidersFound = a && b;
            }
            catch (Exception e)
            {
                _slidersFound = false;
                FtdApi.Log.ErrorOnce("aps-gauge-sliders", "Could not raise the APS gauge slider limits.", e);
            }
        }

        private static bool SetSliderMax(Type dataType, string property, float max)
        {
            if (SliderMax == null) return false;
            var set = DataPackageSaveAndLoad.Gather(dataType);
            var data = set.Attributes.FirstOrDefault(v => v.Property != null && v.Property.Name == property);
            if (!(data?.Attribute is SliderAttribute slider)) return false;
            SliderMax.SetValue(slider, max);
            SliderMiddle?.SetValue(slider, (slider.Min + max) / 2f);
            return true;
        }

        // ------------------------------------------------------------------------------------------
        // Patches
        // ------------------------------------------------------------------------------------------

        // Postfix on the AdvCannonData constructor: the gauge variable clamps to the cap.
        private static void AdvCannonData_Postfix(AdvCannonData __instance)
        {
            if (__instance.DesiredGauge is VarFloatClamp v && v.Max < _maxGauge) v.Max = _maxGauge;
        }

        // Postfix on the ApsShellUiData constructor: the ammo customiser's "gauge for stats" slider.
        private static void ApsShellUiData_Postfix(ApsShellUiData __instance)
        {
            float mm = _maxGauge * 1000f;
            if (__instance.InspectionDiameter is VarFloatClamp v && v.Max < mm) v.Max = mm;
        }

        /// <summary>Called from patched game code in place of the constant 0.5 (m).</summary>
        public static float CapMetres() => _maxGauge;

        /// <summary>Called from patched game code in place of the constant 500 (mm).</summary>
        public static float CapMillimetres() => _maxGauge * 1000f;

        /// <summary>Called from patched game code in place of the constant 8 (gauge increasers).</summary>
        public static int CapIncreasers() => _maxIncreasers;

        private static readonly Dictionary<string, int> _rewrites = new Dictionary<string, int>();

        private static int Rewrites(string key) => _rewrites.TryGetValue(key, out var n) ? n : 0;

        private static void Bump(string key) => _rewrites[key] = Rewrites(key) + 1;

        private static readonly MethodInfo CapMetresMethod = AccessTools.Method(typeof(ApsGauge), nameof(CapMetres));
        private static readonly MethodInfo CapMillimetresMethod = AccessTools.Method(typeof(ApsGauge), nameof(CapMillimetres));
        private static readonly MethodInfo CapIncreasersMethod = AccessTools.Method(typeof(ApsGauge), nameof(CapIncreasers));

        private static bool IsFloat(CodeInstruction ins, float value)
            => ins.opcode == OpCodes.Ldc_R4 && ins.operand is float f && Math.Abs(f - value) < 0.00001f;

        // AdvCannonNode.CheckGauge: Mathf.Min(0.5f, gauge from increasers).
        private static IEnumerable<CodeInstruction> CheckGauge_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if (IsFloat(ins, 0.5f))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = CapMetresMethod;
                    Bump("CheckGauge");
                }
                yield return ins;
            }
        }

        // AdvCannonFiringPiece.GaugeIncreasesToProvideShellDiameter: for (i = 0; i <= 8; i++).
        private static IEnumerable<CodeInstruction> GaugeIncreases_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldc_I4_8 || (ins.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(ins.operand) == 8))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = CapIncreasersMethod;
                    Bump("GaugeIncreases");
                }
                yield return ins;
            }
        }

        // Tooltip and ammo customiser texts: the constant 500 (mm).
        private static IEnumerable<CodeInstruction> FiveHundred_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            foreach (var ins in instructions)
            {
                if (IsFloat(ins, 500f))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = CapMillimetresMethod;
                    Bump(original.Name);
                }
                yield return ins;
            }
        }

        // ApsTab.Build: the desired gauge slider, Quick(_focus, 18f, 500f, ...). Only the 500 right after an 18.
        private static IEnumerable<CodeInstruction> ApsTab_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeInstruction previous = null;
            foreach (var ins in instructions)
            {
                if (previous != null && IsFloat(previous, 18f) && IsFloat(ins, 500f))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = CapMillimetresMethod;
                    Bump("ApsTab");
                }
                previous = ins;
                yield return ins;
            }
        }

        internal static IEnumerable<PatchSpec> Specs()
        {
            var self = typeof(ApsGauge);

            yield return new PatchSpec
            {
                Id = "AdvCannonNode.CheckGauge: gauge cap",
                FeatureId = Features.ApsGaugeCap,
                Target = () => PatchSpec.Method(typeof(AdvCannonNode), nameof(AdvCannonNode.CheckGauge)),
                Transpiler = PatchSpec.Hook(self, nameof(CheckGauge_Transpiler)),
                Verify = () => Rewrites("CheckGauge") == 1 ? null : $"expected one 0.5 m cap, found {Rewrites("CheckGauge")}"
            };

            yield return new PatchSpec
            {
                Id = "AdvCannonFiringPiece.GaugeIncreasesToProvideShellDiameter: more than 8 increasers",
                FeatureId = Features.ApsGaugeCap,
                Target = () => PatchSpec.Method(typeof(AdvCannonFiringPiece), nameof(AdvCannonFiringPiece.GaugeIncreasesToProvideShellDiameter), typeof(float), typeof(float)),
                Transpiler = PatchSpec.Hook(self, nameof(GaugeIncreases_Transpiler)),
                Verify = () => Rewrites("GaugeIncreases") == 1 ? null : $"expected one loop limit of 8, found {Rewrites("GaugeIncreases")}"
            };

            yield return new PatchSpec
            {
                Id = "AdvCannonData constructor: desired gauge limit",
                FeatureId = Features.ApsGaugeCap,
                Target = () => AccessTools.Constructor(typeof(AdvCannonData), new[] { typeof(uint) }),
                Postfix = PatchSpec.Hook(self, nameof(AdvCannonData_Postfix)),
                Verify = () =>
                {
                    ApplySliderLimits();
                    return _slidersFound ? null : "the desired gauge slider attributes were not found";
                }
            };

            yield return new PatchSpec
            {
                Id = "ApsShellUiData constructor: ammo customiser gauge slider",
                FeatureId = Features.ApsGaugeCap,
                Required = false,
                Target = () => AccessTools.Constructor(typeof(ApsShellUiData), new[] { typeof(uint) }),
                Postfix = PatchSpec.Hook(self, nameof(ApsShellUiData_Postfix))
            };

            yield return new PatchSpec
            {
                Id = "APS tooltip and ammo customiser texts: 500 mm",
                FeatureId = Features.ApsGaugeCap,
                Required = false,
                Targets = () => new MethodBase[]
                {
                    AccessTools.Method(typeof(AdvCannonFiringPiece), "AppendGaugeInfo"),
                    AccessTools.Method(typeof(BrilliantSkies.Blocks.AdvCannonComponents.Gui.AmmoCustomising.StatsTab), "HandleDiam")
                }.Where(m => m != null).ToList(),
                Transpiler = PatchSpec.Hook(self, nameof(FiveHundred_Transpiler)),
                Verify = () => Rewrites("AppendGaugeInfo") >= 1 ? null : "the gun tooltip's 500 mm was not found"
            };
        }

        /// <summary>
        /// The firing piece's settings screen needs the game's UI styles, which load with the main menu,
        /// so it is patched then (the same reason other gauge mods delay their patches).
        /// </summary>
        internal static IEnumerable<PatchSpec> MainMenuSpecs()
        {
            yield return new PatchSpec
            {
                Id = "ApsTab.Build: desired gauge slider",
                FeatureId = Features.ApsGaugeCap,
                Required = false,
                Target = () => PatchSpec.Method(typeof(ApsTab), nameof(ApsTab.Build)),
                Transpiler = PatchSpec.Hook(typeof(ApsGauge), nameof(ApsTab_Transpiler)),
                Verify = () => Rewrites("ApsTab") == 1 ? null : $"expected one 18-500 mm slider, found {Rewrites("ApsTab")}"
            };
        }

        internal static void AppendReport(StringBuilder sb)
        {
            sb.AppendLine($"APS GAUGE CAP  {MaxGaugeMm:0} mm (game: {GameMaxGaugeMm:0} mm); a single barrel needs {MaxGaugeIncreasers} gauge increasers to reach it");
            foreach (var r in Requests) sb.AppendLine($"  [{r.Key}] requested {r.Value:0} mm");
            sb.AppendLine();
        }
    }
}
