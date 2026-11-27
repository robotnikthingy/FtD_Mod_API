using System;
using System.Collections.Generic;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Id;
using BrilliantSkies.Ftd.Constructs.Modules.Main.Scrapping;
using BrilliantSkies.Ftd.Persistence.Inits;
using FtdModApi.Patching;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>Craft (construct) lifecycle: spawned, removed, in/out of play, scrapped, defeated, captured, turrets.</summary>
        public static class Constructs
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static readonly EventHook SpawnHook = new EventHook(Features.EventConstructLifecycle, () => new[]
            {
                new PatchSpec
                {
                    Id = "BlueprintInitialisation.Run: Constructs.Spawned",
                    FeatureId = Features.EventConstructLifecycle,
                    Target = () => EventHelpers.Declared(typeof(BlueprintInitialisation), "Run"),
                    Postfix = PatchSpec.Hook(typeof(Constructs), nameof(Run_Postfix))
                }
            });

            private static readonly EventHook DespawnHook = new EventHook(Features.EventConstructLifecycle, () => new[]
            {
                new PatchSpec
                {
                    Id = "MainConstruct.DestroyCompletely: Constructs.Despawned",
                    FeatureId = Features.EventConstructLifecycle,
                    Target = () => EventHelpers.Declared(typeof(MainConstruct), "DestroyCompletely"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(DestroyCompletely_Prefix))
                }
            });

            // Marks "MakeDeadBlueprint is running", so its out-of-play step is reported as DeadBlueprint, not a pull.
            private static readonly EventHook DeadBlueprintHook = new EventHook(Features.EventConstructLifecycle, () => new[]
            {
                new PatchSpec
                {
                    Id = "Force.MakeDeadBlueprint: despawn reason",
                    FeatureId = Features.EventConstructLifecycle,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(Force), "MakeDeadBlueprint"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(MakeDeadBlueprint_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Constructs), nameof(MakeDeadBlueprint_Finalizer))
                }
            });

            private static readonly EventHook PlayHook = new EventHook(Features.EventConstructPlay, () => new[]
            {
                new PatchSpec
                {
                    Id = "ForcePlayingAndPulling.PlayForce: Constructs.Playing",
                    FeatureId = Features.EventConstructPlay,
                    Target = () => EventHelpers.Declared(typeof(ForcePlayingAndPulling), "PlayForce"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(PlayForce_Prefix))
                },
                new PatchSpec
                {
                    Id = "Force.MakeInPlay: Constructs.Played",
                    FeatureId = Features.EventConstructPlay,
                    Target = () => EventHelpers.Declared(typeof(Force), "MakeInPlay"),
                    Postfix = PatchSpec.Hook(typeof(Constructs), nameof(MakeInPlay_Postfix))
                }
            });

            private static readonly EventHook PullHook = new EventHook(Features.EventConstructPlay, () => new[]
            {
                new PatchSpec
                {
                    Id = "ForcePlayingAndPulling.PullForce: Constructs.Pulling",
                    FeatureId = Features.EventConstructPlay,
                    Target = () => EventHelpers.Declared(typeof(ForcePlayingAndPulling), "PullForce"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(PullForce_Prefix))
                },
                new PatchSpec
                {
                    Id = "Force.MakeOutOfPlay: Constructs.Pulled",
                    FeatureId = Features.EventConstructPlay,
                    Target = () => EventHelpers.Declared(typeof(Force), "MakeOutOfPlay"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(MakeOutOfPlay_Prefix))
                }
            });

            private static readonly EventHook ScrapHook = new EventHook(Features.EventConstructScrap, () => new[]
            {
                new PatchSpec
                {
                    Id = "MainConstructScrapping.StartScrap: Constructs.Scrapping/Scrapped",
                    FeatureId = Features.EventConstructScrap,
                    Target = () => EventHelpers.Declared(typeof(MainConstructScrapping), "StartScrap"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(StartScrap_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Constructs), nameof(StartScrap_Postfix))
                }
            });

            private static readonly EventHook DefeatHook = new EventHook(Features.EventConstructScrap, () => new[]
            {
                new PatchSpec
                {
                    Id = "EnemyConstructableStateChecker.DisposeOf: Constructs.Defeating",
                    FeatureId = Features.EventConstructScrap,
                    Target = () => EventHelpers.Declared(typeof(EnemyConstructableStateChecker), "DisposeOf"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(DisposeOf_Prefix))
                }
            });

            private static readonly EventHook TeamChangingHook = new EventHook(Features.EventConstructTeam, () => new[]
            {
                new PatchSpec
                {
                    Id = "FleetForceChangeSync._ChangeForceTeam: Constructs.TeamChanging",
                    FeatureId = Features.EventConstructTeam,
                    Target = () => EventHelpers.Declared(typeof(FleetForceChangeSync), "_ChangeForceTeam"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(ChangeForceTeam_Prefix))
                }
            });

            private static readonly EventHook TeamChangedHook = new EventHook(Features.EventConstructTeam, () => new[]
            {
                new PatchSpec
                {
                    Id = "Force.SetTeam: Constructs.TeamChanged",
                    FeatureId = Features.EventConstructTeam,
                    Target = () => EventHelpers.Declared(typeof(Force), "SetTeam"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(SetTeam_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Constructs), nameof(SetTeam_Postfix))
                }
            });

            private static readonly EventHook SubAddedHook = new EventHook(Features.EventSubconstructs, () => new[]
            {
                new PatchSpec
                {
                    Id = "ConstructBasics.InitialiseSubConstructable: Constructs.SubconstructAdded",
                    FeatureId = Features.EventSubconstructs,
                    Target = () => EventHelpers.Declared(typeof(ConstructBasics), "InitialiseSubConstructable"),
                    Postfix = PatchSpec.Hook(typeof(Constructs), nameof(InitialiseSubConstructable_Postfix))
                }
            });

            private static readonly EventHook SubRemovedHook = new EventHook(Features.EventSubconstructs, () => new[]
            {
                new PatchSpec
                {
                    Id = "SubConstruct.DestroyCompletely: Constructs.SubconstructRemoved",
                    FeatureId = Features.EventSubconstructs,
                    Target = () => EventHelpers.Declared(typeof(SubConstruct), "DestroyCompletely"),
                    Prefix = PatchSpec.Hook(typeof(Constructs), nameof(SubDestroyCompletely_Prefix))
                }
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>
            /// A craft appeared in the world: spawned in the designer, put into play in a campaign, spawned from a blueprint
            /// or prefab, split off by a separator, or a drone. Raised once it is fully set up. All machines.
            /// </summary>
            public static EventChannel<ConstructSpawnedEvent> Spawned { get; } = new EventChannel<ConstructSpawnedEvent>(
                "Constructs.Spawned", EventSide.All, "A craft appeared in the world (any way).", SpawnHook);

            /// <summary>
            /// A craft is being removed from the world (wiped, destroyed after scrapping, or taken out of play).
            /// Raised just before removal, while the craft can still be read. All machines.
            /// </summary>
            public static EventChannel<ConstructDespawnedEvent> Despawned { get; } = new EventChannel<ConstructDespawnedEvent>(
                "Constructs.Despawned", EventSide.All, "A craft is about to be removed from the world.", DespawnHook, DeadBlueprintHook);

            /// <summary>A campaign force is about to be put into play (spawned as a real craft). Cancellable. Host.</summary>
            public static EventChannel<ConstructPlayingEvent> Playing { get; } = new EventChannel<ConstructPlayingEvent>(
                "Constructs.Playing", EventSide.Host, "A force is about to be put into play.", PlayHook);

            /// <summary>A campaign force was put into play; <c>Construct</c> is the new craft. All machines.</summary>
            public static EventChannel<ConstructPlayedEvent> Played { get; } = new EventChannel<ConstructPlayedEvent>(
                "Constructs.Played", EventSide.All, "A force was put into play.", PlayHook);

            /// <summary>A craft is about to be taken out of play (back to the strategic map). Cancellable. Host.</summary>
            public static EventChannel<ConstructPullingEvent> Pulling { get; } = new EventChannel<ConstructPullingEvent>(
                "Constructs.Pulling", EventSide.Host, "A force is about to be taken out of play.", PullHook);

            /// <summary>
            /// A craft is being taken out of play; raised just before it is converted back to a blueprint. Not raised when the
            /// force is being deleted or turned into a dead blueprint (see <see cref="Despawned"/>'s reason). All machines.
            /// </summary>
            public static EventChannel<ConstructPulledEvent> Pulled { get; } = new EventChannel<ConstructPulledEvent>(
                "Constructs.Pulled", EventSide.All, "A force is being taken out of play.", PullHook, DeadBlueprintHook);

            /// <summary>A craft is about to be scrapped (for any reason, including auto-scrap). Cancellable. Host.</summary>
            public static EventChannel<ConstructScrappingEvent> Scrapping { get; } = new EventChannel<ConstructScrappingEvent>(
                "Constructs.Scrapping", EventSide.Host, "A craft is about to be scrapped.", ScrapHook);

            /// <summary>A craft started scrapping. All machines.</summary>
            public static EventChannel<ConstructScrappedEvent> Scrapped { get; } = new EventChannel<ConstructScrappedEvent>(
                "Constructs.Scrapped", EventSide.All, "A craft started scrapping.", ScrapHook);

            /// <summary>
            /// The game's clean-up rules decided a craft is defeated (AI dead, sunk, too damaged, out of control, ...) and its
            /// countdown ran out; it is about to be scrapped. Cancellable: cancelling resets the countdown. Host.
            /// </summary>
            public static EventChannel<ConstructDefeatingEvent> Defeating { get; } = new EventChannel<ConstructDefeatingEvent>(
                "Constructs.Defeating", EventSide.Host, "A craft is about to be auto-scrapped by the clean-up rules.", DefeatHook);

            /// <summary>A force is about to change team (captured or handed over). Cancellable. Host.</summary>
            public static EventChannel<ConstructTeamChangingEvent> TeamChanging { get; } = new EventChannel<ConstructTeamChangingEvent>(
                "Constructs.TeamChanging", EventSide.Host, "A force is about to change team.", TeamChangingHook);

            /// <summary>A force changed team. All machines.</summary>
            public static EventChannel<ConstructTeamChangedEvent> TeamChanged { get; } = new EventChannel<ConstructTeamChangedEvent>(
                "Constructs.TeamChanged", EventSide.All, "A force changed team.", TeamChangedHook);

            /// <summary>
            /// A turret, spin block or other subconstruct was added: placed by a player (<c>IsNewPlacement</c>) or created
            /// while a craft spawns from its blueprint. All machines.
            /// </summary>
            public static EventChannel<SubconstructAddedEvent> SubconstructAdded { get; } = new EventChannel<SubconstructAddedEvent>(
                "Constructs.SubconstructAdded", EventSide.All, "A turret or spin block was added.", SubAddedHook);

            /// <summary>A subconstruct is being removed (removed by a player, or its craft is removed). Raised just before. All machines.</summary>
            public static EventChannel<SubconstructRemovedEvent> SubconstructRemoved { get; } = new EventChannel<SubconstructRemovedEvent>(
                "Constructs.SubconstructRemoved", EventSide.All, "A turret or spin block is about to be removed.", SubRemovedHook);

            // ---- patch bodies ------------------------------------------------------------------------------

            private static void Run_Postfix(MainConstruct __result)
            {
                if (__result == null || !Spawned.HasSubscribers) return;
                Spawned.Raise(new ConstructSpawnedEvent(__result));
            }

            [ThreadStatic] private static int _makingDeadBlueprint;

            private static void MakeDeadBlueprint_Prefix() => _makingDeadBlueprint++;

            private static void MakeDeadBlueprint_Finalizer()
            {
                if (_makingDeadBlueprint > 0) _makingDeadBlueprint--;
            }

            private static void DestroyCompletely_Prefix(MainConstruct __instance, DestroyReason reason)
            {
                if (__instance == null || __instance.Destroyed || !Despawned.HasSubscribers) return;
                Despawned.Raise(new ConstructDespawnedEvent(__instance, ReasonFor(__instance, reason)));
            }

            private static DespawnReason ReasonFor(MainConstruct construct, DestroyReason reason)
            {
                Force force = null;
                try
                {
                    force = construct.GetForce();
                }
                catch
                {
                    // fall through to the game's own reason
                }
                // Deleting a force (e.g. "destroy all vehicles", campaign clean-up) sets Deleted, then takes it out of play.
                if (force != null && force.Deleted) return DespawnReason.Deleted;
                if (reason == DestroyReason.OutOfPlay) return _makingDeadBlueprint > 0 ? DespawnReason.DeadBlueprint : DespawnReason.OutOfPlay;
                return DespawnReason.Wiped;
            }

            private static bool PlayForce_Prefix(Force f, PlayPullRequestSource source)
            {
                if (f == null || Net.IsClient || !Playing.HasSubscribers) return true;
                var e = new ConstructPlayingEvent(f, (RequestSource)(int)source);
                Playing.Raise(e);
                return !e.IsCancelled;
            }

            private static void MakeInPlay_Postfix(Force __instance)
            {
                if (__instance?.C == null || !Played.HasSubscribers) return;
                Played.Raise(new ConstructPlayedEvent(__instance));
            }

            private static bool PullForce_Prefix(Force f, PlayPullRequestSource source)
            {
                if (f == null || Net.IsClient || !Pulling.HasSubscribers) return true;
                var e = new ConstructPullingEvent(f, (RequestSource)(int)source);
                Pulling.Raise(e);
                return !e.IsCancelled;
            }

            private static void MakeOutOfPlay_Prefix(Force __instance)
            {
                // Deletion and dead-blueprint conversion also go out of play first; those aren't pulls.
                if (__instance == null || !Pulled.HasSubscribers || __instance.Deleted || _makingDeadBlueprint > 0) return;
                Pulled.Raise(new ConstructPulledEvent(__instance));
            }

            // __state: true when the game will really start scrapping (so the postfix raises Scrapped).
            private static bool StartScrap_Prefix(MainConstructScrapping __instance, enumScrapType scrapType, enumDumpType dumpType, out bool __state)
            {
                __state = false;
                var construct = __instance?._construct;
                if (construct == null || construct.State == enumConstructableState.scrapping) return true;
                if (!Net.IsClient && Scrapping.HasSubscribers)
                {
                    var e = new ConstructScrappingEvent(construct, MapScrap(scrapType), dumpType == enumDumpType.Dump);
                    Scrapping.Raise(e);
                    if (e.IsCancelled)
                    {
                        // An auto-scrap countdown would otherwise call StartScrap again on every check.
                        if (construct.Warning.State != StateCheck.Okay) construct.Warning.NullWarning();
                        return false;
                    }
                }
                __state = true;
                return true;
            }

            private static void StartScrap_Postfix(MainConstructScrapping __instance, enumScrapType scrapType, enumDumpType dumpType, bool __state)
            {
                if (!__state || !Scrapped.HasSubscribers) return;
                var construct = __instance?._construct;
                if (construct == null) return;
                Scrapped.Raise(new ConstructScrappedEvent(construct, MapScrap(scrapType), dumpType == enumDumpType.Dump));
            }

            private static bool DisposeOf_Prefix(MainConstruct C, bool playerTeam)
            {
                if (C == null || Net.IsClient || !Defeating.HasSubscribers) return true;
                // DisposeOf only acts on these states; don't raise for the others.
                if (C.State != enumConstructableState.normal && C.State != enumConstructableState.frozen && C.State != enumConstructableState.dead) return true;
                var e = new ConstructDefeatingEvent(C, MapCause(C.Warning.State), playerTeam);
                Defeating.Raise(e);
                if (!e.IsCancelled) return true;
                C.Warning.NullWarning(); // restart the countdown so the check doesn't fire again next tick
                return false;
            }

            private static bool ChangeForceTeam_Prefix(Force f, ObjectId team, bool scuttle)
            {
                if (f == null || Net.IsClient || !TeamChanging.HasSubscribers || f.FactionId == team) return true;
                var e = new ConstructTeamChangingEvent(f, f.FactionId, team, scuttle);
                TeamChanging.Raise(e);
                return !e.IsCancelled;
            }

            private static void SetTeam_Prefix(Force __instance, out ObjectId __state)
            {
                __state = __instance != null ? __instance.FactionId : ObjectId.NoLinkage;
            }

            private static void SetTeam_Postfix(Force __instance, ObjectId newFactionId, bool scuttle, ObjectId __state)
            {
                if (__instance == null || !TeamChanged.HasSubscribers || __state == newFactionId) return;
                TeamChanged.Raise(new ConstructTeamChangedEvent(__instance, __state, newFactionId, scuttle));
            }

            private static void InitialiseSubConstructable_Postfix(SubConstruct SC, bool startingBlockSpawn)
            {
                if (SC == null || !SubconstructAdded.HasSubscribers) return;
                SubconstructAdded.Raise(new SubconstructAddedEvent(SC, startingBlockSpawn));
            }

            private static void SubDestroyCompletely_Prefix(SubConstruct __instance)
            {
                if (__instance == null || __instance.Destroyed || !SubconstructRemoved.HasSubscribers) return;
                SubconstructRemoved.Raise(new SubconstructRemovedEvent(__instance));
            }

            private static ScrapKind MapScrap(enumScrapType t)
            {
                switch (t)
                {
                    case enumScrapType.dontdestroy: return ScrapKind.KeepWreck;
                    case enumScrapType.makedeadblueprint: return ScrapKind.KeepAsDeadBlueprint;
                    default: return ScrapKind.Destroy;
                }
            }

            private static DefeatCause MapCause(StateCheck s)
            {
                switch (s)
                {
                    case StateCheck.DamagedAndSunk: return DefeatCause.Sunk;
                    case StateCheck.DamageThresholdReached: return DefeatCause.TooDamaged;
                    case StateCheck.TooFewBlocksAlive: return DefeatCause.TooFewBlocks;
                    case StateCheck.DamagedInEnemyTerritory: return DefeatCause.DamagedInEnemyTerritory;
                    case StateCheck.NotProtected: return DefeatCause.AiDead;
                    case StateCheck.NoHeartstone: return DefeatCause.NoHeartstone;
                    case StateCheck.FleeingCombat: return DefeatCause.FleeingCombat;
                    case StateCheck.OutOfControl: return DefeatCause.OutOfControl;
                    default: return DefeatCause.Other;
                }
            }
        }
    }

    /// <summary>Why a craft is being removed from the world.</summary>
    public enum DespawnReason
    {
        /// <summary>Wiped or destroyed (including the end of scrapping).</summary>
        Wiped,

        /// <summary>Taken out of play back to the strategic map.</summary>
        OutOfPlay,

        /// <summary>The force was deleted (e.g. "destroy all vehicles" in the designer, or campaign clean-up).</summary>
        Deleted,

        /// <summary>Campaign: the craft was turned into a dead blueprint (e.g. the player's defeated craft).</summary>
        DeadBlueprint
    }

    /// <summary>Who asked to put a force into or out of play.</summary>
    public enum RequestSource
    {
        Player = 0,
        Game = 1,
        /// <summary>A multiplayer client's request, handled on the host.</summary>
        Server = 2
    }

    /// <summary>What happens to a scrapped craft.</summary>
    public enum ScrapKind
    {
        /// <summary>The craft is removed.</summary>
        Destroy,

        /// <summary>The craft is scrapped but its wreck is kept.</summary>
        KeepWreck,

        /// <summary>Campaign: the craft is kept as a dead blueprint (repairable later).</summary>
        KeepAsDeadBlueprint
    }

    /// <summary>Which clean-up rule defeated a craft.</summary>
    public enum DefeatCause
    {
        Other,
        Sunk,
        TooDamaged,
        TooFewBlocks,
        DamagedInEnemyTerritory,
        /// <summary>No working AI (the craft is "not protected").</summary>
        AiDead,
        NoHeartstone,
        FleeingCombat,
        OutOfControl
    }

    /// <summary>Common properties of events about one craft.</summary>
    public abstract class ConstructEventBase : GameEvent
    {
        protected ConstructEventBase(MainConstruct construct)
        {
            Construct = construct;
        }

        /// <summary>The craft. Game object: its members can change between game versions.</summary>
        public MainConstruct Construct { get; }

        public string Name => Construct?.GetName();

        /// <summary>The craft's unique id in this game session.</summary>
        public int UniqueId => Construct?.UniqueId ?? 0;

        public ObjectId Team => Construct != null ? Construct.GetTeam() : ObjectId.NoLinkage;
    }

    /// <summary>Common properties of cancellable events about one craft.</summary>
    public abstract class CancellableConstructEventBase : CancellableEvent
    {
        protected CancellableConstructEventBase(MainConstruct construct)
        {
            Construct = construct;
        }

        /// <summary>The craft. Game object: its members can change between game versions.</summary>
        public MainConstruct Construct { get; }

        public string Name => Construct?.GetName();

        public int UniqueId => Construct?.UniqueId ?? 0;

        public ObjectId Team => Construct != null ? Construct.GetTeam() : ObjectId.NoLinkage;
    }

    public sealed class ConstructSpawnedEvent : ConstructEventBase
    {
        internal ConstructSpawnedEvent(MainConstruct construct) : base(construct)
        {
        }
    }

    public sealed class ConstructDespawnedEvent : ConstructEventBase
    {
        internal ConstructDespawnedEvent(MainConstruct construct, DespawnReason reason) : base(construct)
        {
            Reason = reason;
        }

        public DespawnReason Reason { get; }
    }

    public sealed class ConstructPlayingEvent : CancellableEvent
    {
        internal ConstructPlayingEvent(Force force, RequestSource source)
        {
            Force = force;
            Source = source;
        }

        /// <summary>The campaign force (out of play; there is no craft yet).</summary>
        public Force Force { get; }

        public RequestSource Source { get; }

        public string Name => Force?.Name;

        public ObjectId Team => Force != null ? Force.FactionId : ObjectId.NoLinkage;
    }

    public sealed class ConstructPlayedEvent : ConstructEventBase
    {
        internal ConstructPlayedEvent(Force force) : base(force?.C)
        {
            Force = force;
        }

        public Force Force { get; }
    }

    public sealed class ConstructPullingEvent : CancellableConstructEventBase
    {
        internal ConstructPullingEvent(Force force, RequestSource source) : base(force?.C)
        {
            Force = force;
            Source = source;
        }

        public Force Force { get; }

        public RequestSource Source { get; }
    }

    public sealed class ConstructPulledEvent : ConstructEventBase
    {
        internal ConstructPulledEvent(Force force) : base(force?.C)
        {
            Force = force;
        }

        public Force Force { get; }
    }

    public sealed class ConstructScrappingEvent : CancellableConstructEventBase
    {
        internal ConstructScrappingEvent(MainConstruct construct, ScrapKind kind, bool dumpsResources) : base(construct)
        {
            Kind = kind;
            DumpsResources = dumpsResources;
        }

        public ScrapKind Kind { get; }

        /// <summary>True when the craft's materials are dumped as a pickup.</summary>
        public bool DumpsResources { get; }
    }

    public sealed class ConstructScrappedEvent : ConstructEventBase
    {
        internal ConstructScrappedEvent(MainConstruct construct, ScrapKind kind, bool dumpsResources) : base(construct)
        {
            Kind = kind;
            DumpsResources = dumpsResources;
        }

        public ScrapKind Kind { get; }

        public bool DumpsResources { get; }
    }

    public sealed class ConstructDefeatingEvent : CancellableConstructEventBase
    {
        internal ConstructDefeatingEvent(MainConstruct construct, DefeatCause cause, bool playerTeam) : base(construct)
        {
            Cause = cause;
            IsPlayerTeam = playerTeam;
        }

        public DefeatCause Cause { get; }

        /// <summary>True for the player's own craft (in a campaign they become dead blueprints instead of being destroyed).</summary>
        public bool IsPlayerTeam { get; }
    }

    public sealed class ConstructTeamChangingEvent : CancellableConstructEventBase
    {
        internal ConstructTeamChangingEvent(Force force, ObjectId oldTeam, ObjectId newTeam, bool scuttle) : base(force?.C)
        {
            Force = force;
            OldTeam = oldTeam;
            NewTeam = newTeam;
            Scuttle = scuttle;
        }

        public Force Force { get; }

        public ObjectId OldTeam { get; }

        public ObjectId NewTeam { get; }

        public bool Scuttle { get; }
    }

    public sealed class ConstructTeamChangedEvent : ConstructEventBase
    {
        internal ConstructTeamChangedEvent(Force force, ObjectId oldTeam, ObjectId newTeam, bool scuttle) : base(force?.C)
        {
            Force = force;
            OldTeam = oldTeam;
            NewTeam = newTeam;
            Scuttle = scuttle;
        }

        /// <summary>The force (its craft, <c>Construct</c>, is null when it is out of play).</summary>
        public Force Force { get; }

        public ObjectId OldTeam { get; }

        public ObjectId NewTeam { get; }

        public bool Scuttle { get; }
    }

    public sealed class SubconstructAddedEvent : ConstructEventBase
    {
        internal SubconstructAddedEvent(SubConstruct sub, bool isNewPlacement) : base(sub?.Main)
        {
            Subconstruct = sub;
            IsNewPlacement = isNewPlacement;
        }

        /// <summary>The turret, spin block or piston subconstruct.</summary>
        public SubConstruct Subconstruct { get; }

        /// <summary>The construct it sits on (the craft, or another subconstruct).</summary>
        public AllConstruct Parent => Subconstruct?.Parent;

        /// <summary>True when a player just placed it; false when it was created while a craft spawned from its blueprint.</summary>
        public bool IsNewPlacement { get; }
    }

    public sealed class SubconstructRemovedEvent : ConstructEventBase
    {
        internal SubconstructRemovedEvent(SubConstruct sub) : base(sub?.Main)
        {
            Subconstruct = sub;
        }

        public SubConstruct Subconstruct { get; }

        public AllConstruct Parent => Subconstruct?.Parent;
    }
}
