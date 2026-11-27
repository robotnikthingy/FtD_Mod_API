using System;
using BrilliantSkies.Core.Help;
using BrilliantSkies.Core.Timing;

namespace FtdModApi
{
    /// <summary>
    /// Every game event the API offers, grouped by area: <see cref="Constructs"/>, <see cref="Blocks"/>,
    /// <see cref="Weapons"/>, <see cref="Projectiles"/>, <see cref="Session"/>, <see cref="Players"/>, <see cref="Resources"/>,
    /// <see cref="Campaign"/>, <see cref="Ai"/>, <see cref="Detection"/>, <see cref="Lua"/>, <see cref="Input"/>, <see cref="Ui"/>, <see cref="Ticks"/>, <see cref="BuildMode"/>. Subscribe with <c>Events.Blocks.Placed.Subscribe("MyMod", e => ...)</c>.
    /// </summary>
    /// <remarks>
    /// <para>"...ing" events (BlockPlacing, ConstructScrapping) are raised before the game acts and can be cancelled.
    /// "...ed" events are raised after it acted (or just before an object goes away, while it is still readable).</para>
    /// <para>Every event says which machines raise it (<see cref="EventChannel{TEvent}.Side"/>): Host, All or Local.
    /// Events run on the main thread unless <see cref="EventChannel{TEvent}.Thread"/> says <see cref="EventThread.Any"/>
    /// (<c>Weapons.Firing</c> and <c>Projectiles.ShieldHit</c>).</para>
    /// <para>Do not keep event objects after the handler returns: some are reused.</para>
    /// <para>The game patch behind an event is applied the first time a mod subscribes; FtdModApi_status.txt
    /// lists every event, its handlers and whether it is active.</para>
    /// </remarks>
    public static partial class Events
    {
        /// <summary>Timed ticks on the main thread, named by their real interval (the game's own names are misleading).</summary>
        public static class Ticks
        {
            private static EventHook Tick(string name, Func<CallbackWithArg<ITimeStep>> source, Func<EventChannel<TickEvent>> channel)
                => new EventHook(Features.EventTicks, null, () =>
                {
                    var callback = source();
                    if (callback == null) return "GameEvents." + name + " is missing";
                    var evt = new TickEvent();
                    var ch = channel();
                    callback.RegWithEvent(step =>
                    {
                        if (!ch.HasSubscribers) return;
                        evt.Set(step);
                        ch.Raise(evt);
                    });
                    return null;
                });

            /// <summary>Every frame (game update). Keep handlers very cheap.</summary>
            public static EventChannel<TickEvent> Frame { get; } = new EventChannel<TickEvent>("Ticks.Frame", EventSide.All,
                "Every frame.", Tick("UpdateEvent", () => GameEvents.UpdateEvent, () => Frame));

            /// <summary>Every physics step (fixed update). Keep handlers very cheap.</summary>
            public static EventChannel<TickEvent> PhysicsStep { get; } = new EventChannel<TickEvent>("Ticks.PhysicsStep", EventSide.All,
                "Every physics step.", Tick("FixedUpdateEvent", () => GameEvents.FixedUpdateEvent, () => PhysicsStep));

            /// <summary>Four times a second (game time).</summary>
            public static EventChannel<TickEvent> EveryQuarterSecond { get; } = new EventChannel<TickEvent>("Ticks.EveryQuarterSecond", EventSide.All,
                "Every 0.25 s.", Tick("Four_Second", () => GameEvents.Four_Second, () => EveryQuarterSecond));

            /// <summary>Twice a second (game time).</summary>
            public static EventChannel<TickEvent> EveryHalfSecond { get; } = new EventChannel<TickEvent>("Ticks.EveryHalfSecond", EventSide.All,
                "Every 0.5 s.", Tick("Twice_Second", () => GameEvents.Twice_Second, () => EveryHalfSecond));

            /// <summary>Once a second (game time).</summary>
            public static EventChannel<TickEvent> EverySecond { get; } = new EventChannel<TickEvent>("Ticks.EverySecond", EventSide.All,
                "Every 1 s.", Tick("Once_Second", () => GameEvents.Once_Second, () => EverySecond));

            /// <summary>Every 2 seconds (the game calls this one "Half_Second").</summary>
            public static EventChannel<TickEvent> EveryTwoSeconds { get; } = new EventChannel<TickEvent>("Ticks.EveryTwoSeconds", EventSide.All,
                "Every 2 s.", Tick("Half_Second", () => GameEvents.Half_Second, () => EveryTwoSeconds));

            /// <summary>Every 4 seconds (the game calls this one "Fifteen_Minute").</summary>
            public static EventChannel<TickEvent> EveryFourSeconds { get; } = new EventChannel<TickEvent>("Ticks.EveryFourSeconds", EventSide.All,
                "Every 4 s.", Tick("Fifteen_Minute", () => GameEvents.Fifteen_Minute, () => EveryFourSeconds));

            /// <summary>Every 5 seconds (the game calls this one "Twelve_Minute"; campaign AI and capture checks run on it).</summary>
            public static EventChannel<TickEvent> EveryFiveSeconds { get; } = new EventChannel<TickEvent>("Ticks.EveryFiveSeconds", EventSide.All,
                "Every 5 s.", Tick("Twelve_Minute", () => GameEvents.Twelve_Minute, () => EveryFiveSeconds));

            /// <summary>Once a minute (game time).</summary>
            public static EventChannel<TickEvent> EveryMinute { get; } = new EventChannel<TickEvent>("Ticks.EveryMinute", EventSide.All,
                "Every 60 s.", Tick("Once_Minute", () => GameEvents.Once_Minute, () => EveryMinute));
        }

        /// <summary>The local player entering and leaving build mode.</summary>
        public static class BuildMode
        {
            private static readonly EventHook Hook = new EventHook(Features.EventBuildMode, null, () =>
            {
                if (GameEvents.OnEnterBuildMode == null || GameEvents.OnExitBuildMode == null) return "GameEvents.OnEnter/OnExitBuildMode are missing";
                GameEvents.OnEnterBuildMode.RegWithEvent(OnEnter, true);
                GameEvents.OnExitBuildMode.RegWithEvent(OnExit, true);
                return null;
            });

            // The game signals "enter" before it has picked the craft to build on (and may then cancel if there is
            // none), so Entered is raised once the current game call has finished and the craft is known.
            private static bool _enterPending;
            private static bool _inBuildMode;

            private static void OnEnter()
            {
                if (_enterPending || _inBuildMode) return;
                _enterPending = true;
                MainThread.PostLater(() =>
                {
                    if (!_enterPending) return; // left again straight away (no craft to build on)
                    _enterPending = false;
                    _inBuildMode = true;
                    if (Entered.HasSubscribers) Entered.Raise(new BuildModeEvent(true, EventHelpers.BuildModeConstruct()));
                });
            }

            private static void OnExit()
            {
                if (_enterPending)
                {
                    _enterPending = false; // never really entered
                    return;
                }
                if (!_inBuildMode) return;
                _inBuildMode = false;
                // Raised before the game clears its build target, so Construct is the craft that was being built on.
                if (Exited.HasSubscribers) Exited.Raise(new BuildModeEvent(false, EventHelpers.BuildModeConstruct()));
            }

            /// <summary>The local player entered build mode (raised once the craft being built on is known). Local.</summary>
            public static EventChannel<BuildModeEvent> Entered { get; } = new EventChannel<BuildModeEvent>("BuildMode.Entered", EventSide.Local,
                "The local player entered build mode.", Hook);

            /// <summary>The local player left build mode. Local.</summary>
            public static EventChannel<BuildModeEvent> Exited { get; } = new EventChannel<BuildModeEvent>("BuildMode.Exited", EventSide.Local,
                "The local player left build mode.", Hook);
        }

        /// <summary>Creates every event channel and checks the game code behind them. Called once at startup.</summary>
        internal static void Initialise()
        {
            TouchAll();
            EventRegistry.CheckAll();
        }

        /// <summary>Touches each group so its static channels and hooks exist.</summary>
        internal static void TouchAll()
        {
            _ = Ticks.Frame;
            _ = BuildMode.Entered;
            _ = Constructs.Spawned;
            _ = Blocks.Placed;
            _ = Weapons.Firing;
            _ = Aps.ShellFired;
            _ = Cram.ShellFired;
            _ = Missiles.Launched;
            _ = Projectiles.Hit;
            _ = Session.ModeChanged;
            _ = Players.Added;
            _ = Resources.Gained;
            _ = Campaign.FleetCreated;
            _ = Campaign.FleetOrdered;
            _ = Ai.TargetSpotted;
            _ = Detection.ContactMade;
            _ = Lua.ScriptError;
            _ = Input.KeyPressed;
            _ = Ui.WindowOpened;
        }
    }

    /// <summary>A timed tick. The same object is reused for every tick, so don't keep it.</summary>
    public sealed class TickEvent : GameEvent
    {
        /// <summary>The game's time step for this tick.</summary>
        public ITimeStep TimeStep { get; private set; }

        /// <summary>Seconds since the previous tick of this kind (game time).</summary>
        public float DeltaTime { get; private set; }

        internal void Set(ITimeStep step)
        {
            TimeStep = step;
            DeltaTime = step?.DeltaTime ?? 0f;
        }
    }

    /// <summary>The local player entered or left build mode.</summary>
    public sealed class BuildModeEvent : GameEvent
    {
        internal BuildModeEvent(bool entered, AllConstruct construct)
        {
            Entered = entered;
            Construct = construct;
        }

        /// <summary>True when entering build mode, false when leaving.</summary>
        public bool Entered { get; }

        /// <summary>The construct being built on: the craft or a turret/spin block (null if the game has none).</summary>
        public AllConstruct Construct { get; }

        /// <summary>The craft being built on, even when building on a turret.</summary>
        public MainConstruct MainConstruct => EventHelpers.MainOf(Construct);

        public string Name => MainConstruct?.GetName();
    }
}
