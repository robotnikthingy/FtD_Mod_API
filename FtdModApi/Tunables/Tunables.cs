using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FtdModApi
{
    /// <summary>The value type of a <see cref="TunableInfo"/>.</summary>
    public enum TunableKind
    {
        Float,
        Int,
        Double,
        Bool
    }

    /// <summary>Description of one tunable game value.</summary>
    public sealed class TunableInfo
    {
        internal TunableInfo(string key, string group, FieldInfo field, TunableKind kind, double defaultValue)
        {
            Key = key;
            Group = group;
            Field = field;
            Kind = kind;
            DefaultValue = defaultValue;
            Name = field.Name;
            Owner = Tunables.GameOwner;
        }

        internal TunableInfo(TunableValue value, string owner, double? min, double? max, string description, string units, string note = null)
        {
            Key = value.Key;
            Group = value.System.Name;
            Name = value.Name;
            Kind = value.Kind;
            DefaultValue = value.DefaultValue;
            ModValue = value;
            Owner = owner;
            Min = min;
            Max = max;
            _ownDescription = description ?? string.Empty;
            Units = units ?? string.Empty;
            Note = note ?? string.Empty;
        }

        private readonly string _ownDescription = string.Empty;

        /// <summary>"Group.Name", e.g. "AdvCannon.BaseReloadTime". Case-insensitive when looked up.</summary>
        public string Key { get; }

        /// <summary>The constants class without its "Constants" suffix (e.g. "AdvCannon"), or a mod system's name.</summary>
        public string Group { get; }

        public string Name { get; }

        /// <summary>"Game" for the game's own values, otherwise the id of the mod that registered the system.</summary>
        public string Owner { get; }

        /// <summary>True for values a mod registered (<see cref="Tunables.RegisterSystem"/>).</summary>
        public bool IsModValue => ModValue != null && Owner != Tunables.GameOwner;

        /// <summary>True for game values that are read-only getters in the game, made tunable by redirecting their readers.</summary>
        public bool IsRedirectedGetter => ModValue != null && Owner == Tunables.GameOwner;

        /// <summary>Lowest allowed value, if the owner set one (the final value is clamped to it).</summary>
        public double? Min { get; }

        /// <summary>Highest allowed value, if the owner set one.</summary>
        public double? Max { get; }

        /// <summary>
        /// Player-facing text saying what the value does, shown in the settings menu tooltip. The owning mod's own
        /// description wins; otherwise it comes from a FtdModApi.descriptions.json file or <see cref="Tunables.Describe"/>
        /// (FtdModApi ships one for every game value). Empty when nobody described it.
        /// </summary>
        public string Description => !string.IsNullOrEmpty(_ownDescription) ? _ownDescription : TunableDescriptions.Get(Key);

        /// <summary>Technical note for modders (e.g. how a read-only game value was made tunable). Empty if none.</summary>
        public string Note { get; } = string.Empty;

        /// <summary>Units, e.g. "s" or "damage" (mod values; empty for game values).</summary>
        public string Units { get; } = string.Empty;

        public TunableKind Kind { get; }

        /// <summary>The game's own value, captured before any mod changed it. Bools are 0 or 1.</summary>
        public double DefaultValue { get; }

        /// <summary>The value the game is using right now. Bools are 0 or 1.</summary>
        public double CurrentValue => Tunables.ReadField(this);

        /// <summary>Raw field for game values (null for mod values). Unstable: it can be renamed or removed by any game update.</summary>
        public FieldInfo Field { get; }

        internal TunableValue ModValue { get; }
    }

    /// <summary>
    /// Game balance values that FtD keeps as plain static fields on its *Constants classes:
    /// AdvCannonConstants, MissileConstants, CramConstants, LaserConstants, FuelConstants, DetectionConstants
    /// and others, roughly 450 values. FtD never writes these itself, so changing them needs no patches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Several mods can change the same value. Each change is a contribution (set, add or multiply)
    /// applied in <see cref="Priority"/> order to the game's default, so the result does not depend
    /// on mod load order, and removing a mod's contribution restores the correct value.
    /// </para>
    /// <para>
    /// Make changes in your plugin's OnLoad. The game reads most of these values when it computes
    /// stats (for example, shell stats are recomputed when a shell design changes or loads), so a value
    /// changed mid-battle may not affect things already built. In multiplayer every player needs the
    /// same values.
    /// </para>
    /// <para>
    /// Every key, with its default and current value, is listed in Mods/FtdModApi/FtdModApi_tunables.txt
    /// after each launch. Mods add their own groups with <see cref="RegisterSystem"/>.
    /// </para>
    /// <para>
    /// Data-only mods: put a file named <c>FtdModApi.tunables.json</c> in your mod folder (no DLL needed):
    /// <code>
    /// {
    ///   "AdvCannon.BaseReloadTime": 30,                // set
    ///   "Cram.SomeValue":           { "multiply": 1.5 },
    ///   "Missile.SomeValue":        { "add": -10 },
    ///   "Laser.SomeValue":          { "set": 5, "priority": 350 }
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    public static class Tunables
    {
        /// <summary>File name the API looks for in every enabled mod folder.</summary>
        public const string JsonFileName = "FtdModApi.tunables.json";

        private enum Op
        {
            Set,
            Add,
            Multiply
        }

        private sealed class Contribution
        {
            public string Key;
            public string ModId;
            public Op Op;
            public double Value;
            public int Priority;
            public long Sequence;
        }

        private sealed class Entry
        {
            public TunableInfo Info;
            public readonly List<Contribution> Contributions = new List<Contribution>();
        }

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<Contribution>> _pending = new Dictionary<string, List<Contribution>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TunableSystem> _systems = new Dictionary<string, TunableSystem>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _gameGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static long _sequence;
        private static bool _discovered;

        /// <summary>The <see cref="TunableInfo.Owner"/> of the game's own values.</summary>
        public const string GameOwner = "Game";

        /// <summary>True if the key exists in this game build.</summary>
        public static bool Exists(string key)
        {
            EnsureDiscovered();
            lock (_gate) return key != null && _entries.ContainsKey(key);
        }

        /// <summary>Info for one key, or null if it does not exist in this game build.</summary>
        public static TunableInfo Find(string key)
        {
            EnsureDiscovered();
            lock (_gate) return key != null && _entries.TryGetValue(key, out var e) ? e.Info : null;
        }

        /// <summary>All tunables, sorted by key.</summary>
        public static IReadOnlyList<TunableInfo> All
        {
            get
            {
                EnsureDiscovered();
                lock (_gate) return _entries.Values.Select(e => e.Info).OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        /// <summary>Current value (bools as 0/1), or <paramref name="fallback"/> if the key does not exist.</summary>
        public static double Get(string key, double fallback = double.NaN)
        {
            var info = Find(key);
            return info == null ? fallback : info.CurrentValue;
        }

        /// <summary>Replaces the value. Runs at <see cref="Priority.Override"/> unless told otherwise.</summary>
        public static Registration Set(string modId, string key, double value, int priority = Priority.Override)
            => Contribute(modId, key, Op.Set, value, priority);

        /// <summary>Sets a bool tunable.</summary>
        public static Registration Set(string modId, string key, bool value, int priority = Priority.Override)
            => Contribute(modId, key, Op.Set, value ? 1 : 0, priority);

        /// <summary>Adds to the value. Runs at <see cref="Priority.Add"/> unless told otherwise.</summary>
        public static Registration Add(string modId, string key, double amount, int priority = Priority.Add)
            => Contribute(modId, key, Op.Add, amount, priority);

        /// <summary>Scales the value. Runs at <see cref="Priority.Multiply"/> unless told otherwise.</summary>
        public static Registration Multiply(string modId, string key, double factor, int priority = Priority.Multiply)
            => Contribute(modId, key, Op.Multiply, factor, priority);

        /// <summary>
        /// Applies a tunables JSON file (format in the class remarks). Returns how many entries were applied.
        /// Unknown keys and bad entries are logged and skipped.
        /// </summary>
        public static int LoadJson(string modId, string path)
        {
            var log = FtdApi.GetLog(modId);
            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                log.Error("Could not read " + path + ": " + e.Message);
                return 0;
            }

            int applied = 0;
            foreach (var prop in root.Properties())
            {
                try
                {
                    if (ApplyJsonEntry(modId, prop.Name, prop.Value, log)) applied++;
                }
                catch (Exception e)
                {
                    log.Warning($"{Path.GetFileName(path)}: skipping '{prop.Name}': {e.Message}");
                }
            }
            log.Info($"Applied {applied} tunable(s) from {Path.GetFileName(path)}.");
            return applied;
        }

        private static bool ApplyJsonEntry(string modId, string key, JToken token, ModLog log)
        {
            if (!Exists(key) && IsGameGroup(key))
            {
                log.Warning($"Tunable '{key}' does not exist in this game build; skipped. See FtdModApi_tunables.txt for valid keys.");
                return false;
            }

            if (token.Type == JTokenType.Boolean) return Set(modId, key, token.Value<bool>()).IsActive;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return Set(modId, key, token.Value<double>()).IsActive;

            if (token is JObject obj)
            {
                int? priority = obj["priority"]?.Value<int>();
                if (obj["set"] != null)
                {
                    var v = obj["set"];
                    return v.Type == JTokenType.Boolean
                        ? Set(modId, key, v.Value<bool>(), priority ?? Priority.Override).IsActive
                        : Set(modId, key, v.Value<double>(), priority ?? Priority.Override).IsActive;
                }
                if (obj["multiply"] != null) return Multiply(modId, key, obj["multiply"].Value<double>(), priority ?? Priority.Multiply).IsActive;
                if (obj["add"] != null) return Add(modId, key, obj["add"].Value<double>(), priority ?? Priority.Add).IsActive;
            }

            if (ContentJsonKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                log.Warning($"'{key}' belongs in {Content.JsonFileName}, not {JsonFileName}; skipped.");
                return false;
            }
            log.Warning($"Tunable '{key}': expected a number, true/false, or an object with \"set\", \"add\" or \"multiply\".");
            return false;
        }

        private static Registration Contribute(string modId, string key, Op op, double value, int priority)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            EnsureDiscovered();

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                FtdApi.GetLog(modId).Warning($"Tunable '{key}': {op} {value} ignored (not a finite number).");
                return new Registration(null);
            }

            if (string.IsNullOrWhiteSpace(key) || key.IndexOf('.') <= 0)
            {
                FtdApi.GetLog(modId).Warning($"Tunable '{key}': keys look like 'Group.Name', e.g. 'AdvCannon.BaseReloadTime'.");
                return new Registration(null);
            }

            Entry entry;
            TunableValue changed = null;
            var contribution = new Contribution { Key = key, ModId = modId, Op = op, Value = value, Priority = priority };
            lock (_gate)
            {
                contribution.Sequence = ++_sequence;
                if (!_entries.TryGetValue(key, out entry))
                {
                    if (IsGameGroup(key))
                    {
                        FtdApi.GetLog(modId).WarningOnce("tunable-missing:" + key,
                            $"Tunable '{key}' does not exist in this game build, so this change does nothing. Check Tunables.Exists first, or see FtdModApi_tunables.txt.");
                        return new Registration(null);
                    }
                    // A mod system that has not registered yet (load order), or is not installed. Keep the change
                    // and apply it when the system registers.
                    if (!_pending.TryGetValue(key, out var list)) _pending[key] = list = new List<Contribution>();
                    list.Add(contribution);
                    FtdApi.GetLog(modId).Verbose(() => $"Tunable '{key}': {op.ToString().ToLowerInvariant()} {Format(value)} is waiting for its system to register.");
                }
                else
                {
                    if (entry.Info.Kind == TunableKind.Bool && op != Op.Set)
                    {
                        FtdApi.GetLog(modId).Warning($"Tunable '{key}' is true/false; only Set is allowed.");
                        return new Registration(null);
                    }
                    WarnOnConflictingOverrides(entry, contribution);
                    entry.Contributions.Add(contribution);
                    changed = Recompute(entry);
                    var info = entry.Info;
                    FtdApi.GetLog(modId).Verbose(() => $"Tunable '{key}': {op.ToString().ToLowerInvariant()} {Format(value)} (priority {priority}) -> now {Format(info.CurrentValue)} (default {Format(info.DefaultValue)}).");
                }
            }
            changed?.RaiseChanged();

            return new Registration(() =>
            {
                TunableValue after = null;
                lock (_gate)
                {
                    if (_entries.TryGetValue(contribution.Key, out var e) && e.Contributions.Remove(contribution))
                    {
                        after = Recompute(e);
                    }
                    else if (_pending.TryGetValue(contribution.Key, out var list))
                    {
                        list.Remove(contribution);
                        if (list.Count == 0) _pending.Remove(contribution.Key);
                    }
                }
                after?.RaiseChanged();
            });
        }

        /// <summary>One change made to a tunable, for menus and reports.</summary>
        public readonly struct Change
        {
            internal Change(string modId, string op, double value, int priority)
            {
                ModId = modId;
                Operation = op;
                Value = value;
                Priority = priority;
            }

            /// <summary>The mod that made it ("Player" for the settings menu).</summary>
            public string ModId { get; }

            /// <summary>"set", "add" or "multiply".</summary>
            public string Operation { get; }

            public double Value { get; }

            public int Priority { get; }

            public override string ToString() => ModId + " " + Operation + " " + Format(Value);
        }

        /// <summary>Every change applied to a key, in the order they run.</summary>
        public static IReadOnlyList<Change> ChangesOf(string key)
        {
            lock (_gate)
            {
                if (key == null || !_entries.TryGetValue(key, out var e)) return new Change[0];
                return e.Contributions.OrderBy(c => c.Priority).ThenBy(c => c.ModId, StringComparer.Ordinal).ThenBy(c => c.Sequence)
                    .Select(c => new Change(c.ModId, c.Op.ToString().ToLowerInvariant(), c.Value, c.Priority)).ToList();
            }
        }

        /// <summary>Formats a value the way reports and the settings menu show it.</summary>
        public static string Format(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        private static readonly string[] ContentJsonKeys =
        {
            "hideItems", "hidePrefabs", "hideModFolders", "replaceItems", "hideShellParts", "cramPellets", "missileParts",
            "apsLoaders", "apsMaxGaugeMm", "blockHealth", "blockArmour"
        };

        private static bool IsGameGroup(string key)
        {
            EnsureDiscovered();
            int dot = key?.LastIndexOf('.') ?? -1;
            if (dot <= 0) return false;
            lock (_gate) return _gameGroups.Contains(key.Substring(0, dot));
        }

        // ---------------------------------------------------------------------------------------------
        // Mod-registered systems
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Registers a system: a group of values your mod owns (e.g. "Bulldozers" with BaseDamage, DesignSpeedModifier).
        /// They become tunables like the game's own: other mods change them with <see cref="Set(string,string,double,int)"/>,
        /// <see cref="Add"/> and <see cref="Multiply"/>, players with FtdModApi.tunables.json, and they are listed in
        /// FtdModApi_tunables.txt with their owner, default and current value. Returns null if the name is taken.
        /// </summary>
        /// <param name="modId">Your mod's id.</param>
        /// <param name="name">System name: letters, digits and '_' (it becomes the first part of every key).</param>
        /// <param name="description">One line shown in reports and the settings menu.</param>
        public static TunableSystem RegisterSystem(string modId, string name, string description = null)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            var log = FtdApi.GetLog(modId);
            if (!TunableSystem.IsValidName(name))
            {
                log.Error($"Tunables.RegisterSystem('{name}'): use letters, digits and '_' only.");
                return null;
            }
            EnsureDiscovered();
            lock (_gate)
            {
                if (_gameGroups.Contains(name))
                {
                    log.Error($"Tunables.RegisterSystem('{name}'): the game already has a '{name}' group. Pick another name.");
                    return null;
                }
                if (_systems.TryGetValue(name, out var existing))
                {
                    if (existing.ModId == modId) return existing;
                    log.Error($"Tunables.RegisterSystem('{name}'): already registered by '{existing.ModId}'.");
                    return null;
                }
                var system = new TunableSystem(modId, name, description ?? string.Empty);
                _systems[name] = system;
                log.Info($"Registered tunable system '{name}'.");
                return system;
            }
        }

        /// <summary>All mod-registered systems.</summary>
        public static IReadOnlyList<TunableSystem> Systems
        {
            get { lock (_gate) return _systems.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        }

        /// <summary>The mod system with this name, or null.</summary>
        public static TunableSystem FindSystem(string name)
        {
            lock (_gate) return name != null && _systems.TryGetValue(name, out var s) ? s : null;
        }

        /// <summary>Adds one of the game's read-only constants (see ReadOnlyTunables) as a game value.</summary>
        internal static bool RegisterGameGetter(TunableValue value, string note)
        {
            EnsureDiscovered();
            lock (_gate) _gameGroups.Add(value.System.Name);
            return RegisterValue(value, null, null, null, null, note);
        }

        /// <summary>
        /// Gives a value a player-facing description for the settings menu tooltip, for values whose owner did not
        /// describe them (game values, or another mod's). The owner's own description always wins. The key does not
        /// have to exist yet. The same can be done without code with a FtdModApi.descriptions.json file
        /// (<c>{ "Group.Name": "text" }</c>) in your mod folder.
        /// </summary>
        /// <returns>False if the key already has a description from another mod (the first one is kept).</returns>
        public static bool Describe(string modId, string key, string description)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(description)) return false;
            return TunableDescriptions.Add(modId ?? "unknown", key.Trim(), description.Trim());
        }

        /// <summary>Adds a value to the tunable table, applying any changes that were waiting for it.</summary>
        internal static bool RegisterValue(TunableValue value, double? min, double? max, string description, string units, string note = null)
        {
            TunableValue changed;
            lock (_gate)
            {
                if (_entries.ContainsKey(value.Key)) return false;
                var entry = new Entry { Info = new TunableInfo(value, value.System.ModId, min, max, description, units, note) };
                _entries[value.Key] = entry;
                if (_pending.TryGetValue(value.Key, out var waiting))
                {
                    _pending.Remove(value.Key);
                    FtdApi.GetLog(value.System.ModId).Verbose(() => $"Tunable '{value.Key}' registered; applying {waiting.Count} waiting change(s).");
                    foreach (var c in waiting)
                    {
                        if (value.Kind == TunableKind.Bool && c.Op != Op.Set)
                        {
                            FtdApi.GetLog(c.ModId).Warning($"Tunable '{value.Key}' is true/false; only Set is allowed. Change dropped.");
                            continue;
                        }
                        entry.Contributions.Add(c);
                    }
                }
                changed = Recompute(entry);
            }
            changed?.RaiseChanged();
            return true;
        }

        private static void WarnOnConflictingOverrides(Entry entry, Contribution incoming)
        {
            if (incoming.Op != Op.Set || incoming.ModId == PlayerValues.Owner) return;
            foreach (var c in entry.Contributions)
            {
                if (c.Op == Op.Set && c.ModId != incoming.ModId && c.ModId != PlayerValues.Owner && c.Value != incoming.Value)
                {
                    FtdApi.Log.Warning(
                        $"Tunable '{entry.Info.Key}' is set by both '{c.ModId}' ({Format(c.Value)}) and '{incoming.ModId}' ({Format(incoming.Value)}). " +
                        "The one that runs last (higher priority, then mod id) wins.");
                }
            }
        }

        private static TunableValue Recompute(Entry entry)
        {
            double v = entry.Info.DefaultValue;
            foreach (var c in entry.Contributions.OrderBy(c => c.Priority).ThenBy(c => c.ModId, StringComparer.Ordinal).ThenBy(c => c.Sequence))
            {
                switch (c.Op)
                {
                    case Op.Set: v = c.Value; break;
                    case Op.Add: v += c.Value; break;
                    case Op.Multiply: v *= c.Value; break;
                }
            }
            if (entry.Info.Min.HasValue && v < entry.Info.Min.Value) v = entry.Info.Min.Value;
            if (entry.Info.Max.HasValue && v > entry.Info.Max.Value) v = entry.Info.Max.Value;
            return WriteField(entry.Info, v);
        }

        // ---------------------------------------------------------------------------------------------
        // Field access
        // ---------------------------------------------------------------------------------------------

        internal static double ReadField(TunableInfo info)
        {
            if (info.ModValue != null) return info.ModValue.Raw;
            var raw = info.Field.GetValue(null);
            switch (info.Kind)
            {
                case TunableKind.Bool: return (bool)raw ? 1 : 0;
                case TunableKind.Int: return (int)raw;
                case TunableKind.Float: return (float)raw;
                default: return (double)raw;
            }
        }

        /// <summary>Writes the value; returns the mod value if it changed (its Changed event is raised outside the lock).</summary>
        private static TunableValue WriteField(TunableInfo info, double v)
        {
            if (info.ModValue != null)
            {
                switch (info.Kind)
                {
                    case TunableKind.Bool: v = v != 0 ? 1 : 0; break;
                    case TunableKind.Int: v = Math.Round(v); break;
                    case TunableKind.Float: v = (float)v; break;
                }
                return info.ModValue.SetRaw(v) ? info.ModValue : null;
            }
            object boxed;
            switch (info.Kind)
            {
                case TunableKind.Bool: boxed = v != 0; break;
                case TunableKind.Int: boxed = (int)Math.Round(v); break;
                case TunableKind.Float: boxed = (float)v; break;
                default: boxed = v; break;
            }
            info.Field.SetValue(null, boxed);
            return null;
        }

        // ---------------------------------------------------------------------------------------------
        // Discovery
        // ---------------------------------------------------------------------------------------------

        private static readonly string[] ScannedAssemblies = { "Ftd", "Core", "Common" };

        private static void EnsureDiscovered()
        {
            if (_discovered) return;
            lock (_gate)
            {
                if (_discovered) return;
                Discover();
                _discovered = true;
            }
        }

        /// <summary>
        /// Finds every class named *Constants in the game assemblies and records its public, static,
        /// writable float/int/double/bool fields. Defaults are captured here, before any mod changes them.
        /// </summary>
        private static void Discover()
        {
            var log = FtdApi.Log;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => ScannedAssemblies.Contains(a.GetName().Name, StringComparer.Ordinal))
                .ToList();

            var groupsSeen = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in assemblies)
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }

                foreach (var type in types)
                {
                    if (!type.IsClass || type.IsGenericTypeDefinition || !type.Name.EndsWith("Constants", StringComparison.Ordinal)) continue;

                    FieldInfo[] fields;
                    try
                    {
                        fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                            .Where(f => !f.IsLiteral && !f.IsInitOnly && KindOf(f.FieldType).HasValue)
                            .ToArray();
                    }
                    catch
                    {
                        continue;
                    }
                    if (fields.Length == 0) continue;

                    var group = type.Name.Substring(0, type.Name.Length - "Constants".Length);
                    if (group.Length == 0) group = type.Name;
                    if (groupsSeen.TryGetValue(group, out var other) && other != type)
                    {
                        // Two *Constants classes with the same name in different namespaces.
                        group = (type.Namespace?.Split('.').LastOrDefault() ?? "Global") + "." + group;
                    }
                    groupsSeen[group] = type;
                    _gameGroups.Add(group);

                    foreach (var f in fields)
                    {
                        try
                        {
                            var kind = KindOf(f.FieldType).Value;
                            var key = group + "." + f.Name;
                            var probe = new TunableInfo(key, group, f, kind, 0);
                            var info = new TunableInfo(key, group, f, kind, ReadField(probe));
                            _entries[key] = new Entry { Info = info };
                        }
                        catch (Exception e)
                        {
                            log.Warning($"Could not read {type.Name}.{f.Name}: {e.Message}");
                        }
                    }
                }
            }
        }

        private static TunableKind? KindOf(Type t)
        {
            if (t == typeof(float)) return TunableKind.Float;
            if (t == typeof(int)) return TunableKind.Int;
            if (t == typeof(double)) return TunableKind.Double;
            if (t == typeof(bool)) return TunableKind.Bool;
            return null;
        }

        // ---------------------------------------------------------------------------------------------
        // Startup helpers used by the API
        // ---------------------------------------------------------------------------------------------

        internal static int Count
        {
            get
            {
                EnsureDiscovered();
                lock (_gate) return _entries.Count;
            }
        }

        /// <summary>Applies FtdModApi.tunables.json from every enabled mod folder.</summary>
        internal static void LoadDataOnlyMods(string modsRoot)
        {
            if (string.IsNullOrEmpty(modsRoot) || !Directory.Exists(modsRoot)) return;
            foreach (var dir in Directory.GetDirectories(modsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(dir, JsonFileName);
                if (!File.Exists(file)) continue;
                try
                {
                    if (!BrilliantSkies.Modding.ModDisabler.CheckModEnabled(dir, out _)) continue;
                }
                catch
                {
                    // No readable header: FtD would not load this mod either.
                    continue;
                }
                LoadJson(Path.GetFileName(dir), file);
            }
        }

        internal static string BuildReport()
        {
            EnsureDiscovered();
            var sb = new StringBuilder();
            sb.AppendLine("FtdModApi tunables (regenerated every launch)");
            sb.AppendLine("Change these from code with Tunables.Set/Add/Multiply, or from a data-only mod with " + JsonFileName + ".");
            sb.AppendLine("Columns: key, type, default, current value, mods changing it.");
            sb.AppendLine("Game groups come first, then systems registered by mods ([System] (mod: owner)).");
            lock (_gate)
            {
                foreach (var group in _entries.Values.GroupBy(e => e.Info.Group)
                             .OrderBy(g => g.First().Info.IsModValue ? 1 : 0)
                             .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                    var first = group.First().Info;
                    if (first.IsModValue)
                    {
                        var system = _systems.TryGetValue(group.Key, out var sys) ? sys : null;
                        sb.AppendLine($"[{group.Key}]  (mod: {first.Owner}){(string.IsNullOrEmpty(system?.Description) ? string.Empty : "  " + system.Description)}");
                    }
                    else
                    {
                        sb.AppendLine("[" + group.Key + "]");
                    }
                    foreach (var e in group.OrderBy(e => e.Info.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var current = e.Info.CurrentValue;
                        var changedBy = e.Contributions.Count == 0
                            ? string.Empty
                            : "   <- " + string.Join(", ", e.Contributions.Select(c => c.ModId + " " + c.Op.ToString().ToLowerInvariant() + " " + Format(c.Value)));
                        var limits = e.Info.Min.HasValue || e.Info.Max.HasValue
                            ? $"   [{(e.Info.Min.HasValue ? Format(e.Info.Min.Value) : "")}..{(e.Info.Max.HasValue ? Format(e.Info.Max.Value) : "")}]"
                            : string.Empty;
                        var about = string.IsNullOrEmpty(e.Info.Description) ? string.Empty : "   " + e.Info.Description + (string.IsNullOrEmpty(e.Info.Units) ? string.Empty : $" ({e.Info.Units})");
                        sb.AppendLine($"  {e.Info.Key,-60} {e.Info.Kind,-6} {Format(e.Info.DefaultValue),12} {Format(current),12}{changedBy}{limits}{about}");
                    }
                }

                foreach (var system in _systems.Values.Where(s => s.Pipelines.Count > 0).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                    sb.AppendLine($"Hooks published by [{system.Name}]: " + string.Join(", ", system.Pipelines));
                }

                if (_pending.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("WAITING  (changes to systems that are not registered: the mod that owns them is missing, disabled, or the key is misspelled)");
                    foreach (var kv in _pending.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"  {kv.Key,-60} <- " + string.Join(", ", kv.Value.Select(c => c.ModId + " " + c.Op.ToString().ToLowerInvariant() + " " + Format(c.Value))));
                    }
                }
            }
            return sb.ToString();
        }

    }
}
