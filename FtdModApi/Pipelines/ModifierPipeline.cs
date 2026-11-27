using System;
using System.Linq;
using System.Threading;

namespace FtdModApi
{
    /// <summary>
    /// Standard priorities for modifiers. Lower runs first. Within the same priority, modifiers run in
    /// order of mod id, then registration order, so the result does not depend on mod load order.
    /// Any int works; these are conventions so independent mods compose predictably.
    /// </summary>
    public static class Priority
    {
        /// <summary>Flat additions/subtractions (e.g. +50 damage).</summary>
        public const int Add = 100;
        /// <summary>Percentage scaling (e.g. x1.2 damage). Multipliers commute, so they stack cleanly.</summary>
        public const int Multiply = 200;
        /// <summary>Replace the value outright. The last override wins, so use sparingly.</summary>
        public const int Override = 300;
        /// <summary>Clamp to limits after everything else (e.g. never more than 10k per block).</summary>
        public const int Clamp = 400;
        /// <summary>Read-only observers that want the final value. Should not change anything.</summary>
        public const int Observe = 1000;
    }

    /// <summary>Returned when you register a modifier or event handler. Dispose it to unregister.</summary>
    public sealed class Registration : IDisposable
    {
        private Action _remove;

        internal Registration(Action remove)
        {
            _remove = remove;
        }

        public bool IsActive => _remove != null;

        public void Dispose()
        {
            Interlocked.Exchange(ref _remove, null)?.Invoke();
        }
    }

    /// <summary>
    /// Ordered list of callbacks with per-callback fault isolation. Shared by modifier pipelines and events.
    /// Reads are lock-free (copy-on-write array), because damage hooks run on the game's worker threads.
    /// </summary>
    public abstract class CallbackList<TArg>
    {
        /// <summary>A callback that throws this many times is switched off so it cannot keep breaking the game.</summary>
        public const int MaxFailures = 5;

        private sealed class Entry
        {
            public string ModId;
            public string Name;
            public int Priority;
            public long Sequence;
            public Action<TArg> Callback;
            public int Failures;
            public volatile bool Disabled;
        }

        private static readonly Entry[] Empty = new Entry[0];
        private static long _sequence;

        private readonly object _writeLock = new object();
        private Entry[] _entries = Empty;

        protected CallbackList(string id)
        {
            Id = id;
        }

        /// <summary>Name used in logs, e.g. "Damage.Block".</summary>
        public string Id { get; }

        /// <summary>True when nothing is registered. Hooks check this first so unused features cost almost nothing.</summary>
        public bool IsEmpty => Volatile.Read(ref _entries).Length == 0;

        /// <summary>Number of registered callbacks (including any switched off after repeated failures).</summary>
        public int Count => Volatile.Read(ref _entries).Length;

        protected Registration AddEntry(string modId, Action<TArg> callback, int priority, string name)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required so problems can be traced to the right mod.", nameof(modId));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var entry = new Entry
            {
                ModId = modId,
                Name = name ?? callback.Method.Name,
                Priority = priority,
                Sequence = Interlocked.Increment(ref _sequence),
                Callback = callback
            };

            lock (_writeLock)
            {
                Volatile.Write(ref _entries, _entries.Concat(new[] { entry })
                    .OrderBy(e => e.Priority)
                    .ThenBy(e => e.ModId, StringComparer.Ordinal)
                    .ThenBy(e => e.Sequence)
                    .ToArray());
            }

            FtdApi.GetLog(modId).Verbose(() => $"Added '{entry.Name}' to {Id} at priority {priority} ({Count} registered).");
            return new Registration(() =>
            {
                lock (_writeLock)
                {
                    Volatile.Write(ref _entries, _entries.Where(e => !ReferenceEquals(e, entry)).ToArray());
                }
                FtdApi.GetLog(modId).Verbose(() => $"Removed '{entry.Name}' from {Id}.");
            });
        }

        /// <summary>Removes every callback registered by a mod.</summary>
        public void RemoveAll(string modId)
        {
            lock (_writeLock)
            {
                Volatile.Write(ref _entries, _entries.Where(e => !string.Equals(e.ModId, modId, StringComparison.Ordinal)).ToArray());
            }
        }

        private int _ranOnce;

        protected void Invoke(TArg arg)
        {
            var entries = Volatile.Read(ref _entries);
            if (_ranOnce == 0 && ModLog.VerboseEnabled && Interlocked.Exchange(ref _ranOnce, 1) == 0)
            {
                FtdApi.Log.Verbose(() => $"{Id} ran for the first time with {entries.Length} callback(s): "
                                         + string.Join(", ", entries.Select(e => $"{e.ModId}/{e.Name}@{e.Priority}")));
            }
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e.Disabled) continue;
                try
                {
                    e.Callback(arg);
                }
                catch (Exception ex)
                {
                    OnFailure(e, ex);
                }
            }
        }

        private void OnFailure(Entry e, Exception ex)
        {
            int failures = Interlocked.Increment(ref e.Failures);
            var log = FtdApi.GetLog(e.ModId);
            if (failures == 1)
            {
                log.Error($"'{e.Name}' on {Id} threw. It will be switched off after {MaxFailures} failures.", ex);
            }
            if (failures >= MaxFailures && !e.Disabled)
            {
                e.Disabled = true;
                log.Error($"'{e.Name}' on {Id} failed {failures} times and has been switched off for this session.");
            }
        }
    }

    /// <summary>
    /// A chain of modifiers that each get to change a mutable context object, in priority order.
    /// This is how the API lets several mods change the same game value without overwriting each other.
    /// </summary>
    /// <example>
    /// <code>
    /// DamageApi.Block.Add("MyMod", ctx => { if (ctx.DamageType == ApiDamageType.Explosive) ctx.Damage *= 1.25f; });
    /// </code>
    /// </example>
    public sealed class ModifierPipeline<TContext> : CallbackList<TContext> where TContext : class
    {
        internal ModifierPipeline(string id) : base(id)
        {
        }

        /// <summary>Registers a modifier. Keep it fast: some pipelines run thousands of times per second, on worker threads.</summary>
        /// <param name="modId">Your mod's id (used in logs and for <see cref="CallbackList{TArg}.RemoveAll"/>).</param>
        /// <param name="modifier">Changes the context in place.</param>
        /// <param name="priority">When to run relative to other modifiers; see <see cref="Priority"/>.</param>
        /// <param name="name">Optional label for logs. Defaults to the method name.</param>
        public Registration Add(string modId, Action<TContext> modifier, int priority = Priority.Multiply, string name = null)
            => AddEntry(modId, modifier, priority, name);

        /// <summary>
        /// Runs every modifier on the context, in priority order. The API runs its own pipelines; call this for pipelines
        /// your mod published with <see cref="TunableSystem.CreatePipeline{TContext}"/>.
        /// </summary>
        public void Run(TContext context) => Invoke(context);
    }

    /// <summary>
    /// A notification that something happened. Handlers cannot change the outcome; use a
    /// <see cref="ModifierPipeline{TContext}"/> for that.
    /// </summary>
    public sealed class ApiEvent<TArgs> : CallbackList<TArgs>
    {
        internal ApiEvent(string id) : base(id)
        {
        }

        /// <summary>Subscribes a handler. Handlers run in priority order like modifiers.</summary>
        public Registration Subscribe(string modId, Action<TArgs> handler, int priority = 0, string name = null)
            => AddEntry(modId, handler, priority, name);

        /// <summary>Notifies every handler. The API raises its own events; call this for events your mod published.</summary>
        public void Raise(TArgs args) => Invoke(args);
    }
}
