using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BrilliantSkies.Blocks.AdvCannonComponents.Gui.AmmoCustomising;
using BrilliantSkies.Ftd.Game.Pools;
using FtdModApi.Patching;
using HarmonyLib;
using UnityEngine;

namespace FtdModApi
{
    /// <summary>
    /// A new APS shell part. Start from an existing game part (<see cref="BehavesAs"/>) and override
    /// the numbers you want to change.
    /// </summary>
    public sealed class ShellPartDefinition
    {
        /// <summary>
        /// Permanent id, e.g. "MyMod.CorditeCasing". It is turned into the number saved in blueprints,
        /// so never change it after release. Renaming the part is fine; renaming the id breaks saved shells.
        /// </summary>
        public string Id { get; set; }

        /// <summary>Name shown in the ammo customiser.</summary>
        public string Name { get; set; }

        /// <summary>Description shown in the ammo customiser tooltip.</summary>
        public string Description { get; set; }

        /// <summary>
        /// The game part whose behaviour this part copies (its game name, e.g. "PropellantCasing", "HeWarhead",
        /// "CompositeHead", "TimedFuse"; see FtdModApi_content.txt). Wherever the game asks "is this an HE body?",
        /// "is this a casing?" and so on, this part answers as that part. Placement, category, mesh and any part
        /// settings are also copied from it unless you set them below.
        /// </summary>
        public string BehavesAs { get; set; }

        /// <summary>Optional: use another game part's 3D model instead of <see cref="BehavesAs"/>'s.</summary>
        public string LooksLike { get; set; }

        // Stats. Leave null to copy the BehavesAs part's value. These are the same numbers the game uses for
        // its own parts: each body part contributes in proportion to its length, the head multiplies the result.

        /// <summary>Relative kinetic damage (game parts use roughly 0.1–1.6).</summary>
        public float? KineticDamage { get; set; }

        /// <summary>Relative armour piercing.</summary>
        public float? ArmourPiercing { get; set; }

        /// <summary>Relative speed.</summary>
        public float? Speed { get; set; }

        /// <summary>Relative shell health (resistance to lasers and anti-munition defences).</summary>
        public float? Health { get; set; }

        /// <summary>Relative inaccuracy (lower is more accurate).</summary>
        public float? Inaccuracy { get; set; }

        /// <summary>Relative ammo cost.</summary>
        public float? AmmoCost { get; set; }

        /// <summary>Maximum length in calibres (game parts are 1, or shorter for fuses).</summary>
        public float? MaximumLength { get; set; }

        /// <summary>How visible the shell is to enemy warning systems (1 = normal).</summary>
        public float? Detectability { get; set; }

        /// <summary>Where the part may go. Raw game type: may change between game versions.</summary>
        public ShellPlacement? Placement { get; set; }

        /// <summary>Category it is listed under. Raw game type: may change between game versions.</summary>
        public ShellCategory? Category { get; set; }

        /// <summary>Order within its category in the customiser list (-100 to 100).</summary>
        public int? SortingIndex { get; set; }

        /// <summary>
        /// Optional: a payload of your own that goes off when the shell detonates (see <see cref="ApsPayload"/>).
        /// Feature: <see cref="Features.ApsPayloads"/>.
        /// </summary>
        public ApsPayload Payload { get; set; }

        /// <summary>
        /// Only with <see cref="Payload"/>. False (default): the part carries none of its BehavesAs part's payload
        /// (no HE, frag, EMP, fire, smoke, flak, HEAT or HESH from it), so it only does what your payload does.
        /// True: it keeps that payload too and yours goes off as well.
        /// </summary>
        public bool KeepGamePayload { get; set; }
    }

    /// <summary>A registered modded shell part.</summary>
    public sealed class ShellPartInfo
    {
        internal ShellPartInfo(string modId, ShellPartDefinition def, ShellModuleType type, ShellModuleType behavesAs, ShellModuleType looksLike)
        {
            ModId = modId;
            Definition = def;
            Type = type;
            BehavesAs = behavesAs;
            LooksLike = looksLike;
            Payload = def.Payload;
            KeepGamePayload = def.KeepGamePayload;
        }

        public string ModId { get; }

        public string Id => Definition.Id;

        /// <summary>The number saved in blueprints for this part.</summary>
        public int Number => (int)Type;

        /// <summary>Raw game enum value (outside the game's own range).</summary>
        public ShellModuleType Type { get; }

        public ShellModuleType BehavesAs { get; }

        public ShellModuleType LooksLike { get; }

        /// <summary>The part's own payload, or null (as registered; later changes to the definition are ignored).</summary>
        public ApsPayload Payload { get; }

        /// <summary>Whether the part also keeps its BehavesAs part's game payload.</summary>
        public bool KeepGamePayload { get; }

        internal ShellPartDefinition Definition { get; }
    }

    /// <summary>
    /// Add new APS shell parts. Feature ids: <see cref="Features.ShellPartRegistry"/> (the part exists and
    /// appears in the customiser) and <see cref="Features.ShellPartBehaviour"/> (it acts as its BehavesAs part
    /// in shell stats, fusing, projectile flight and the part settings UI).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shells are saved with a number per part. Modded parts get a number derived from their <c>Id</c>
    /// (<see cref="ContentIds"/>), the same on every machine and every launch, so saved shells keep their
    /// modded parts. If the mod is removed, those parts are dropped from the shell when it loads
    /// (the game skips parts it does not know); the save itself is not damaged until it is re-saved.
    /// </para>
    /// <para>
    /// Register parts in your plugin's OnLoad. In multiplayer every player needs the same parts.
    /// </para>
    /// </remarks>
    public static class ShellParts
    {
        internal const string IdCategory = "ApsShellPart";

        private static readonly object _gate = new object();
        private static readonly List<ShellPartInfo> _parts = new List<ShellPartInfo>();
        private static volatile Dictionary<ShellModuleType, ShellModuleType> _behaviour = new Dictionary<ShellModuleType, ShellModuleType>();

        // Parts with their own payload, and those of them that carry no game payload.
        private static volatile ShellPartInfo[] _payloadParts = new ShellPartInfo[0];
        private static volatile HashSet<ShellModuleType> _noGamePayload = new HashSet<ShellModuleType>();

        /// <summary>The type game payload code sees for a part that carries no game payload: a solid body (no payload).</summary>
        private const ShellModuleType NoPayloadType = ShellModuleType.SolidWarhead;

        internal static ShellPartInfo[] PayloadParts => _payloadParts;

        /// <summary>
        /// Registers a new shell part and returns it, or null if the definition is invalid (the reason is logged).
        /// </summary>
        public static ShellPartInfo Register(string modId, ShellPartDefinition definition)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var log = FtdApi.GetLog(modId);

            if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Name))
            {
                log.Error("ShellParts.Register: Id and Name are required.");
                return null;
            }
            if (!TryParseGamePart(definition.BehavesAs, out var behavesAs))
            {
                log.Error($"ShellParts.Register('{definition.Id}'): BehavesAs '{definition.BehavesAs}' is not a game shell part. Valid names are in FtdModApi_content.txt.");
                return null;
            }
            var looksLike = behavesAs;
            if (!string.IsNullOrEmpty(definition.LooksLike) && !TryParseGamePart(definition.LooksLike, out looksLike))
            {
                log.Warning($"ShellParts.Register('{definition.Id}'): LooksLike '{definition.LooksLike}' is not a game shell part; using {behavesAs}'s model.");
                looksLike = behavesAs;
            }

            int number;
            try
            {
                number = ContentIds.Register(IdCategory, definition.Id);
            }
            catch (InvalidOperationException e)
            {
                log.Error("ShellParts.Register: " + e.Message);
                return null;
            }

            var info = new ShellPartInfo(modId, definition, (ShellModuleType)number, behavesAs, looksLike);
            lock (_gate)
            {
                if (_parts.Any(p => p.Number == number))
                {
                    log.Warning($"Shell part '{definition.Id}' is already registered.");
                    return _parts.First(p => p.Number == number);
                }
                _parts.Add(info);
                _behaviour = _parts.ToDictionary(p => p.Type, p => p.BehavesAs);
                _payloadParts = _parts.Where(p => p.Payload != null).ToArray();
                _noGamePayload = new HashSet<ShellModuleType>(_parts.Where(p => p.Payload != null && !p.KeepGamePayload).Select(p => p.Type));
            }

            // If the game has already built its part list (registration after OnLoad), add the part now.
            var existing = ExistingDictionary();
            if (existing != null) AddToDictionary(existing, info);

            log.Info($"Registered shell part '{definition.Name}' ({definition.Id}, number {number}, behaves as {behavesAs}"
                     + (info.Payload == null ? ")." : $", own payload {info.Payload.GetType().Name}{(info.KeepGamePayload ? " plus the game payload" : "")})."));
            return info;
        }

        /// <summary>All registered modded parts.</summary>
        public static IReadOnlyList<ShellPartInfo> All
        {
            get { lock (_gate) return _parts.ToList(); }
        }

        /// <summary>The modded part with this id, or null.</summary>
        public static ShellPartInfo Find(string id)
        {
            lock (_gate) return _parts.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        }

        /// <summary>The modded part with this saved number, or null.</summary>
        public static ShellPartInfo Find(ShellModuleType type)
        {
            lock (_gate) return _parts.FirstOrDefault(p => p.Type == type);
        }

        /// <summary>
        /// The game part a part behaves as: itself for game parts, its BehavesAs for modded ones. Useful when
        /// your own patches check part types.
        /// </summary>
        public static ShellModuleType EffectiveType(ShellModuleType type)
        {
            if ((int)type < ContentIds.FirstModdedId) return type;
            return _behaviour.TryGetValue(type, out var b) ? b : type;
        }

        internal static bool TryParseGamePart(string name, out ShellModuleType type)
        {
            type = default;
            return !string.IsNullOrEmpty(name)
                   && Enum.TryParse(name, ignoreCase: true, out type)
                   && Enum.IsDefined(typeof(ShellModuleType), type);
        }

        /// <summary>Display key for reports: the modded id, or the game name.</summary>
        internal static string Describe(ShellModuleType type) => Find(type)?.Id ?? type.ToString();

        // ------------------------------------------------------------------------------------------
        // Adding parts to the game's part list
        // ------------------------------------------------------------------------------------------

        private static ShellModuleDictionary ExistingDictionary()
        {
            try
            {
                return AccessTools.Field(typeof(ShellModuleDictionary), "_instance")?.GetValue(null) as ShellModuleDictionary;
            }
            catch
            {
                return null;
            }
        }

        private static void GenerateDictionary_Postfix(ShellModuleDictionary __instance)
        {
            List<ShellPartInfo> parts;
            lock (_gate) parts = _parts.ToList();
            foreach (var part in parts) AddToDictionary(__instance, part);
        }

        private static void AddToDictionary(ShellModuleDictionary dictionary, ShellPartInfo part)
        {
            try
            {
                var modules = dictionary.ModuleDictionary;
                if (!modules.TryGetValue(part.BehavesAs, out var behaviour) || behaviour == null)
                {
                    FtdApi.GetLog(part.ModId).Error($"Shell part '{part.Id}': the game has no '{part.BehavesAs}' part to copy.");
                    return;
                }
                if (!modules.TryGetValue(part.LooksLike, out var look) || look == null) look = behaviour;

                var d = part.Definition;
                var module = new ShellModule(
                    d.Name,
                    d.Description ?? string.Empty,
                    look.GameObjectLoader,
                    d.Placement ?? behaviour.Placement,
                    d.Category ?? behaviour.Category,
                    d.SortingIndex ?? behaviour.SortingIndex,
                    d.KineticDamage ?? behaviour.RelativeKineticDamage,
                    d.ArmourPiercing ?? behaviour.RelativeArmourPiercing,
                    d.Speed ?? behaviour.RelativeSpeedModifier,
                    d.Health ?? behaviour.HealthModifier)
                {
                    RelativeInaccuracyModifier = d.Inaccuracy ?? behaviour.RelativeInaccuracyModifier,
                    RelativeAmmoCost = d.AmmoCost ?? behaviour.RelativeAmmoCost,
                    MaximumLength = d.MaximumLength ?? behaviour.MaximumLength,
                    DetectabilityFactor = d.Detectability ?? behaviour.DetectabilityFactor
                };

                module.Type = part.Type;
                var defaults = behaviour.DefaultParameters;
                module.DefaultParameters = new ShellModuleParams
                {
                    Type = part.Type,
                    P1 = defaults?.P1 ?? 0f,
                    P2 = defaults?.P2 ?? 0f,
                    P3 = defaults?.P3 ?? 0f
                };

                modules[part.Type] = module;
                FtdApi.GetLog(part.ModId).Verbose(() => $"Shell part '{part.Id}' (#{part.Number}) added to the customiser list as a copy of {part.BehavesAs} (looks like {part.LooksLike}).");
            }
            catch (Exception e)
            {
                FtdApi.GetLog(part.ModId).Error($"Shell part '{part.Id}' could not be added.", e);
            }
        }

        // ------------------------------------------------------------------------------------------
        // Patches
        // ------------------------------------------------------------------------------------------

        /// <summary>Game classes that decide what a shell does from its parts' types. Every method in them is checked.</summary>
        private static readonly string[] BehaviourClasses =
        {
            "ShellModel_ExplosiveCharges",
            "ShellModel_Propellant",
            "ShellModel_Fusing",
            "ShellModel_Accuracy",
            "ShellModel_AmmoCost",
            "ShellModel_EffectiveRange",
            "ShellModel_KineticDamage",
            "ShellModelParameters"
        };

        private static int _rewrites;

        internal static IEnumerable<PatchSpec> Specs()
        {
            var self = typeof(ShellParts);

            yield return new PatchSpec
            {
                Id = "ShellModuleDictionary.GenerateDictionary: add modded shell parts",
                FeatureId = Features.ShellPartRegistry,
                Target = () => AccessTools.Method(typeof(ShellModuleDictionary), "GenerateDictionary"),
                Postfix = PatchSpec.Hook(self, nameof(GenerateDictionary_Postfix))
            };

            yield return new PatchSpec
            {
                Id = "Shell stat classes: modded parts behave as their BehavesAs part",
                FeatureId = Features.ShellPartBehaviour,
                Targets = () =>
                {
                    _rewrites = 0;
                    return BehaviourClasses.SelectMany(name =>
                    {
                        var type = AccessTools.TypeByName(name);
                        if (type == null) throw new TypeLoadException("game class " + name + " not found");
                        return MethodsReadingPartType(type);
                    }).ToList();
                },
                Transpiler = PatchSpec.Hook(self, nameof(Behaviour_Transpiler)),
                Verify = () => _rewrites > 0 ? null : "no part-type checks found to redirect"
            };

            yield return new PatchSpec
            {
                Id = "Projectile flight, firing, ejection and part settings UI: modded parts behave as their BehavesAs part",
                FeatureId = Features.ShellPartBehaviour,
                Required = false,
                Targets = () =>
                {
                    _rewrites = 0;
                    return new MethodBase[]
                    {
                        AccessTools.Method(typeof(AdvPooledProjectile), nameof(AdvPooledProjectile.ActivateHere), new[] { typeof(Vector3), typeof(Vector3), typeof(ShellModel) }),
                        AccessTools.Method(typeof(AdvCannonFiringPiece), "MainThreadFireCallback"),
                        AccessTools.Method(typeof(ShellConstants), nameof(ShellConstants.EjectShellList)),
                        AccessTools.Method(typeof(PickerTab), "ShowCustomisationOfParts")
                    }.Where(m => m != null).ToList();
                },
                Transpiler = PatchSpec.Hook(self, nameof(Behaviour_Transpiler)),
                Verify = () => _rewrites >= 4 ? null : $"only {_rewrites} part-type checks found (expected at least 4)"
            };
        }

        private static IEnumerable<MethodBase> MethodsReadingPartType(Type type)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var types = new List<Type> { type };
            // Lambdas (e.g. Parameters.FirstOrDefault(t => t.Type == ...)) live in compiler-generated nested classes.
            for (int i = 0; i < types.Count; i++) types.AddRange(types[i].GetNestedTypes(all));

            foreach (var t in types)
            {
                if (t.IsGenericTypeDefinition) continue;
                foreach (var m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
                    if (ReadsPartType(m)) yield return m;
                }
            }
        }

        /// <summary>
        /// Cheap pre-check so only methods that actually read a part type get patched. Scans the raw IL for
        /// ldfld/call/callvirt followed by the metadata token of ShellModule.Type or ShellModuleParams.get_Type.
        /// Tokens are only comparable within one module; all these game classes live in Ftd.dll, so the check
        /// is skipped (method kept) for anything else. A rare false positive just means a no-op patch.
        /// </summary>
        private static bool ReadsPartType(MethodBase m)
        {
            try
            {
                var field = AccessTools.Field(typeof(ShellModule), nameof(ShellModule.Type));
                var getter = AccessTools.PropertyGetter(typeof(ShellModuleParams), nameof(ShellModuleParams.Type));
                if (m.Module != field.Module || m.Module != getter.Module) return true;

                var il = m.GetMethodBody()?.GetILAsByteArray();
                if (il == null) return false;
                int fieldToken = field.MetadataToken;
                int getterToken = getter.MetadataToken;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    byte op = il[i];
                    if (op != 0x7B && op != 0x28 && op != 0x6F) continue; // ldfld, call, callvirt
                    int token = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);
                    if ((op == 0x7B && token == fieldToken) || (op != 0x7B && token == getterToken)) return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static FieldInfo _moduleTypeField;
        private static MethodInfo _paramsTypeGetter;

        private static bool IsPartTypeRead(CodeInstruction ins)
        {
            _moduleTypeField = _moduleTypeField ?? AccessTools.Field(typeof(ShellModule), nameof(ShellModule.Type));
            _paramsTypeGetter = _paramsTypeGetter ?? AccessTools.PropertyGetter(typeof(ShellModuleParams), nameof(ShellModuleParams.Type));
            if (ins.opcode == OpCodes.Ldfld && Equals(ins.operand, _moduleTypeField)) return true;
            if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && Equals(ins.operand, _paramsTypeGetter)) return true;
            return false;
        }

        /// <summary>
        /// Replaces "part.Type" reads with <see cref="EffectiveModuleType"/> / <see cref="EffectiveParamsType"/>.
        /// Both take the object the original instruction consumed and push the same type of value, so the
        /// stack is unchanged. Labels and exception blocks stay on the instruction because it is edited in place.
        /// </summary>
        private static IEnumerable<CodeInstruction> Behaviour_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            // In the game's payload class, parts with their own payload (and no game payload) count as a plain solid body.
            bool payloadCode = IsInside(original?.DeclaringType, "ShellModel_ExplosiveCharges");
            var ofModule = AccessTools.Method(typeof(ShellParts), payloadCode ? nameof(EffectivePayloadModuleType) : nameof(EffectiveModuleType));
            var ofParams = AccessTools.Method(typeof(ShellParts), payloadCode ? nameof(EffectivePayloadParamsType) : nameof(EffectiveParamsType));
            foreach (var ins in instructions)
            {
                if (IsPartTypeRead(ins))
                {
                    bool isField = ins.opcode == OpCodes.Ldfld;
                    ins.opcode = OpCodes.Call;
                    ins.operand = isField ? ofModule : ofParams;
                    _rewrites++;
                }
                yield return ins;
            }
        }

        private static bool IsInside(Type type, string name)
        {
            for (var t = type; t != null; t = t.DeclaringType)
            {
                if (t.Name == name) return true;
            }
            return false;
        }

        /// <summary>As <see cref="EffectiveModuleType"/>, in the game's payload code.</summary>
        public static ShellModuleType EffectivePayloadModuleType(ShellModule module)
            => module == null ? default : EffectivePayloadType(module.Type);

        /// <summary>As <see cref="EffectiveParamsType"/>, in the game's payload code.</summary>
        public static ShellModuleType EffectivePayloadParamsType(ShellModuleParams parameters)
            => parameters == null ? default : EffectivePayloadType(parameters.Type);

        private static ShellModuleType EffectivePayloadType(ShellModuleType type)
        {
            if ((int)type < ContentIds.FirstModdedId) return type;
            return _noGamePayload.Contains(type) ? NoPayloadType : EffectiveType(type);
        }

        /// <summary>Called from patched game code in place of reading <c>ShellModule.Type</c>.</summary>
        public static ShellModuleType EffectiveModuleType(ShellModule module)
            => module == null ? default : EffectiveType(module.Type);

        /// <summary>Called from patched game code in place of reading <c>ShellModuleParams.Type</c>.</summary>
        public static ShellModuleType EffectiveParamsType(ShellModuleParams parameters)
            => parameters == null ? default : EffectiveType(parameters.Type);
    }
}
