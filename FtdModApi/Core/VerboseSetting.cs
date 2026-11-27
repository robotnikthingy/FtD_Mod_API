using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FtdModApi.Core
{
    /// <summary>
    /// The "FtdModApi.VerboseLogging" setting: one switch for extra troubleshooting detail from the API and every
    /// mod that logs through it (<see cref="ModLog.Verbose(string)"/>). Turn it on with
    /// <c>"FtdModApi.VerboseLogging": true</c> in any enabled mod's FtdModApi.tunables.json (later: the settings menu).
    /// </summary>
    internal static class VerboseSetting
    {
        internal const string Key = "FtdModApi.VerboseLogging";

        /// <summary>The API's own settings, published like any mod system.</summary>
        internal static TunableSystem Settings { get; private set; }

        /// <summary>
        /// Reads the switch straight from the JSON files before the API patches anything, so the patching itself
        /// can be logged. The registered setting takes over once tunables are loaded.
        /// </summary>
        internal static void Peek(string modsRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(modsRoot) || !Directory.Exists(modsRoot)) return;
                foreach (var dir in Directory.GetDirectories(modsRoot))
                {
                    var file = Path.Combine(dir, Tunables.JsonFileName);
                    if (!File.Exists(file)) continue;
                    try
                    {
                        if (!BrilliantSkies.Modding.ModDisabler.CheckModEnabled(dir, out _)) continue;
                        var token = JObject.Parse(File.ReadAllText(file)).Properties()
                            .FirstOrDefault(p => string.Equals(p.Name, Key, StringComparison.OrdinalIgnoreCase))?.Value;
                        if (token == null) continue;
                        bool on = token.Type == JTokenType.Boolean ? token.Value<bool>()
                            : token is JObject o && o["set"] != null && o["set"].Type == JTokenType.Boolean && o["set"].Value<bool>();
                        if (on)
                        {
                            ModLog.VerboseEnabled = true;
                            FtdApi.Log.Info($"Verbose logging is on ({Key} in {Path.GetFileName(dir)}).");
                            return;
                        }
                    }
                    catch
                    {
                        // Bad files are reported properly when tunables load.
                    }
                }
            }
            catch
            {
                // Never block startup over a logging setting.
            }
        }

        /// <summary>Registers the setting; call after tunable discovery and before the JSON files are applied.</summary>
        internal static void Register()
        {
            Settings = Tunables.RegisterSystem(FtdApi.ModId, "FtdModApi", "Settings of the modding API itself.");
            if (Settings == null) return;
            var verbose = Settings.Bool("VerboseLogging", false, "Write extra troubleshooting detail to the log (all mods using the API).");
            verbose.Changed += v =>
            {
                bool on = ((TunableBool)v).Value;
                if (ModLog.VerboseEnabled == on) return;
                ModLog.VerboseEnabled = on;
                FtdApi.Log.Info("Verbose logging " + (on ? "on." : "off."));
            };

            var loopback = Settings.Bool("ModNetLoopback", false, "For testing mods alone: on the host or in single player, ModNet messages sent to clients are also delivered to this machine.");
            loopback.Changed += v =>
            {
                ModNet.LoopbackClients = ((TunableBool)v).Value;
                FtdApi.Log.Info("ModNet loopback " + (ModNet.LoopbackClients ? "on" : "off") + ".");
            };

            ApsGauge.RegisterTunable(Settings);
        }
    }
}
