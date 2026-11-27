using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BrilliantSkies.Common.Circuits;
using BrilliantSkies.Common.Circuits.Ui.Segments;
using BrilliantSkies.DataManagement.Attributes;
using BrilliantSkies.DataManagement.Vars;
using FtdModApi.Patching;
using HarmonyLib;
using CircuitComponent = BrilliantSkies.Common.Circuits.Component;

namespace FtdModApi
{
    /// <summary>Which breadboards a mod component can be placed on.</summary>
    [Flags]
    public enum BreadboardKinds
    {
        /// <summary>The ordinary breadboard block.</summary>
        Vehicle = 1,

        /// <summary>The AI breadboard (it also offers everything the ordinary breadboard has).</summary>
        Ai = 2,

        /// <summary>The missile breadboard.</summary>
        Missile = 4,

        VehicleAndAi = Vehicle | Ai,
        All = Vehicle | Ai | Missile
    }

    /// <summary>The kind of breadboard a component is on.</summary>
    public enum BreadboardKind
    {
        Unknown,
        Vehicle,
        Ai,
        Missile
    }

    /// <summary>A registered mod breadboard component.</summary>
    public sealed class BreadboardComponentInfo
    {
        internal BreadboardComponentInfo(string modId, string id, string name, string description, Type type, Guid guid, BreadboardKinds kinds)
        {
            ModId = modId;
            Id = id;
            Name = name;
            Description = description ?? string.Empty;
            Type = type;
            Guid = guid;
            Kinds = kinds;
        }

        public string ModId { get; }

        /// <summary>The id given at registration. Saved crafts refer to the component by mod id + this id.</summary>
        public string Id { get; }

        public string Name { get; }

        public string Description { get; }

        public Type Type { get; }

        /// <summary>The id the game saves, made from the mod id and component id.</summary>
        public Guid Guid { get; }

        public BreadboardKinds Kinds { get; }
    }

    /// <summary>
    /// Mod components for players' breadboards. Write a class deriving from <see cref="ModBreadboardComponent"/> and
    /// register it in your plugin's OnLoad; it appears under "New Component Inventory" on the chosen breadboards.
    /// </summary>
    public static class BreadboardComponents
    {
        private static readonly object _gate = new object();
        private static readonly Dictionary<Guid, BreadboardComponentInfo> _byGuid = new Dictionary<Guid, BreadboardComponentInfo>();
        private static readonly Dictionary<Type, BreadboardComponentInfo> _byType = new Dictionary<Type, BreadboardComponentInfo>();
        private static readonly Dictionary<Guid, BoardTypes.ComponentType> _gameEntries = new Dictionary<Guid, BoardTypes.ComponentType>();

        // Ids of mod components start with these bytes, so a component of a missing mod can still be recognised.
        private static readonly byte[] Marker = { 0x46, 0x74, 0x64, 0x4D }; // "FtdM"

        private static AccessTools.FieldRef<CircuitComponentEditorSegment, CircuitComponent> _editorSelected;
        private static AccessTools.FieldRef<CircuitComponentStandardEditorSegment, CircuitComponent> _standardSelected;

        /// <summary>Every registered component.</summary>
        public static IReadOnlyList<BreadboardComponentInfo> All
        {
            get
            {
                lock (_gate) return _byGuid.Values.ToList();
            }
        }

        /// <summary>
        /// Adds a component to players' breadboards. Call on the main thread in your plugin's OnLoad (the AI breadboard
        /// takes its list when the block is placed, so later registrations only reach AI breadboards placed after).
        /// The mod id and id are what saved crafts refer to: never change them, or players' components become
        /// "missing". The name and description can change.
        /// </summary>
        public static BreadboardComponentInfo Register<T>(string modId, string id, string name, string description = null, BreadboardKinds kinds = BreadboardKinds.VehicleAndAi)
            where T : ModBreadboardComponent, new()
        {
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A component id is required.", nameof(id));
            if ((kinds & BreadboardKinds.All) == 0) throw new ArgumentException("Choose at least one kind of breadboard.", nameof(kinds));
            var type = typeof(T);
            if (type == typeof(MissingBreadboardComponent)) throw new ArgumentException("MissingBreadboardComponent can't be registered.");
            CheckType(type);
            try
            {
                // The game's own check of the saved properties (unique save indexes...), done now instead of on load.
                BrilliantSkies.DataManagement.Saving.DataPackageSaveAndLoad.Gather(type);
            }
            catch (Exception e)
            {
                throw new ArgumentException($"{type.Name} can't be saved by the game: {e.Message} (don't use save index {ModBreadboardComponent.ModDataIndex}, 900-903 or 8000-8003).", e);
            }

            var guid = IdFor(modId, id);
            var info = new BreadboardComponentInfo(modId, id, string.IsNullOrWhiteSpace(name) ? id : name, description, type, guid, kinds & BreadboardKinds.All);
            lock (_gate)
            {
                if (_byType.TryGetValue(type, out var sameType) && sameType.Guid != guid)
                    throw new ArgumentException($"{type.Name} is already registered as {sameType.ModId}/{sameType.Id}; each class can be registered once.");
                if (_byGuid.TryGetValue(guid, out var old))
                {
                    _byType.Remove(old.Type);
                    RemoveFromGame(guid);
                }
                _byGuid[guid] = info;
                _byType[type] = info;
                AddToGame(info);
            }
            FtdApi.Log.Verbose(() => $"Breadboard component {modId}/{id} ('{info.Name}', {info.Kinds}) registered.");
            return info;
        }

        /// <summary>The id the game saves for a mod component.</summary>
        public static Guid IdFor(string modId, string id)
        {
            byte[] hash;
            using (var sha = SHA1.Create())
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes("FtdModApi.Breadboard/" + modId + "/" + id));
            var bytes = new byte[16];
            Array.Copy(hash, bytes, 16);
            Array.Copy(Marker, bytes, Marker.Length);
            return new Guid(bytes);
        }

        /// <summary>True when the id belongs to a mod component (registered or not).</summary>
        public static bool IsModComponentId(Guid id)
        {
            var b = id.ToByteArray();
            for (int i = 0; i < Marker.Length; i++)
                if (b[i] != Marker[i]) return false;
            return true;
        }

        internal static BreadboardComponentInfo InfoFor(Type type)
        {
            lock (_gate) return _byType.TryGetValue(type, out var info) ? info : null;
        }

        // The game refuses public Var properties without a save index, and only ModData survives a missing mod.
        private static void CheckType(Type type)
        {
            if (type.IsAbstract) throw new ArgumentException(type.Name + " is abstract.");
            foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (p.DeclaringType == typeof(ModBreadboardComponent) || !typeof(Var).IsAssignableFrom(p.PropertyType)) continue;
                if (!p.DeclaringType.IsSubclassOf(typeof(ModBreadboardComponent))) continue;
                if (p.GetCustomAttribute<IgnoreVariable>() != null) continue;
                if (p.GetCustomAttribute<VariableAttribute>() == null)
                    throw new ArgumentException($"{type.Name}.{p.Name}: the game can't save a public Var property without a [Variable] index; keep player settings in AddNumberSetting/AddToggleSetting/AddTextSetting instead.");
                FtdApi.Log.Warning($"{type.Name}.{p.Name}: a game Var property is saved, but lost if a player loads the craft without your mod; prefer AddNumberSetting/AddToggleSetting/AddTextSetting.");
            }
        }

        private static void AddToGame(BreadboardComponentInfo info)
        {
            var entry = new BoardTypes.ComponentType(info.Type, info.Guid)
            {
                BoardAttribute = new ModComponentName(info.Name, Describe(info))
            };
            _gameEntries[info.Guid] = entry;
            // The AI breadboard copies the ordinary list as well as its own.
            if ((info.Kinds & BreadboardKinds.Vehicle) != 0) BoardTypes.FtdBreadboard.Add(entry);
            else if ((info.Kinds & BreadboardKinds.Ai) != 0) BoardTypes.FtdAiBreadboard.Add(entry);
            if ((info.Kinds & BreadboardKinds.Missile) != 0) BoardTypes.MissileBreadboard.Add(entry);
        }

        private static string Describe(BreadboardComponentInfo info)
            => (string.IsNullOrEmpty(info.Description) ? "" : info.Description + "\n\n") + "Added by the mod '" + info.ModId + "'";

        private static void RemoveFromGame(Guid guid)
        {
            if (!_gameEntries.TryGetValue(guid, out var entry)) return;
            BoardTypes.FtdBreadboard.Remove(entry);
            BoardTypes.FtdAiBreadboard.Remove(entry);
            BoardTypes.MissileBreadboard.Remove(entry);
            _gameEntries.Remove(guid);
        }

        internal static void ReportError(ModBreadboardComponent component, Exception e)
        {
            var info = component.Info;
            var who = info != null ? info.ModId + "/" + info.Id : component.GetType().FullName;
            FtdApi.Log.ErrorOnce("bb-run-" + who, $"Breadboard component {who} threw while running (reported once; it keeps running).", e);
        }

        // ---- game hooks (always on, so components of missing mods are kept) ----------------------------

        internal static void Initialise()
        {
            PatchManager.Apply(Specs());
        }

        internal static PatchSpec[] Specs()
        {
            return new[]
            {
                new PatchSpec
                {
                    Id = "Board.MakeSystem: keep mod breadboard components (missing mods too)",
                    FeatureId = Features.BreadboardComponents,
                    Target = () => EventHelpers.Declared(typeof(Board), "MakeSystem"),
                    Prefix = PatchSpec.Hook(typeof(BreadboardComponents), nameof(MakeSystem_Prefix))
                },
                new PatchSpec
                {
                    Id = "Board.OnPackageAddedToSystem: give mod components their board",
                    FeatureId = Features.BreadboardComponents,
                    Target = () => EventHelpers.Declared(typeof(Board), "OnPackageAddedToSystem"),
                    Postfix = PatchSpec.Hook(typeof(BreadboardComponents), nameof(Added_Postfix))
                },
                new PatchSpec
                {
                    Id = "Board.Load: add pins a newer mod version declares",
                    FeatureId = Features.BreadboardComponents,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(Board), "Load"),
                    Postfix = PatchSpec.Hook(typeof(BreadboardComponents), nameof(Load_Postfix))
                },
                new PatchSpec
                {
                    Id = "CircuitComponentEditorSegment.UiEntry: mod component editor title",
                    FeatureId = Features.BreadboardComponents,
                    Required = false,
                    Target = () =>
                    {
                        _editorSelected = FieldRefs.Get<CircuitComponentEditorSegment, CircuitComponent>("_selectedComponent");
                        return EventHelpers.Declared(typeof(CircuitComponentEditorSegment), "UiEntry");
                    },
                    Postfix = PatchSpec.Hook(typeof(BreadboardComponents), nameof(Editor_Postfix))
                },
                new PatchSpec
                {
                    Id = "CircuitComponentStandardEditorSegment.UiEntry: mod component editor title",
                    FeatureId = Features.BreadboardComponents,
                    Required = false,
                    Target = () =>
                    {
                        _standardSelected = FieldRefs.Get<CircuitComponentStandardEditorSegment, CircuitComponent>("_selectedComponent");
                        return EventHelpers.Declared(typeof(CircuitComponentStandardEditorSegment), "UiEntry");
                    },
                    Postfix = PatchSpec.Hook(typeof(BreadboardComponents), nameof(StandardEditor_Postfix))
                }
            };
        }

        private static readonly HashSet<Guid> _reportedMissing = new HashSet<Guid>();

        private static bool MakeSystem_Prefix(Guid id, ref CircuitComponent __result)
        {
            if (!IsModComponentId(id)) return true;
            // Made here (not by the game) so a constructor that throws can't break loading the craft.
            BreadboardComponentInfo info;
            lock (_gate) _byGuid.TryGetValue(id, out info);
            if (info != null)
            {
                try
                {
                    __result = (CircuitComponent)Activator.CreateInstance(info.Type);
                    return false;
                }
                catch (Exception e)
                {
                    FtdApi.Log.ErrorOnce("bb-make-" + id, $"Breadboard component {info.ModId}/{info.Id} could not be created; it is kept as missing so the craft loses nothing.", e.InnerException ?? e);
                }
            }
            else
            {
                bool first;
                lock (_gate) first = _reportedMissing.Add(id);
                if (first) FtdApi.Log.Warning($"A breadboard has a component from a mod that is not installed (id {id}); it is kept and shown as missing.");
            }
            __result = new MissingBreadboardComponent(id);
            return false;
        }

        private static void Added_Postfix(Board __instance, CircuitComponent system)
        {
            if (system is ModBreadboardComponent mod) mod.Attach(__instance);
        }

        private static void Load_Postfix(Board __instance)
        {
            var packages = __instance?.Packages;
            if (packages == null) return;
            foreach (var p in packages)
                if (p is ModBreadboardComponent mod) mod.EnsurePins();
        }

        private static void Editor_Postfix(CircuitComponentEditorSegment __instance)
        {
            if (_editorSelected == null) return;
            if (_editorSelected(__instance) is ModBreadboardComponent mod)
            {
                var title = mod.DisplayName + " Editor";
                if (__instance.NameWhereApplicable != title) __instance.NameWhereApplicable = title;
            }
        }

        private static void StandardEditor_Postfix(CircuitComponentStandardEditorSegment __instance)
        {
            if (_standardSelected == null) return;
            if (_standardSelected(__instance) is ModBreadboardComponent mod)
            {
                var title = mod.DisplayName + " Common Editor";
                if (__instance.NameWhereApplicable != title) __instance.NameWhereApplicable = title;
            }
        }

        // The name and description shown in the "new component" menu.
        private sealed class ModComponentName : LocalBoardComponentScraped
        {
            private readonly string _name;
            private readonly string _description;

            public ModComponentName(string name, string description)
                : base(name, name, description)
            {
                _name = name;
                _description = description;
            }

            public override string Name => _name;

            public override string Description => _description;
        }
    }
}
