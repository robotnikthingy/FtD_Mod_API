using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BrilliantSkies.Ai;
using BrilliantSkies.Core;
using FtdModApi.Patching;
using HarmonyLib;
using UnityEngine;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>
        /// AI mainframes: targets spotted and lost, the primary target changing, and the mainframe's movement mode, firing
        /// mode, behaviour and manoeuvre being changed. A craft with several mainframes raises these once per mainframe.
        /// </summary>
        public static class Ai
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static AccessTools.FieldRef<AITargetManager, AiNode> _node;
            private static AccessTools.FieldRef<AITargetManager, ConcurrentDictionary<I_Targetable, TargetObject>> _targets;
            private static AccessTools.FieldRef<AITargetManager, TargetObject> _primary;

            private static readonly EventHook TargetHook = new EventHook(Features.EventAi, () => new[]
            {
                new PatchSpec
                {
                    Id = "AITargetManager.TargetDetected: Ai.TargetSpotted",
                    FeatureId = Features.EventAi,
                    Target = () =>
                    {
                        _node = FieldRefs.Get<AITargetManager, AiNode>("_node");
                        _targets = FieldRefs.Get<AITargetManager, ConcurrentDictionary<I_Targetable, TargetObject>>("_dictionaryOfTargetsAndScores");
                        _primary = FieldRefs.Get<AITargetManager, TargetObject>("_primaryTarget");
                        return EventHelpers.Declared(typeof(AITargetManager), "TargetDetected");
                    },
                    Prefix = PatchSpec.Hook(typeof(Ai), nameof(TargetDetected_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Ai), nameof(TargetDetected_Postfix))
                },
                new PatchSpec
                {
                    Id = "AITargetManager.Update: Ai.TargetLost/PrimaryTargetChanged",
                    FeatureId = Features.EventAi,
                    Target = () => EventHelpers.Declared(typeof(AITargetManager), "Update"),
                    Prefix = PatchSpec.Hook(typeof(Ai), nameof(TargetUpdate_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Ai), nameof(TargetUpdate_Postfix))
                }
            });

            private static readonly EventHook ModeHook = new EventHook(Features.EventAi, () => new[]
            {
                new PatchSpec
                {
                    // Compares each mainframe's settings once a frame (main thread), whatever changed them.
                    Id = "AiNode.OnUpdate: Ai.MovementChanged/FiringChanged/RoutineChanged",
                    FeatureId = Features.EventAi,
                    Target = () => EventHelpers.Declared(typeof(AiNode), "OnUpdate"),
                    Postfix = PatchSpec.Hook(typeof(Ai), nameof(AiNodeUpdate_Postfix))
                }
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>
            /// An AI mainframe started tracking a new target (a craft, or a missile when it targets missiles). Raised the first
            /// time it sees it, and again after <see cref="TargetLost"/> if it is seen again. Host.
            /// </summary>
            public static EventChannel<AiTargetEvent> TargetSpotted { get; } = new EventChannel<AiTargetEvent>(
                "Ai.TargetSpotted", EventSide.Host, "An AI mainframe started tracking a target.", TargetHook);

            /// <summary>
            /// An AI mainframe dropped a target: destroyed (<see cref="AiTargetEvent.Destroyed"/>, raised at once), or not
            /// detected for 4 seconds or no longer attackable (raised 2 seconds later, and not at all if the mainframe picks
            /// it up again in that time: the game drops and re-adds some targets every update, e.g. wrecks). Not raised when the mainframe itself is destroyed or removed (its
            /// targets are simply dropped), so watch <see cref="Constructs.Despawned"/> too if you keep a list. Host.
            /// </summary>
            public static EventChannel<AiTargetEvent> TargetLost { get; } = new EventChannel<AiTargetEvent>(
                "Ai.TargetLost", EventSide.Host, "An AI mainframe dropped a target.", TargetHook);

            /// <summary>An AI mainframe picked a different primary target (or lost its last one). Host.</summary>
            public static EventChannel<AiPrimaryTargetEvent> PrimaryTargetChanged { get; } = new EventChannel<AiPrimaryTargetEvent>(
                "Ai.PrimaryTargetChanged", EventSide.Host, "An AI mainframe changed its primary target.", TargetHook);

            /// <summary>
            /// A mainframe's movement mode changed (Off, Manual/patrol, Automatic, Fleet): from its menu, the map, fleet
            /// orders, breadboards or the game. Checked once a frame while the craft is in play; the settings a craft spawns
            /// with are not reported. All machines.
            /// </summary>
            public static EventChannel<AiMovementChangedEvent> MovementChanged { get; } = new EventChannel<AiMovementChangedEvent>(
                "Ai.MovementChanged", EventSide.All, "A mainframe's movement mode changed.", ModeHook);

            /// <summary>A mainframe's firing mode changed (On/Off). Same rules as <see cref="MovementChanged"/>. All machines.</summary>
            public static EventChannel<AiFiringChangedEvent> FiringChanged { get; } = new EventChannel<AiFiringChangedEvent>(
                "Ai.FiringChanged", EventSide.All, "A mainframe's firing mode changed.", ModeHook);

            /// <summary>
            /// A mainframe's selected behaviour or manoeuvre changed: another one was picked, the selected one was deleted
            /// (<see cref="AiRoutineChangedEvent.NewName"/> is null), or one was added and selected. Same rules as
            /// <see cref="MovementChanged"/>. All machines.
            /// </summary>
            public static EventChannel<AiRoutineChangedEvent> RoutineChanged { get; } = new EventChannel<AiRoutineChangedEvent>(
                "Ai.RoutineChanged", EventSide.All, "A mainframe's behaviour or manoeuvre changed.", ModeHook);

            // ---- targets (detection thread) ----------------------------------------------------------------

            private static void TargetDetected_Prefix(AITargetManager __instance, I_Targetable C, out bool __state)
            {
                __state = true;
                if (C == null || _targets == null || Net.IsClient || !TargetSpotted.HasSubscribers) return;
                try
                {
                    __state = _targets(__instance)?.ContainsKey(C) ?? true;
                }
                catch
                {
                    __state = true;
                }
            }

            private static void TargetDetected_Postfix(AITargetManager __instance, I_Targetable C, Vector3 position, bool __state)
            {
                if (__state) return;
                try
                {
                    var dict = _targets(__instance);
                    if (dict == null || !dict.ContainsKey(C)) return; // rejected (bad position)
                    var pending = _pendingLost.GetOrCreateValue(__instance);
                    lock (pending)
                    {
                        if (pending.Remove(C)) return; // dropped and picked up again before TargetLost was due: never really lost
                    }
                    var node = _node(__instance);
                    TargetSpotted.RaiseOnMainThread(new AiTargetEvent(node, C, position, false));
                }
                catch
                {
                }
            }

            private struct UpdateState
            {
                public List<I_Targetable> Before;
                public I_Targetable Primary;
                public bool Active;
            }

            [ThreadStatic] private static List<I_Targetable> _spare;

            private const double LostDelaySeconds = 2.0;

            private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

            /// <summary>Per target manager: targets it dropped, and when, waiting to be reported as lost.</summary>
            private static readonly ConditionalWeakTable<AITargetManager, Dictionary<I_Targetable, double>> _pendingLost =
                new ConditionalWeakTable<AITargetManager, Dictionary<I_Targetable, double>>();

            private static void TargetUpdate_Prefix(AITargetManager __instance, out UpdateState __state)
            {
                __state = default;
                if (_targets == null || Net.IsClient || (!TargetLost.HasSubscribers && !PrimaryTargetChanged.HasSubscribers)) return;
                try
                {
                    __state.Active = true;
                    __state.Primary = _primary(__instance)?.C;
                    if (TargetLost.HasSubscribers)
                    {
                        var dict = _targets(__instance);
                        if (dict != null && !dict.IsEmpty)
                        {
                            var list = _spare ?? new List<I_Targetable>();
                            _spare = null;
                            list.Clear();
                            list.AddRange(dict.Keys);
                            __state.Before = list;
                        }
                    }
                }
                catch
                {
                    __state = default;
                }
            }

            private static void TargetUpdate_Postfix(AITargetManager __instance, UpdateState __state)
            {
                if (!__state.Active) return;
                try
                {
                    var node = _node(__instance);
                    var dict = _targets(__instance);
                    double now = _clock.Elapsed.TotalSeconds;
                    if (__state.Before != null)
                    {
                        var pending = _pendingLost.GetOrCreateValue(__instance);
                        foreach (var t in __state.Before)
                        {
                            if (t == null || (dict != null && dict.ContainsKey(t))) continue;
                            bool destroyed;
                            try
                            {
                                destroyed = !t.Exists;
                            }
                            catch
                            {
                                destroyed = true;
                            }
                            if (destroyed)
                            {
                                lock (pending) pending.Remove(t);
                                TargetLost.RaiseOnMainThread(new AiTargetEvent(node, t, Vector3.zero, true));
                            }
                            else
                            {
                                lock (pending)
                                {
                                    if (!pending.ContainsKey(t)) pending[t] = now;
                                }
                            }
                        }
                        __state.Before.Clear();
                        _spare = __state.Before;
                    }
                    if (TargetLost.HasSubscribers && _pendingLost.TryGetValue(__instance, out var waiting))
                    {
                        List<I_Targetable> due = null;
                        lock (waiting)
                        {
                            foreach (var kv in waiting)
                            {
                                if (now - kv.Value < LostDelaySeconds) continue;
                                (due ?? (due = new List<I_Targetable>())).Add(kv.Key);
                            }
                            if (due != null)
                                foreach (var t in due) waiting.Remove(t);
                        }
                        if (due != null)
                        {
                            foreach (var t in due)
                            {
                                if (dict != null && dict.ContainsKey(t)) continue; // back again
                                bool destroyed;
                                try
                                {
                                    destroyed = !t.Exists;
                                }
                                catch
                                {
                                    destroyed = true;
                                }
                                TargetLost.RaiseOnMainThread(new AiTargetEvent(node, t, Vector3.zero, destroyed));
                            }
                        }
                    }
                    if (PrimaryTargetChanged.HasSubscribers)
                    {
                        var primaryNow = _primary(__instance)?.C;
                        if (!ReferenceEquals(primaryNow, __state.Primary))
                            PrimaryTargetChanged.RaiseOnMainThread(new AiPrimaryTargetEvent(node, __state.Primary, primaryNow));
                    }
                }
                catch
                {
                }
            }

            // ---- modes (polled once a frame, main thread) ------------------------------------------------

            private sealed class ModeState
            {
                public bool Seen;
                public MovementType Movement;
                public FiringType Firing;
                public uint Behaviour;
                public uint Manoeuvre;
                public string BehaviourName;
                public string ManoeuvreName;
            }

            private static readonly ConditionalWeakTable<AiNode, ModeState> _modes = new ConditionalWeakTable<AiNode, ModeState>();

            private static void AiNodeUpdate_Postfix(AiNode __instance)
            {
                if (!MovementChanged.HasSubscribers && !FiringChanged.HasSubscribers && !RoutineChanged.HasSubscribers) return;
                try
                {
                    var pack = __instance?.Master?.Pack;
                    if (pack == null) return;
                    var movement = pack.Movement.Us;
                    var firing = pack.Firing.Us;
                    uint behaviour = pack.SelectedBehaviourId.Us;
                    uint manoeuvre = pack.SelectedManoeuvreId.Us;
                    var st = _modes.GetOrCreateValue(__instance);
                    if (!st.Seen)
                    {
                        // First look at this mainframe: remember its settings, don't report them.
                        st.Seen = true;
                        st.Movement = movement;
                        st.Firing = firing;
                        st.Behaviour = behaviour;
                        st.Manoeuvre = manoeuvre;
                        st.BehaviourName = RoutineName(pack, behaviour);
                        st.ManoeuvreName = RoutineName(pack, manoeuvre);
                        return;
                    }
                    if (movement != st.Movement)
                    {
                        var old = st.Movement;
                        st.Movement = movement;
                        if (MovementChanged.HasSubscribers) MovementChanged.Raise(new AiMovementChangedEvent(__instance, old, movement));
                    }
                    if (firing != st.Firing)
                    {
                        var old = st.Firing;
                        st.Firing = firing;
                        if (FiringChanged.HasSubscribers) FiringChanged.Raise(new AiFiringChangedEvent(__instance, old, firing));
                    }
                    // A routine also changes when the selected one is deleted (its id stays but no longer names a
                    // routine), so check whether the id still names a routine as well as whether it changed. Names are
                    // looked up every frame; mainframes have only a few routines.
                    {
                        string bName = RoutineName(pack, behaviour);
                        if (behaviour != st.Behaviour || (bName != null) != (st.BehaviourName != null))
                            RaiseRoutine(__instance, AiRoutineType.Behaviour, st.Behaviour, st.BehaviourName, behaviour, bName);
                        st.Behaviour = behaviour;
                        st.BehaviourName = bName; // also follows renames without reporting them
                        string mName = RoutineName(pack, manoeuvre);
                        if (manoeuvre != st.Manoeuvre || (mName != null) != (st.ManoeuvreName != null))
                            RaiseRoutine(__instance, AiRoutineType.Manoeuvre, st.Manoeuvre, st.ManoeuvreName, manoeuvre, mName);
                        st.Manoeuvre = manoeuvre;
                        st.ManoeuvreName = mName; // also follows renames without reporting them
                    }
                }
                catch
                {
                }
            }

            private static void RaiseRoutine(AiNode node, AiRoutineType kind, uint oldId, string oldName, uint newId, string newName)
            {
                if (!RoutineChanged.HasSubscribers) return;
                if (oldName == null && newName == null) return; // between ids that aren't routines (e.g. none to an unset id)
                RoutineChanged.Raise(new AiRoutineChangedEvent(node, kind, oldId, oldName, newId, newName));
            }

            internal static string RoutineName(AiMasterPack pack, uint id)
            {
                if (pack == null || id == uint.MaxValue) return null;
                try
                {
                    var packages = pack.Packages;
                    for (int i = 0; i < packages.Count; i++)
                    {
                        var p = packages[i];
                        if (p != null && p.UniqueId == id) return p.OurId?.Us?.Name;
                    }
                }
                catch
                {
                }
                return null;
            }
        }
    }

    /// <summary>Shared members of AI mainframe events.</summary>
    public abstract class AiNodeEventBase : GameEvent
    {
        internal AiNodeEventBase(AiNode node)
        {
            Node = node;
            Construct = node?.MainConstruct;
            try
            {
                MainframeName = node?.Name;
            }
            catch
            {
                MainframeName = null;
            }
        }

        /// <summary>The AI mainframe's node. Game object (it is replaced when the craft relinks).</summary>
        public AiNode Node { get; }

        /// <summary>The craft the mainframe is on.</summary>
        public MainConstruct Construct { get; }

        public string Name => EventHelpers.SafeName(Construct);

        /// <summary>The mainframe's name (as set in its menu).</summary>
        public string MainframeName { get; }
    }

    /// <summary>An AI mainframe started or stopped tracking a target.</summary>
    public sealed class AiTargetEvent : AiNodeEventBase
    {
        internal AiTargetEvent(AiNode node, I_Targetable target, Vector3 position, bool destroyed)
            : base(node)
        {
            Target = target;
            Position = position;
            Destroyed = destroyed;
            try
            {
                TargetName = target?.GetName();
            }
            catch
            {
                TargetName = null;
            }
        }

        /// <summary>The target: a craft (<see cref="TargetConstruct"/>) or a projectile. Game object.</summary>
        public I_Targetable Target { get; }

        /// <summary>The target craft, or null when the target is a missile or other projectile.</summary>
        public MainConstruct TargetConstruct => Target as MainConstruct;

        public string TargetName { get; }

        /// <summary>Where it was seen (game world). Zero for TargetLost.</summary>
        public Vector3 Position { get; }

        /// <summary>TargetLost only: the target no longer exists (destroyed or removed).</summary>
        public bool Destroyed { get; }
    }

    /// <summary>An AI mainframe changed its primary target.</summary>
    public sealed class AiPrimaryTargetEvent : AiNodeEventBase
    {
        internal AiPrimaryTargetEvent(AiNode node, I_Targetable oldTarget, I_Targetable newTarget)
            : base(node)
        {
            OldTarget = oldTarget;
            NewTarget = newTarget;
            OldTargetName = SafeName(oldTarget);
            NewTargetName = SafeName(newTarget);
        }

        private static string SafeName(I_Targetable t)
        {
            try
            {
                return t?.GetName();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The previous primary target (null if there was none). Game object.</summary>
        public I_Targetable OldTarget { get; }

        /// <summary>The new primary target (null if it has none now). Game object.</summary>
        public I_Targetable NewTarget { get; }

        public string OldTargetName { get; }

        public string NewTargetName { get; }
    }

    /// <summary>A mainframe's movement mode changed.</summary>
    public sealed class AiMovementChangedEvent : AiNodeEventBase
    {
        internal AiMovementChangedEvent(AiNode node, MovementType oldMode, MovementType newMode)
            : base(node)
        {
            OldMode = oldMode;
            NewMode = newMode;
        }

        public MovementType OldMode { get; }

        public MovementType NewMode { get; }
    }

    /// <summary>A mainframe's firing mode changed.</summary>
    public sealed class AiFiringChangedEvent : AiNodeEventBase
    {
        internal AiFiringChangedEvent(AiNode node, FiringType oldMode, FiringType newMode)
            : base(node)
        {
            OldMode = oldMode;
            NewMode = newMode;
        }

        public FiringType OldMode { get; }

        public FiringType NewMode { get; }
    }

    /// <summary>A mainframe's selected behaviour or manoeuvre changed.</summary>
    public sealed class AiRoutineChangedEvent : AiNodeEventBase
    {
        internal AiRoutineChangedEvent(AiNode node, AiRoutineType kind, uint oldId, string oldName, uint newId, string newName)
            : base(node)
        {
            Kind = kind;
            OldId = oldId;
            NewId = newId;
            OldName = oldName;
            NewName = newName;
        }

        /// <summary>Behaviour or Manoeuvre.</summary>
        public AiRoutineType Kind { get; }

        /// <summary>The routine's id in the mainframe (uint.MaxValue means none).</summary>
        public uint OldId { get; }

        public uint NewId { get; }

        /// <summary>The routine's name (as shown in the mainframe menu), or null.</summary>
        public string OldName { get; }

        public string NewName { get; }
    }
}
