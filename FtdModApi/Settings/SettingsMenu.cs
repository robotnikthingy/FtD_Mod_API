using System;
using System.Collections.Generic;
using System.Reflection;
using BrilliantSkies.Core.Timing;
using BrilliantSkies.Ftd.Modes.MainMenu.Ui;
using BrilliantSkies.Ui.Consoles.Interpretters.Subjective.Buttons;
using BrilliantSkies.Ui.Consoles.Styles;
using FtdModApi.Patching;
using HarmonyLib;
using UnityEngine;

namespace FtdModApi.Settings
{
    /// <summary>
    /// Opens the settings window: a hotkey (Ctrl+Shift+M, no patching) and a "Mod settings" button on the main
    /// menu's Settings tab (one optional patch). Feature id: <see cref="Features.SettingsMenu"/>.
    /// </summary>
    public static class SettingsMenu
    {
        /// <summary>The hotkey, as shown to players.</summary>
        public const string HotkeyText = "Ctrl+Shift+M";

        private static SettingsUi _ui;
        private static bool _hotkeyInstalled;

        /// <summary>Opens the settings window (or brings it back if it is already open).</summary>
        public static void Open()
        {
            try
            {
                if (_ui == null) _ui = new SettingsUi(new SettingsModel());
                _ui.ActivateGui(BrilliantSkies.Ui.Displayer.GuiActivateType.Stack);
                FtdApi.Log.Verbose("Settings window opened.");
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("settings-open", "Could not open the settings window.", e);
            }
        }

        /// <summary>Closes the settings window if it is open (saving player values).</summary>
        public static void Close()
        {
            try
            {
                if (_ui != null && _ui.MenuActive) _ui.DeactivateGui();
                PlayerValues.Save();
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("settings-close", "Could not close the settings window.", e);
            }
        }

        /// <summary>Registers the hotkey check on the game's per-frame update (no patching needed).</summary>
        internal static void InstallHotkey()
        {
            if (_hotkeyInstalled) return;
            try
            {
                GameEvents.UpdateEvent.RegWithEvent(OnUpdate);
                _hotkeyInstalled = true;
                FtdApi.Log.Verbose($"Settings hotkey {HotkeyText} installed.");
            }
            catch (Exception e)
            {
                Features.SetState(Features.SettingsMenu, FeatureState.Unavailable, "could not hook the per-frame update: " + e.Message);
                FtdApi.Log.Error("Could not install the settings hotkey.", e);
            }
        }

        private static void OnUpdate(ITimeStep step)
        {
            try
            {
                if (!Input.GetKeyDown(KeyCode.M)) return;
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (!ctrl || !shift) return;
                if (_ui != null && _ui.MenuActive) Close();
                else Open();
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("settings-hotkey", "The settings hotkey failed.", e);
            }
        }

        // ------------------------------------------------------------------------------------------
        // Main menu button
        // ------------------------------------------------------------------------------------------

        private static MethodInfo _buttonHelper;

        // Prefix on MainMenuAbstractTab.Done(bool): on the Settings tab, add our button before the tab is finished
        // (so it sits with the other buttons, above the tip of the day).
        private static void Done_Prefix(MainMenuAbstractTab __instance)
        {
            if (!(__instance is MainMenuSettingsTab)) return;
            try
            {
                var button = __instance.B("Mod settings",
                    $"FtdModApi: see and change every game and mod value (also {HotkeyText} anywhere).",
                    R_Icons.MenuOptions.Get(),
                    menu => Open());
                if (_buttonHelper == null)
                {
                    _buttonHelper = AccessTools.Method(typeof(MainMenuAbstractTab), "Button")?.MakeGenericMethod(button.GetType());
                }
                _buttonHelper?.Invoke(__instance, new object[] { button });
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("settings-menu-button", "Could not add the Mod settings button to the main menu.", e);
            }
        }

        internal static IEnumerable<PatchSpec> Specs()
        {
            yield return new PatchSpec
            {
                Id = "MainMenuAbstractTab.Done: Mod settings button on the main menu",
                FeatureId = Features.SettingsMenu,
                Required = false,
                Target = () => PatchSpec.Method(typeof(MainMenuAbstractTab), "Done", typeof(bool)),
                Prefix = PatchSpec.Hook(typeof(SettingsMenu), nameof(Done_Prefix)),
                Verify = () => AccessTools.Method(typeof(MainMenuAbstractTab), "Button") != null ? null : "the main menu's button helper was not found"
            };
            yield return new PatchSpec
            {
                // Ctrl+M and Ctrl+J type a newline, which the multiplayer chat reads as Enter: pressing the settings
                // hotkey (or a mod keybind on those keys) opened the chat behind the window and it took the keyboard.
                Id = "ChatGUI.CheckForInput: Ctrl+M / Ctrl+J don't open the chat",
                FeatureId = Features.SettingsMenu,
                Required = false,
                Target = () => EventHelpers.Declared(typeof(ChatGUI), "CheckForInput"),
                Prefix = PatchSpec.Hook(typeof(SettingsMenu), nameof(ChatCheckForInput_Prefix))
            };
        }

        private static bool ChatCheckForInput_Prefix()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return true;
            if ((e.character == '\n' || e.character == '\r') && (e.control || e.command || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return false;
            return true;
        }
    }
}
