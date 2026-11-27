using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FtdModApi
{
    /// <summary>
    /// Values the player typed into the settings menu's "Player Value" column. Each one overrides every mod and the
    /// game (it is applied last, at the highest priority). Saved to Documents/From The Depths/FtdModApi.player.json,
    /// outside the Mods folder so Workshop updates never touch it.
    /// </summary>
    public static class PlayerValues
    {
        /// <summary>The owner name player values show under in reports.</summary>
        public const string Owner = "Player";

        /// <summary>The file name, in the root of the game's user folder.</summary>
        public const string FileName = "FtdModApi.player.json";

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, double> _values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Registration> _applied = new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);
        private static bool _dirty;

        /// <summary>Full path of the player file (null until the API has started).</summary>
        public static string FilePath { get; private set; }

        /// <summary>How many player values are set.</summary>
        public static int Count
        {
            get { lock (_gate) return _values.Count; }
        }

        /// <summary>The player's value for a key, if they set one.</summary>
        public static bool TryGet(string key, out double value)
        {
            lock (_gate) return _values.TryGetValue(key ?? string.Empty, out value);
        }

        /// <summary>All player values (including ones for systems that are not installed right now).</summary>
        public static IReadOnlyDictionary<string, double> All
        {
            get { lock (_gate) return new Dictionary<string, double>(_values, StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>Sets (or with null, clears) the player's value for a key. Applies immediately; saved when the menu closes.</summary>
        public static void Set(string key, double? value)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            Registration old;
            lock (_gate)
            {
                if (value.HasValue && _values.TryGetValue(key, out var current) && current == value.Value) return;
                if (!value.HasValue && !_values.ContainsKey(key)) return;
                _applied.TryGetValue(key, out old);
                _applied.Remove(key);
                if (value.HasValue) _values[key] = value.Value;
                else _values.Remove(key);
                _dirty = true;
            }
            old?.Dispose();
            if (value.HasValue)
            {
                var reg = Tunables.Set(Owner, key, value.Value, int.MaxValue);
                lock (_gate) _applied[key] = reg;
            }
            FtdApi.Log.Verbose(() => value.HasValue ? $"Player value {key} = {value.Value.ToString(CultureInfo.InvariantCulture)}." : $"Player value {key} cleared.");
        }

        /// <summary>Clears every player value whose key matches (all of them when no filter is given).</summary>
        public static int ClearAll(Func<string, bool> filter = null)
        {
            List<string> keys;
            lock (_gate) keys = _values.Keys.Where(k => filter == null || filter(k)).ToList();
            foreach (var k in keys) Set(k, null);
            return keys.Count;
        }

        /// <summary>Reads the player file and applies every value. Called once at startup.</summary>
        internal static void Load(string userFolder)
        {
            if (string.IsNullOrEmpty(userFolder)) return;
            FilePath = Path.Combine(userFolder, FileName);
            if (!File.Exists(FilePath))
            {
                FtdApi.Log.Verbose(() => $"No player values file yet ({FilePath}).");
                return;
            }

            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                FtdApi.Log.Error($"Could not read {FilePath}: {e.Message}. Player values were not applied; the file is left as it is.");
                FilePath = null; // never overwrite a file we could not read
                return;
            }

            int count = 0;
            if (root["values"] is JObject values)
            {
                foreach (var p in values.Properties())
                {
                    double? v = p.Value.Type == JTokenType.Boolean ? (p.Value.Value<bool>() ? 1 : 0)
                        : (p.Value.Type == JTokenType.Float || p.Value.Type == JTokenType.Integer) ? p.Value.Value<double>() : (double?)null;
                    if (!v.HasValue)
                    {
                        FtdApi.Log.Warning($"{FileName}: '{p.Name}' is not a number or true/false; ignored.");
                        continue;
                    }
                    Set(p.Name, v.Value);
                    count++;
                }
            }
            lock (_gate) _dirty = false;
            FtdApi.Log.Info($"Applied {count} player value(s) from {FileName}.");
        }

        /// <summary>Writes the player file if anything changed.</summary>
        internal static void Save()
        {
            Dictionary<string, double> snapshot;
            lock (_gate)
            {
                if (!_dirty || FilePath == null) return;
                snapshot = new Dictionary<string, double>(_values, StringComparer.OrdinalIgnoreCase);
                _dirty = false;
            }
            try
            {
                var values = new JObject();
                foreach (var kv in snapshot.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var info = Tunables.Find(kv.Key);
                    values[kv.Key] = info != null && info.Kind == TunableKind.Bool ? (JToken)(kv.Value != 0) : kv.Value;
                }
                var root = new JObject
                {
                    ["about"] = "Values set in the FtdModApi settings menu. They override every mod and the game. Delete a line (or this file) to undo.",
                    ["version"] = 1,
                    ["values"] = values
                };
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, root.ToString(Formatting.Indented));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                FtdApi.Log.Verbose(() => $"Saved {snapshot.Count} player value(s) to {FilePath}.");
            }
            catch (Exception e)
            {
                lock (_gate) _dirty = true;
                FtdApi.Log.Error($"Could not save {FilePath}: {e.Message}");
            }
        }
    }
}
