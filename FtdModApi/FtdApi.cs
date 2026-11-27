using System;
using System.Collections.Generic;

namespace FtdModApi
{
    /// <summary>
    /// Entry point for mods built on the API.
    /// </summary>
    /// <remarks>
    /// To use the API from your mod:
    /// <list type="number">
    /// <item>Reference FtdModApi.dll (and 0Harmony.dll if you write your own patches). Do not ship either with your mod.</item>
    /// <item>Add "FtdModApi" to "depends" in your plugin.json, so FtD loads the API first.</item>
    /// <item>Register modifiers in your plugin's OnLoad, e.g. <c>DamageApi.ProjectileLaunch.Add("MyMod", ctx => ctx.KineticDamage *= 1.2f);</c></item>
    /// </list>
    /// </remarks>
    public static class FtdApi
    {
        /// <summary>API version. Minor bumps add features; a major bump means something was removed or changed.</summary>
        public static Version Version { get; } = new Version(0, 28, 1);

        /// <summary>Id the API uses for its own log lines and Harmony instance.</summary>
        public const string ModId = "FtdModApi";

        private static readonly Dictionary<string, ModLog> _logs = new Dictionary<string, ModLog>(StringComparer.Ordinal);

        /// <summary>True once the API has applied its patches (during its own OnLoad).</summary>
        public static bool IsReady { get; internal set; }

        /// <summary>The game version string the API is running under, e.g. "4.3.4".</summary>
        public static string GameVersion
        {
            get
            {
                try
                {
                    return BrilliantSkies.Core.Constants.Get.Game.VersionString;
                }
                catch
                {
                    return "unknown";
                }
            }
        }

        /// <summary>A logger that prefixes every line with your mod id.</summary>
        public static ModLog GetLog(string modId)
        {
            if (string.IsNullOrEmpty(modId)) modId = "unknown mod";
            lock (_logs)
            {
                if (!_logs.TryGetValue(modId, out var log))
                {
                    log = new ModLog(modId);
                    _logs.Add(modId, log);
                }
                return log;
            }
        }

        internal static ModLog Log => GetLog(ModId);

        /// <summary>True when verbose logging is on. Turn it on with "FtdModApi.VerboseLogging": true in any FtdModApi.tunables.json.</summary>
        public static bool VerboseLogging => ModLog.VerboseEnabled;

        /// <summary>
        /// Throws if the API is older than your mod needs. Call it first thing in your OnLoad:
        /// <c>FtdApi.Require(new Version(0, 1, 0), "MyMod");</c> FtD then lists your mod as failed with a clear reason.
        /// </summary>
        public static void Require(Version minimum, string modId)
        {
            if (minimum == null) throw new ArgumentNullException(nameof(minimum));
            if (Version < minimum || Version.Major != minimum.Major)
            {
                throw new InvalidOperationException(
                    $"{modId} needs FtdModApi {minimum.Major}.x at version {minimum} or later, but {Version} is installed.");
            }
        }
    }
}
