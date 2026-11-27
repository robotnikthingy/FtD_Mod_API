using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using BrilliantSkies.Ftd.Missiles;
using BrilliantSkies.Ftd.Missiles.Blueprints;
using BrilliantSkies.Ftd.Missiles.Components;
using BrilliantSkies.Ftd.Missiles.Editor;
using BrilliantSkies.Ftd.Missiles.Ui;
using FtdModApi.Patching;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace FtdModApi
{
    /// <summary>
    /// A new missile part made from data: a copy of a game part (<see cref="BehavesAs"/>) with its own name
    /// and numbers. For new behaviour, write a class instead and use <see cref="MissileParts.Register{T}"/>.
    /// </summary>
    public sealed class MissilePartDefinition
    {
        /// <summary>
        /// Permanent id, e.g. "MyMod.HeavyHeWarhead". It becomes the number saved in missile designs and
        /// blueprints, so never change it after release.
        /// </summary>
        public string Id { get; set; }

        /// <summary>Name shown in the missile designer. Must differ from every other part's name.</summary>
        public string Name { get; set; }

        /// <summary>Description shown in the missile designer tooltip.</summary>
        public string Description { get; set; }

        /// <summary>
        /// The game part to copy: its class name with or without the "Missile" prefix (e.g. "ExplosiveWarhead",
        /// "MissileFins") or its number. FtdModApi_content.txt lists them. The new part is an instance of that
        /// class, so it has its settings, model and behaviour everywhere the game checks for that part.
        /// </summary>
        public string BehavesAs { get; set; }

        // Overrides. Leave null to keep the BehavesAs part's value. FtdModApi_content.txt shows the game's values.

        /// <summary>Health of the part relative to the missile's base health (warheads 0.1, bodies 1.6, most parts 0.4).</summary>
        public float? HealthModifier { get; set; }

        /// <summary>Launch speed factor (propulsion parts).</summary>
        public float? LaunchSpeed { get; set; }

        /// <summary>Ammo cost factor.</summary>
        public float? AmmoCostModifier { get; set; }

        /// <summary>Mass factor.</summary>
        public float? MassModifier { get; set; }

        /// <summary>Drag factor.</summary>
        public float? DragModifier { get; set; }

        /// <summary>Angular drag factor.</summary>
        public float? AngularDragModifier { get; set; }

        /// <summary>
        /// How much payload the part counts as for its load type (HE, frag, EMP, incendiary, fuel, reinforcement).
        /// Game parts count as 1. 1.25 means "a quarter more HE than a normal HE warhead".
        /// </summary>
        public float? PayloadVolume { get; set; }
    }

    /// <summary>A registered modded missile part.</summary>
    public sealed class MissilePartInfo
    {
        internal MissilePartInfo(string modId, string id, int number, MissilePartDefinition definition, Type codeType)
        {
            ModId = modId;
            Id = id;
            Number = number;
            Definition = definition;
            CodeType = codeType;
        }

        public string ModId { get; }

        public string Id { get; }

        /// <summary>The number saved in missile designs for this part.</summary>
        public int Number { get; }

        /// <summary>The game part number it behaves as (0 until the game's missile parts are loaded).</summary>
        public int BehavesAs { get; internal set; }

        /// <summary>True once the part is in the game's part list.</summary>
        public bool IsActive { get; internal set; }

        /// <summary>What happened when the part was added, for reports.</summary>
        public string Status { get; internal set; } = "waiting for the main menu";

        internal MissilePartDefinition Definition { get; }

        /// <summary>The mod's own class for code parts; null for data parts.</summary>
        internal Type CodeType { get; }

        internal bool IsData => CodeType == null;
    }

    /// <summary>
    /// Add new missile parts. Feature ids: <see cref="Features.MissilePartRegistry"/> (parts exist, appear in the
    /// missile designer and survive saving) and <see cref="Features.MissilePartBehaviour"/> (they count as the
    /// game part they are based on wherever the game checks part numbers).
    /// </summary>
    /// <remarks>
    /// <para>Two ways to make a part:</para>
    /// <list type="bullet">
    /// <item><b>Data</b>: <see cref="Register(string, MissilePartDefinition)"/> or <c>"missileParts"</c> in
    /// FtdModApi.content.json. A copy of a game part with a new name and numbers.</item>
    /// <item><b>Code</b>: <see cref="Register{T}"/> with your own class deriving from a game part (or from
    /// <c>MissileComponent</c>), overriding whatever you like. Declare
    /// <c>public new const enumMissileComponentType ComponentType = enumMissileComponentType.None;</c> in it so
    /// the game's own part scan skips it quietly; the API gives it its number.</item>
    /// </list>
    /// <para>
    /// Parts are added when the main menu first opens. If a mod that added parts is removed, missiles that used
    /// them load with a plain body in that slot (the API replaces unknown modded parts instead of letting the whole
    /// missile fail to load).
    /// </para>
    /// </remarks>
    public static class MissileParts
    {
        internal const string IdCategory = "MissilePart";

        private static readonly object _gate = new object();
        private static readonly List<MissilePartInfo> _parts = new List<MissilePartInfo>();
        private static volatile Dictionary<int, MissilePartInfo> _byNumber = new Dictionary<int, MissilePartInfo>();
        private static volatile Dictionary<int, int> _behaviour = new Dictionary<int, int>();
        private static bool _activated;

        /// <summary>Registers a data part. Returns null if the definition is invalid (the reason is logged).</summary>
        public static MissilePartInfo Register(string modId, MissilePartDefinition definition)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.BehavesAs))
            {
                FtdApi.GetLog(modId).Error($"MissileParts.Register('{definition.Id}'): Id, Name and BehavesAs are required.");
                return null;
            }
            return Add(modId, definition.Id, definition, null);
        }

        /// <summary>
        /// Registers a part implemented by your own class. <typeparamref name="T"/> needs a public
        /// (MissileSize, UIParameterBag) constructor, like the game's parts.
        /// </summary>
        public static MissilePartInfo Register<T>(string modId, string id) where T : MissileComponent
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            var type = typeof(T);
            if (type.IsAbstract || type.GetConstructor(new[] { typeof(MissileSize), typeof(UIParameterBag) }) == null)
            {
                FtdApi.GetLog(modId).Error($"MissileParts.Register<{type.Name}>: the class must be non-abstract with a public (MissileSize, UIParameterBag) constructor.");
                return null;
            }
            return Add(modId, id, null, type);
        }

        private static MissilePartInfo Add(string modId, string id, MissilePartDefinition definition, Type codeType)
        {
            var log = FtdApi.GetLog(modId);
            if (string.IsNullOrWhiteSpace(id))
            {
                log.Error("MissileParts.Register: an Id is required.");
                return null;
            }

            int number;
            try
            {
                number = ContentIds.Register(IdCategory, id);
            }
            catch (InvalidOperationException e)
            {
                log.Error("MissileParts.Register: " + e.Message);
                return null;
            }

            var info = new MissilePartInfo(modId, id, number, definition, codeType);
            bool activated;
            lock (_gate)
            {
                var existing = _parts.FirstOrDefault(p => p.Number == number);
                if (existing != null)
                {
                    log.Warning($"Missile part '{id}' is already registered.");
                    return existing;
                }
                _parts.Add(info);
                _byNumber = _parts.ToDictionary(p => p.Number);
                activated = _activated;
            }

            log.Info($"Registered missile part '{id}' (number {number}, {(codeType != null ? "class " + codeType.Name : "behaves as " + definition.BehavesAs)}).");
            if (activated) Activate(info); // registered after the main menu opened
            return info;
        }

        /// <summary>All registered modded parts.</summary>
        public static IReadOnlyList<MissilePartInfo> All
        {
            get { lock (_gate) return _parts.ToList(); }
        }

        /// <summary>The modded part with this id, or null.</summary>
        public static MissilePartInfo Find(string id)
        {
            lock (_gate) return _parts.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        }

        /// <summary>
        /// The game part number a part behaves as: itself for game parts, the BehavesAs part for modded ones.
        /// Use it in your own patches instead of reading <c>componentType</c> directly.
        /// </summary>
        public static int EffectiveType(int number)
        {
            if (number < ContentIds.FirstModdedId) return number;
            return _behaviour.TryGetValue(number, out var b) ? b : number;
        }

        /// <summary>Called from patched game code in place of reading <c>MissileComponent.componentType</c>.</summary>
        public static enumMissileComponentType EffectiveTypeOf(MissileComponent component)
            => component == null ? enumMissileComponentType.None : (enumMissileComponentType)EffectiveType((int)component.componentType);

        // ------------------------------------------------------------------------------------------
        // Adding parts to the game's part list (R_Missiles)
        // ------------------------------------------------------------------------------------------

        private static Dictionary<enumMissileComponentType, Type> GameTypes()
            => (Dictionary<enumMissileComponentType, Type>)AccessTools.Field(typeof(R_Missiles), "_componentTypes").GetValue(null);

        private static Dictionary<enumMissileComponentCategory, SortedList<string, MissileComponent>> GameCategories()
            => (Dictionary<enumMissileComponentCategory, SortedList<string, MissileComponent>>)AccessTools.Field(typeof(R_Missiles), "_componentCategoryLookup").GetValue(null);

        /// <summary>
        /// Adds every registered part to the game's lists. Runs when the main menu first opens: the game builds its
        /// missile part list from plugin classes, so it must not be touched while plugins are still loading.
        /// </summary>
        internal static void ActivateAll()
        {
            if (!Features.IsAvailable(Features.MissilePartRegistry)) return;
            List<MissilePartInfo> parts;
            lock (_gate)
            {
                _activated = true;
                parts = _parts.ToList();
            }
            try
            {
                RuntimeHelpers.RunClassConstructor(typeof(R_Missiles).TypeHandle);
            }
            catch (Exception e)
            {
                FtdApi.Log.Error("The game's missile part list could not be loaded; modded missile parts are unavailable.", e);
                return;
            }
            foreach (var p in parts) Activate(p);
        }

        private static void Activate(MissilePartInfo part)
        {
            var log = FtdApi.GetLog(part.ModId);
            try
            {
                var types = GameTypes();
                var categories = GameCategories();
                if (types == null || categories == null) throw new MissingFieldException("R_Missiles part tables not found");

                Type cls;
                int behavesAs;
                if (part.IsData)
                {
                    if (!TryFindGamePart(types, part.Definition.BehavesAs, out behavesAs, out cls))
                    {
                        part.Status = $"BehavesAs '{part.Definition.BehavesAs}' is not a game missile part (see FtdModApi_content.txt)";
                        log.Error($"Missile part '{part.Id}': {part.Status}.");
                        return;
                    }
                }
                else
                {
                    cls = part.CodeType;
                    behavesAs = NearestGameNumber(cls) ?? part.Number;
                }

                if (types.TryGetValue((enumMissileComponentType)part.Number, out var taken) && taken != cls)
                {
                    part.Status = $"number {part.Number} is already used by {taken.Name}";
                    log.Error($"Missile part '{part.Id}': {part.Status}.");
                    return;
                }

                part.BehavesAs = behavesAs;
                lock (_gate) _behaviour = new Dictionary<int, int>(_behaviour) { [part.Number] = behavesAs };

                // The designer list holds one sample of each part, keyed by name.
                var sample = (MissileComponent)Activator.CreateInstance(cls, MissileSize.M, new UIParameterBag(0u));
                sample.ComponentTypeInitValue = (enumMissileComponentType)part.Number;
                if (!categories.TryGetValue(sample.Category, out var list))
                {
                    categories[sample.Category] = list = new SortedList<string, MissileComponent>();
                }
                if (list.ContainsKey(sample.Name))
                {
                    part.Status = $"another part in {sample.Category} is already called '{sample.Name}'";
                    log.Error($"Missile part '{part.Id}': {part.Status}. Give it a unique name.");
                    lock (_gate)
                    {
                        var copy = new Dictionary<int, int>(_behaviour);
                        copy.Remove(part.Number);
                        _behaviour = copy;
                    }
                    return;
                }

                types[(enumMissileComponentType)part.Number] = cls;
                list.Add(sample.Name, sample);
                part.IsActive = true;
                part.Status = $"active: '{sample.Name}' in {sample.Category}, behaves as #{behavesAs} ({GameName(types, behavesAs)})";
                log.Verbose(() => $"Missile part '{part.Id}' (#{part.Number}) {part.Status}.");
            }
            catch (Exception e)
            {
                part.Status = "could not be added: " + e.Message;
                log.Error($"Missile part '{part.Id}' could not be added.", e);
            }
        }

        private static string GameName(Dictionary<enumMissileComponentType, Type> types, int number)
            => types.TryGetValue((enumMissileComponentType)number, out var t) ? t.Name : "its own class";

        private static bool TryFindGamePart(Dictionary<enumMissileComponentType, Type> types, string key, out int number, out Type cls)
        {
            number = 0;
            cls = null;
            key = key?.Trim();
            if (string.IsNullOrEmpty(key)) return false;
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                && n > 0 && n < ContentIds.FirstModdedId && types.TryGetValue((enumMissileComponentType)n, out cls))
            {
                number = n;
                return true;
            }
            foreach (var kv in types.OrderBy(k => (int)k.Key))
            {
                int k = (int)kv.Key;
                if (k <= 0 || k >= ContentIds.FirstModdedId || kv.Value == null) continue;
                var name = kv.Value.Name;
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Missile" + key, StringComparison.OrdinalIgnoreCase))
                {
                    number = k;
                    cls = kv.Value;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The number of the closest game class a code part derives from (its "ComponentType" constant).</summary>
        private static int? NearestGameNumber(Type type)
        {
            for (var t = type.BaseType; t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField("ComponentType", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (f == null) continue;
                var value = (int)(enumMissileComponentType)f.GetValue(null);
                if (value != 0) return value;
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------
        // Data parts: their names and numbers
        // ------------------------------------------------------------------------------------------

        /// <summary>The data definition behind a component, or null for game parts and code parts.</summary>
        private static MissilePartDefinition DefinitionOf(MissileComponent c)
        {
            if (c == null) return null;
            int n = (int)c.componentType;
            if (n < ContentIds.FirstModdedId) return null;
            return _byNumber.TryGetValue(n, out var p) && p.IsData ? p.Definition : null;
        }

        private static void Name_Postfix(MissileComponent __instance, ref string __result)
        {
            var d = DefinitionOf(__instance);
            if (d != null && !string.IsNullOrEmpty(d.Name)) __result = d.Name;
        }

        private static void Description_Postfix(MissileComponent __instance, ref string __result)
        {
            var d = DefinitionOf(__instance);
            if (d != null && !string.IsNullOrEmpty(d.Description)) __result = d.Description;
        }

        private static void HealthModifier_Postfix(MissileComponent __instance, ref float __result)
        {
            var v = DefinitionOf(__instance)?.HealthModifier;
            if (v.HasValue) __result = v.Value;
        }

        private static void LaunchSpeed_Postfix(MissileComponent __instance, ref float __result)
        {
            var v = DefinitionOf(__instance)?.LaunchSpeed;
            if (v.HasValue) __result = v.Value;
        }

        private static void AmmoCostModifier_Postfix(MissileComponent __instance, ref float __result)
        {
            var v = DefinitionOf(__instance)?.AmmoCostModifier;
            if (v.HasValue) __result = v.Value;
        }

        private static void MassModifier_Postfix(MissileComponent __instance, ref float __result)
        {
            var v = DefinitionOf(__instance)?.MassModifier;
            if (v.HasValue) __result = v.Value;
        }

        private static void DragModifier_Postfix(MissileComponent __instance, ref float __result)
        {
            var v = DefinitionOf(__instance)?.DragModifier;
            if (v.HasValue) __result = v.Value;
        }

        private static void AngularDragModifier_Postfix(MissileComponent __instance, ref float __result)
        {
            var v = DefinitionOf(__instance)?.AngularDragModifier;
            if (v.HasValue) __result = v.Value;
        }

        // Postfix on MissileBlueprint.GetLoadVolume: modded parts can count as more or less than one part of payload.
        private static void GetLoadVolume_Postfix(MissileBlueprint __instance, MissileLoad __0, ref float __result)
        {
            if (_byNumber.Count == 0 || __0 == null) return;
            var components = __instance.Components;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                var volume = DefinitionOf(c)?.PayloadVolume;
                if (volume.HasValue && c.LoadType == __0) __result += volume.Value - 1f;
            }
        }

        // Prefix on MissileBlueprintPersistence.UnserializeComponent: a part from a mod that is no longer installed
        // becomes a plain body, instead of the game failing to load the whole missile design.
        private static bool UnserializeComponent_Prefix(JToken __0, MissileSize __1, ref MissileComponent __result)
        {
            int number;
            try
            {
                number = __0[0].ToObject<int>();
            }
            catch
            {
                return true;
            }
            if (number < ContentIds.FirstModdedId) return true;
            if (_byNumber.TryGetValue(number, out var known) && known.IsActive) return true;

            FtdApi.Log.WarningOnce("missing-missile-part-" + number, $"A saved missile uses modded part #{number}, which is not installed. It was replaced with a plain body.");
            __result = R_Missiles.GetNewMissileComponent((enumMissileComponentType)1, __1, new UIParameterBag(0u));
            return false;
        }

        // ------------------------------------------------------------------------------------------
        // Patches
        // ------------------------------------------------------------------------------------------

        private static readonly string[] OverridableProperties =
        {
            "Name", "Description", "HealthModifier", "LaunchSpeed", "AmmoCostModifier", "MassModifier", "DragModifier", "AngularDragModifier"
        };

        /// <summary>Every getter of a property, declared anywhere in the game's missile part classes.</summary>
        private static IEnumerable<MethodBase> GameGetters(string property)
        {
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var asm = typeof(MissileComponent).Assembly;
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }
            foreach (var t in types)
            {
                if (!typeof(MissileComponent).IsAssignableFrom(t)) continue;
                var getter = t.GetProperty(property, declared)?.GetGetMethod(true);
                if (getter != null && !getter.IsAbstract) yield return getter;
            }
        }

        /// <summary>Game classes that act on part numbers. Saving, copying and the designer's selection keep the real number.</summary>
        private static IEnumerable<Type> BehaviourTypes() => new[]
        {
            typeof(MissileBlueprint), typeof(MissileWarheads), typeof(MissileExplosionHandler), typeof(MissileImpactAndTriggering),
            typeof(MissileMovementControl), typeof(MissileMovementInfo), typeof(MissileDetection), typeof(Missile),
            typeof(MissileMirvHolder), typeof(MissileMeshBuilder), typeof(LaunchpadAbstract), typeof(MainConstructInteraction),
            typeof(StatPanel)
        };

        private static readonly HashSet<string> KeepRealNumber = new HashSet<string> { "MissileBlueprint.Copy", "MissileBlueprint.CopyFrom" };

        private static int _rewrites;

        internal static IEnumerable<PatchSpec> Specs()
        {
            var self = typeof(MissileParts);

            foreach (var property in OverridableProperties)
            {
                var name = property;
                yield return new PatchSpec
                {
                    Id = $"Missile parts .{name}: values of modded parts",
                    FeatureId = Features.MissilePartRegistry,
                    Targets = () => GameGetters(name).ToList(),
                    Postfix = PatchSpec.Hook(self, name + "_Postfix")
                };
            }

            yield return new PatchSpec
            {
                Id = "MissileBlueprint.GetLoadVolume: payload of modded parts",
                FeatureId = Features.MissilePartRegistry,
                Target = () => PatchSpec.Method(typeof(MissileBlueprint), nameof(MissileBlueprint.GetLoadVolume), typeof(MissileLoad)),
                Postfix = PatchSpec.Hook(self, nameof(GetLoadVolume_Postfix))
            };

            yield return new PatchSpec
            {
                Id = "MissileBlueprintPersistence.UnserializeComponent: missing modded parts load as a body",
                FeatureId = Features.MissilePartRegistry,
                Required = false,
                Target = () => AccessTools.Method(typeof(MissileBlueprintPersistence), "UnserializeComponent"),
                Prefix = PatchSpec.Hook(self, nameof(UnserializeComponent_Prefix))
            };

            yield return new PatchSpec
            {
                Id = "Missile behaviour classes: modded parts behave as their BehavesAs part",
                FeatureId = Features.MissilePartBehaviour,
                Targets = () =>
                {
                    _rewrites = 0;
                    return BehaviourTypes().SelectMany(MethodsReadingPartNumber).ToList();
                },
                Transpiler = PatchSpec.Hook(self, nameof(Behaviour_Transpiler)),
                Verify = () => _rewrites > 0 ? null : "no part-number checks found to redirect"
            };
        }

        private static IEnumerable<MethodBase> MethodsReadingPartNumber(Type type)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var types = new List<Type> { type };
            for (int i = 0; i < types.Count; i++) types.AddRange(types[i].GetNestedTypes(all));

            foreach (var t in types)
            {
                if (t.IsGenericTypeDefinition) continue;
                foreach (var m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
                    if (KeepRealNumber.Contains(type.Name + "." + m.Name) && t == type) continue;
                    if (ReadsPartNumber(m)) yield return m;
                }
            }
        }

        private static FieldInfo _numberField;

        private static FieldInfo NumberField => _numberField ?? (_numberField = AccessTools.Field(typeof(MissileComponent), nameof(MissileComponent.componentType)));

        /// <summary>Raw IL scan for ldfld MissileComponent.componentType (see ShellParts.ReadsPartType).</summary>
        private static bool ReadsPartNumber(MethodBase m)
        {
            try
            {
                var field = NumberField;
                if (m.Module != field.Module) return true;
                var il = m.GetMethodBody()?.GetILAsByteArray();
                if (il == null) return false;
                int token = field.MetadataToken;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x7B) continue; // ldfld
                    int t = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);
                    if (t == token) return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Replaces "component.componentType" with <see cref="EffectiveTypeOf"/>; the stack is unchanged.</summary>
        private static IEnumerable<CodeInstruction> Behaviour_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var effective = AccessTools.Method(typeof(MissileParts), nameof(EffectiveTypeOf));
            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldfld && Equals(ins.operand, NumberField))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = effective;
                    _rewrites++;
                }
                yield return ins;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Report
        // ------------------------------------------------------------------------------------------

        internal static void AppendReport(StringBuilder sb)
        {
            sb.AppendLine("MISSILE PARTS  (number  class  |  designer name  |  category; use the class name or number as BehavesAs)");
            try
            {
                var categories = GameCategories();
                var types = GameTypes();
                var samples = categories.Values.SelectMany(l => l.Values).ToDictionary(c => (int)c.componentType, c => c);
                foreach (var kv in types.OrderBy(k => (int)k.Key))
                {
                    int n = (int)kv.Key;
                    samples.TryGetValue(n, out var sample);
                    var modded = n >= ContentIds.FirstModdedId && _byNumber.TryGetValue(n, out var p) ? $"   [modded by {p.ModId}: {p.Id}]" : string.Empty;
                    sb.AppendLine($"  {n,-10} {kv.Value?.Name,-32} |  {sample?.Name ?? "(not in designer)"}  |  {sample?.Category.ToString() ?? "-"}  health {sample?.HealthModifier.ToString("0.##", CultureInfo.InvariantCulture) ?? "-"}{modded}");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("  Could not read missile parts: " + e.Message);
            }
            foreach (var p in All.Where(p => !p.IsActive))
            {
                sb.AppendLine($"  NOT ADDED  {p.Id} [{p.ModId}]: {p.Status}");
            }
            sb.AppendLine();
        }
    }
}
