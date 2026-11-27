using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BrilliantSkies.Core.JsonPlus;
using BrilliantSkies.Core.Timing;
using FtdModApi.Patching;
using Newtonsoft.Json;

namespace FtdModApi
{
    /// <summary>
    /// Values a mod keeps on a craft (a MainConstruct: ship, plane, fortress...). They are saved with the craft: in its
    /// blueprint file, in campaign saves, and when a craft leaves play and comes back. Text values (or JSON with
    /// <see cref="GetJson{T}"/> / <see cref="SetJson{T}"/>), per mod and key.
    /// <para>
    /// Values belong to the design as saved: every craft spawned from a saved blueprint file starts with the values it
    /// was saved with (copies and other players' downloads too). Retrofitting a craft to another design takes that
    /// design's values. Values of mods that aren't installed are kept, so a craft saved without your mod still has its
    /// values when the mod comes back.
    /// </para>
    /// <para>
    /// Every machine in a multiplayer game loads the values the craft was spawned with; later changes are not synced
    /// (send them with <see cref="ModNet"/> if clients need them), and a client keeps its own copy when a craft goes
    /// out of play.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// CraftData.Set(craft, "MyMod", "kills", "3");
    /// int kills = int.TryParse(CraftData.Get(craft, "MyMod", "kills"), out var k) ? k : 0;
    /// </code>
    /// </example>
    public static class CraftData
    {
        /// <summary>Every key the API writes into a blueprint starts with this.</summary>
        internal const string Prefix = "FtdModApi.";
        internal const string DataPrefix = Prefix + "data/";
        internal const string ModulePrefix = Prefix + "module/";

        private static readonly ConditionalWeakTable<MainConstruct, CraftState> _states = new ConditionalWeakTable<MainConstruct, CraftState>();

        /// <summary>The value, or null when there is none (or the craft is null).</summary>
        public static string Get(MainConstruct craft, string modId, string key)
        {
            if (craft == null) return null;
            return StateOf(craft).Get(DataKey(modId, key));
        }

        /// <summary>Sets the value; null removes it. Any thread.</summary>
        public static void Set(MainConstruct craft, string modId, string key, string value)
        {
            if (craft == null) throw new ArgumentNullException(nameof(craft));
            StateOf(craft).Set(DataKey(modId, key), value);
        }

        /// <summary>True when the craft has a value for the key.</summary>
        public static bool Has(MainConstruct craft, string modId, string key) => Get(craft, modId, key) != null;

        /// <summary>The value read as JSON, or the fallback when there is none or it can't be read.</summary>
        public static T GetJson<T>(MainConstruct craft, string modId, string key, T fallback = default)
        {
            var text = Get(craft, modId, key);
            if (text == null) return fallback;
            try
            {
                return JsonConvert.DeserializeObject<T>(text);
            }
            catch (Exception e)
            {
                FtdApi.GetLog(modId).WarningOnce("craftdata-json-" + modId + "/" + key, $"CraftData {modId}/{key}: the saved value could not be read as {typeof(T).Name} ({e.Message}); using the fallback.");
                return fallback;
            }
        }

        /// <summary>Stores the value as JSON (null removes it).</summary>
        public static void SetJson<T>(MainConstruct craft, string modId, string key, T value)
            => Set(craft, modId, key, value == null ? null : JsonConvert.SerializeObject(value));

        /// <summary>Removes every value of the mod from the craft.</summary>
        public static void Clear(MainConstruct craft, string modId)
        {
            if (craft == null) return;
            var prefix = DataPrefix + Check(modId, nameof(modId)) + "/";
            StateOf(craft).RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
        }

        /// <summary>The keys the mod has on the craft.</summary>
        public static IReadOnlyList<string> Keys(MainConstruct craft, string modId)
        {
            if (craft == null) return Array.Empty<string>();
            var prefix = DataPrefix + Check(modId, nameof(modId)) + "/";
            return StateOf(craft).KeysStartingWith(prefix).Select(k => k.Substring(prefix.Length)).ToList();
        }

        private static string DataKey(string modId, string key) => DataPrefix + Check(modId, nameof(modId)) + "/" + Check(key, nameof(key));

        internal static string Check(string part, string name)
        {
            if (string.IsNullOrWhiteSpace(part)) throw new ArgumentException("Required.", name);
            if (part.IndexOf('/') >= 0) throw new ArgumentException("Can't contain '/'.", name);
            return part;
        }

        internal static CraftState StateOf(MainConstruct craft) => _states.GetValue(craft, c => new CraftState(c));

        internal static bool TryState(MainConstruct craft, out CraftState state) => _states.TryGetValue(craft, out state);

        // ---- game hooks (always on: they also keep values of mods that aren't installed) --------------

        internal static IEnumerable<PatchSpec> Specs()
        {
            yield return new PatchSpec
            {
                Id = "MainConstruct.InitialiseStage2: load mod craft data and modules",
                FeatureId = Features.CraftData,
                Target = () => EventHelpers.Declared(typeof(MainConstruct), "InitialiseStage2"),
                Prefix = PatchSpec.Hook(typeof(CraftData), nameof(Init_Prefix))
            };
            yield return new PatchSpec
            {
                // The last step of every spawn (blueprint, designer, campaign, multiplayer client, drones, retrofit).
                Id = "MainConstruct.BlueprintInitialisationDone: start craft modules",
                FeatureId = Features.CraftData,
                Target = () => EventHelpers.Declared(typeof(MainConstruct), "BlueprintInitialisationDone"),
                Postfix = PatchSpec.Hook(typeof(CraftData), nameof(Spawned_Postfix))
            };
            yield return new PatchSpec
            {
                Id = "MainConstruct.Save: save mod craft data and modules",
                FeatureId = Features.CraftData,
                Target = () => EventHelpers.Declared(typeof(MainConstruct), "Save"),
                Postfix = PatchSpec.Hook(typeof(CraftData), nameof(Save_Postfix))
            };
            yield return new PatchSpec
            {
                // Prefix: the craft is still intact (the game tears it down inside this method).
                Id = "MainConstruct.DestroyCompletely: remove craft modules",
                FeatureId = Features.CraftData,
                Target = () => EventHelpers.Declared(typeof(MainConstruct), "DestroyCompletely"),
                Prefix = PatchSpec.Hook(typeof(CraftData), nameof(Destroy_Prefix))
            };
        }

        // All the hooks or none: if saving weren't hooked, values loaded here would be dropped by the next save.
        private static bool Active => Features.IsAvailable(Features.CraftData);

        // Before the blocks are set up: read what the blueprint carried.
        private static void Init_Prefix(MainConstruct __instance, MiscDataSerialisation data)
        {
            if (__instance == null || !Active) return;
            try
            {
                var state = StateOf(__instance);
                var dict = data?.JsonDictionary;
                if (dict == null) return;
                state.LoadFrom(dict);
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("craftdata-load", "Could not read mod data from a craft's blueprint.", e);
            }
        }

        // The spawn is complete (blocks, force, position, name), so modules start now.
        private static void Spawned_Postfix(MainConstruct __instance)
        {
            if (__instance == null || !Active) return;
            try
            {
                CraftModules.Attach(__instance, StateOf(__instance));
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("craftmodules-attach", "Could not start mod craft modules.", e);
            }
        }

        private static void Save_Postfix(MainConstruct __instance, MiscDataSerialisation info)
        {
            if (__instance == null || info?.JsonDictionary == null || !Active) return;
            try
            {
                if (!TryState(__instance, out var state)) return;
                CraftModules.CollectSaves(state);
                var dict = info.JsonDictionary;
                foreach (var key in dict.Keys.Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).ToList()) dict.Remove(key);
                foreach (var kv in state.Snapshot()) dict[kv.Key] = kv.Value;
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("craftdata-save", "Could not save mod data with a craft.", e);
            }
        }

        private static void Destroy_Prefix(MainConstruct __instance)
        {
            if (__instance == null || !TryState(__instance, out var state)) return;
            try
            {
                CraftModules.Detach(state);
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("craftmodules-detach", "Could not remove mod craft modules.", e);
            }
        }
    }

    /// <summary>Everything the API keeps for one craft.</summary>
    internal sealed class CraftState
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        internal CraftState(MainConstruct craft)
        {
            Craft = new WeakReference<MainConstruct>(craft);
        }

        internal WeakReference<MainConstruct> Craft { get; }

        internal List<CraftModule> Modules { get; } = new List<CraftModule>();

        internal bool Attached;

        internal bool Removed;

        internal string Get(string key)
        {
            lock (_lock) return _values.TryGetValue(key, out var v) ? v : null;
        }

        internal void Set(string key, string value)
        {
            lock (_lock)
            {
                if (value == null) _values.Remove(key);
                else _values[key] = value;
            }
        }

        internal void RemoveWhere(Func<string, bool> match)
        {
            lock (_lock)
            {
                foreach (var k in _values.Keys.Where(match).ToList()) _values.Remove(k);
            }
        }

        internal List<string> KeysStartingWith(string prefix)
        {
            lock (_lock) return _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        internal void LoadFrom(Dictionary<string, string> dict)
        {
            lock (_lock)
            {
                foreach (var kv in dict)
                    if (kv.Key.StartsWith(CraftData.Prefix, StringComparison.Ordinal) && kv.Value != null)
                        _values[kv.Key] = kv.Value;
            }
        }

        internal List<KeyValuePair<string, string>> Snapshot()
        {
            lock (_lock) return _values.ToList();
        }
    }

    /// <summary>
    /// A mod's object attached to each craft: created when the craft spawns (with what it saved last time), asked for
    /// its data whenever the craft is saved, updated every frame if it wants, and told when the craft is removed.
    /// Register the class with <see cref="CraftModules.Register{T}"/>.
    /// <para>
    /// A module exists on every machine in multiplayer (each machine has its own copy of the craft). Use
    /// <see cref="ModNet"/> to keep clients in step when the host changes something. <see cref="OnSave"/> can run on
    /// a worker thread (the game saves craft off the main thread at times): only read your own fields there.
    /// </para>
    /// </summary>
    public abstract class CraftModule
    {
        /// <summary>The craft. Game object.</summary>
        public MainConstruct Craft { get; internal set; }

        /// <summary>What was registered for this module.</summary>
        public CraftModuleInfo Info { get; internal set; }

        internal int UpdateFailures;

        internal bool SpawnFailed;

        /// <summary>
        /// The craft has spawned (blocks, force, name and position are all set). savedData is what <see cref="OnSave"/>
        /// returned when it was last saved, or null for a new craft (or one saved without this module). If this throws,
        /// the module never saves, so the data it couldn't read is kept.
        /// </summary>
        protected internal virtual void OnSpawned(string savedData)
        {
        }

        /// <summary>The craft is being saved. Return the text to keep (null keeps nothing).</summary>
        protected internal virtual string OnSave() => null;

        /// <summary>
        /// Every frame on the main thread while the craft exists. Only called for modules that override it.
        /// </summary>
        protected internal virtual void OnUpdate()
        {
        }

        /// <summary>
        /// The craft is being removed from the game (destroyed, deleted, pulled out of play, mode change). Called while
        /// it is still intact. Not called when the game quits.
        /// </summary>
        protected internal virtual void OnRemoved()
        {
        }
    }

    /// <summary>A registered craft module.</summary>
    public sealed class CraftModuleInfo
    {
        internal CraftModuleInfo(string modId, string id, Type type, Func<MainConstruct, bool> appliesTo)
        {
            ModId = modId;
            Id = id;
            Type = type;
            AppliesTo = appliesTo;
            HasUpdate = type.GetMethod(nameof(CraftModule.OnUpdate), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, Type.EmptyTypes, null)?.DeclaringType != typeof(CraftModule);
        }

        public string ModId { get; }

        public string Id { get; }

        public Type Type { get; }

        internal Func<MainConstruct, bool> AppliesTo { get; }

        internal bool HasUpdate { get; }

        internal string SaveKey => CraftData.ModulePrefix + ModId + "/" + Id;
    }

    /// <summary>Registers and finds craft modules (<see cref="CraftModule"/>).</summary>
    public static class CraftModules
    {
        private static readonly object _gate = new object();
        private static readonly List<CraftModuleInfo> _registered = new List<CraftModuleInfo>();
        private static readonly List<CraftState> _updating = new List<CraftState>();
        private static bool _updateHooked;

        /// <summary>Every registered module type.</summary>
        public static IReadOnlyList<CraftModuleInfo> All
        {
            get { lock (_gate) return _registered.ToList(); }
        }

        /// <summary>
        /// Attaches a new T to every craft that spawns from now on (register in OnLoad). The mod id and id name the
        /// saved data: keep them the same between versions. appliesTo can limit it to some craft (e.g.
        /// <c>c => c is Fortress</c>... any test on the MainConstruct); it is checked once, when the craft spawns.
        /// </summary>
        public static CraftModuleInfo Register<T>(string modId, string id, Func<MainConstruct, bool> appliesTo = null) where T : CraftModule, new()
        {
            CraftData.Check(modId, nameof(modId));
            CraftData.Check(id, nameof(id));
            var info = new CraftModuleInfo(modId, id, typeof(T), appliesTo);
            lock (_gate)
            {
                if (_registered.Any(r => r.ModId == modId && r.Id == id))
                    throw new ArgumentException($"A craft module {modId}/{id} is already registered.");
                _registered.Add(info);
            }
            if (info.HasUpdate) HookUpdate();
            FtdApi.Log.Verbose(() => $"Craft module {modId}/{id} ({typeof(T).Name}) registered{(info.HasUpdate ? ", with per-frame update" : "")}.");
            return info;
        }

        /// <summary>The craft's module of type T, or null.</summary>
        public static T Get<T>(MainConstruct craft) where T : CraftModule
        {
            if (craft == null || !CraftData.TryState(craft, out var state)) return null;
            lock (state.Modules) return state.Modules.OfType<T>().FirstOrDefault();
        }

        /// <summary>All modules on the craft.</summary>
        public static IReadOnlyList<CraftModule> Of(MainConstruct craft)
        {
            if (craft == null || !CraftData.TryState(craft, out var state)) return Array.Empty<CraftModule>();
            lock (state.Modules) return state.Modules.ToList();
        }

        internal static void Attach(MainConstruct craft, CraftState state)
        {
            if (state.Attached) return;
            state.Attached = true;
            List<CraftModuleInfo> registered;
            lock (_gate) registered = _registered.ToList();
            bool anyUpdate = false;
            foreach (var info in registered)
            {
                CraftModule module;
                try
                {
                    if (info.AppliesTo != null && !info.AppliesTo(craft)) continue;
                    module = (CraftModule)Activator.CreateInstance(info.Type);
                    module.Craft = craft;
                    module.Info = info;
                }
                catch (Exception e)
                {
                    FtdApi.GetLog(info.ModId).ErrorOnce("craftmodule-create-" + info.ModId + "/" + info.Id, $"Craft module {info.ModId}/{info.Id} could not be created (reported once).", e.InnerException ?? e);
                    continue;
                }
                lock (state.Modules) state.Modules.Add(module);
                // If it can't read its saved data, it must not save over it later.
                if (!Call(module, "OnSpawned", () => module.OnSpawned(state.Get(info.SaveKey)))) module.SpawnFailed = true;
                anyUpdate |= info.HasUpdate;
            }
            if (anyUpdate)
            {
                lock (_gate) _updating.Add(state);
            }
        }

        internal static void CollectSaves(CraftState state)
        {
            List<CraftModule> modules;
            lock (state.Modules) modules = state.Modules.ToList();
            foreach (var module in modules)
            {
                if (module.SpawnFailed) continue;
                string data = null;
                bool ok = Call(module, "OnSave", () => data = module.OnSave());
                // A module that failed keeps its last saved data rather than losing it.
                if (ok) state.Set(module.Info.SaveKey, data);
            }
        }

        internal static void Detach(CraftState state)
        {
            if (state.Removed) return;
            state.Removed = true;
            List<CraftModule> modules;
            lock (state.Modules) modules = state.Modules.ToList();
            foreach (var module in modules) Call(module, "OnRemoved", module.OnRemoved);
            lock (_gate) _updating.Remove(state);
            lock (state.Modules) state.Modules.Clear();
            foreach (var module in modules) module.Craft = null;
        }

        private static void HookUpdate()
        {
            lock (_gate)
            {
                if (_updateHooked) return;
                _updateHooked = true;
            }
            MainThread.PostLater(() =>
            {
                try
                {
                    GameEvents.UpdateEvent.RegWithEvent(Update);
                }
                catch (Exception e)
                {
                    FtdApi.Log.Error("Craft modules: could not hook the frame update; OnUpdate won't run.", e);
                }
            });
        }

        private static void Update(ITimeStep step)
        {
            CraftState[] states;
            lock (_gate)
            {
                if (_updating.Count == 0) return;
                states = _updating.ToArray();
            }
            foreach (var state in states)
            {
                if (state.Removed || !state.Craft.TryGetTarget(out var craft) || craft.Destroyed)
                {
                    lock (_gate) _updating.Remove(state);
                    continue;
                }
                CraftModule[] modules;
                lock (state.Modules) modules = state.Modules.ToArray();
                foreach (var module in modules)
                {
                    if (!module.Info.HasUpdate || module.UpdateFailures >= 5) continue;
                    if (!Call(module, "OnUpdate", module.OnUpdate) && ++module.UpdateFailures == 5)
                        FtdApi.GetLog(module.Info.ModId).Warning($"Craft module {module.Info.ModId}/{module.Info.Id}: OnUpdate failed 5 times on '{EventHelpers.SafeName(craft)}'; not calling it again for that craft.");
                }
            }
        }

        private static bool Call(CraftModule module, string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                var info = module.Info;
                FtdApi.GetLog(info.ModId).ErrorOnce($"craftmodule-{what}-{info.ModId}/{info.Id}", $"Craft module {info.ModId}/{info.Id} threw in {what} (reported once).", e);
                return false;
            }
        }
    }
}
