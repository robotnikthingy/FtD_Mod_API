using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FtdModApi
{
    /// <summary>
    /// Player-facing descriptions of tunable values, shown in the settings menu tooltip.
    /// Sources, strongest first: the owning mod's own description (TunableSystem.Float(..., description:)),
    /// then FtdModApi.descriptions.json files in mod folders and <see cref="Tunables.Describe"/> (first mod wins),
    /// then the descriptions FtdModApi ships for the game's values (embedded in the DLL).
    /// </summary>
    internal static class TunableDescriptions
    {
        /// <summary>File name the API looks for in every enabled mod folder.</summary>
        internal const string FileName = "FtdModApi.descriptions.json";

        private const string BuiltInSource = "FtdModApi (built in)";

        private sealed class Text
        {
            public string Value;
            public string Source;
            public bool BuiltIn;
        }

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, Text> _texts = new Dictionary<string, Text>(StringComparer.OrdinalIgnoreCase);

        internal static int Count
        {
            get { lock (_gate) return _texts.Count; }
        }

        internal static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            lock (_gate) return _texts.TryGetValue(key, out var t) ? t.Value : string.Empty;
        }

        internal static bool Add(string source, string key, string text)
        {
            lock (_gate)
            {
                if (_texts.TryGetValue(key, out var existing) && !existing.BuiltIn)
                {
                    if (!string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase))
                    {
                        FtdApi.GetLog(source).Verbose(() => $"Description for '{key}' ignored: {existing.Source} already described it.");
                        return false;
                    }
                }
                _texts[key] = new Text { Value = text, Source = source, BuiltIn = false };
                return true;
            }
        }

        /// <summary>Loads the built-in descriptions (embedded resource), then FtdModApi.descriptions.json from enabled mod folders.</summary>
        internal static void Load(string modsRoot)
        {
            int builtIn = LoadBuiltIn();
            FtdApi.Log.Verbose(() => $"Loaded {builtIn} built-in value descriptions.");

            if (string.IsNullOrEmpty(modsRoot) || !Directory.Exists(modsRoot)) return;
            foreach (var dir in Directory.GetDirectories(modsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(dir, FileName);
                if (!File.Exists(file)) continue;
                try
                {
                    if (!BrilliantSkies.Modding.ModDisabler.CheckModEnabled(dir, out _)) continue;
                }
                catch
                {
                    continue; // no readable header: FtD would not load this mod either
                }
                var modId = Path.GetFileName(dir);
                try
                {
                    int n = 0;
                    foreach (var p in ReadObject(File.ReadAllText(file)))
                        if (Add(modId, p.Key, p.Value)) n++;
                    FtdApi.GetLog(modId).Info($"{FileName}: {n} description(s) loaded.");
                }
                catch (Exception e)
                {
                    FtdApi.GetLog(modId).Error($"{FileName} could not be read: {e.Message}");
                }
            }
        }

        private static int LoadBuiltIn()
        {
            try
            {
                var asm = typeof(TunableDescriptions).Assembly;
                using (var stream = asm.GetManifestResourceStream(FileName))
                {
                    if (stream == null)
                    {
                        FtdApi.Log.Warning("Built-in value descriptions are missing from FtdModApi.dll; tooltips will show no descriptions for game values.");
                        return 0;
                    }
                    string json;
                    using (var reader = new StreamReader(stream)) json = reader.ReadToEnd();
                    int n = 0;
                    lock (_gate)
                    {
                        foreach (var p in ReadObject(json))
                        {
                            if (_texts.ContainsKey(p.Key)) continue;
                            _texts[p.Key] = new Text { Value = p.Value, Source = BuiltInSource, BuiltIn = true };
                            n++;
                        }
                    }
                    return n;
                }
            }
            catch (Exception e)
            {
                FtdApi.Log.Error("Built-in value descriptions could not be read.", e);
                return 0;
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> ReadObject(string json)
        {
            var root = JObject.Parse(json);
            foreach (var p in root.Properties())
            {
                if (p.Name.StartsWith("$", StringComparison.Ordinal) || p.Name.StartsWith("_", StringComparison.Ordinal)) continue; // comments
                if (p.Value.Type != JTokenType.String) continue;
                var text = ((string)p.Value)?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                yield return new KeyValuePair<string, string>(p.Name.Trim(), text);
            }
        }
    }
}
