using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace FtdModApi
{
    /// <summary>
    /// A group of values owned by one mod, e.g. a "Bulldozers" system with BaseDamage and DesignSpeedModifier.
    /// Create it with <see cref="Tunables.RegisterSystem"/>, then declare values with <see cref="Float"/>,
    /// <see cref="Int"/> and <see cref="Bool"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var dozers = Tunables.RegisterSystem("BulldozerMod", "Bulldozers", "Bulldozer blades and drive.");
    /// var baseDamage = dozers.Float("BaseDamage", 500f, min: 0f, description: "Damage per blade hit.", units: "damage");
    /// // later, wherever it is needed (cheap, thread-safe, always the current value):
    /// float damage = baseDamage.Value;
    /// </code>
    /// Other mods change it like any game value: <c>Tunables.Multiply("OtherMod", "Bulldozers.BaseDamage", 1.5)</c>,
    /// or in FtdModApi.tunables.json: <c>"Bulldozers.BaseDamage": { "multiply": 1.5 }</c>.
    /// </example>
    public sealed class TunableSystem
    {
        private static readonly Regex NamePattern = new Regex("^[A-Za-z_][A-Za-z0-9_]*$");

        private readonly object _gate = new object();
        private readonly List<TunableValue> _values = new List<TunableValue>();
        private readonly List<string> _pipelines = new List<string>();

        internal TunableSystem(string modId, string name, string description)
        {
            ModId = modId;
            Name = name;
            Description = description;
        }

        /// <summary>The mod that registered the system.</summary>
        public string ModId { get; }

        /// <summary>The system's name: the first part of every key, e.g. "Bulldozers".</summary>
        public string Name { get; }

        public string Description { get; }

        /// <summary>The values declared so far.</summary>
        public IReadOnlyList<TunableValue> Values
        {
            get { lock (_gate) return _values.ToList(); }
        }

        internal IReadOnlyList<string> Pipelines
        {
            get { lock (_gate) return _pipelines.ToList(); }
        }

        internal static bool IsValidName(string name) => !string.IsNullOrEmpty(name) && NamePattern.IsMatch(name);

        /// <summary>Declares a decimal value. Min and max clamp the final value after every mod's change.</summary>
        public TunableFloat Float(string name, float defaultValue, float? min = null, float? max = null, string description = null, string units = null)
            => Declare(new TunableFloat(this, name, defaultValue), min, max, description, units);

        /// <summary>Declares a whole-number value (changes are rounded).</summary>
        public TunableInt Int(string name, int defaultValue, int? min = null, int? max = null, string description = null, string units = null)
            => Declare(new TunableInt(this, name, defaultValue), min, max, description, units);

        /// <summary>Declares an on/off value (other mods can only Set it).</summary>
        public TunableBool Bool(string name, bool defaultValue, string description = null)
            => Declare(new TunableBool(this, name, defaultValue), null, null, description, null);

        private T Declare<T>(T value, double? min, double? max, string description, string units) where T : TunableValue
        {
            var log = FtdApi.GetLog(ModId);
            if (!IsValidName(value.Name))
            {
                log.Error($"{Name}: value name '{value.Name}' must use letters, digits and '_' only. It keeps its default and cannot be changed.");
                return value;
            }
            if (min.HasValue && max.HasValue && min.Value > max.Value)
            {
                log.Warning($"{value.Key}: min is above max; limits ignored.");
                min = max = null;
            }
            if (!Tunables.RegisterValue(value, min, max, description, units))
            {
                log.Error($"{value.Key} is declared twice. The second declaration keeps its default and cannot be changed.");
                return value;
            }
            lock (_gate) _values.Add(value);
            return value;
        }

        /// <summary>
        /// Publishes a hook other mods can add modifiers to, with the same priority and fault-isolation rules as the
        /// API's own hooks. Your own code runs it with <c>pipeline.Run(context)</c> wherever the hook belongs.
        /// </summary>
        /// <example>
        /// <code>
        /// public static readonly ModifierPipeline&lt;DozerHit&gt; Hit = dozers.CreatePipeline&lt;DozerHit&gt;("Hit");
        /// // in your bulldozer code: Hit.Run(context);
        /// </code>
        /// </example>
        public ModifierPipeline<TContext> CreatePipeline<TContext>(string name) where TContext : class
        {
            if (!IsValidName(name)) throw new ArgumentException("Use letters, digits and '_' only.", nameof(name));
            lock (_gate) _pipelines.Add(Name + "." + name);
            return new ModifierPipeline<TContext>(Name + "." + name);
        }

        /// <summary>Publishes an event other mods can subscribe to. Your own code raises it with <c>evt.Raise(args)</c>.</summary>
        public ApiEvent<TArgs> CreateEvent<TArgs>(string name)
        {
            if (!IsValidName(name)) throw new ArgumentException("Use letters, digits and '_' only.", nameof(name));
            lock (_gate) _pipelines.Add(Name + "." + name + " (event)");
            return new ApiEvent<TArgs>(Name + "." + name);
        }

        public override string ToString() => Name + " (" + ModId + ")";
    }

    /// <summary>One value in a <see cref="TunableSystem"/>. Reading it is cheap and safe from any thread.</summary>
    public abstract class TunableValue
    {
        private long _bits;

        internal TunableValue(TunableSystem system, string name, TunableKind kind, double defaultValue)
        {
            System = system;
            Name = name ?? string.Empty;
            Kind = kind;
            DefaultValue = defaultValue;
            _bits = BitConverter.DoubleToInt64Bits(defaultValue);
        }

        public TunableSystem System { get; }

        public string Name { get; }

        /// <summary>"System.Name", the key other mods and JSON files use.</summary>
        public string Key => System.Name + "." + Name;

        public TunableKind Kind { get; }

        /// <summary>The value the owning mod declared.</summary>
        public double DefaultValue { get; }

        /// <summary>The current value as a double (bools are 0 or 1).</summary>
        public double AsDouble => Raw;

        /// <summary>Full description, defaults and who changed it.</summary>
        public TunableInfo Info => Tunables.Find(Key);

        /// <summary>
        /// Raised (on the thread that made the change) when another mod or the player changes the value. Useful when
        /// your mod caches something computed from it.
        /// </summary>
        public event Action<TunableValue> Changed;

        internal double Raw => BitConverter.Int64BitsToDouble(Volatile.Read(ref _bits));

        internal bool SetRaw(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            return Interlocked.Exchange(ref _bits, bits) != bits;
        }

        internal void RaiseChanged()
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (Action<TunableValue> h in handlers.GetInvocationList())
            {
                try
                {
                    h(this);
                }
                catch (Exception e)
                {
                    FtdApi.GetLog(System.ModId).Error($"A Changed handler for {Key} threw.", e);
                }
            }
        }

        public override string ToString() => Key + " = " + Raw;
    }

    /// <summary>A decimal value in a <see cref="TunableSystem"/>.</summary>
    public sealed class TunableFloat : TunableValue
    {
        internal TunableFloat(TunableSystem system, string name, float defaultValue)
            : base(system, name, TunableKind.Float, defaultValue)
        {
        }

        /// <summary>The current value.</summary>
        public float Value => (float)Raw;

        public static implicit operator float(TunableFloat v) => v?.Value ?? 0f;
    }

    /// <summary>A whole-number value in a <see cref="TunableSystem"/>.</summary>
    public sealed class TunableInt : TunableValue
    {
        internal TunableInt(TunableSystem system, string name, int defaultValue)
            : base(system, name, TunableKind.Int, defaultValue)
        {
        }

        /// <summary>The current value.</summary>
        public int Value => (int)Raw;

        public static implicit operator int(TunableInt v) => v?.Value ?? 0;
    }

    /// <summary>An on/off value in a <see cref="TunableSystem"/>.</summary>
    public sealed class TunableBool : TunableValue
    {
        internal TunableBool(TunableSystem system, string name, bool defaultValue)
            : base(system, name, TunableKind.Bool, defaultValue ? 1 : 0)
        {
        }

        /// <summary>The current value.</summary>
        public bool Value => Raw != 0;

        public static implicit operator bool(TunableBool v) => v != null && v.Value;
    }
}
