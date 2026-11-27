using System;
using System.Collections.Generic;
using System.Reflection;
using FtdModApi.Patching;
using HarmonyLib;

namespace FtdModApi
{
    /// <summary>Game lifecycle events mods commonly need.</summary>
    public static class Lifecycle
    {
        private static readonly object _gate = new object();
        private static bool _mainMenuReached;
        private static readonly List<KeyValuePair<string, Action>> _pendingMainMenu = new List<KeyValuePair<string, Action>>();

        /// <summary>True once the main menu has been shown for the first time this session.</summary>
        public static bool MainMenuReached
        {
            get { lock (_gate) return _mainMenuReached; }
        }

        /// <summary>
        /// Raised every time the game enters the main menu. Feature id: <see cref="Features.MainMenuEvent"/>.
        /// </summary>
        public static ApiEvent<EventArgs> MainMenuEntered { get; } = new ApiEvent<EventArgs>(Features.MainMenuEvent);

        /// <summary>
        /// Runs <paramref name="action"/> once, the first time the main menu is shown (immediately if it already was).
        /// Some game systems, notably UI styles, are not ready while plugins load; code that patches or builds
        /// UI should wait for this. It replaces the "patch on MainMenuChangeSequence.StartFromChange" trick
        /// several existing mods copy.
        /// </summary>
        public static void WhenMainMenuReady(string modId, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            bool runNow;
            lock (_gate)
            {
                runNow = _mainMenuReached;
                if (!runNow) _pendingMainMenu.Add(new KeyValuePair<string, Action>(modId, action));
            }
            if (runNow)
            {
                RunSafely(modId, action);
            }
            else if (Features.Get(Features.MainMenuEvent)?.State == FeatureState.Unavailable)
            {
                FtdApi.GetLog(modId ?? "unknown mod").Warning(
                    "WhenMainMenuReady was called, but the main-menu hook is unavailable in this game build, so the callback will not run.");
            }
        }

        internal static IEnumerable<PatchSpec> Specs()
        {
            yield return new PatchSpec
            {
                Id = "MainMenuChangeSequence.StartFromChange: main menu event",
                FeatureId = Features.MainMenuEvent,
                Target = () =>
                {
                    var type = AccessTools.TypeByName("BrilliantSkies.Ftd.Modes.Changing.MainMenuChangeSequence");
                    return type?.GetMethod("StartFromChange", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                },
                Postfix = PatchSpec.Hook(typeof(Lifecycle), nameof(StartFromChange_Postfix))
            };
        }

        private static void StartFromChange_Postfix()
        {
            List<KeyValuePair<string, Action>> pending = null;
            lock (_gate)
            {
                if (!_mainMenuReached)
                {
                    _mainMenuReached = true;
                    pending = new List<KeyValuePair<string, Action>>(_pendingMainMenu);
                    _pendingMainMenu.Clear();
                }
            }
            if (pending != null)
            {
                FtdApi.Log.Verbose(() => $"Main menu reached for the first time; running {pending.Count} WhenMainMenuReady callback(s).");
                foreach (var p in pending) RunSafely(p.Key, p.Value);
            }
            MainMenuEntered.Raise(EventArgs.Empty);
        }

        private static void RunSafely(string modId, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                FtdApi.GetLog(modId ?? "unknown mod").Error("WhenMainMenuReady callback threw.", e);
            }
        }
    }
}
