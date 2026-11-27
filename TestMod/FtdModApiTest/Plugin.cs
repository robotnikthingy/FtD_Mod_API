using System;
using System.Linq;
using BrilliantSkies.Modding;
using FtdModApi;

namespace FtdModApiTest
{
    /// <summary>
    /// FtdModApi test mod. It checks every API feature it can by itself, watches every hook and event while you play,
    /// and writes what worked to Mods/FtdModApiTest/FtdModApiTest_report.txt. It changes no gameplay.
    /// </summary>
    public class Plugin : GamePlugin_PostLoad
    {
        internal const string ModId = "FtdModApiTest";
        internal static readonly Version TestModVersion = new Version(1, 0, 0);
        internal static readonly Version RequiredApi = new Version(0, 28, 1);

        internal static ModLog Log { get; private set; }
        internal static TunableSystem TestSystem { get; private set; }
        internal static TunableFloat TestValue { get; private set; }
        internal static TunableFloat TestLimited { get; private set; }

        private static int _seconds;
        private static bool _menuReady;

        public string name => ModId;

        public Version version => TestModVersion;

        public void OnLoad()
        {
            FtdApi.Require(RequiredApi, ModId);
            Log = FtdApi.GetLog(ModId);

            Report.Info("Setup", "Versions", $"FtdModApi {FtdApi.Version}, test mod {TestModVersion}, game {SafeGameVersion()}");

            // Values only the self-tests use (they put them back). Listed in the FtdModApi settings under "ApiTest".
            TestSystem = Tunables.RegisterSystem(ModId, "ApiTest", "Values used by the FtdModApi test mod's self-tests. Changing them does nothing.");
            TestValue = TestSystem.Float("Value", 10f, description: "Self-test value (default 10).");
            TestLimited = TestSystem.Float("Limited", 5f, min: 0f, max: 10f, description: "Self-test value kept between 0 and 10.");

            TestParts.RegisterAll();
            LiveChecks.WatchLifecycle();
            LiveChecks.WatchModifierHooks();
            LiveChecks.WatchSaveData();
            LiveChecks.WatchModNet();
            LiveChecks.WatchAllEvents();

            // The report key, rebindable in the FtdModApi settings (Keybinds tab).
            Report.Run("Setup", "Keybind registered", () =>
            {
                var key = KeyBinds.Register(ModId, "report", "Write the API test report",
                    new KeyCombo(UnityEngine.KeyCode.T, KeyModifiers.Ctrl | KeyModifiers.Shift),
                    "Writes Mods/FtdModApiTest/FtdModApiTest_report.txt now and shows the summary.");
                key.OnPressed(e => WriteReport(true, true));
                Report.Expect("Setup", "Keybind pressed", "press " + key.Current);
                key.OnPressed(e => Report.Seen("Setup", "Keybind pressed", e.Combo.ToString()));
                return key.Current.ToString();
            });

            Lifecycle.WhenMainMenuReady(ModId, () =>
            {
                CheckFeatures();
                SelfTests.RunAll();
                LiveChecks.CheckEventAvailability();
                _menuReady = true;
                WriteReport(true, false);
                var (pass, fail, waiting) = Report.Counts();
                if (fail > 0) Log.Hud($"FtdModApi test: {fail} check(s) FAILED. See Mods/FtdModApiTest/{Report.FileName}.");
                else Log.Info($"FtdModApi test: {pass} passed, {waiting} wait for something to happen in game. Report: Mods/FtdModApiTest/{Report.FileName}.");
            });

            Events.Ticks.EveryFiveSeconds.Subscribe(ModId, e =>
            {
                LiveChecks.ModNetTick();
                _seconds += 5;
                if (_menuReady && _seconds % 30 == 0)
                {
                    CheckFeatures();
                    LiveChecks.CheckEventAvailability();
                    WriteReport(false, false);
                }
            });
            Events.Session.Quitting.Subscribe(ModId, e => WriteReport(true, false), EventPriority.Monitor);

            Log.Info($"Loaded: watching the API. Press Ctrl+Shift+T in game to write the report ({Report.FileName}).");
        }

        public void OnSave()
        {
        }

        public bool AfterAllPluginsLoaded() => true;

        private static void WriteReport(bool force, bool tellPlayer)
        {
            CheckFeatures();
            bool written = Report.Write(force);
            var summary = Report.Summary();
            if (tellPlayer) Log.Hud($"FtdModApi test: {summary}. {(written ? "Report written" : "Report not written (see log)")}: Mods/FtdModApiTest/{Report.FileName}");
            else if (written) Log.Verbose(() => "Report written: " + summary);
        }

        /// <summary>Every API feature should be working; Pending ones are waiting for the game (e.g. a session).</summary>
        private static void CheckFeatures()
        {
            const string group = "API features";
            foreach (var f in Features.All)
            {
                var name = f.Id;
                var detail = string.IsNullOrEmpty(f.Detail) ? f.Description : f.Description + " (" + f.Detail + ")";
                switch (f.State)
                {
                    case FeatureState.Available:
                        Report.Pass(group, name, f.Description);
                        break;
                    case FeatureState.Pending:
                        Report.Expect(group, name, "starts when a game is loaded: " + f.Description);
                        break;
                    default:
                        Report.Fail(group, name, f.State + ": " + detail);
                        break;
                }
            }
        }

        private static string SafeGameVersion()
        {
            try
            {
                return FtdApi.GameVersion;
            }
            catch
            {
                return "?";
            }
        }
    }
}
