using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BrilliantSkies.Ui.Consoles;
using BrilliantSkies.Ui.Consoles.Getters;
using BrilliantSkies.Ui.Consoles.Interpretters.Simple;
using BrilliantSkies.Ui.Consoles.Interpretters.Subjective;
using BrilliantSkies.Ui.Consoles.Interpretters.Subjective.Buttons;
using BrilliantSkies.Ui.Consoles.Interpretters.Subjective.Texts;
using BrilliantSkies.Ui.Consoles.Segments;
using BrilliantSkies.Ui.Layouts.DropDowns;
using BrilliantSkies.Ui.Tips;
using UiContent = BrilliantSkies.Ui.Consoles.Content;

namespace FtdModApi.Settings
{
    /// <summary>What the settings window is showing: one per open window.</summary>
    internal sealed class SettingsModel
    {
        public string GameGroup;
        public string ModGroup;
        public string SearchDraft = string.Empty;
        public string Search = string.Empty;
        public SettingsUi Ui;
    }

    /// <summary>One value's row in the table. Holds what the player is typing.</summary>
    internal sealed class ValueRow
    {
        public ValueRow(TunableInfo info)
        {
            Info = info;
            Draft = PlayerValues.TryGet(info.Key, out var v) ? FormatFor(info, v) : string.Empty;
        }

        public TunableInfo Info { get; }

        /// <summary>The text in the Player Value box.</summary>
        public string Draft { get; private set; }

        public bool DraftIsInvalid { get; private set; }

        public void Edit(string text)
        {
            Draft = text ?? string.Empty;
            var trimmed = Draft.Trim();
            if (trimmed.Length == 0)
            {
                DraftIsInvalid = false;
                PlayerValues.Set(Info.Key, null);
                return;
            }
            if (TryParse(Info, trimmed, out var value))
            {
                DraftIsInvalid = false;
                PlayerValues.Set(Info.Key, value);
            }
            else
            {
                DraftIsInvalid = true; // keep the last good value applied until the text parses
            }
        }

        public void Clear()
        {
            FtdApi.Log.Verbose(() => "Settings: Clear pressed for " + Info.Key + ".");
            Draft = string.Empty;
            DraftIsInvalid = false;
            PlayerValues.Set(Info.Key, null);
        }

        public string CurrentText()
        {
            var text = FormatFor(Info, Info.CurrentValue);
            if (PlayerValues.TryGet(Info.Key, out _)) return text + "  (player)";
            return Tunables.ChangesOf(Info.Key).Count > 0 ? text + "  (modded)" : text;
        }

        public string ToolTipText()
        {
            var lines = new List<string> { Info.Key };
            if (!string.IsNullOrEmpty(Info.Description)) lines.Add(Info.Description + (string.IsNullOrEmpty(Info.Units) ? string.Empty : $" ({Info.Units})"));
            lines.Add($"Type: {Info.Kind}. Owner: {Info.Owner}.");
            if (Info.Min.HasValue || Info.Max.HasValue)
                lines.Add($"Allowed range: {(Info.Min.HasValue ? Tunables.Format(Info.Min.Value) : "-")} to {(Info.Max.HasValue ? Tunables.Format(Info.Max.Value) : "-")}.");
            if (!string.IsNullOrEmpty(Info.Note)) lines.Add(Info.Note);
            var changes = Tunables.ChangesOf(Info.Key).Where(c => c.ModId != PlayerValues.Owner).ToList();
            lines.Add(changes.Count == 0 ? "No mod changes it." : "Changed by: " + string.Join("; ", changes.Select(c => c.ToString())));
            return string.Join("\n", lines);
        }

        internal static string FormatFor(TunableInfo info, double v)
            => info.Kind == TunableKind.Bool ? (v != 0 ? "true" : "false") : Tunables.Format(v);

        internal static bool TryParse(TunableInfo info, string text, out double value)
        {
            value = 0;
            if (info.Kind == TunableKind.Bool)
            {
                switch (text.ToLowerInvariant())
                {
                    case "true": case "on": case "yes": case "1": value = 1; return true;
                    case "false": case "off": case "no": case "0": value = 0; return true;
                    default: return false;
                }
            }
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            if (info.Kind == TunableKind.Int) value = Math.Round(value);
            return true;
        }
    }

    /// <summary>The FtdModApi settings window: every game and mod value with Default / Current / Player Value columns.</summary>
    internal sealed class SettingsUi : ConsoleUi<SettingsModel>
    {
        public SettingsUi(SettingsModel model)
            : base(model)
        {
            model.Ui = this;
        }

        protected override ConsoleWindow BuildInterface(string suggestedName = "")
        {
            var window = NewWindow(0, "FtdModApi settings", WindowSizing.GetCentralHuge());
            window.DisplayTextPrompt = false;
            window.SetMultipleTabs(
                new ValuesTab(window, _focus, ValuesTab.Kind.Game),
                new ValuesTab(window, _focus, ValuesTab.Kind.Mods),
                new ValuesTab(window, _focus, ValuesTab.Kind.Changed),
                new KeybindsTab(window, _focus),
                new AboutTab(window, _focus));
            return window;
        }

        public override void EscapePressed()
        {
            FtdApi.Log.Verbose(() => $"Settings window: Esc (capturing: {KeyBinds.Capturing != null}, capture ended frame {KeyBinds.CaptureEndedFrame}, frame {UnityEngine.Time.frameCount}).");
            // Esc while setting a key cancels that, not the window.
            if (KeyBinds.Capturing != null || KeyBinds.CaptureEndedFrame >= UnityEngine.Time.frameCount - 1)
            {
                if (KeyBinds.Capturing != null) KeyBinds.CancelCapture();
                return;
            }
            base.EscapePressed();
        }

        public override void OnDeactivateGui()
        {
            FtdApi.Log.Verbose("Settings window closed.");
            base.OnDeactivateGui();
            if (KeyBinds.Capturing != null) KeyBinds.CancelCapture();
            PlayerValues.Save();
            KeyBinds.Save();
        }
    }

    /// <summary>One tab of values: the game's, the mods', or everything that is changed.</summary>
    internal sealed class ValuesTab : SuperScreen<SettingsModel>
    {
        internal enum Kind
        {
            Game,
            Mods,
            Changed
        }

        private const int MaxRows = 250;
        private readonly Kind _kind;

        public ValuesTab(ConsoleWindow window, SettingsModel focus, Kind kind)
            : base(window, focus)
        {
            _kind = kind;
            switch (kind)
            {
                case Kind.Game:
                    Name = new UiContent("Game values", new ToolTip("The game's balance values (the *Constants classes, plus read-only values the API unlocked)."));
                    break;
                case Kind.Mods:
                    Name = new UiContent("Mod values", new ToolTip("Values registered by mods (their own systems)."));
                    break;
                default:
                    Name = new UiContent("Changed", new ToolTip("Every value a mod or you have changed."));
                    break;
            }
        }

        private IEnumerable<TunableInfo> Pool()
        {
            var all = Tunables.All;
            switch (_kind)
            {
                case Kind.Game: return all.Where(i => !i.IsModValue);
                case Kind.Mods: return all.Where(i => i.IsModValue);
                default: return all.Where(i => PlayerValues.TryGet(i.Key, out _) || Tunables.ChangesOf(i.Key).Count > 0);
            }
        }

        private string SelectedGroup
        {
            get => _kind == Kind.Game ? _focus.GameGroup : _focus.ModGroup;
            set
            {
                if (_kind == Kind.Game) _focus.GameGroup = value;
                else _focus.ModGroup = value;
            }
        }

        public override void Build()
        {
            var pool = Pool().ToList();
            var groups = pool.Select(i => i.Group).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            bool searching = !string.IsNullOrWhiteSpace(_focus.Search);

            var top = CreateStandardSegment();
            top.AddInterpretter(StringDisplay.Quick(
                "Type a number in Player Value to override every mod and the game. Clear it to hand control back. " +
                "Saved to " + (PlayerValues.FilePath ?? PlayerValues.FileName) + " when this window closes."));

            if (_kind != Kind.Changed && groups.Count > 0)
            {
                if (SelectedGroup == null || !groups.Contains(SelectedGroup, StringComparer.OrdinalIgnoreCase)) SelectedGroup = groups[0];
                var menu = new DropDownMenuAlt<string>();
                menu.SetItems(groups.Select(g => new DropDownMenuAltItem<string>
                {
                    ObjectForAction = g,
                    Name = g + "  (" + pool.Count(i => string.Equals(i.Group, g, StringComparison.OrdinalIgnoreCase)) + ")",
                    ToolTip = GroupTip(g)
                }).ToArray());
                top.AddInterpretter(new DropDown<SettingsModel, string>(_focus, menu,
                    (m, g) => string.Equals(g, SelectedGroup, StringComparison.OrdinalIgnoreCase),
                    (m, g) =>
                    {
                        SelectedGroup = g;
                        m.Search = string.Empty;
                        m.SearchDraft = string.Empty;
                        m.Ui?.TriggerScreenRebuild();
                    }));
            }

            var searchRow = CreateStandardHorizontalSegment();
            searchRow.AddInterpretter(TextInput<SettingsModel>.Quick(_focus, M.m<SettingsModel>(m => m.SearchDraft), "Search all groups",
                new ToolTip("Part of a value's name, e.g. 'Reload'. Press Search to apply."), (m, s) => m.SearchDraft = s ?? string.Empty));
            searchRow.AddInterpretter(SubjectiveButton<SettingsModel>.Quick(_focus, "Search", new ToolTip("Show matching values from every group in this tab."), m =>
            {
                m.Search = m.SearchDraft?.Trim() ?? string.Empty;
                m.Ui?.TriggerScreenRebuild();
            }));
            if (searching)
            {
                searchRow.AddInterpretter(SubjectiveButton<SettingsModel>.Quick(_focus, "Clear search", new ToolTip("Back to the group list."), m =>
                {
                    m.Search = string.Empty;
                    m.SearchDraft = string.Empty;
                    m.Ui?.TriggerScreenRebuild();
                }));
            }

            IEnumerable<TunableInfo> shown = pool;
            if (searching)
            {
                var term = _focus.Search.Trim();
                shown = pool.Where(i => i.Key.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                                        || (i.Description ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            else if (_kind != Kind.Changed)
            {
                shown = pool.Where(i => string.Equals(i.Group, SelectedGroup, StringComparison.OrdinalIgnoreCase));
            }

            var rows = shown.OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(i => new ValueRow(i)).ToList();
            int total = rows.Count;
            if (rows.Count > MaxRows) rows = rows.Take(MaxRows).ToList();

            var actions = CreateStandardHorizontalSegment();
            var keys = rows.Select(r => r.Info.Key).ToList();
            actions.AddInterpretter(SubjectiveButton<SettingsModel>.Quick(_focus, "Clear player values shown", new ToolTip("Removes your values for every row below."), m =>
            {
                PlayerValues.ClearAll(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase));
                PlayerValues.Save();
                m.Ui?.TriggerScreenRebuild();
            }));
            actions.AddInterpretter(StringDisplay.Quick(total > MaxRows
                ? $"Showing {MaxRows} of {total} values; search to narrow it down."
                : $"{total} value(s)."));

            if (rows.Count == 0)
            {
                CreateStandardSegment().AddInterpretter(StringDisplay.Quick(_kind == Kind.Changed ? "Nothing is changed: every value is the game's or the owning mod's default." : "No values match."));
                return;
            }

            var table = CreateTableSegment(5, rows.Count);
            table.eTableOrder = ScreenSegmentTable.TableOrder.Rows;
            table.SetColumnHeadings("Value", "Default", "Current", "Player Value", string.Empty);
            table.SetColumnFractionalWidths(0.36f, 0.13f, 0.17f, 0.22f, 0.12f);
            bool showGroup = searching || _kind == Kind.Changed;
            foreach (var row in rows)
            {
                var r = row;
                string label = showGroup ? r.Info.Key : r.Info.Name;
                table.AddInterpretter(SubjectiveDisplay<ValueRow>.Quick(r, M.m<ValueRow>(x => label), r.ToolTipText()));
                table.AddInterpretter(SubjectiveDisplay<ValueRow>.Quick(r, M.m<ValueRow>(x => ValueRow.FormatFor(x.Info, x.Info.DefaultValue))));
                table.AddInterpretter(SubjectiveDisplay<ValueRow>.Quick(r, M.m<ValueRow>(x => x.CurrentText())));
                table.AddInterpretter(TextInput<ValueRow>.Quick(r, M.m<ValueRow>(x => x.Draft), string.Empty,
                    new ToolTip(r.Info.Kind == TunableKind.Bool ? "true or false. Empty = no override." : "A number. Empty = no override."),
                    (x, s) => x.Edit(s)));
                table.AddInterpretter(SubjectiveButton<ValueRow>.Quick(r, "Clear", new ToolTip("Remove your value for this row."), x => x.Clear()));
            }
        }

        private static string GroupTip(string group)
        {
            var system = Tunables.FindSystem(group);
            return system != null ? $"Registered by {system.ModId}. {system.Description}" : "Game values (" + group + "Constants).";
        }
    }

    /// <summary>Every mod keybind: see and change them.</summary>
    internal sealed class KeybindsTab : SuperScreen<SettingsModel>
    {
        public KeybindsTab(ConsoleWindow window, SettingsModel focus)
            : base(window, focus)
        {
            Name = new UiContent("Keybinds", new ToolTip("Keys registered by mods. Click Set, then press the new key (with Ctrl/Shift/Alt if you like)."));
        }

        private static string CurrentText(KeyBind b)
        {
            if (ReferenceEquals(KeyBinds.Capturing, b)) return "Press a key... (Esc cancels)";
            var text = b.Current.ToString() + (b.IsPlayerSet ? "  (yours)" : string.Empty);
            var clash = KeyBinds.ConflictsWith(b);
            if (clash.Count > 0) text += "  also used by " + string.Join(", ", clash.Select(c => c.FullId));
            return text;
        }

        public override void Build()
        {
            var top = CreateStandardSegment();
            top.AddInterpretter(StringDisplay.Quick(
                "Click Set, then press the key you want (hold Ctrl, Shift or Alt with it for a combination); Esc cancels. " +
                "Mod keys don't fire while you type in a text box. They are not checked against the game's own keys, and the key " +
                "you press while setting one still does its normal job in the game. " +
                (KeyBinds.FilePath != null ? "Saved to " + KeyBinds.FilePath + "." : "Not saved: the keybinds file is unavailable this session.")));
            var binds = KeyBinds.All.OrderBy(b => b.ModId, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (binds.Count == 0)
            {
                CreateStandardSegment().AddInterpretter(StringDisplay.Quick("No mod has registered a keybind."));
                return;
            }
            var table = CreateTableSegment(7, binds.Count);
            table.eTableOrder = ScreenSegmentTable.TableOrder.Rows;
            table.SetColumnHeadings("Mod", "Action", "Default", "Current", string.Empty, string.Empty, string.Empty);
            table.SetColumnFractionalWidths(0.15f, 0.22f, 0.12f, 0.24f, 0.09f, 0.09f, 0.09f);
            foreach (var bind in binds)
            {
                var b = bind;
                string tip = string.IsNullOrEmpty(b.Description) ? b.FullId : b.FullId + "\n" + b.Description;
                table.AddInterpretter(SubjectiveDisplay<KeyBind>.Quick(b, M.m<KeyBind>(x => x.ModId), tip));
                table.AddInterpretter(SubjectiveDisplay<KeyBind>.Quick(b, M.m<KeyBind>(x => x.Name), tip));
                table.AddInterpretter(SubjectiveDisplay<KeyBind>.Quick(b, M.m<KeyBind>(x => x.Default.ToString())));
                table.AddInterpretter(SubjectiveDisplay<KeyBind>.Quick(b, M.m<KeyBind>(x => CurrentText(x))));
                table.AddInterpretter(SubjectiveButton<KeyBind>.Quick(b, "Set", new ToolTip("Then press the new key. Esc cancels."), x => KeyBinds.StartCapture(x)));
                table.AddInterpretter(SubjectiveButton<KeyBind>.Quick(b, "Reset", new ToolTip("Back to the mod's default."), x =>
                {
                    KeyBinds.SetPlayer(x, null);
                    KeyBinds.Save();
                }));
                table.AddInterpretter(SubjectiveButton<KeyBind>.Quick(b, "Unbind", new ToolTip("No key for this action."), x =>
                {
                    KeyBinds.SetPlayer(x, KeyCombo.None);
                    KeyBinds.Save();
                }));
            }
        }
    }

    /// <summary>How to use the menu, and what the API is doing.</summary>
    internal sealed class AboutTab : SuperScreen<SettingsModel>
    {
        public AboutTab(ConsoleWindow window, SettingsModel focus)
            : base(window, focus)
        {
            Name = new UiContent("About", new ToolTip("How the settings work."));
        }

        public override void Build()
        {
            var s = CreateStandardSegment();
            s.AddInterpretter(StringDisplay.Quick($"FtdModApi {FtdApi.Version} on From The Depths {FtdApi.GameVersion}."));
            s.AddInterpretter(StringDisplay.Quick("Open this window with " + SettingsMenu.HotkeyText + " (anywhere), or Settings > Mod settings in the main menu."));
            s.AddInterpretter(StringDisplay.Quick("Default: the game's value, or the value the owning mod declared."));
            s.AddInterpretter(StringDisplay.Quick("Current: the value in use right now, after every mod's change and yours."));
            s.AddInterpretter(StringDisplay.Quick("Player Value: yours. It overrides every mod and the game. Leave it empty to let the mods and the game decide."));
            s.AddInterpretter(StringDisplay.Quick("Some values are only read when something is built (for example shell stats when a design changes), so a change may need a reload of the vehicle to show."));
            s.AddInterpretter(StringDisplay.Quick($"Player values set: {PlayerValues.Count}. File: {PlayerValues.FilePath ?? "(not available)"}"));
            s.AddInterpretter(StringDisplay.Quick("Verbose logging: " + (FtdApi.VerboseLogging ? "on" : "off") + " (Mod values > FtdModApi > VerboseLogging)."));
            var problems = Features.All.Where(f => f.State != FeatureState.Available).ToList();
            s.AddInterpretter(StringDisplay.Quick(problems.Count == 0
                ? $"All {Features.All.Count} API features are working."
                : "Not fully working: " + string.Join(", ", problems.Select(f => f.Id + " (" + f.State + ")")) + ". See FtdModApi_status.txt."));
            s.AddInterpretter(StringDisplay.Quick("In multiplayer every player needs the same values."));
        }
    }
}
