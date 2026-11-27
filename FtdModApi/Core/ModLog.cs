using System;
using System.Collections.Generic;
using BrilliantSkies.Core.Logger;

namespace FtdModApi
{
    /// <summary>
    /// Prefixed logger for one mod. Writes to the normal FtD log (Player.log / the in-game console).
    /// Get one with <see cref="FtdApi.GetLog"/>. The game's log file leaves out a line repeated within 0.1 s of game
    /// time and writes any one line at most 51 times; lines written through this logger get " (repeat N)" added
    /// instead of being left out.
    /// </summary>
    public sealed class ModLog
    {
        private readonly string _prefix;
        private readonly HashSet<string> _onceKeys = new HashSet<string>();

        internal ModLog(string modId)
        {
            ModId = modId;
            _prefix = "[" + modId + "] ";
        }

        /// <summary>The id this logger prefixes every line with.</summary>
        public string ModId { get; }

        private static volatile bool _verbose;

        /// <summary>
        /// True when verbose logging is on (the "FtdModApi.VerboseLogging" setting). One switch for the API and
        /// every mod that logs through it.
        /// </summary>
        public static bool VerboseEnabled
        {
            get => _verbose;
            internal set => _verbose = value;
        }

        /// <summary>Same as <see cref="VerboseEnabled"/>; check it before building expensive messages.</summary>
        public bool IsVerbose => _verbose;

        /// <summary>
        /// Extra detail for troubleshooting, written only when verbose logging is on. Lines are tagged "[verbose]".
        /// </summary>
        public void Verbose(string message)
        {
            if (_verbose) Write(LogPriority.Info, "[verbose] " + message, null, LogOptions.None);
        }

        /// <summary>Verbose line whose text is only built when verbose logging is on (use on hot paths).</summary>
        public void Verbose(Func<string> message)
        {
            if (!_verbose || message == null) return;
            string text;
            try
            {
                text = message();
            }
            catch (Exception e)
            {
                text = "(could not build verbose message: " + e.Message + ")";
            }
            Write(LogPriority.Info, "[verbose] " + text, null, LogOptions.None);
        }

        /// <summary>Verbose line written only the first time <paramref name="key"/> is seen.</summary>
        public void VerboseOnce(string key, Func<string> message)
        {
            if (!_verbose) return;
            lock (_onceKeys)
            {
                if (!_onceKeys.Add("verbose:" + key)) return;
            }
            Verbose(message);
        }

        public void Info(string message) => Write(LogPriority.Info, message, null, LogOptions.None);

        public void Warning(string message) => Write(LogPriority.Warning, message, null, LogOptions.None);

        public void Error(string message) => Write(LogPriority.Error, message, null, LogOptions.None);

        public void Error(string message, Exception e) => Write(LogPriority.Error, message, e, LogOptions.None);

        /// <summary>Shows the message on the in-game HUD as well as writing it to the log.</summary>
        public void Hud(string message) => Write(LogPriority.Info, message, null, LogOptions.Hud);

        /// <summary>
        /// Logs a warning only the first time <paramref name="key"/> is seen. Use this on hot paths
        /// (damage, per-frame code) so a recurring problem does not flood the log.
        /// </summary>
        public void WarningOnce(string key, string message)
        {
            lock (_onceKeys)
            {
                if (!_onceKeys.Add(key)) return;
            }
            Warning(message);
        }

        /// <summary>Error variant of <see cref="WarningOnce"/>.</summary>
        public void ErrorOnce(string key, string message, Exception e = null)
        {
            lock (_onceKeys)
            {
                if (!_onceKeys.Add(key)) return;
            }
            Error(message, e);
        }

        // The game's log file drops a line identical to one written less than 0.1 s of game time ago (the clock
        // stops while paused), and writes any one line at most 51 times per session. This copies that rule so a
        // line the game would drop gets " (repeat N)" added and is written anyway.
        private const float GameRepeatWindow = 0.1f;
        private const int GameRepeatLimit = 50;
        private const int RepeatTrackLimit = 20000;

        private sealed class RepeatState
        {
            public float Time;
            public int Shown;
            public int Total;
        }

        private static readonly Dictionary<string, RepeatState> _repeats = new Dictionary<string, RepeatState>(StringComparer.Ordinal);

        private static string AvoidRepeatFilter(LogPriority priority, string text, Exception e, LogOptions options)
        {
            float now;
            try
            {
                now = BrilliantSkies.Core.Timing.GameTimer.Instance.TimeCache;
            }
            catch
            {
                return text;
            }
            var key = (int)priority + "|" + (int)options + "|" + text + (e != null ? "|" + e : string.Empty);
            lock (_repeats)
            {
                if (!_repeats.TryGetValue(key, out var s))
                {
                    if (_repeats.Count >= RepeatTrackLimit) _repeats.Clear();
                    _repeats[key] = new RepeatState { Time = now, Total = 1 };
                    return text;
                }
                s.Total++;
                bool gameWritesIt = now - s.Time >= GameRepeatWindow && s.Shown < GameRepeatLimit;
                s.Time = now;
                if (gameWritesIt)
                {
                    s.Shown++;
                    return text;
                }
                return text + " (repeat " + s.Total + ")";
            }
        }

        /// <summary>Outside the game (the patch checker tool): lines are kept here instead of going to the game's log.</summary>
        internal static volatile bool Offline;

        internal static readonly List<string> OfflineLines = new List<string>();

        private void Write(LogPriority priority, string message, Exception e, LogOptions options)
        {
            if (Offline)
            {
                lock (OfflineLines) OfflineLines.Add($"{priority}: {_prefix}{message}{(e != null ? " " + e.GetType().Name + ": " + e.Message : "")}");
                return;
            }
            try
            {
                message = AvoidRepeatFilter(priority, _prefix + message, e, options).Substring(_prefix.Length);
                if (e == null)
                    AdvLogger.Log(priority, _prefix + message, options);
                else
                    AdvLogger.Log(priority, _prefix + message, e, options);
            }
            catch
            {
                // Logging must never take the game down (the logger may not be ready during early boot).
                Console.WriteLine(_prefix + message + (e != null ? " " + e : string.Empty));
            }
        }
    }
}
