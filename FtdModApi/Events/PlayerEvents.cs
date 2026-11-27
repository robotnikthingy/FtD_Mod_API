using System;
using System.Collections.Generic;
using BrilliantSkies.Core.Id;
using BrilliantSkies.Core.Types;
using BrilliantSkies.Ftd.Avatar;
using BrilliantSkies.Ftd.Avatar.Build;
using BrilliantSkies.Ftd.Avatar.Build.UndoRedo;
using BrilliantSkies.Ftd.Avatar.Health;
using Ftd.Ftd.Avatar.TeleportingAndHolding;
using FtdModApi.Patching;
using HarmonyLib;
using NetInfrastructure;
using UnityEngine;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>Players and their characters: added/removed, spawned/died, seats, prefabs, chat.</summary>
        public static class Players
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static readonly EventHook RosterHook = new EventHook(Features.EventPlayers, () => new[]
            {
                new PatchSpec
                {
                    Id = "StaticPlayers.RegisterPlayer: Players.Added",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(StaticPlayers), "RegisterPlayer"),
                    Postfix = PatchSpec.Hook(typeof(Players), nameof(RegisterPlayer_Postfix))
                },
                new PatchSpec
                {
                    Id = "StaticPlayers.UnregisterPlayer: Players.Removed",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(StaticPlayers), "UnregisterPlayer"),
                    Prefix = PatchSpec.Hook(typeof(Players), nameof(UnregisterPlayer_Prefix))
                }
            });

            private static AccessTools.FieldRef<sHealth, bool> _dead;

            private static readonly EventHook AvatarHook = new EventHook(Features.EventPlayers, () => new[]
            {
                new PatchSpec
                {
                    Id = "bAvatar.DoTheSpawn: Players.AvatarSpawned",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(bAvatar), "DoTheSpawn"),
                    Postfix = PatchSpec.Hook(typeof(Players), nameof(DoTheSpawn_Postfix))
                },
                new PatchSpec
                {
                    // Single player and the host's own player: the client-side avatar spawns the model and tells the server
                    // component through this (explicitly implemented) interface method.
                    Id = "sAvatar.unnetworked_AvatarSpawned: Players.AvatarSpawned (local player)",
                    FeatureId = Features.EventPlayers,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(sAvatar), "BrilliantSkies.Ftd.Avatar.I_unnetworked_cAvatar_sAvatar.unnetworked_AvatarSpawned"),
                    Postfix = PatchSpec.Hook(typeof(Players), nameof(LocalAvatarSpawned_Postfix))
                },
                new PatchSpec
                {
                    // Deaths from damage go through Die; the player removing the avatar calls ForceDie directly.
                    Id = "sHealth.Die: Players.AvatarDied (killed by damage)",
                    FeatureId = Features.EventPlayers,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(sHealth), "Die"),
                    Prefix = PatchSpec.Hook(typeof(Players), nameof(Die_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Players), nameof(Die_Finalizer))
                },
                new PatchSpec
                {
                    Id = "sHealth.ForceDie: Players.AvatarDied",
                    FeatureId = Features.EventPlayers,
                    Target = () =>
                    {
                        _dead = FieldRefs.Get<sHealth, bool>("_dead");
                        return EventHelpers.Declared(typeof(sHealth), "ForceDie");
                    },
                    Prefix = PatchSpec.Hook(typeof(Players), nameof(ForceDie_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Players), nameof(ForceDie_Postfix))
                }
            });

            private static readonly EventHook SeatHook = new EventHook(Features.EventPlayers, () => new[]
            {
                new PatchSpec
                {
                    Id = "sTeleportAndHold.unnetworked_Server_RequestToEnterChair: Players.SeatEntering",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(sTeleportAndHold), "unnetworked_Server_RequestToEnterChair"),
                    Prefix = PatchSpec.Hook(typeof(Players), nameof(RequestToEnterChair_Prefix))
                },
                new PatchSpec
                {
                    Id = "bTeleportAndHold.ALL_ActuallyEnterTheFuckingChair: Players.SeatEntered",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(bTeleportAndHold), "ALL_ActuallyEnterTheFuckingChair"),
                    Postfix = PatchSpec.Hook(typeof(Players), nameof(EnterChair_Postfix))
                },
                new PatchSpec
                {
                    Id = "bTeleportAndHold.ALL_ActuallyLeaveTheFuckingChair: Players.SeatLeft",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(bTeleportAndHold), "ALL_ActuallyLeaveTheFuckingChair"),
                    Prefix = PatchSpec.Hook(typeof(Players), nameof(LeaveChair_Prefix))
                }
            });

            private static readonly EventHook PrefabHook = new EventHook(Features.EventPlayers, () => new[]
            {
                new PatchSpec
                {
                    Id = "PrefabSaveAndLoad.PlacePrefab: Players.PrefabPlacing",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(PrefabSaveAndLoad), "PlacePrefab"),
                    Prefix = PatchSpec.Hook(typeof(Players), nameof(PlacePrefab_Prefix))
                },
                new PatchSpec
                {
                    Id = "PrefabSaveAndLoad.PlacePrefabLocal: Players.PrefabPlaced",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(PrefabSaveAndLoad), "PlacePrefabLocal"),
                    Postfix = PatchSpec.Hook(typeof(Players), nameof(PlacePrefabLocal_Postfix))
                }
            });

            private static readonly EventHook ChatHook = new EventHook(Features.EventPlayers, () => new[]
            {
                new PatchSpec
                {
                    Id = "ChatLog.Send: Players.ChatSending",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(ChatLog), "Send"),
                    Prefix = PatchSpec.Hook(typeof(Players), nameof(ChatSend_Prefix))
                },
                new PatchSpec
                {
                    Id = "ChatLog._AddChatEntry: Players.ChatReceived",
                    FeatureId = Features.EventPlayers,
                    Target = () => EventHelpers.Declared(typeof(ChatLog), "_AddChatEntry"),
                    Postfix = PatchSpec.Hook(typeof(Players), nameof(AddChatEntry_Postfix))
                }
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>A player joined the game (including the local player when a game starts). All machines.</summary>
            public static EventChannel<PlayerEvent> Added { get; } = new EventChannel<PlayerEvent>(
                "Players.Added", EventSide.All, "A player joined the game.", RosterHook);

            /// <summary>A player is leaving the game (disconnect, or the game ending). Raised before removal. All machines.</summary>
            public static EventChannel<PlayerEvent> Removed { get; } = new EventChannel<PlayerEvent>(
                "Players.Removed", EventSide.All, "A player is leaving the game.", RosterHook);

            /// <summary>A player's character (avatar) spawned or respawned. Host.</summary>
            public static EventChannel<PlayerEvent> AvatarSpawned { get; } = new EventChannel<PlayerEvent>(
                "Players.AvatarSpawned", EventSide.Host, "A player's character spawned.", AvatarHook);

            /// <summary>
            /// A player's character died, or was removed by the player (the avatar spawn/remove key, or going to the map with
            /// auto-spawn off): <see cref="AvatarDiedEvent.Killed"/> tells which. Host.
            /// </summary>
            public static EventChannel<AvatarDiedEvent> AvatarDied { get; } = new EventChannel<AvatarDiedEvent>(
                "Players.AvatarDied", EventSide.Host, "A player's character died.", AvatarHook);

            /// <summary>A player is about to sit in a seat (cockpit, chair) on a craft. Cancellable. Host.</summary>
            public static EventChannel<SeatEnteringEvent> SeatEntering { get; } = new EventChannel<SeatEnteringEvent>(
                "Players.SeatEntering", EventSide.Host, "A player is about to sit in a seat.", SeatHook);

            /// <summary>A player sat in a seat. Host.</summary>
            public static EventChannel<SeatEvent> SeatEntered { get; } = new EventChannel<SeatEvent>(
                "Players.SeatEntered", EventSide.Host, "A player sat in a seat.", SeatHook);

            /// <summary>A player is leaving a seat (raised before, while the seat is still known). Host.</summary>
            public static EventChannel<SeatEvent> SeatLeft { get; } = new EventChannel<SeatEvent>(
                "Players.SeatLeft", EventSide.Host, "A player left a seat.", SeatHook);

            /// <summary>The local player is about to place a prefab. Cancellable. Local.</summary>
            public static EventChannel<PrefabPlacingEvent> PrefabPlacing { get; } = new EventChannel<PrefabPlacingEvent>(
                "Players.PrefabPlacing", EventSide.Local, "A player is about to place a prefab.", PrefabHook);

            /// <summary>A prefab was placed on a construct. All machines.</summary>
            public static EventChannel<PrefabPlacedEvent> PrefabPlaced { get; } = new EventChannel<PrefabPlacedEvent>(
                "Players.PrefabPlaced", EventSide.All, "A prefab was placed.", PrefabHook);

            /// <summary>
            /// A chat message is about to be sent (by the local player, or relayed by the host for a client). Change
            /// <c>Message</c> or cancel it. Multiplayer only (single player has no chat). Raised on the sending machine and
            /// again on the host when it relays a client's message.
            /// </summary>
            public static EventChannel<ChatSendingEvent> ChatSending { get; } = new EventChannel<ChatSendingEvent>(
                "Players.ChatSending", EventSide.All, "A chat message is about to be sent.", ChatHook);

            /// <summary>A chat message was added to this machine's chat log. Multiplayer only. All machines.</summary>
            public static EventChannel<ChatReceivedEvent> ChatReceived { get; } = new EventChannel<ChatReceivedEvent>(
                "Players.ChatReceived", EventSide.All, "A chat message arrived.", ChatHook);

            // ---- patch bodies ------------------------------------------------------------------------------

            private static void RegisterPlayer_Postfix(PlayerSetupBase P)
            {
                if (P == null || !Added.HasSubscribers) return;
                Added.Raise(new PlayerEvent(P));
            }

            private static void UnregisterPlayer_Prefix(PlayerSetupBase P)
            {
                if (P == null || !Removed.HasSubscribers) return;
                Removed.Raise(new PlayerEvent(P));
            }

            private static void DoTheSpawn_Postfix(bAvatar __instance)
            {
                // Remote players on a host: the server-side avatar spawns the model itself. (Client and "other player"
                // avatar components also call DoTheSpawn; they are skipped so each spawn is reported once.)
                if (!(__instance is sAvatar) || !AvatarSpawned.HasSubscribers) return;
                AvatarSpawned.Raise(new PlayerEvent(PlayerOf(__instance)));
            }

            private static void LocalAvatarSpawned_Postfix(sAvatar __instance)
            {
                if (__instance == null || !AvatarSpawned.HasSubscribers) return;
                AvatarSpawned.Raise(new PlayerEvent(PlayerOf(__instance)));
            }

            [ThreadStatic] private static bool _dyingFromDamage;

            private static void Die_Prefix() => _dyingFromDamage = true;

            private static void Die_Finalizer() => _dyingFromDamage = false;

            private static void ForceDie_Prefix(sHealth __instance, out bool __state)
            {
                __state = _dead != null && __instance != null && _dead(__instance);
            }

            private static void ForceDie_Postfix(sHealth __instance, bool __state)
            {
                if (__state || __instance == null || _dead == null || !AvatarDied.HasSubscribers) return;
                if (!_dead(__instance)) return; // didn't actually die
                AvatarDied.Raise(new AvatarDiedEvent(PlayerOf(__instance), _dyingFromDamage));
            }

            private static bool RequestToEnterChair_Prefix(sTeleportAndHold __instance, Chair c)
            {
                if (c == null) return true;
                if (SeatEntering.HasSubscribers)
                {
                    var e = new SeatEnteringEvent(PlayerOf(__instance), c);
                    SeatEntering.Raise(e);
                    if (e.IsCancelled) return false;
                }
                // Moving straight from one seat to another doesn't go through "leave", so report it here.
                var current = __instance?.Chair;
                if (current != null && current != c && SeatLeft.HasSubscribers)
                    SeatLeft.Raise(new SeatEvent(PlayerOf(__instance), current));
                return true;
            }

            private static void EnterChair_Postfix(bTeleportAndHold __instance, Chair theChair)
            {
                // The host also mirrors the move on its client-side component; report the server's one only.
                if (!(__instance is sTeleportAndHold) || theChair == null || !SeatEntered.HasSubscribers) return;
                if (__instance.Chair != theChair) return; // the game couldn't move the character
                SeatEntered.Raise(new SeatEvent(PlayerOf(__instance), theChair));
            }

            private static void LeaveChair_Prefix(bTeleportAndHold __instance)
            {
                if (!(__instance is sTeleportAndHold) || !SeatLeft.HasSubscribers) return;
                var chair = __instance.Chair;
                if (chair == null) return;
                SeatLeft.Raise(new SeatEvent(PlayerOf(__instance), chair));
            }

            private static bool PlacePrefab_Prefix(Blueprint PREFAB, AllConstruct C, Vector3i LP, ObjectId team, PrefabCommand source)
            {
                // Undo of erase/prefab/disconnect commands also goes through here (with no source command): never cancel those.
                if (PREFAB == null || C == null || source == null || !PrefabPlacing.HasSubscribers) return true;
                var e = new PrefabPlacingEvent(PREFAB, C, LP, team);
                PrefabPlacing.Raise(e);
                return !e.IsCancelled;
            }

            private static void PlacePrefabLocal_Postfix(Blueprint PREFAB, AllConstruct C, Vector3i LP, ObjectId team, ref IList<Vector3i> placedBlocks)
            {
                if (PREFAB == null || C == null || !PrefabPlaced.HasSubscribers) return;
                PrefabPlaced.Raise(new PrefabPlacedEvent(PREFAB, C, LP, team, placedBlocks?.Count ?? 0));
            }

            private static bool ChatSend_Prefix(string name, ref string messageToSend, ChatMode mode, IConnectionData sender)
            {
                if (!ChatSending.HasSubscribers) return true;
                var e = new ChatSendingEvent(name, messageToSend, mode == ChatMode.Team, sender);
                ChatSending.Raise(e);
                if (e.IsCancelled) return false;
                messageToSend = e.Message ?? string.Empty;
                return true;
            }

            private static void AddChatEntry_Postfix(string name, string msg, ChatMode chatMode, IConnectionData player)
            {
                if (!ChatReceived.HasSubscribers) return;
                ChatReceived.Raise(new ChatReceivedEvent(name, msg, chatMode == ChatMode.Team, player));
            }

            private static PlayerSetupBase PlayerOf(Component c)
            {
                try
                {
                    return c != null ? c.GetComponentInParent<PlayerSetupBase>() : null;
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    /// <summary>A player's character died or was removed.</summary>
    public sealed class AvatarDiedEvent : PlayerEvent
    {
        internal AvatarDiedEvent(PlayerSetupBase player, bool killed)
            : base(player)
        {
            Killed = killed;
        }

        /// <summary>True when it was killed by damage; false when the player removed it (spawn/remove key, map view).</summary>
        public bool Killed { get; }
    }

    /// <summary>An event about one player.</summary>
    public class PlayerEvent : GameEvent
    {
        internal PlayerEvent(PlayerSetupBase player)
        {
            Player = player;
        }

        /// <summary>The game's player object (may be null if the game couldn't tell which player). Game object.</summary>
        public PlayerSetupBase Player { get; }

        /// <summary>The player's display name in multiplayer; "Local player" in single player.</summary>
        public string Name
        {
            get
            {
                try
                {
                    var connection = Player?.myNetworkPlayerOnServer;
                    if (connection != null && !string.IsNullOrEmpty(connection.DisplayName)) return connection.DisplayName;
                }
                catch
                {
                    // fall through
                }
                return Player != null && Player.IsLocal() ? "Local player" : "Player";
            }
        }

        /// <summary>True for the player on this machine.</summary>
        public bool IsLocal => Player != null && Player.IsLocal();

        public ObjectId Team => Player != null ? Player.team : ObjectId.NoLinkage;
    }

    public sealed class SeatEvent : PlayerEvent
    {
        internal SeatEvent(PlayerSetupBase player, Chair seat) : base(player)
        {
            Seat = seat;
        }

        /// <summary>The seat block (cockpit, chair).</summary>
        public Chair Seat { get; }

        /// <summary>The craft the seat is on.</summary>
        public MainConstruct Construct => Seat?.MainConstruct as MainConstruct;
    }

    public sealed class SeatEnteringEvent : CancellableEvent
    {
        internal SeatEnteringEvent(PlayerSetupBase player, Chair seat)
        {
            Player = player;
            Seat = seat;
        }

        public PlayerSetupBase Player { get; }

        public Chair Seat { get; }

        public MainConstruct Construct => Seat?.MainConstruct as MainConstruct;
    }

    public sealed class PrefabPlacingEvent : CancellableEvent
    {
        internal PrefabPlacingEvent(Blueprint prefab, AllConstruct construct, Vector3i position, ObjectId team)
        {
            Prefab = prefab;
            Construct = construct;
            LocalPosition = position;
            Team = team;
        }

        /// <summary>The prefab's blueprint. Game object.</summary>
        public Blueprint Prefab { get; }

        /// <summary>The prefab's name, or null when it has none (a copied selection that was never saved as a prefab).</summary>
        public string PrefabName => EventHelpers.PrefabName(Prefab);

        /// <summary>The construct it is being placed on.</summary>
        public AllConstruct Construct { get; }

        public Vector3i LocalPosition { get; }

        public ObjectId Team { get; }
    }

    public sealed class PrefabPlacedEvent : GameEvent
    {
        internal PrefabPlacedEvent(Blueprint prefab, AllConstruct construct, Vector3i position, ObjectId team, int blocksPlaced)
        {
            Prefab = prefab;
            Construct = construct;
            LocalPosition = position;
            Team = team;
            BlocksPlaced = blocksPlaced;
        }

        public Blueprint Prefab { get; }

        /// <summary>The prefab's name, or null when it has none (a copied selection that was never saved as a prefab).</summary>
        public string PrefabName => EventHelpers.PrefabName(Prefab);

        public AllConstruct Construct { get; }

        public Vector3i LocalPosition { get; }

        public ObjectId Team { get; }

        /// <summary>How many blocks the prefab placed.</summary>
        public int BlocksPlaced { get; }
    }

    public sealed class ChatSendingEvent : CancellableEvent
    {
        internal ChatSendingEvent(string name, string message, bool teamOnly, IConnectionData sender)
        {
            Name = name;
            Message = message;
            TeamOnly = teamOnly;
            Sender = sender;
        }

        /// <summary>The sender's display name.</summary>
        public string Name { get; }

        /// <summary>The message text. Set it to change what is sent.</summary>
        public string Message { get; set; }

        /// <summary>True for team chat.</summary>
        public bool TeamOnly { get; }

        /// <summary>The sending player's connection (may be null).</summary>
        public IConnectionData Sender { get; }
    }

    public sealed class ChatReceivedEvent : GameEvent
    {
        internal ChatReceivedEvent(string name, string message, bool teamOnly, IConnectionData sender)
        {
            Name = name;
            Message = message;
            TeamOnly = teamOnly;
            Sender = sender;
        }

        public string Name { get; }

        public string Message { get; }

        public bool TeamOnly { get; }

        public IConnectionData Sender { get; }
    }
}
