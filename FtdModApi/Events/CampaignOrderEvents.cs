using System;
using System.Runtime.CompilerServices;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Id;
using BrilliantSkies.Core.Returns;
using BrilliantSkies.Core.Types;
using BrilliantSkies.Core.UniverseRepresentation.Orders;
using BrilliantSkies.Core.UniverseRepresentation.Positioning.Frames.Points;
using BrilliantSkies.Ftd.Game.Progression.AiInterface;
using BrilliantSkies.Ftd.Pathfinding;
using FtdModApi.Patching;
using HarmonyLib;
using UnityEngine;

namespace FtdModApi
{
    public static partial class Events
    {
        public static partial class Campaign
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static AccessTools.FieldRef<FleetMovementOrders, Fleet> _ordersFleet;
            private static AccessTools.FieldRef<FleetMovement, Fleet> _movementFleet;
            private static AccessTools.FieldRef<FleetMovement, FleetMovementOrders> _movementOrders;

            private static readonly EventHook OrderHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    Id = "FleetMovementOrders.IssueMoveOrder(position): Campaign.FleetOrdered",
                    FeatureId = Features.EventCampaign,
                    Target = () =>
                    {
                        _ordersFleet = FieldRefs.Get<FleetMovementOrders, Fleet>("_fleet");
                        return PatchSpec.Method(typeof(FleetMovementOrders), "IssueMoveOrder", typeof(Vector3d), typeof(Quaternion),
                            typeof(enumFleetOrderIssueType), typeof(enumPathfindingRequestPriority), typeof(float), typeof(ObjectId));
                    },
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(IssueToPosition_Postfix))
                },
                new PatchSpec
                {
                    Id = "FleetMovementOrders.IssueMoveOrder(fleet): Campaign.FleetOrdered",
                    FeatureId = Features.EventCampaign,
                    Target = () => PatchSpec.Method(typeof(FleetMovementOrders), "IssueMoveOrder", typeof(Fleet),
                        typeof(enumFleetOrderIssueType), typeof(enumPathfindingRequestPriority), typeof(float), typeof(ObjectId),
                        typeof(Vector3d), typeof(double)),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(IssueToFleet_Postfix))
                },
                new PatchSpec
                {
                    // Tags orders made by the AI factions' strategic planner.
                    Id = "MoveHandler.ExecuteMoveOrder: Campaign.FleetOrdered (AI)",
                    FeatureId = Features.EventCampaign,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(MoveHandler), "ExecuteMoveOrder"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(AiOrder_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Campaign), nameof(AiOrder_Finalizer))
                }
            });

            private static readonly EventHook FleetCtorHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    // A new fleet orders itself to stay where it is; that order (and reaching it) is not reported.
                    Id = "Fleet..ctor: Campaign.FleetOrdered (skip a new fleet's own order)",
                    FeatureId = Features.EventCampaign,
                    Target = () =>
                    {
                        var ctor = AccessTools.Constructor(typeof(Fleet), new[] { typeof(FactionObjectId), typeof(Vector3d), typeof(Quaternion) });
                        if (ctor == null) throw new MissingMethodException(typeof(Fleet).FullName, ".ctor(FactionObjectId, Vector3d, Quaternion)");
                        return ctor;
                    },
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(FleetCtor_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Campaign), nameof(FleetCtor_Finalizer))
                }
            });

            private static readonly EventHook WaypointHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    Id = "FleetMovement.RunPositionFixedUpdate: Campaign.FleetWaypointReached",
                    FeatureId = Features.EventCampaign,
                    Target = () =>
                    {
                        _movementFleet = FieldRefs.Get<FleetMovement, Fleet>("F");
                        _movementOrders = FieldRefs.Get<FleetMovement, FleetMovementOrders>("orders");
                        return EventHelpers.Declared(typeof(FleetMovement), "RunPositionFixedUpdate");
                    },
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(FleetMove_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(FleetMove_Postfix))
                }
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>
            /// A fleet was given a move order on the map: to a position, or to follow another fleet. From the player, the
            /// AI factions (<see cref="FleetOrderedEvent.ByAi"/>), or restored when a save loads. The "stay here" order every
            /// new fleet starts with is not reported. All machines.
            /// </summary>
            public static EventChannel<FleetOrderedEvent> FleetOrdered { get; } = new EventChannel<FleetOrderedEvent>(
                "Campaign.FleetOrdered", EventSide.All, "A fleet was given a move order.", OrderHook, FleetCtorHook);

            /// <summary>
            /// A fleet moving on the map reached one of its waypoints (and moves on to the next, if it has one). The
            /// "stay here" order every new fleet starts with is not reported. Host.
            /// </summary>
            public static EventChannel<FleetWaypointEvent> FleetWaypointReached { get; } = new EventChannel<FleetWaypointEvent>(
                "Campaign.FleetWaypointReached", EventSide.Host, "A fleet reached a waypoint.", WaypointHook, FleetCtorHook, OrderHook);

            // ---- orders ------------------------------------------------------------------------------------

            [ThreadStatic] private static int _aiOrderDepth;
            [ThreadStatic] private static int _fleetCtorDepth;

            // A new fleet's own "stay here" orders, so reaching them is not reported either.
            private static readonly ConditionalWeakTable<Order<IUniversalTransformReturn, UniversalTransform, AuxPacket>, object> _initialOrders =
                new ConditionalWeakTable<Order<IUniversalTransformReturn, UniversalTransform, AuxPacket>, object>();

            private static readonly object _marker = new object();

            private static void FleetCtor_Prefix() => _fleetCtorDepth++;

            private static void FleetCtor_Finalizer()
            {
                if (_fleetCtorDepth > 0) _fleetCtorDepth--;
            }

            private static void AiOrder_Prefix() => _aiOrderDepth++;

            private static void AiOrder_Finalizer()
            {
                if (_aiOrderDepth > 0) _aiOrderDepth--;
            }

            private static void IssueToPosition_Postfix(FleetMovementOrders __instance, Vector3d universalPosition, enumFleetOrderIssueType issueType, Order<IUniversalTransformReturn, UniversalTransform, AuxPacket> __result)
            {
                if (__result == null) return;
                if (_fleetCtorDepth > 0)
                {
                    try
                    {
                        _initialOrders.Add(__result, _marker);
                    }
                    catch (ArgumentException)
                    {
                    }
                    return;
                }
                if (_ordersFleet == null || !FleetOrdered.HasSubscribers) return;
                Fleet fleet;
                try
                {
                    fleet = _ordersFleet(__instance);
                }
                catch
                {
                    return;
                }
                if (fleet == null) return;
                FleetOrdered.RaiseOnMainThread(new FleetOrderedEvent(fleet, universalPosition, null, issueType == enumFleetOrderIssueType.additional, _aiOrderDepth > 0));
            }

            private static void IssueToFleet_Postfix(FleetMovementOrders __instance, Fleet targetF, enumFleetOrderIssueType issueType, Order<IUniversalTransformReturn, UniversalTransform, AuxPacket> __result)
            {
                if (__result == null || _ordersFleet == null || !FleetOrdered.HasSubscribers) return;
                Fleet fleet;
                try
                {
                    fleet = _ordersFleet(__instance);
                }
                catch
                {
                    return;
                }
                if (fleet == null) return;
                Vector3d target = default;
                try
                {
                    if (targetF != null) target = targetF.UniversalPosition;
                }
                catch
                {
                }
                FleetOrdered.RaiseOnMainThread(new FleetOrderedEvent(fleet, target, targetF, issueType == enumFleetOrderIssueType.additional, _aiOrderDepth > 0));
            }

            // ---- waypoints (main thread, every fixed update per fleet) --------------------------------------

            private struct MoveState
            {
                public int Metric;
                public Order<IUniversalTransformReturn, UniversalTransform, AuxPacket> Current;
            }

            private static void FleetMove_Prefix(FleetMovement __instance, out MoveState __state)
            {
                __state = new MoveState { Metric = -1 };
                if (_movementOrders == null || Net.IsClient || !FleetWaypointReached.HasSubscribers) return;
                try
                {
                    var orders = _movementOrders(__instance);
                    if (orders?.Orders == null) return;
                    __state.Metric = orders.Orders.OrdersReachedMetric;
                    __state.Current = orders.Orders.Current;
                }
                catch
                {
                    __state.Metric = -1;
                }
            }

            private static void FleetMove_Postfix(FleetMovement __instance, MoveState __state)
            {
                if (__state.Metric < 0) return;
                try
                {
                    var orders = _movementOrders(__instance);
                    if (orders?.Orders == null) return;
                    int reached = orders.Orders.OrdersReachedMetric - __state.Metric;
                    if (reached > 0 && __state.Current != null && _initialOrders.TryGetValue(__state.Current, out _)) return; // its own "stay here" order
                    if (reached <= 0) return;
                    var fleet = _movementFleet(__instance);
                    if (fleet == null) return;
                    bool more = orders.Orders.HasOrder;
                    FleetWaypointReached.RaiseOnMainThread(new FleetWaypointEvent(fleet, more));
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>A fleet was given a move order.</summary>
    public sealed class FleetOrderedEvent : GameEvent
    {
        internal FleetOrderedEvent(Fleet fleet, Vector3d destination, Fleet follow, bool queued, bool byAi)
        {
            Fleet = fleet;
            Destination = destination;
            Follow = follow;
            Queued = queued;
            ByAi = byAi;
            Name = EventHelpers.FleetName(fleet);
            FollowName = EventHelpers.FleetName(follow);
        }

        /// <summary>The fleet ordered. Game object.</summary>
        public Fleet Fleet { get; }

        public string Name { get; }

        public ObjectId Faction => Fleet?.FactionId;

        /// <summary>Where to (universe coordinates); for a follow order, where the followed fleet was.</summary>
        public Vector3d Destination { get; }

        /// <summary>The fleet to follow, or null for an order to a position. Game object.</summary>
        public Fleet Follow { get; }

        public string FollowName { get; }

        /// <summary>True when added after its current orders; false when it replaces them.</summary>
        public bool Queued { get; }

        /// <summary>True when an AI faction's strategic planner gave the order.</summary>
        public bool ByAi { get; }

        /// <summary>True for a fleet of a player's faction.</summary>
        public bool IsPlayerFleet
        {
            get
            {
                try
                {
                    return Fleet != null && Fleet.IsAPlayerTeam;
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    /// <summary>A fleet reached a waypoint.</summary>
    public sealed class FleetWaypointEvent : GameEvent
    {
        internal FleetWaypointEvent(Fleet fleet, bool hasMoreOrders)
        {
            Fleet = fleet;
            HasMoreOrders = hasMoreOrders;
            Name = EventHelpers.FleetName(fleet);
        }

        /// <summary>The fleet. Game object.</summary>
        public Fleet Fleet { get; }

        public string Name { get; }

        public ObjectId Faction => Fleet?.FactionId;

        /// <summary>True when it has another waypoint to go to; false when this was its last.</summary>
        public bool HasMoreOrders { get; }

        public bool IsPlayerFleet
        {
            get
            {
                try
                {
                    return Fleet != null && Fleet.IsAPlayerTeam;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
