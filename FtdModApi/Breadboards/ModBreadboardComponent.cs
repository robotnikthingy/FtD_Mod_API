using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BrilliantSkies.Blocks;
using BrilliantSkies.Blocks.BreadBoards;
using BrilliantSkies.Common.Circuits;
using BrilliantSkies.Common.Circuits.Ui;
using BrilliantSkies.Common.Circuits.Ui.Segments;
using BrilliantSkies.DataManagement.Attributes;
using BrilliantSkies.DataManagement.Vars;
using BrilliantSkies.Ftd.Missiles.Breadboard;
using BrilliantSkies.Ui.Consoles.Getters;
using BrilliantSkies.Ui.Consoles.Interpretters.Subjective.Choices;
using BrilliantSkies.Ui.Consoles.Interpretters.Subjective.Numbers;
using BrilliantSkies.Ui.Consoles.Interpretters.Subjective.Texts;
using BrilliantSkies.Ui.Tips;
using UnityEngine;
using CircuitComponent = BrilliantSkies.Common.Circuits.Component;

namespace FtdModApi
{
    /// <summary>
    /// Base class for a breadboard component added by a mod. Derive from it, add inputs, outputs and settings in the
    /// constructor, compute the outputs in <see cref="Compute"/>, and register it with
    /// <see cref="BreadboardComponents.Register{T}"/>.
    /// <para>
    /// Keep everything the player sets in settings (<see cref="AddNumberSetting"/> and friends): they are saved with the
    /// craft, copied with prefabs and synced in multiplayer. Don't add your own game Var properties. If a player loads
    /// a craft without your mod, the component is kept (shown as missing) with its settings and links, so it comes
    /// back when the mod does.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// public class Scale : ModBreadboardComponent
    /// {
    ///     private readonly NumberSetting _factor;
    ///     public Scale()
    ///     {
    ///         AddInput("Value");
    ///         AddOutput("Scaled");
    ///         _factor = AddNumberSetting("factor", "Factor", 2, -10, 10, 0.1);
    ///     }
    ///     protected override void Compute() => Out(0, InNumber(0) * _factor.Value);
    /// }
    /// // in OnLoad:
    /// BreadboardComponents.Register&lt;Scale&gt;("MyMod", "scale", "Scale", "Multiplies the input by a factor.");
    /// </code>
    /// </example>
    public abstract class ModBreadboardComponent : CircuitComponent
    {
        /// <summary>The save index of <see cref="ModData"/>; don't use it for anything else in a component.</summary>
        public const uint ModDataIndex = 7777u;

        internal const string KeyGuid = "@guid";
        internal const string KeyMod = "@mod";
        internal const string KeyId = "@id";
        internal const string KeyName = "@name";

        private readonly List<string[]> _inputs = new List<string[]>();
        private readonly List<string[]> _outputs = new List<string[]>();
        private readonly List<BreadboardSetting> _settings = new List<BreadboardSetting>();
        private readonly object _dataLock = new object();
        private string _parsedFrom;
        private Dictionary<string, string> _parsed = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Everything about this component that is saved: its identity and its settings. Managed by the API; don't
        /// change it yourself.
        /// </summary>
        [Variable(ModDataIndex, "Mod data", "Settings of a breadboard component added by a mod (FtdModApi).")]
        public VarString ModData { get; set; } = new VarString("", 9500);

        protected ModBreadboardComponent()
        {
            Info = BreadboardComponents.InfoFor(GetType());
            if (Info != null)
            {
                // The identity is the default value and always saved, so a craft keeps it without the mod.
                ModData.AlterDefault(Serialise(Identity(Info), null));
                ModData.SaveDefault = true;
            }
            Width.AlterDefault(110f);
        }

        /// <summary>What was registered for this component; null for a missing mod's component.</summary>
        public BreadboardComponentInfo Info { get; }

        // ---- the board it is on ------------------------------------------------------------------------

        /// <summary>The board this component is on. Game object.</summary>
        public Board Board { get; private set; }

        /// <summary>Which kind of breadboard it is on.</summary>
        public BreadboardKind Kind
        {
            get
            {
                var board = Board;
                if (board is MissileBreadboardBoard) return BreadboardKind.Missile;
                if (board is FtdAiBoard) return BreadboardKind.Ai;
                if (board is FtdBoard) return BreadboardKind.Vehicle;
                return BreadboardKind.Unknown;
            }
        }

        /// <summary>
        /// The craft the breadboard is on (for a missile breadboard, the craft that launched the missile). Null in the
        /// prefab editor and while the board is not on a craft.
        /// </summary>
        public MainConstruct Construct
        {
            get
            {
                var board = Board;
                if (board is FtdBoard ftd) return ftd.Construct;
                if (board is MissileBreadboardBoard missile)
                    return (missile.Missile?.LaunchPad?.MainConstruct as MainConstruct) ?? (missile.ParentBlock?.MainConstruct as MainConstruct);
                return null;
            }
        }

        /// <summary>The breadboard block (vehicle, AI or missile breadboard). Game object.</summary>
        public Block BreadboardBlock
        {
            get
            {
                var board = Board;
                if (board is FtdBoard ftd) return ftd.BreadboardBlock;
                if (board is MissileBreadboardBoard missile) return missile.ParentBlock;
                return null;
            }
        }

        /// <summary>
        /// On a missile breadboard, the missile being computed right now (one board runs once per missile), else null.
        /// Game object.
        /// </summary>
        public BrilliantSkies.Ftd.Missiles.Missile Missile => (Board as MissileBreadboardBoard)?.Missile;

        internal void Attach(Board board)
        {
            Board = board;
            EnsurePins();
        }

        // ---- inputs and outputs ------------------------------------------------------------------------

        /// <summary>Adds an input pin (call in the constructor). Returns its index.</summary>
        protected int AddInput(string name, string description = null)
        {
            _inputs.Add(new[] { name ?? "Input", description ?? string.Empty });
            CreateInput();
            FitHeight();
            return _inputs.Count - 1;
        }

        /// <summary>Adds an output pin (call in the constructor). Returns its index.</summary>
        protected int AddOutput(string name, string description = null)
        {
            _outputs.Add(new[] { name ?? "Output", description ?? string.Empty });
            CreateOutput();
            FitHeight();
            return _outputs.Count - 1;
        }

        private void FitHeight()
        {
            Height.AlterDefault(Mathf.Max(25f, 20f * Mathf.Max(_inputs.Count, _outputs.Count)));
        }

        // A craft saved with an older version of the mod may have fewer pins than the component now declares.
        internal void EnsurePins()
        {
            try
            {
                while (BInputs.Count < _inputs.Count) CreateInput();
                while (AOutputs.Count < _outputs.Count) CreateOutput();
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("bb-pins-" + GetType().FullName, "Could not add pins to breadboard component " + DisplayName + ".", e);
            }
        }

        public override int MinInputs => _inputs.Count;

        public override int MaxInputs => _inputs.Count;

        public override int MinOutputs => _outputs.Count;

        public override int MaxOutputs => _outputs.Count;

        /// <summary>True when input i has a link to it.</summary>
        protected bool IsConnected(int input)
            => input >= 0 && input < BInputs.Count && BInputs[input].OurOutput.IsLatched;

        /// <summary>Input i as a number (vectors give their length), or the fallback when nothing is linked.</summary>
        protected double InNumber(int input, double fallback = 0)
        {
            if (!IsConnected(input)) return fallback;
            var v = BInputs[input].ConvertToDouble();
            return double.IsNaN(v) ? fallback : v;
        }

        /// <summary>Input i as true/false: true when it is above 0. The fallback when nothing is linked.</summary>
        protected bool InBool(int input, bool fallback = false)
            => IsConnected(input) ? BInputs[input].GetBool(BoolLogic.Greater0) : fallback;

        /// <summary>Input i as a vector (a rotation gives its forward direction), or the fallback.</summary>
        protected Vector3 InVector(int input, Vector3 fallback = default)
            => IsConnected(input) ? BInputs[input].ConvertToVector3() : fallback;

        /// <summary>Input i as a rotation, or the fallback (identity when not given).</summary>
        protected Quaternion InRotation(int input, Quaternion? fallback = null)
            => IsConnected(input) ? BInputs[input].ConvertToQuaternion() : (fallback ?? Quaternion.identity);

        /// <summary>Input i as text, or the fallback.</summary>
        protected string InText(int input, string fallback = "")
            => IsConnected(input) ? BInputs[input].GetValueString() : fallback;

        /// <summary>Sets output i to a number.</summary>
        protected void Out(int output, double value) => AssignToOutput(output, double.IsNaN(value) ? 0.0 : value);

        /// <summary>Sets output i to 1 (true) or 0 (false).</summary>
        protected void Out(int output, bool value) => AssignToOutput(output, value ? 1.0 : 0.0);

        /// <summary>Sets output i to a vector.</summary>
        protected void Out(int output, Vector3 value) => AssignToOutput(output, value);

        /// <summary>Sets output i to a rotation.</summary>
        protected void Out(int output, Quaternion value) => AssignToOutput(output, value);

        /// <summary>Sets output i to text.</summary>
        protected void Out(int output, string value) => AssignToOutput(output, value ?? string.Empty);

        /// <summary>Sets output i to a whole number (the breadboard's 64-bit integer type).</summary>
        protected void OutInteger(int output, long value) => AssignToOutput(output, value);

        // ---- memory ------------------------------------------------------------------------------------

        /// <summary>
        /// Remembers a number until the next run, in slot 0-4. Use this instead of fields for anything that changes
        /// while running: a missile breadboard runs the same component once for every missile, and this memory is kept
        /// per missile. Not saved.
        /// </summary>
        protected void Remember(int slot, double value)
        {
            if (slot < 0 || slot > 4 || STATE == null) return;
            MyState.SetDouble((uint)slot, value);
        }

        /// <summary>A number stored with <see cref="Remember"/>, or the fallback if none was.</summary>
        protected double Recall(int slot, double fallback = 0)
        {
            if (slot < 0 || slot > 4 || STATE == null) return fallback;
            return MyState.GetDouble((uint)slot, fallback);
        }

        // ---- running -----------------------------------------------------------------------------------

        /// <summary>
        /// Reads the inputs and sets the outputs, every time the board runs. Runs on one of the game's worker threads,
        /// like the game's own components: keep it quick, reading the craft is fine, but don't create or destroy game
        /// objects or touch the UI (post that with MainThread.Post). An exception is logged once and the outputs keep
        /// their last values.
        /// </summary>
        protected abstract void Compute();

        public sealed override bool Run()
        {
            if (!AllInputsReadyOrNotLatched) return false;
            try
            {
                Compute();
            }
            catch (Exception e)
            {
                BreadboardComponents.ReportError(this, e);
            }
            return true;
        }

        // ---- how it looks ------------------------------------------------------------------------------

        internal virtual string DisplayName => Info?.Name ?? GetType().Name;

        /// <summary>Text shown on the component on the board. Defaults to its name.</summary>
        protected virtual string Label => DisplayName;

        public override string GetString()
        {
            try
            {
                return Label ?? string.Empty;
            }
            catch
            {
                return DisplayName;
            }
        }

        public override string Description => Info?.Description ?? string.Empty;

        public override string DescribeInput(int i)
            => i >= 0 && i < _inputs.Count ? Pin(_inputs[i]) : "Input " + (i + 1);

        public override string DescribeOutput(int i)
            => i >= 0 && i < _outputs.Count ? Pin(_outputs[i]) : "Output " + (i + 1);

        public override string DescribeOutputLink(int index)
            => index >= 0 && index < _outputs.Count ? _outputs[index][0] : "output";

        private static string Pin(string[] p) => string.IsNullOrEmpty(p[1]) ? p[0] : p[0] + ": " + p[1];

        public override void PopulateSegment(CircuitComponentEditorSegment segment, CircuitBoardDisplay displayer)
        {
            segment.Resize(1, 3 + _settings.Count);
            CreateWidthAdjuster(segment);
            CreateHeightAdjuster(segment);
            foreach (var setting in _settings)
            {
                try
                {
                    setting.AddTo(this, segment);
                }
                catch (Exception e)
                {
                    FtdApi.Log.ErrorOnce("bb-ui-" + GetType().FullName + "-" + setting.Key, "Could not show setting '" + setting.Label + "' of " + DisplayName + ".", e);
                }
            }
        }

        // ---- settings ----------------------------------------------------------------------------------

        /// <summary>The settings, in the order they were added.</summary>
        public IReadOnlyList<BreadboardSetting> Settings => _settings;

        /// <summary>Adds a number the player sets with a slider (call in the constructor).</summary>
        protected NumberSetting AddNumberSetting(string key, string label, double defaultValue, double min, double max, double step = 0.01, string description = null)
            => AddSetting(new NumberSetting(this, key, label, description, defaultValue, min, max, step));

        /// <summary>Adds an on/off switch (call in the constructor).</summary>
        protected ToggleSetting AddToggleSetting(string key, string label, bool defaultValue, string description = null)
            => AddSetting(new ToggleSetting(this, key, label, description, defaultValue));

        /// <summary>Adds a text box (call in the constructor).</summary>
        protected TextSetting AddTextSetting(string key, string label, string defaultValue = "", string description = null)
            => AddSetting(new TextSetting(this, key, label, description, defaultValue));

        private T AddSetting<T>(T setting) where T : BreadboardSetting
        {
            if (!ValidKey(setting.Key)) throw new ArgumentException("Setting keys must be letters, digits, '_', '-' or '.': '" + setting.Key + "'.");
            foreach (var s in _settings)
                if (s.Key == setting.Key) throw new ArgumentException("Setting '" + setting.Key + "' was added twice.");
            _settings.Add(setting);
            return setting;
        }

        private static bool ValidKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 64) return false;
            foreach (var c in key)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }

        internal string GetRaw(string key)
        {
            lock (_dataLock)
            {
                Refresh();
                return _parsed.TryGetValue(key, out var v) ? v : null;
            }
        }

        internal void SetRaw(string key, string value)
        {
            string text;
            lock (_dataLock)
            {
                Refresh();
                var copy = new Dictionary<string, string>(_parsed, StringComparer.Ordinal);
                if (value == null) copy.Remove(key);
                else copy[key] = value;
                text = Serialise(Info != null ? Identity(Info) : null, copy);
                if (Encoding.UTF8.GetByteCount(text) > 9500)
                {
                    FtdApi.Log.WarningOnce("bb-too-long-" + GetType().FullName, "Breadboard component " + DisplayName + ": its settings are too long to save (over 9500 bytes); the change was not kept.");
                    return;
                }
            }
            ModData.Us = text;
        }

        // Kept in sync with ModData.Us; re-read whenever it changed (loading, multiplayer sync, the player).
        private void Refresh()
        {
            var current = ModData.Us ?? string.Empty;
            if (ReferenceEquals(current, _parsedFrom)) return;
            var parsed = Parse(current);
            // A prefab finds its class by name, so another mod's class of the same name could get this data.
            if (Info != null && parsed.TryGetValue(KeyGuid, out var g) && Guid.TryParse(g, out var guid) && guid != Info.Guid)
            {
                FtdApi.Log.WarningOnce("bb-guid-" + GetType().FullName, $"Breadboard component {DisplayName}: its saved settings belong to another component ({g}); using defaults.");
                parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            _parsed = parsed;
            _parsedFrom = current;
        }

        internal string Stored(string key) => GetRaw(key);

        private static Dictionary<string, string> Identity(BreadboardComponentInfo info)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KeyGuid] = info.Guid.ToString("D"),
                [KeyMod] = info.ModId,
                [KeyId] = info.Id,
                [KeyName] = info.Name
            };
        }

        // The identity entries come first: the registered ones, or (for a missing mod's component) the saved ones.
        private static string Serialise(Dictionary<string, string> identity, Dictionary<string, string> values)
        {
            var sb = new StringBuilder();
            void Add(string k, string v)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(k).Append('=').Append(Escape(v));
            }
            if (identity != null)
                foreach (var kv in identity) Add(kv.Key, kv.Value);
            if (values != null)
            {
                if (identity == null)
                    foreach (var k in new[] { KeyGuid, KeyMod, KeyId, KeyName })
                        if (values.TryGetValue(k, out var v)) Add(k, v);
                foreach (var kv in values)
                    if (!kv.Key.StartsWith("@", StringComparison.Ordinal)) Add(kv.Key, kv.Value);
            }
            return sb.ToString();
        }

        internal static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return d;
            foreach (var line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[line.Substring(0, eq)] = Unescape(line.Substring(eq + 1));
            }
            return d;
        }

        private static string Escape(string v) => (v ?? string.Empty).Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");

        private static string Unescape(string v)
        {
            if (v.IndexOf('\\') < 0) return v;
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '\\' && i + 1 < v.Length)
                {
                    char n = v[++i];
                    sb.Append(n == 'n' ? '\n' : n == 'r' ? '\r' : n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public override Guid ComponentTypeId => Info?.Guid ?? Guid.Empty;
    }

    /// <summary>A setting of a mod breadboard component, saved with the craft.</summary>
    public abstract class BreadboardSetting
    {
        internal BreadboardSetting(ModBreadboardComponent owner, string key, string label, string description)
        {
            Owner = owner;
            Key = key;
            Label = string.IsNullOrEmpty(label) ? key : label;
            Description = description ?? string.Empty;
        }

        internal ModBreadboardComponent Owner { get; }

        /// <summary>The name it is saved under. Don't change it between versions, or players lose the value.</summary>
        public string Key { get; }

        public string Label { get; }

        public string Description { get; }

        internal abstract void AddTo(ModBreadboardComponent component, CircuitComponentEditorSegment segment);
    }

    /// <summary>A number setting (a slider in the component's editor).</summary>
    public sealed class NumberSetting : BreadboardSetting
    {
        internal NumberSetting(ModBreadboardComponent owner, string key, string label, string description, double defaultValue, double min, double max, double step)
            : base(owner, key, label, description)
        {
            if (max < min)
            {
                var t = min;
                min = max;
                max = t;
            }
            Min = min;
            Max = max;
            Step = step > 0 ? step : 0.01;
            Default = Clamp(defaultValue);
        }

        public double Min { get; }

        public double Max { get; }

        public double Step { get; }

        public double Default { get; }

        /// <summary>The value (kept between Min and Max). Setting it saves and syncs it; do that from the main thread.</summary>
        public double Value
        {
            get
            {
                var raw = Owner.GetRaw(Key);
                return raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Clamp(v) : Default;
            }
            set
            {
                var v = Clamp(value);
                Owner.SetRaw(Key, v == Default ? null : v.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        private double Clamp(double v) => double.IsNaN(v) ? Default : Math.Max(Min, Math.Min(Max, v));

        internal override void AddTo(ModBreadboardComponent component, CircuitComponentEditorSegment segment)
        {
            segment.AddInterpretter(SubjectiveFloatClampedWithBar<ModBreadboardComponent>.Quick(component, (float)Min, (float)Max, (float)Step,
                M.m((ModBreadboardComponent I) => (float)Value), Label.Replace("{", "{{").Replace("}", "}}") + " {0}", (I, f) => Value = f, new ToolTip(Description)));
        }
    }

    /// <summary>An on/off setting.</summary>
    public sealed class ToggleSetting : BreadboardSetting
    {
        internal ToggleSetting(ModBreadboardComponent owner, string key, string label, string description, bool defaultValue)
            : base(owner, key, label, description)
        {
            Default = defaultValue;
        }

        public bool Default { get; }

        /// <summary>The value. Setting it saves and syncs it; do that from the main thread.</summary>
        public bool Value
        {
            get
            {
                var raw = Owner.GetRaw(Key);
                return raw == null ? Default : raw == "1";
            }
            set => Owner.SetRaw(Key, value == Default ? null : value ? "1" : "0");
        }

        internal override void AddTo(ModBreadboardComponent component, CircuitComponentEditorSegment segment)
        {
            segment.AddInterpretter(SubjectiveToggle<ModBreadboardComponent>.Quick(component, Label, new ToolTip(Description), (I, b) => Value = b, I => Value));
        }
    }

    /// <summary>A text setting.</summary>
    public sealed class TextSetting : BreadboardSetting
    {
        internal TextSetting(ModBreadboardComponent owner, string key, string label, string description, string defaultValue)
            : base(owner, key, label, description)
        {
            Default = defaultValue ?? string.Empty;
        }

        public string Default { get; }

        /// <summary>The value. Setting it saves and syncs it; do that from the main thread.</summary>
        public string Value
        {
            get => Owner.GetRaw(Key) ?? Default;
            set => Owner.SetRaw(Key, value == null || value == Default ? null : value);
        }

        internal override void AddTo(ModBreadboardComponent component, CircuitComponentEditorSegment segment)
        {
            segment.AddInterpretter(TextInput<ModBreadboardComponent>.Quick(component, M.m((ModBreadboardComponent I) => Value), Label, new ToolTip(Description), (I, s) => Value = s));
        }
    }

    /// <summary>
    /// Stands in for a component whose mod is not installed (or no longer registers it). It keeps the component's
    /// place, links and settings so the craft can be saved without losing them; it outputs nothing.
    /// </summary>
    public sealed class MissingBreadboardComponent : ModBreadboardComponent
    {
        private Guid _guid;

        public MissingBreadboardComponent()
        {
        }

        internal MissingBreadboardComponent(Guid guid)
        {
            _guid = guid;
        }

        public override Guid ComponentTypeId
        {
            get
            {
                if (_guid != Guid.Empty) return _guid;
                // Made from a prefab: the id is in the saved data.
                var raw = Stored(KeyGuid);
                if (raw != null && Guid.TryParse(raw, out var g)) _guid = g;
                return _guid;
            }
        }

        /// <summary>The mod the component came from, if known.</summary>
        public string ModId => Stored(KeyMod);

        /// <summary>The component's name, if known.</summary>
        public string ComponentName => Stored(KeyName);

        internal override string DisplayName => ComponentName ?? "Unknown component";

        protected override string Label => "<color=#ff8080>Missing:</color> " + DisplayName;

        public override string Description => "This component comes from the mod '" + (ModId ?? "unknown") + "', which is not installed. It is kept, with its settings and links, and works again when the mod is.";

        // Keep exactly the pins that were saved, so the wiring can't be lost by editing it.
        public override int MinInputs => BInputs.Count;

        public override int MaxInputs => BInputs.Count;

        public override int MinOutputs => AOutputs.Count;

        public override int MaxOutputs => AOutputs.Count;

        public override string DescribeInput(int i) => "Input " + (i + 1) + " of a missing component";

        public override string DescribeOutput(int i) => "Output " + (i + 1) + " of a missing component (outputs nothing)";

        protected override void Compute()
        {
        }
    }
}
