using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assets.Scripts.Persistence;
using BrilliantSkies.Common.Modes;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Enumerations;
using BrilliantSkies.Core.Timing;
using BrilliantSkies.Ftd.Modes.Changing;
using BrilliantSkies.Ftd.Persistence.PlanetFileSaving;
using BrilliantSkies.Ftd.Planets.Instances.VictoryConditions;
using BrilliantSkies.FromTheDepths.Game;
using FtdModApi.Patching;
using Newtonsoft.Json.Linq;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>The game session: mode changes, saving and loading (with mod save data), pause, speed, victory, quit.</summary>
        public static class Session
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static readonly EventHook ModeHook = new EventHook(Features.EventSession, () => new[]
            {
                new PatchSpec
                {
                    Id = "ModeChanger.DoPrior: Session.ModeChanging",
                    FeatureId = Features.EventSession,
                    Target = () => EventHelpers.Declared(typeof(ModeChanger), "DoPrior"),
                    Prefix = PatchSpec.Hook(typeof(Session), nameof(DoPrior_Prefix))
                },
                new PatchSpec
                {
                    Id = "ModeChanger.DoStart: Session.ModeChanged",
                    FeatureId = Features.EventSession,
                    Target = () => EventHelpers.Declared(typeof(ModeChanger), "DoStart"),
                    Postfix = PatchSpec.Hook(typeof(Session), nameof(DoStart_Postfix))
                }
            });

            private static readonly EventHook SaveHook = new EventHook(Features.EventSession, () => new[]
            {
                new PatchSpec
                {
                    Id = "PlanetFileSaveLoad.SaveToSlot: Session.Saving/Saved + mod save data",
                    FeatureId = Features.EventSession,
                    Target = () => EventHelpers.Declared(typeof(PlanetFileSaveLoad), "SaveToSlot"),
                    Prefix = PatchSpec.Hook(typeof(Session), nameof(SaveToSlot_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Session), nameof(SaveToSlot_Postfix))
                },
                new PatchSpec
                {
                    Id = "PlanetFileSaveLoad.OpenSaveSlot: Session.Loaded + mod save data",
                    FeatureId = Features.EventSession,
                    Target = () => EventHelpers.Declared(typeof(PlanetFileSaveLoad), "OpenSaveSlot"),
                    Prefix = PatchSpec.Hook(typeof(Session), nameof(OpenSaveSlot_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Session), nameof(OpenSaveSlot_Postfix))
                }
            });

            private static readonly EventHook SpeedHook = new EventHook(Features.EventSession, () => new[]
            {
                new PatchSpec
                {
                    // Every pause, unpause and slow-motion change goes through this one private method.
                    Id = "GameSpeedManager.SetTimeScale: Session.Paused/Unpaused/TimeScaleChanged",
                    FeatureId = Features.EventSession,
                    Target = () => EventHelpers.Declared(typeof(GameSpeedManager), "SetTimeScale"),
                    Prefix = PatchSpec.Hook(typeof(Session), nameof(SetTimeScale_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Session), nameof(SetTimeScale_Postfix))
                }
            });

            private static readonly EventHook FinishedHook = new EventHook(Features.EventSession, () => new[]
            {
                new PatchSpec
                {
                    Id = "VictoryAssessment._Client_GameFinished: Session.GameFinished",
                    FeatureId = Features.EventSession,
                    Target = () => EventHelpers.Declared(typeof(VictoryAssessment), "_Client_GameFinished"),
                    Postfix = PatchSpec.Hook(typeof(Session), nameof(GameFinished_Postfix))
                }
            });

            private static readonly EventHook QuitHook = new EventHook(Features.EventSession, null, () =>
            {
                if (GameEvents.PreGameQuit == null) return "GameEvents.PreGameQuit is missing";
                GameEvents.PreGameQuit.RegWithEvent(() => { if (Quitting.HasSubscribers) Quitting.Raise(new GameQuittingEvent()); }, true);
                return null;
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>
            /// The game is about to change mode (designer, campaign, adventure, story, multiplayer, back to the main menu,
            /// loading a save). Raised before the old mode's craft are cleared, so they can still be read. Local.
            /// </summary>
            public static EventChannel<ModeChangeEvent> ModeChanging { get; } = new EventChannel<ModeChangeEvent>(
                "Session.ModeChanging", EventSide.Local, "The game is about to change mode.", ModeHook);

            /// <summary>The game finished changing mode; the new mode's world and craft are set up. Local.</summary>
            public static EventChannel<ModeChangeEvent> ModeChanged { get; } = new EventChannel<ModeChangeEvent>(
                "Session.ModeChanged", EventSide.Local, "The game changed mode.", ModeHook);

            /// <summary>
            /// A campaign or adventure game is about to be saved (manual or autosave). Store your mod's data for this save with
            /// <c>e.Write("MyMod", text)</c> or <see cref="ModSaveData.Set"/>; it is written next to the save. Host.
            /// </summary>
            public static EventChannel<GameSavingEvent> Saving { get; } = new EventChannel<GameSavingEvent>(
                "Session.Saving", EventSide.Host, "The game is about to be saved.", SaveHook);

            /// <summary>The game was saved (including mod save data). Host.</summary>
            public static EventChannel<GameSavedEvent> Saved { get; } = new EventChannel<GameSavedEvent>(
                "Session.Saved", EventSide.Host, "The game was saved.", SaveHook);

            /// <summary>
            /// A saved campaign or adventure game was loaded and its world is set up (raised before the player is placed in
            /// it). Read your mod's data with
            /// <c>e.Read("MyMod")</c> or <see cref="ModSaveData.Get"/>. Host.
            /// </summary>
            public static EventChannel<GameLoadedEvent> Loaded { get; } = new EventChannel<GameLoadedEvent>(
                "Session.Loaded", EventSide.Host, "A saved game was loaded.", SaveHook);

            /// <summary>
            /// The game was paused (pause key, or a menu that pauses). In practice single player only: multiplayer can't pause.
            /// Also: leaving a paused game for another mode raises <see cref="Unpaused"/> as the new mode starts. Local.
            /// </summary>
            public static EventChannel<GameSpeedEvent> Paused { get; } = new EventChannel<GameSpeedEvent>(
                "Session.Paused", EventSide.Local, "The game was paused.", SpeedHook);

            /// <summary>The game was unpaused. Local.</summary>
            public static EventChannel<GameSpeedEvent> Unpaused { get; } = new EventChannel<GameSpeedEvent>(
                "Session.Unpaused", EventSide.Local, "The game was unpaused.", SpeedHook);

            /// <summary>
            /// The game speed changed while running (slow motion, the game slowing itself under load). Changes smaller than
            /// 0.05 are not reported (the game smooths speed changes over many frames). All machines.
            /// </summary>
            public static EventChannel<GameSpeedEvent> TimeScaleChanged { get; } = new EventChannel<GameSpeedEvent>(
                "Session.TimeScaleChanged", EventSide.All, "The game speed changed.", SpeedHook);

            /// <summary>A scenario was won or lost (victory conditions met). All machines.</summary>
            public static EventChannel<GameFinishedEvent> GameFinished { get; } = new EventChannel<GameFinishedEvent>(
                "Session.GameFinished", EventSide.All, "A scenario was won or lost.", FinishedHook);

            /// <summary>The game is quitting to the desktop. Local.</summary>
            public static EventChannel<GameQuittingEvent> Quitting { get; } = new EventChannel<GameQuittingEvent>(
                "Session.Quitting", EventSide.Local, "The game is quitting.", QuitHook);

            // ---- patch bodies ------------------------------------------------------------------------------

            private static void DoPrior_Prefix(ModeChanger __instance)
            {
                // A new mode starts with no mod save data; loading a save fills it again (OpenSaveSlot).
                ModSaveData.Clear();
                Campaign.ResetGameState();
                if (!ModeChanging.HasSubscribers) return;
                ModeChanging.Raise(new ModeChangeEvent(__instance?.CurrentChangeSequence));
            }

            private static void DoStart_Postfix(ModeChanger __instance)
            {
                if (!ModeChanged.HasSubscribers) return;
                ModeChanged.Raise(new ModeChangeEvent(__instance?.CurrentChangeSequence));
            }

            private static void SaveToSlot_Prefix(PlanetFile file, int i, SaveFileType type, string saveName)
            {
                if (Saving.HasSubscribers) Saving.Raise(new GameSavingEvent(i, type == SaveFileType.Adventure, saveName));
            }

            private static void SaveToSlot_Postfix(PlanetFile file, int i, SaveFileType type, string saveName)
            {
                string folder = null;
                try
                {
                    folder = file?.GetSaveSlotFolder(i, type).FullName;
                }
                catch (Exception e)
                {
                    FtdApi.Log.Warning("Could not find the save folder for mod save data: " + e.Message);
                }
                bool written = folder != null && ModSaveData.WriteTo(folder);
                if (Saved.HasSubscribers) Saved.Raise(new GameSavedEvent(i, type == SaveFileType.Adventure, saveName, folder, written));
            }

            private static void OpenSaveSlot_Prefix(PlanetFile file, int i, SaveFileType type)
            {
                // Read the mod data before the game loads the world, so mods can use it while craft spawn.
                ModSaveData.Clear();
                try
                {
                    var folder = file?.GetSaveSlotFolder(i, type).FullName;
                    if (folder != null) ModSaveData.ReadFrom(folder);
                }
                catch (Exception e)
                {
                    FtdApi.Log.Warning("Could not read mod save data: " + e.Message);
                }
            }

            private static void OpenSaveSlot_Postfix(int i, SaveFileType type, bool __result)
            {
                if (!__result || !Loaded.HasSubscribers) return;
                Loaded.Raise(new GameLoadedEvent(i, type == SaveFileType.Adventure));
            }

            private static float _lastReportedScale = 1f;

            private static void SetTimeScale_Prefix(out float __state)
            {
                __state = UnityEngine.Time.timeScale;
            }

            private static void SetTimeScale_Postfix(float newTimeScale, float __state)
            {
                if (__state == newTimeScale) return; // called every frame with the same value
                bool wasPaused = __state <= 0f;
                bool isPaused = newTimeScale <= 0f;
                // Slow-motion smoothing and network interpolation nudge the scale every frame; report real changes only.
                if (!wasPaused && !isPaused)
                {
                    if (Math.Abs(newTimeScale - _lastReportedScale) < 0.05f) return;
                }
                _lastReportedScale = newTimeScale;
                if (!wasPaused && isPaused)
                {
                    if (Paused.HasSubscribers) Paused.Raise(new GameSpeedEvent(__state, newTimeScale));
                }
                else if (wasPaused && !isPaused)
                {
                    if (Unpaused.HasSubscribers) Unpaused.Raise(new GameSpeedEvent(__state, newTimeScale));
                }
                else if (!isPaused && TimeScaleChanged.HasSubscribers)
                {
                    TimeScaleChanged.Raise(new GameSpeedEvent(__state, newTimeScale));
                }
            }

            private static void GameFinished_Postfix(ConditionHeader result)
            {
                if (result == null || !GameFinished.HasSubscribers) return;
                GameFinished.Raise(new GameFinishedEvent(result));
            }
        }
    }

    /// <summary>The kind of game mode a change leads to.</summary>
    public enum GameModeKind
    {
        Other,
        MainMenu,
        Designer,
        /// <summary>A new campaign.</summary>
        NewCampaign,
        /// <summary>Loading a saved campaign or adventure.</summary>
        LoadSave,
        Adventure,
        Story,
        Multiplayer,
        CustomBattle,
        Tutorial,
        WorldEditor
    }

    public sealed class ModeChangeEvent : GameEvent
    {
        internal ModeChangeEvent(ModeChangeSequence sequence)
        {
            Sequence = sequence;
            Mode = KindOf(sequence);
        }

        /// <summary>The game's change-sequence object (its type says what is being loaded). May be null.</summary>
        public ModeChangeSequence Sequence { get; }

        /// <summary>What the change leads to.</summary>
        public GameModeKind Mode { get; }

        /// <summary>The game's name for the change, e.g. "DesignerLoadRequest".</summary>
        public string SequenceName => Sequence?.GetType().Name;

        private static GameModeKind KindOf(ModeChangeSequence s)
        {
            switch (s)
            {
                case null: return GameModeKind.Other;
                case MultiplayerReturnToLobbyChangeSequence _: return GameModeKind.Multiplayer;
                case MainMenuChangeSequence _: return GameModeKind.MainMenu;
                case DesignerLoadRequest _: return GameModeKind.Designer;
                case NewGameLaunchOptions _: return GameModeKind.NewCampaign;
                case LoadGameOptions _: return GameModeKind.LoadSave;
                case NewAdventureStuff _: return GameModeKind.Adventure;
                case StoryMissionChangeSequence _: return GameModeKind.Story;
                case MultiplayerChangeSequence _: return GameModeKind.Multiplayer;
                case CustomBattleModeChangeSequence _: return GameModeKind.CustomBattle;
                case TutorialChangeSequence _: return GameModeKind.Tutorial;
                case WorldEditorChangeSequence _: return GameModeKind.WorldEditor;
                default: return GameModeKind.Other;
            }
        }
    }

    public sealed class GameSavingEvent : GameEvent
    {
        internal GameSavingEvent(int slot, bool adventure, string name)
        {
            Slot = slot;
            IsAdventure = adventure;
            SaveName = name;
        }

        /// <summary>The save slot (29 is the autosave).</summary>
        public int Slot { get; }

        public bool IsAutosave => !IsAdventure && Slot == 29;

        public bool IsAdventure { get; }

        public string SaveName { get; }

        /// <summary>Stores your mod's data in this save (replaces what you stored before). Same as <see cref="ModSaveData.Set"/>.</summary>
        public void Write(string modId, string data) => ModSaveData.Set(modId, data);
    }

    public sealed class GameSavedEvent : GameEvent
    {
        internal GameSavedEvent(int slot, bool adventure, string name, string folder, bool modDataWritten)
        {
            Slot = slot;
            IsAdventure = adventure;
            SaveName = name;
            Folder = folder;
            ModDataWritten = modDataWritten;
        }

        public int Slot { get; }

        public bool IsAutosave => !IsAdventure && Slot == 29;

        public bool IsAdventure { get; }

        public string SaveName { get; }

        /// <summary>The save's folder on disk (the mod save data file is in it).</summary>
        public string Folder { get; }

        /// <summary>True when mod save data was written (false if no mod stored any, or writing failed).</summary>
        public bool ModDataWritten { get; }
    }

    public sealed class GameLoadedEvent : GameEvent
    {
        internal GameLoadedEvent(int slot, bool adventure)
        {
            Slot = slot;
            IsAdventure = adventure;
        }

        public int Slot { get; }

        public bool IsAdventure { get; }

        /// <summary>Your mod's data from this save, or null if it stored none. Same as <see cref="ModSaveData.Get"/>.</summary>
        public string Read(string modId) => ModSaveData.Get(modId);
    }

    public sealed class GameSpeedEvent : GameEvent
    {
        internal GameSpeedEvent(float oldScale, float newScale)
        {
            OldTimeScale = oldScale;
            NewTimeScale = newScale;
        }

        /// <summary>Unity time scale before (0 = paused, 1 = normal, below 1 = slow motion).</summary>
        public float OldTimeScale { get; }

        public float NewTimeScale { get; }
    }

    public sealed class GameFinishedEvent : GameEvent
    {
        internal GameFinishedEvent(ConditionHeader result)
        {
            Result = result;
        }

        /// <summary>The game's result object. Game object.</summary>
        public ConditionHeader Result { get; }

        /// <summary>True when the local player's faction won.</summary>
        public bool PlayerWon => Result != null && Result.IsVictoryFor(GAME_STATE.MyTeam);

        /// <summary>A one-line summary in the player's language, if the scenario has one.</summary>
        public string OneLiner
        {
            get
            {
                try
                {
                    return Result?.OneLiner?.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    public sealed class GameQuittingEvent : GameEvent
    {
    }

    /// <summary>
    /// Per-save data for mods. Each mod stores one string (JSON, CSV, anything) per save, written to
    /// <c>FtdModApi.moddata.json</c> in the save's folder when the game saves, and read back when that save is loaded.
    /// Cleared when the game changes mode, so a new game starts empty.
    /// </summary>
    /// <example>
    /// <code>
    /// Events.Session.Saving.Subscribe("MyMod", e => e.Write("MyMod", JsonConvert.SerializeObject(myState)));
    /// Events.Session.Loaded.Subscribe("MyMod", e => myState = Parse(e.Read("MyMod")));
    /// </code>
    /// </example>
    public static class ModSaveData
    {
        /// <summary>The file name, in each save's folder.</summary>
        public const string FileName = "FtdModApi.moddata.json";

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, string> _data = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Stores data for the current game (written at the next save). Null removes it.</summary>
        public static void Set(string modId, string data)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            lock (_gate)
            {
                if (data == null) _data.Remove(modId);
                else _data[modId] = data;
            }
        }

        /// <summary>The data stored for the current game (from the loaded save or set since), or null.</summary>
        public static string Get(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return null;
            lock (_gate) return _data.TryGetValue(modId, out var v) ? v : null;
        }

        /// <summary>Mods that have data in the current game.</summary>
        public static IReadOnlyList<string> Mods
        {
            get { lock (_gate) return _data.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); }
        }

        internal static void Clear()
        {
            lock (_gate) _data.Clear();
        }

        internal static bool WriteTo(string folder)
        {
            Dictionary<string, string> copy;
            lock (_gate) copy = new Dictionary<string, string>(_data);
            var path = Path.Combine(folder, FileName);
            try
            {
                if (copy.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path); // don't leave stale data from an earlier save in this slot
                    return false;
                }
                var mods = new JObject();
                foreach (var kv in copy.OrderBy(k => k.Key, StringComparer.Ordinal)) mods[kv.Key] = kv.Value;
                var root = new JObject
                {
                    ["about"] = "Mod data for this save, written by FtdModApi. Each mod stores one string.",
                    ["apiVersion"] = FtdApi.Version.ToString(),
                    ["mods"] = mods
                };
                Directory.CreateDirectory(folder);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                FtdApi.Log.Verbose(() => $"Mod save data written for {copy.Count} mod(s) to {path}.");
                return true;
            }
            catch (Exception e)
            {
                FtdApi.Log.Error("Could not write mod save data to " + path, e);
                return false;
            }
        }

        internal static void ReadFrom(string folder)
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return;
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                if (!(root["mods"] is JObject mods)) return;
                lock (_gate)
                {
                    foreach (var p in mods.Properties())
                        if (p.Value.Type == JTokenType.String) _data[p.Name] = (string)p.Value;
                }
                FtdApi.Log.Verbose(() => $"Mod save data read for {mods.Count} mod(s) from {path}.");
            }
            catch (Exception e)
            {
                FtdApi.Log.Error("Could not read mod save data from " + path, e);
            }
        }
    }
}
