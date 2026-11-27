using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrilliantSkies.Core.Timing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FtdModApi
{
    /// <summary>Modifier keys of a key combination.</summary>
    [Flags]
    public enum KeyModifiers
    {
        None = 0,
        Ctrl = 1,
        Shift = 2,
        Alt = 4
    }

    /// <summary>A key plus modifiers, e.g. Ctrl+Shift+K. <see cref="None"/> means unbound.</summary>
    public struct KeyCombo : IEquatable<KeyCombo>
    {
        public KeyCombo(KeyCode key, KeyModifiers modifiers = KeyModifiers.None)
        {
            Key = key;
            Modifiers = key == KeyCode.None ? KeyModifiers.None : modifiers;
        }

        public KeyCode Key { get; }

        public KeyModifiers Modifiers { get; }

        /// <summary>No key (unbound).</summary>
        public static KeyCombo None => new KeyCombo(KeyCode.None);

        public bool IsNone => Key == KeyCode.None;

        public override string ToString()
        {
            if (IsNone) return "None";
            var parts = new List<string>();
            if ((Modifiers & KeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
            if ((Modifiers & KeyModifiers.Shift) != 0) parts.Add("Shift");
            if ((Modifiers & KeyModifiers.Alt) != 0) parts.Add("Alt");
            parts.Add(Key.ToString());
            return string.Join("+", parts);
        }

        /// <summary>Reads "Ctrl+Shift+K", "F5", "Alt+Keypad1" or "None" (Unity KeyCode names; case does not matter).</summary>
        public static bool TryParse(string text, out KeyCombo combo)
        {
            combo = None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) return false;
            if (parts.Count == 1 && string.Equals(parts[0], "None", StringComparison.OrdinalIgnoreCase)) return true;
            var mods = KeyModifiers.None;
            for (int i = 0; i < parts.Count - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= KeyModifiers.Ctrl; break;
                    case "shift": mods |= KeyModifiers.Shift; break;
                    case "alt": mods |= KeyModifiers.Alt; break;
                    default: return false;
                }
            }
            if (!Enum.TryParse(parts[parts.Count - 1], true, out KeyCode key) || key == KeyCode.None
                || !Enum.IsDefined(typeof(KeyCode), key) || KeyBinds.IsModifier(key)) return false;
            combo = new KeyCombo(key, mods);
            return true;
        }

        public bool Equals(KeyCombo other) => Key == other.Key && Modifiers == other.Modifiers;

        public override bool Equals(object obj) => obj is KeyCombo other && Equals(other);

        public override int GetHashCode() => ((int)Key * 8) ^ (int)Modifiers;

        public static bool operator ==(KeyCombo a, KeyCombo b) => a.Equals(b);

        public static bool operator !=(KeyCombo a, KeyCombo b) => !a.Equals(b);
    }

    /// <summary>
    /// A keybind registered by a mod. The player can rebind it in the FtdModApi settings window (Keybinds tab); their
    /// choice is saved to Documents/From The Depths/FtdModApi.keybinds.json. React to it with <see cref="OnPressed"/>,
    /// <see cref="Events.Input.KeyPressed"/>, or by checking <see cref="IsHeld"/>.
    /// </summary>
    public sealed class KeyBind
    {
        internal KeyBind(string modId, string id, string name, KeyCombo defaultCombo, string description, bool whileTyping)
        {
            ModId = modId;
            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? id : name;
            Default = defaultCombo;
            Description = description ?? string.Empty;
            WorksWhileTyping = whileTyping;
        }

        public string ModId { get; }

        /// <summary>The id within the mod.</summary>
        public string Id { get; }

        /// <summary>"ModId.Id": the key in the keybinds file.</summary>
        public string FullId => ModId + "." + Id;

        /// <summary>The name shown to players.</summary>
        public string Name { get; }

        public string Description { get; }

        /// <summary>The mod's default combination.</summary>
        public KeyCombo Default { get; }

        /// <summary>The combination in use: the player's, or the default.</summary>
        public KeyCombo Current => KeyBinds.PlayerCombo(FullId) ?? Default;

        /// <summary>True when the player rebound (or unbound) it.</summary>
        public bool IsPlayerSet => KeyBinds.PlayerCombo(FullId).HasValue;

        /// <summary>
        /// False (the default): ignored while the player is typing in a text box (chat, a name field). True: always.
        /// </summary>
        public bool WorksWhileTyping { get; }

        /// <summary>True while the key and exactly its modifiers are held.</summary>
        public bool IsHeld
        {
            get
            {
                var c = Current;
                if (c.IsNone) return false;
                try
                {
                    return UnityEngine.Input.GetKey(c.Key) && KeyBinds.HeldModifiers() == c.Modifiers;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal bool WasDown;

        /// <summary>Runs your handler each time this key is pressed. Same as subscribing to KeyPressed and filtering.</summary>
        public Registration OnPressed(Action<KeyBindEvent> handler, int priority = EventPriority.Normal)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var me = this;
            return Events.Input.KeyPressed.Subscribe(ModId, e => { if (ReferenceEquals(e.Bind, me)) handler(e); }, priority, true, "key " + FullId);
        }

        /// <summary>Runs your handler each time this key is released (after a press that was reported; also when the player rebinds it while held).</summary>
        public Registration OnReleased(Action<KeyBindEvent> handler, int priority = EventPriority.Normal)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var me = this;
            return Events.Input.KeyReleased.Subscribe(ModId, e => { if (ReferenceEquals(e.Bind, me)) handler(e); }, priority, true, "key up " + FullId);
        }

        public override string ToString() => $"{FullId} ({Current})";
    }

    /// <summary>
    /// Mod keybinds: register them in your plugin's OnLoad, and the player can rebind them in the FtdModApi settings window.
    /// </summary>
    /// <example>
    /// <code>
    /// var boost = KeyBinds.Register("MyMod", "boost", "Engine boost", new KeyCombo(KeyCode.B, KeyModifiers.Ctrl), "Doubles engine power for 5 s.");
    /// boost.OnPressed(e => StartBoost());
    /// </code>
    /// </example>
    public static class KeyBinds
    {
        /// <summary>The file name, in the root of the game's user folder.</summary>
        public const string FileName = "FtdModApi.keybinds.json";

        private static readonly object _gate = new object();
        private static readonly List<KeyBind> _binds = new List<KeyBind>();
        private static readonly Dictionary<string, KeyCombo> _player = new Dictionary<string, KeyCombo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _unknown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _dirty;
        private static bool _polling;
        private static KeyCode[] _captureKeys;

        /// <summary>Full path of the keybinds file (null until the API has started).</summary>
        public static string FilePath { get; private set; }

        /// <summary>Every registered keybind, in registration order.</summary>
        public static IReadOnlyList<KeyBind> All
        {
            get { lock (_gate) return _binds.ToList(); }
        }

        /// <summary>
        /// Registers a keybind (or returns the one already registered with this mod and id). Use KeyCombo.None as the
        /// default to ship it unbound.
        /// </summary>
        public static KeyBind Register(string modId, string id, string name, KeyCombo defaultCombo, string description = null, bool worksWhileTyping = false)
        {
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A keybind id is required.", nameof(id));
            KeyBind bind;
            lock (_gate)
            {
                bind = _binds.FirstOrDefault(b => string.Equals(b.ModId, modId, StringComparison.OrdinalIgnoreCase) && string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
                if (bind != null) return bind;
                bind = new KeyBind(modId, id, name, defaultCombo, description, worksWhileTyping);
                _binds.Add(bind);
                if (_unknown.TryGetValue(bind.FullId, out var raw))
                {
                    _unknown.Remove(bind.FullId);
                    if (KeyCombo.TryParse(raw, out var combo)) _player[bind.FullId] = combo;
                }
            }
            InstallPolling();
            FtdApi.Log.Verbose(() => $"Keybind {bind.FullId} registered: {bind.Current}{(bind.IsPlayerSet ? " (player)" : "")}.");
            return bind;
        }

        /// <summary>The keybind with this "ModId.Id", or null.</summary>
        public static KeyBind Find(string fullId)
        {
            lock (_gate) return _binds.FirstOrDefault(b => string.Equals(b.FullId, fullId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Other mod keybinds using the same combination (none for an unbound one).</summary>
        public static IReadOnlyList<KeyBind> ConflictsWith(KeyBind bind)
        {
            if (bind == null) return new KeyBind[0];
            var c = bind.Current;
            if (c.IsNone) return new KeyBind[0];
            lock (_gate) return _binds.Where(b => !ReferenceEquals(b, bind) && b.Current == c).ToList();
        }

        internal static KeyCombo? PlayerCombo(string fullId)
        {
            lock (_gate) return _player.TryGetValue(fullId ?? string.Empty, out var c) ? c : (KeyCombo?)null;
        }

        /// <summary>Sets the player's combination for a keybind (null returns it to the mod's default). Saved by <see cref="Save"/>.</summary>
        internal static void SetPlayer(KeyBind bind, KeyCombo? combo)
        {
            if (bind == null) return;
            if (bind.WasDown && Events.Input.KeyReleased.HasSubscribers)
            {
                try
                {
                    Events.Input.KeyReleased.Raise(new KeyBindEvent(bind, bind.Current, false));
                }
                catch
                {
                }
            }
            lock (_gate)
            {
                if (combo.HasValue) _player[bind.FullId] = combo.Value;
                else _player.Remove(bind.FullId);
                _dirty = true;
            }
            bind.WasDown = false;
            FtdApi.Log.Verbose(() => $"Keybind {bind.FullId} set to {bind.Current}{(combo.HasValue ? " (player)" : " (default)")}.");
        }

        internal static KeyModifiers HeldModifiers()
        {
            var m = KeyModifiers.None;
            if (UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl)) m |= KeyModifiers.Ctrl;
            if (UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift)) m |= KeyModifiers.Shift;
            if (UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt) || UnityEngine.Input.GetKey(KeyCode.AltGr)) m |= KeyModifiers.Alt;
            return m;
        }

        internal static bool IsModifier(KeyCode k)
            => k == KeyCode.LeftControl || k == KeyCode.RightControl || k == KeyCode.LeftShift || k == KeyCode.RightShift
               || k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.AltGr
               || k == KeyCode.LeftCommand || k == KeyCode.RightCommand || k == KeyCode.LeftWindows || k == KeyCode.RightWindows;

        // ---- capture (settings window "Set" button) ------------------------------------------------------

        /// <summary>The keybind waiting for the player to press its new key, or null.</summary>
        internal static KeyBind Capturing { get; private set; }

        /// <summary>The frame a capture ended on (so the Esc that cancelled it doesn't also close the window).</summary>
        internal static int CaptureEndedFrame = -10;

        internal static void StartCapture(KeyBind bind)
        {
            InstallPolling();
            Capturing = bind;
        }

        internal static void CancelCapture()
        {
            Capturing = null;
            CaptureEndedFrame = Time.frameCount;
        }

        private static void PollCapture()
        {
            var bind = Capturing;
            if (bind == null) return;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                CancelCapture();
                return;
            }
            if (_captureKeys == null)
            {
                _captureKeys = ((KeyCode[])Enum.GetValues(typeof(KeyCode)))
                    .Where(k => k != KeyCode.None && k != KeyCode.Escape && !IsModifier(k) && (k < KeyCode.Mouse0 || k > KeyCode.Mouse6) && k < KeyCode.JoystickButton0)
                    .Distinct().ToArray();
            }
            foreach (var k in _captureKeys)
            {
                if (!UnityEngine.Input.GetKeyDown(k)) continue;
                SetPlayer(bind, new KeyCombo(k, HeldModifiers()));
                Capturing = null;
                CaptureEndedFrame = Time.frameCount;
                Save();
                return;
            }
        }

        // ---- polling -----------------------------------------------------------------------------------

        internal static void InstallPolling()
        {
            if (_polling) return;
            lock (_gate)
            {
                if (_polling) return;
                if (GameEvents.UpdateEvent == null) return;
                GameEvents.UpdateEvent.RegWithEvent(Poll);
                _polling = true;
            }
            FtdApi.Log.Verbose("Keybind polling installed.");
        }

        private static void Poll(ITimeStep step)
        {
            try
            {
                if (Capturing != null)
                {
                    PollCapture();
                    return; // the key being set must not also trigger its old action
                }
                List<KeyBind> binds;
                lock (_gate)
                {
                    if (_binds.Count == 0) return;
                    binds = _binds.ToList();
                }
                bool typing = IsTyping();
                KeyModifiers held = HeldModifiers();
                foreach (var b in binds)
                {
                    var c = b.Current;
                    if (c.IsNone)
                    {
                        b.WasDown = false;
                        continue;
                    }
                    if (b.WasDown)
                    {
                        if (!UnityEngine.Input.GetKey(c.Key))
                        {
                            b.WasDown = false;
                            if (Events.Input.KeyReleased.HasSubscribers) Events.Input.KeyReleased.Raise(new KeyBindEvent(b, c, false));
                        }
                        continue;
                    }
                    if (!UnityEngine.Input.GetKeyDown(c.Key) || held != c.Modifiers) continue;
                    if (typing && !b.WorksWhileTyping) continue;
                    b.WasDown = true;
                    if (Events.Input.KeyPressed.HasSubscribers) Events.Input.KeyPressed.Raise(new KeyBindEvent(b, c, true));
                }
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("keybind-poll", "Mod keybind check failed.", e);
            }
        }

        private static System.Reflection.FieldInfo _chatInstance;
        private static bool _chatLookedUp;

        // A text box has keyboard focus, or the multiplayer chat line is open.
        private static bool IsTyping()
        {
            if (GUIUtility.keyboardControl != 0) return true;
            try
            {
                if (!_chatLookedUp)
                {
                    _chatLookedUp = true;
                    _chatInstance = HarmonyLib.AccessTools.Field(typeof(ChatGUI), "_instance");
                }
                // Read the existing chat window only; don't create it.
                return _chatInstance?.GetValue(null) is ChatGUI chat && chat.IsTyping();
            }
            catch
            {
                return false;
            }
        }

        // ---- file --------------------------------------------------------------------------------------

        internal static void Load(string userFolder)
        {
            if (string.IsNullOrEmpty(userFolder)) return;
            FilePath = Path.Combine(userFolder, FileName);
            if (!File.Exists(FilePath)) return;
            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                FtdApi.Log.Error($"Could not read {FilePath}: {e.Message}. Player keybinds were not applied; the file is left as it is.");
                FilePath = null; // never overwrite a file we could not read
                return;
            }
            int count = 0;
            if (root["keys"] is JObject keys)
            {
                lock (_gate)
                {
                    foreach (var p in keys.Properties())
                    {
                        if (p.Value.Type != JTokenType.String) continue;
                        var raw = (string)p.Value;
                        var bind = _binds.FirstOrDefault(b => string.Equals(b.FullId, p.Name, StringComparison.OrdinalIgnoreCase));
                        if (bind == null)
                        {
                            _unknown[p.Name] = raw; // a mod that registers later, or is not installed now: keep it
                            count++;
                        }
                        else if (KeyCombo.TryParse(raw, out var combo))
                        {
                            _player[bind.FullId] = combo;
                            count++;
                        }
                        else
                        {
                            _unknown[p.Name] = raw; // keep it in the file
                            FtdApi.Log.Warning($"{FileName}: '{p.Name}' has an unknown key '{raw}'; using the mod's default.");
                        }
                    }
                    _dirty = false;
                }
            }
            FtdApi.Log.Verbose(() => $"Read {count} player keybind(s) from {FileName}.");
        }

        /// <summary>Writes the keybinds file if anything changed.</summary>
        internal static void Save()
        {
            Dictionary<string, string> snapshot;
            lock (_gate)
            {
                if (!_dirty || FilePath == null) return;
                snapshot = new Dictionary<string, string>(_unknown, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _player) snapshot[kv.Key] = kv.Value.ToString();
                _dirty = false;
            }
            try
            {
                var keys = new JObject();
                foreach (var kv in snapshot.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) keys[kv.Key] = kv.Value;
                var root = new JObject
                {
                    ["about"] = "Mod keybinds set in the FtdModApi settings window (Keybinds tab). Delete a line (or this file) to go back to the mod's default.",
                    ["version"] = 1,
                    ["keys"] = keys
                };
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, root.ToString(Formatting.Indented));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                lock (_gate) _dirty = true;
                FtdApi.Log.Error($"Could not save {FilePath}: {e.Message}");
            }
        }
    }

    public static partial class Events
    {
        /// <summary>Mod keybinds (see <see cref="KeyBinds"/>).</summary>
        public static class Input
        {
            private static readonly EventHook PollHook = new EventHook(Features.Keybinds, null, () =>
            {
                KeyBinds.InstallPolling();
                return null;
            });

            /// <summary>A mod keybind was pressed (exactly its modifiers held; not while typing unless the bind allows it). Local.</summary>
            public static EventChannel<KeyBindEvent> KeyPressed { get; } = new EventChannel<KeyBindEvent>(
                "Input.KeyPressed", EventSide.Local, "A mod keybind was pressed.", PollHook);

            /// <summary>A mod keybind that was reported as pressed was released. Local.</summary>
            public static EventChannel<KeyBindEvent> KeyReleased { get; } = new EventChannel<KeyBindEvent>(
                "Input.KeyReleased", EventSide.Local, "A mod keybind was released.", PollHook);
        }
    }

    /// <summary>A mod keybind was pressed or released.</summary>
    public sealed class KeyBindEvent : GameEvent
    {
        internal KeyBindEvent(KeyBind bind, KeyCombo combo, bool pressed)
        {
            Bind = bind;
            Combo = combo;
            Pressed = pressed;
        }

        public KeyBind Bind { get; }

        /// <summary>The combination that was used.</summary>
        public KeyCombo Combo { get; }

        /// <summary>True for KeyPressed, false for KeyReleased.</summary>
        public bool Pressed { get; }
    }
}
