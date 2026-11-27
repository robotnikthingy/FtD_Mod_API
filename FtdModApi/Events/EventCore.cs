using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Timing;
using FtdModApi.Patching;

namespace FtdModApi
{
    /// <summary>
    /// Handler priorities for game events, in the order handlers run (lowest first), like Spigot's EventPriority.
    /// A later handler can undo an earlier one's change, so <see cref="Highest"/> has the final say and
    /// <see cref="Monitor"/> only watches the result. Any int works; these are conventions.
    /// </summary>
    public static class EventPriority
    {
        public const int Lowest = -200;
        public const int Low = -100;
        public const int Normal = 0;
        public const int High = 100;
        public const int Highest = 200;

        /// <summary>
        /// Runs last and must not change anything: cancelling at this priority is ignored (with a warning).
        /// Use it to log or react to the final outcome.
        /// </summary>
        public const int Monitor = 1000;
    }

    /// <summary>Which machines raise an event in multiplayer.</summary>
    public enum EventSide
    {
        /// <summary>Only the host (or the single-player game). Cancellable events are almost always host-only.</summary>
        Host,

        /// <summary>Every machine: the host and each client (for example, every client spawns its own copy of a craft).</summary>
        All,

        /// <summary>Only the machine of the player who did it (UI, build mode, input).</summary>
        Local
    }

    /// <summary>Which thread handlers run on.</summary>
    public enum EventThread
    {
        /// <summary>The game's main thread (safe to touch Unity objects and game state).</summary>
        Main,

        /// <summary>
        /// Whatever thread the game is on, often one of its worker threads (AI weapon control, projectile maths). Used for
        /// cancellable events that must be answered immediately. Handlers must be fast and thread-safe, and must not touch
        /// Unity objects.
        /// </summary>
        Any
    }

    /// <summary>An event that can be cancelled (Pre events, named "...ing": BlockPlacing, ConstructScrapping).</summary>
    public interface ICancellable
    {
        /// <summary>True when a handler cancelled the action. Handlers can also set it back to false.</summary>
        bool IsCancelled { get; set; }
    }

    /// <summary>Base class of every API game event.</summary>
    public abstract class GameEvent
    {
        internal bool InMonitorPhase;

        /// <summary>True on the host or in single player (false on a multiplayer client).</summary>
        public static bool IsHost => !Net.IsClient;
    }

    /// <summary>
    /// Base class of events raised before the game acts. Call <see cref="Cancel"/> to stop the action.
    /// After a cancel, no matching "...ed" event is raised.
    /// </summary>
    public abstract class CancellableEvent : GameEvent, ICancellable
    {
        private static readonly ConcurrentDictionary<Type, bool> _monitorWarned = new ConcurrentDictionary<Type, bool>();
        private bool _cancelled;

        public bool IsCancelled
        {
            get => _cancelled;
            set
            {
                if (InMonitorPhase)
                {
                    if (_monitorWarned.TryAdd(GetType(), true))
                        FtdApi.Log.Warning($"A Monitor-priority handler tried to change the cancel state of {GetType().Name}; Monitor handlers only observe, so it was ignored.");
                    return;
                }
                _cancelled = value;
            }
        }

        /// <summary>Stops the action. Same as setting <see cref="IsCancelled"/> to true.</summary>
        public void Cancel() => IsCancelled = true;
    }

    /// <summary>
    /// One kind of game event that mods can subscribe to, e.g. <c>Events.Blocks.Placed</c>.
    /// The game patch behind it is applied the first time any mod subscribes, so events nobody uses cost nothing.
    /// </summary>
    /// <example>
    /// <code>
    /// Events.Blocks.Placing.Subscribe("MyMod", e =>
    /// {
    ///     if (e.ItemName == "Lead Block") e.Cancel();
    /// });
    /// </code>
    /// </example>
    public sealed class EventChannel<TEvent> : IEventChannelInfo where TEvent : GameEvent
    {
        private readonly ApiEvent<TEvent> _handlers;
        private readonly EventHook[] _hooks;
        private int _raisedOnce;

        internal EventChannel(string id, EventSide side, string description, params EventHook[] hooks)
            : this(id, side, EventThread.Main, description, hooks)
        {
        }

        internal EventChannel(string id, EventSide side, EventThread thread, string description, params EventHook[] hooks)
        {
            Id = id;
            Side = side;
            Thread = thread;
            Description = description;
            _hooks = hooks ?? new EventHook[0];
            _handlers = new ApiEvent<TEvent>(id);
            EventRegistry.Add(this);
        }

        /// <summary>Stable id, e.g. "Blocks.Placed".</summary>
        public string Id { get; }

        /// <summary>Which machines raise this event in multiplayer.</summary>
        public EventSide Side { get; }

        /// <summary>Which thread handlers run on (<see cref="EventThread.Main"/> unless the event's docs say otherwise).</summary>
        public EventThread Thread { get; }

        public string Description { get; }

        /// <summary>True for "...ing" events that handlers can cancel.</summary>
        public bool IsCancellable => typeof(ICancellable).IsAssignableFrom(typeof(TEvent));

        /// <summary>True when at least one handler is subscribed.</summary>
        public bool HasSubscribers => !_handlers.IsEmpty;

        public int SubscriberCount => _handlers.Count;

        /// <summary>
        /// False when the game code behind this event could not be found or patched in this game build
        /// (handlers are kept but never called). See FtdModApi_status.txt for the reason.
        /// </summary>
        public bool IsAvailable => _hooks.All(h => h.IsUsable);

        IReadOnlyList<EventHook> IEventChannelInfo.Hooks => _hooks;

        /// <summary>Subscribes a handler.</summary>
        /// <param name="modId">Your mod's id (logs, fault isolation, <see cref="UnsubscribeAll"/>).</param>
        /// <param name="handler">Called with the event. Keep it fast; some events happen often.</param>
        /// <param name="priority">When to run; see <see cref="EventPriority"/>. Lower runs first.</param>
        /// <param name="ignoreCancelled">
        /// When true (the default), the handler is skipped for events an earlier handler cancelled.
        /// Pass false to see cancelled events too (Spigot's default is the opposite).
        /// </param>
        /// <param name="name">Optional label for logs. Defaults to the handler's method name.</param>
        public Registration Subscribe(string modId, Action<TEvent> handler, int priority = EventPriority.Normal, bool ignoreCancelled = true, string name = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            bool monitor = priority >= EventPriority.Monitor;
            bool skipCancelled = ignoreCancelled && IsCancellable;
            void Wrapped(TEvent e)
            {
                if (monitor) e.InMonitorPhase = true;
                if (skipCancelled && ((ICancellable)e).IsCancelled) return;
                handler(e);
            }

            var registration = _handlers.Subscribe(modId, Wrapped, priority, name ?? handler.Method.Name);
            foreach (var hook in _hooks)
            {
                if (!hook.Ensure())
                    FtdApi.GetLog(modId).Warning($"Event {Id} is unavailable in this game build ({hook.Error}); the handler will not be called.");
            }
            return registration;
        }

        /// <summary>Removes every handler a mod subscribed to this event.</summary>
        public void UnsubscribeAll(string modId) => _handlers.RemoveAll(modId);

        /// <summary>Raises the event on the current thread. Hooks check <see cref="HasSubscribers"/> first.</summary>
        internal void Raise(TEvent e)
        {
            if (_raisedOnce == 0 && Interlocked.Exchange(ref _raisedOnce, 1) == 0)
                FtdApi.Log.Verbose(() => $"Event {Id} raised for the first time ({SubscriberCount} handler(s)).");
            _handlers.Raise(e);
        }

        /// <summary>Raises the event now if on the main thread, otherwise queues it for the next frame.</summary>
        internal void RaiseOnMainThread(TEvent e)
        {
            if (MainThread.IsCurrent) Raise(e);
            else MainThread.Post(() => Raise(e));
        }
    }

    internal interface IEventChannelInfo
    {
        string Id { get; }
        EventSide Side { get; }
        EventThread Thread { get; }
        int SubscriberCount { get; }
        bool IsCancellable { get; }
        bool IsAvailable { get; }
        IReadOnlyList<EventHook> Hooks { get; }
    }

    /// <summary>
    /// The game hook behind one or more events: Harmony patches, or a subscription to one of the game's own
    /// callbacks. Installed the first time a mod subscribes to one of its events.
    /// </summary>
    internal sealed class EventHook
    {
        private readonly object _gate = new object();
        private readonly Func<IEnumerable<PatchSpec>> _specs;
        private readonly Func<string> _native;
        private volatile int _state; // 0 = not installed, 1 = installed, -1 = failed

        /// <param name="featureId">Feature this hook belongs to (several hooks can share one).</param>
        /// <param name="specs">Patches to apply (null for none).</param>
        /// <param name="native">Subscribes to a game callback; returns null on success or the reason it failed.</param>
        internal EventHook(string featureId, Func<IEnumerable<PatchSpec>> specs, Func<string> native = null)
        {
            FeatureId = featureId;
            _specs = specs;
            _native = native;
            EventRegistry.Add(this);
        }

        internal string FeatureId { get; }

        /// <summary>The hook's patches, built fresh (for the offline patch checker). Null when it only uses a game callback.</summary>
        internal IEnumerable<PatchSpec> BuildSpecs() => _specs?.Invoke();

        internal bool IsInstalled => _state == 1;

        /// <summary>False when the game code behind the hook was not found at startup or could not be patched.</summary>
        internal bool IsUsable => _state != -1 && CheckError == null;

        internal bool HasFailed => _state == -1 || CheckError != null;

        /// <summary>Why the hook failed (empty when it didn't).</summary>
        internal string Error { get; private set; } = string.Empty;

        /// <summary>Set by <see cref="Check"/> when the targets could not be found at startup.</summary>
        internal string CheckError { get; private set; }

        /// <summary>Resolves the patch targets without patching, so the status file shows broken events at startup.</summary>
        internal string Check()
        {
            CheckError = CheckTargets();
            if (CheckError != null) Error = CheckError;
            return CheckError;
        }

        private string CheckTargets()
        {
            if (_specs == null) return null;
            List<PatchSpec> specs;
            try
            {
                specs = _specs().ToList();
            }
            catch (Exception e)
            {
                return "could not build the patches (" + e.GetType().Name + ": " + e.Message + ")";
            }
            foreach (var spec in specs)
            {
                try
                {
                    var targets = spec.Targets != null ? spec.Targets().ToList() : new List<System.Reflection.MethodBase> { spec.Target() };
                    if (targets.Count == 0 || targets.Any(t => t == null))
                    {
                        if (spec.Required) return spec.Id + ": target not found";
                    }
                }
                catch (Exception e)
                {
                    if (spec.Required) return spec.Id + ": target not found (" + e.GetType().Name + ": " + e.Message + ")";
                }
            }
            return null;
        }

        /// <summary>Installs the hook if it is not installed yet. Returns false if it could not be installed.</summary>
        internal bool Ensure()
        {
            if (_state != 0) return _state == 1;
            if (CheckError != null) return false;
            lock (_gate)
            {
                if (_state != 0) return _state == 1;
                string error = null;
                string what = null;
                try
                {
                    if (_specs != null)
                    {
                        var results = PatchManager.Apply(_specs());
                        what = string.Join(", ", results.Where(r => r.Success).Select(r => (r.Id ?? "").Split(':')[0]));
                        var failed = results.Where(r => !r.Success && r.Required).ToList();
                        if (failed.Count > 0) error = string.Join("; ", failed.Select(r => r.Id + ": " + r.Error));
                    }
                    if (error == null && _native != null) error = _native();
                }
                catch (Exception e)
                {
                    error = e.GetType().Name + ": " + e.Message;
                }

                if (error == null)
                {
                    _state = 1;
                    FtdApi.Log.Verbose(() => $"Event hook for {FeatureId} installed" + (string.IsNullOrEmpty(what) ? "." : $" ({what})."));
                }
                else
                {
                    Error = error;
                    _state = -1;
                    FtdApi.Log.Error($"Event hook for {FeatureId} could not be installed: {error}");
                }
                // PatchManager sets the feature from this batch alone; recompute it from all of the feature's hooks.
                EventRegistry.RefreshFeature(FeatureId);
                return _state == 1;
            }
        }
    }

    /// <summary>Every event channel and hook, for startup checks and the status file.</summary>
    internal static class EventRegistry
    {
        private static readonly object _gate = new object();
        private static readonly List<IEventChannelInfo> _channels = new List<IEventChannelInfo>();
        private static readonly List<EventHook> _hooks = new List<EventHook>();

        internal static void Add(IEventChannelInfo channel)
        {
            lock (_gate) _channels.Add(channel);
        }

        internal static void Add(EventHook hook)
        {
            lock (_gate) _hooks.Add(hook);
        }

        internal static List<EventHook> Hooks
        {
            get { lock (_gate) return _hooks.ToList(); }
        }

        /// <summary>Checks every hook's targets and sets each event feature's state. Call after features are registered.</summary>
        internal static void CheckAll()
        {
            List<EventHook> hooks;
            lock (_gate) hooks = _hooks.ToList();
            foreach (var hook in hooks) hook.Check();
            foreach (var featureId in hooks.Select(h => h.FeatureId).Distinct())
            {
                RefreshFeature(featureId);
                var f = Features.Get(featureId);
                FtdApi.Log.Verbose(() => $"Event feature {featureId}: {(f == null || f.State == FeatureState.Available ? "targets found, patched on first subscription" : f.State + " (" + f.Detail + ")")}.");
            }
        }

        /// <summary>
        /// Sets an event feature's state from its hooks: Available when none failed, Degraded when some did (their events
        /// are unavailable, the others work), Unavailable when all did.
        /// </summary>
        internal static void RefreshFeature(string featureId)
        {
            List<EventHook> mine;
            lock (_gate) mine = _hooks.Where(h => h.FeatureId == featureId).ToList();
            if (mine.Count == 0) return;
            var failed = mine.Where(h => h.HasFailed).ToList();
            var detail = string.Join("; ", failed.Select(h => h.Error));
            if (failed.Count == 0) Features.SetState(featureId, FeatureState.Available);
            else if (failed.Count == mine.Count) Features.SetState(featureId, FeatureState.Unavailable, detail);
            else Features.SetState(featureId, FeatureState.Degraded, detail);
        }

        /// <summary>Lines for the status file: every event, its side, handlers and whether its hook is installed.</summary>
        internal static IEnumerable<string> Report()
        {
            List<IEventChannelInfo> channels;
            lock (_gate) channels = _channels.ToList();
            foreach (var c in channels.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                var hooks = c.Hooks;
                string hook = !c.IsAvailable ? "UNAVAILABLE"
                    : hooks.Count == 0 || hooks.All(h => h.IsInstalled) ? "active"
                    : c.SubscriberCount == 0 ? "not patched (no handlers)" : "not patched";
                yield return $"  {c.Id,-30} {c.Side,-6} {(c.Thread == EventThread.Any ? "any thread " : "main thread")}  {(c.IsCancellable ? "cancellable" : "           ")}  {c.SubscriberCount,3} handler(s)  {hook}";
            }
        }
    }

    /// <summary>Knows which thread is the game's main thread and runs queued work there once per frame.</summary>
    internal static class MainThread
    {
        private static int _mainThreadId = -1;
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private static int _installed;

        internal static bool IsCurrent => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>Call from the plugin's OnLoad, which the game runs on its main thread.</summary>
        internal static void Initialise()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Install();
        }

        internal static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1) return;
            try
            {
                GameEvents.UpdateEvent.RegWithEvent(OnUpdate);
            }
            catch (Exception e)
            {
                _installed = 0;
                FtdApi.Log.Warning("Could not hook the per-frame update yet (" + e.Message + "); retrying at the main menu.");
            }
        }

        internal static void Post(Action action)
        {
            _queue.Enqueue(action);
        }

        /// <summary>
        /// Runs the action on the main thread after the current game call returns (in the API's next per-frame update),
        /// even when already on the main thread.
        /// </summary>
        internal static void PostLater(Action action)
        {
            if (_installed == 0) Install();
            _queue.Enqueue(action);
        }

        private static void OnUpdate(ITimeStep step)
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            int budget = 10000;
            while (budget-- > 0 && _queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    FtdApi.Log.Error("A queued event threw.", e);
                }
            }
        }
    }
}
