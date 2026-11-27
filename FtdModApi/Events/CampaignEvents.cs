using System;
using System.Collections.Generic;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Id;
using BrilliantSkies.Core.Types;
using BrilliantSkies.FromTheDepths.Game.Battle;
using BrilliantSkies.FromTheDepths.Planets;
using BrilliantSkies.Ftd.Game.BattleOrchestration.EndingOutcomes;
using BrilliantSkies.Ftd.Game.FactionBattleStarting;
using BrilliantSkies.Ftd.Game.FactionBattleStarting.Blockading;
using BrilliantSkies.Ftd.Game.FactionBattleStarting.FactionVsFaction;
using BrilliantSkies.Ftd.Game.Progression.AiInterface;
using BrilliantSkies.Ftd.Modes.Campaign.AiSpawner;
using BrilliantSkies.Ftd.Modes.Campaign.AreaCapturing;
using BrilliantSkies.Ftd.Modes.Campaign.AreaNeutralization;
using BrilliantSkies.Ftd.Planets.Factions.Designs;
using BrilliantSkies.Ftd.Planets.Instances.Factions;
using BrilliantSkies.Ftd.Planets.World;
using BrilliantSkies.Ftd.Planets.World.Layouts;
using BrilliantSkies.StrategicAi.Orders;
using FtdModApi.Patching;
using UnityEngine;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>
        /// The campaign: fleets, territory, diplomacy, battles (yours and AI-vs-AI), blockades, and AI shipyards.
        /// These only happen in campaign games (and some in adventure).
        /// </summary>
        public static partial class Campaign
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static readonly EventHook FleetHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    Id = "FleetManager.AddFleetToList: Campaign.FleetCreated",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(FleetManager), "AddFleetToList"),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(AddFleet_Postfix))
                },
                new PatchSpec
                {
                    Id = "FleetManager.RemoveFleet: Campaign.FleetRemoved",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(FleetManager), "RemoveFleet"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(RemoveFleet_Prefix))
                }
            });

            private static readonly EventHook TerritoryHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    Id = "CampaignSync.ChangeBoardOwner: Campaign.TerritoryChanging",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(CampaignSync), "ChangeBoardOwner"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(ChangeBoardOwner_Prefix))
                },
                new PatchSpec
                {
                    Id = "CampaignSync._ChangeBoardOwner: Campaign.TerritoryChanged",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(CampaignSync), "_ChangeBoardOwner"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(ChangeBoardOwnerLocal_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(ChangeBoardOwnerLocal_Postfix))
                },
                new PatchSpec
                {
                    Id = "AreaCapture.LocationCaptured: territory change cause",
                    FeatureId = Features.EventCampaign,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(AreaCapture), "LocationCaptured"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(Captured_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Campaign), nameof(Cause_Finalizer))
                },
                new PatchSpec
                {
                    Id = "AreaNeutralizer.NeutralizeDisconnected: territory change cause",
                    FeatureId = Features.EventCampaign,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(AreaNeutralizer), "NeutralizeDisconnected"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(Disconnected_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Campaign), nameof(Cause_Finalizer))
                }
            });

            private static readonly EventHook EliminatedHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    // Also tags the territory changes it makes as FactionEliminated.
                    Id = "AreaNeutralizer.NeutralizeEverything: Campaign.FactionEliminated",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(AreaNeutralizer), "NeutralizeEverything"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(NeutralizeEverything_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Campaign), nameof(Cause_Finalizer))
                }
            });

            private static readonly EventHook RelationsHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    Id = "FactionRelations.ChangeRelationship(int, int, float): Campaign.RelationsChanged",
                    FeatureId = Features.EventCampaign,
                    Target = () => PatchSpec.Method(typeof(FactionRelations), "ChangeRelationship", typeof(int), typeof(int), typeof(float)),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(Relation_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(Relation_Postfix))
                },
                new PatchSpec
                {
                    // Declaring war and land grabs cap the relationship through this.
                    Id = "FactionRelations.ImposeUpperLimit: Campaign.RelationsChanged",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(FactionRelations), "ImposeUpperLimit"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(Relation_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(Relation_Postfix))
                },
                new PatchSpec
                {
                    // Multiplayer clients receive the host's changes through this.
                    Id = "FactionRelations.SetRelationship: Campaign.RelationsChanged",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(FactionRelations), "SetRelationship"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(Relation_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(Relation_Postfix))
                }
            });

            private static readonly EventHook BattleHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    Id = "BattleOrchestratorSync._BeginSetupPhase: Campaign.BattlePreparing",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(BattleOrchestratorSync), "_BeginSetupPhase"),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(BeginSetup_Postfix))
                },
                new PatchSpec
                {
                    Id = "BattleOrchestratorSync._BeginBattle: Campaign.BattleStarted",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(BattleOrchestratorSync), "_BeginBattle"),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(BeginBattle_Postfix))
                },
                new PatchSpec
                {
                    Id = "BattleOrchestratorSync._EndBattle: Campaign.BattleEnded",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(BattleOrchestratorSync), "_EndBattle"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(EndBattle_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Campaign), nameof(EndBattle_Finalizer))
                },
                new PatchSpec
                {
                    Id = "BattleOrchestratorSync._EndBattleEarly: Campaign.BattleEnded",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(BattleOrchestratorSync), "_EndBattleEarly"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(EndBattleEarly_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Campaign), nameof(EndBattle_Finalizer))
                },
                new PatchSpec
                {
                    // Only the real end (the battle GUI also builds an EndBattleInfo for its summary preview).
                    Id = "EndBattleInfo.PerformTheBattleEnd: Campaign.BattleEnded",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(EndBattleInfo), "PerformTheBattleEnd"),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(PerformTheBattleEnd_Postfix))
                },
                new PatchSpec
                {
                    Id = "BattleOrchestratorSync._CancelBattle: Campaign.BattleCancelled",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(BattleOrchestratorSync), "_CancelBattle"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(CancelBattle_Prefix))
                }
            });

            private static readonly EventHook BlockadeHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    Id = "BlockadeManager.RunBlockadeChecks: Campaign.BlockadeStarted/Ended",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(BlockadeManager), "RunBlockadeChecks"),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(Blockade_Postfix))
                }
            });

            private static readonly EventHook OffscreenHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    Id = "OffscreenFactionBattleCoordinator.BeginBattle: Campaign.OffscreenBattleStarted",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(OffscreenFactionBattleCoordinator), "BeginBattle"),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(OffscreenBegin_Postfix))
                },
                new PatchSpec
                {
                    Id = "OffscreenFactionBattleCoordinator.EndBattles: Campaign.OffscreenBattleEnded",
                    FeatureId = Features.EventCampaign,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(OffscreenFactionBattleCoordinator), "EndBattles"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(OffscreenEnd_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(OffscreenEnd_Postfix))
                },
                new PatchSpec
                {
                    Id = "BattleStarterCore.OnKilled: Campaign.OffscreenForceKilled",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(BattleStarterCore), "OnKilled"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(OnKilled_Prefix))
                }
            });

            private static readonly EventHook BuildHook = new EventHook(Features.EventCampaign, () => new[]
            {
                new PatchSpec
                {
                    Id = "HandleBuilding.ExecuteBuildOrder: Campaign.DesignBuilding",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(HandleBuilding), "ExecuteBuildOrder"),
                    Prefix = PatchSpec.Hook(typeof(Campaign), nameof(ExecuteBuildOrder_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Campaign), nameof(ExecuteBuildOrder_Finalizer))
                },
                new PatchSpec
                {
                    Id = "AiUnitSpawner.SpawnExactThing: Campaign.DesignBuilt",
                    FeatureId = Features.EventCampaign,
                    Target = () => EventHelpers.Declared(typeof(AiUnitSpawner), "SpawnExactThing"),
                    Postfix = PatchSpec.Hook(typeof(Campaign), nameof(SpawnExactThing_Postfix))
                }
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>A fleet was created (new, split off, built by the AI, or loaded with a save). All machines.</summary>
            public static EventChannel<FleetEvent> FleetCreated { get; } = new EventChannel<FleetEvent>(
                "Campaign.FleetCreated", EventSide.All, "A fleet was created.", FleetHook);

            /// <summary>A fleet is being removed (emptied, merged, destroyed). Raised while it can still be read. All machines.</summary>
            public static EventChannel<FleetEvent> FleetRemoved { get; } = new EventChannel<FleetEvent>(
                "Campaign.FleetRemoved", EventSide.All, "A fleet is being removed.", FleetHook);

            /// <summary>
            /// A map square is about to change owner (captured, made neutral because it was cut off from its faction's HQ,
            /// or its faction was eliminated). <c>SetNewOwner</c> gives it to another faction; cancelling keeps the current owner.
            /// A cancelled capture resets that square's capture countdown (the fleet has to capture it again); a cancelled
            /// cut-off or elimination change is tried again at the next 5-second check while the reason remains. Host.
            /// </summary>
            public static EventChannel<TerritoryChangingEvent> TerritoryChanging { get; } = new EventChannel<TerritoryChangingEvent>(
                "Campaign.TerritoryChanging", EventSide.Host, "A map square is about to change owner.", TerritoryHook);

            /// <summary>A map square changed owner. All machines.</summary>
            public static EventChannel<TerritoryChangedEvent> TerritoryChanged { get; } = new EventChannel<TerritoryChangedEvent>(
                "Campaign.TerritoryChanged", EventSide.All, "A map square changed owner.", TerritoryHook);

            /// <summary>
            /// A faction lost its last HQ and is being eliminated: its squares are about to become neutral and its fleets
            /// deleted. Host.
            /// </summary>
            public static EventChannel<FactionEliminatedEvent> FactionEliminated { get; } = new EventChannel<FactionEliminatedEvent>(
                "Campaign.FactionEliminated", EventSide.Host, "A faction is being eliminated.", EliminatedHook);

            /// <summary>
            /// The relationship between two factions changed (-100..100; below 0 is war, above 50 allied). Changes from
            /// diplomacy, bribes, insults, friendly fire and land grabs, including the knock-on changes to other factions'
            /// opinions. Flags say when this started or ended a war or an alliance. All machines.
            /// </summary>
            public static EventChannel<RelationsChangedEvent> RelationsChanged { get; } = new EventChannel<RelationsChangedEvent>(
                "Campaign.RelationsChanged", EventSide.All, "Two factions' relationship changed.", RelationsHook);

            /// <summary>A battle's setup phase began (all craft taken out of play; forces are being chosen). All machines.</summary>
            public static EventChannel<BattlePreparingEvent> BattlePreparing { get; } = new EventChannel<BattlePreparingEvent>(
                "Campaign.BattlePreparing", EventSide.All, "A battle's setup phase began.", BattleHook);

            /// <summary>A battle began (setup finished). All machines.</summary>
            public static EventChannel<BattleStartedEvent> BattleStarted { get; } = new EventChannel<BattleStartedEvent>(
                "Campaign.BattleStarted", EventSide.All, "A battle began.", BattleHook);

            /// <summary>A battle ended (merits awarded, retreats and resets applied). All machines.</summary>
            public static EventChannel<BattleEndedEvent> BattleEnded { get; } = new EventChannel<BattleEndedEvent>(
                "Campaign.BattleEnded", EventSide.All, "A battle ended.", BattleHook);

            /// <summary>A battle was cancelled during setup. All machines.</summary>
            public static EventChannel<BattleCancelledEvent> BattleCancelled { get; } = new EventChannel<BattleCancelledEvent>(
                "Campaign.BattleCancelled", EventSide.All, "A battle was cancelled.", BattleHook);

            /// <summary>
            /// A fleet became blockaded: an enemy fleet came close enough to stop it (checked every 5 seconds on the map;
            /// not reported during a battle's setup phase). All machines.
            /// </summary>
            public static EventChannel<BlockadeEvent> BlockadeStarted { get; } = new EventChannel<BlockadeEvent>(
                "Campaign.BlockadeStarted", EventSide.All, "A fleet became blockaded.", BlockadeHook);

            /// <summary>A fleet is no longer blockaded (the enemy left or was destroyed). All machines.</summary>
            public static EventChannel<BlockadeEvent> BlockadeEnded { get; } = new EventChannel<BlockadeEvent>(
                "Campaign.BlockadeEnded", EventSide.All, "A fleet is no longer blockaded.", BlockadeHook);

            /// <summary>Two AI fleets started fighting off screen (simulated, not in play). Host.</summary>
            public static EventChannel<OffscreenBattleEvent> OffscreenBattleStarted { get; } = new EventChannel<OffscreenBattleEvent>(
                "Campaign.OffscreenBattleStarted", EventSide.Host, "An AI-vs-AI battle started.", OffscreenHook);

            /// <summary>An off-screen battle ended; <c>Fleets</c> are the fleets still in it (the winners). Host.</summary>
            public static EventChannel<OffscreenBattleEvent> OffscreenBattleEnded { get; } = new EventChannel<OffscreenBattleEvent>(
                "Campaign.OffscreenBattleEnded", EventSide.Host, "An AI-vs-AI battle ended.", OffscreenHook);

            /// <summary>A craft was destroyed in an off-screen battle (its materials are left as a dump). Host.</summary>
            public static EventChannel<OffscreenForceKilledEvent> OffscreenForceKilled { get; } = new EventChannel<OffscreenForceKilledEvent>(
                "Campaign.OffscreenForceKilled", EventSide.Host, "A craft was destroyed in an AI-vs-AI battle.", OffscreenHook);

            /// <summary>
            /// An AI faction is about to build a design at one of its shipyards (it has the materials). Cancelling stops this
            /// build; the AI may choose it again later. The build can still fail afterwards (no place found for a wreck),
            /// so <see cref="DesignBuilt"/> does not always follow. Host.
            /// </summary>
            public static EventChannel<DesignBuildingEvent> DesignBuilding { get; } = new EventChannel<DesignBuildingEvent>(
                "Campaign.DesignBuilding", EventSide.Host, "An AI faction is about to build a design.", BuildHook);

            /// <summary>An AI faction built a design: <c>Fleet</c> is the new fleet. Host.</summary>
            public static EventChannel<DesignBuiltEvent> DesignBuilt { get; } = new EventChannel<DesignBuiltEvent>(
                "Campaign.DesignBuilt", EventSide.Host, "An AI faction built a design.", BuildHook);

            // ---- fleets ------------------------------------------------------------------------------------

            private static void AddFleet_Postfix(Fleet F)
            {
                if (F == null || !FleetCreated.HasSubscribers) return;
                // A new fleet is listed before its craft are added; raise once the current game call has finished.
                MainThread.PostLater(() =>
                {
                    if (!F.Deleted && FleetCreated.HasSubscribers) FleetCreated.Raise(new FleetEvent(F));
                });
            }

            private static void RemoveFleet_Prefix(Fleet F)
            {
                // The game only removes fleets on the main thread (elsewhere it logs an error and does nothing).
                if (F == null || !FleetRemoved.HasSubscribers || !MainThread.IsCurrent) return;
                FleetRemoved.Raise(new FleetEvent(F));
            }

            // ---- territory ---------------------------------------------------------------------------------

            [ThreadStatic] private static TerritoryChangeCause _cause;

            private static void Captured_Prefix() => _cause = TerritoryChangeCause.Captured;

            private static void Disconnected_Prefix() => _cause = TerritoryChangeCause.CutOff;

            private static void Cause_Finalizer() => _cause = TerritoryChangeCause.Other;

            private static ObjectId OwnerOf(int east, int north)
            {
                try
                {
                    return WorldSpecification.i.BoardLayout.BoardSections[east, north].FactionOwnedBy;
                }
                catch
                {
                    return null;
                }
            }

            private static MapBoardSection SectionOf(int east, int north)
            {
                try
                {
                    return WorldSpecification.i.BoardLayout.BoardSections[east, north];
                }
                catch
                {
                    return null;
                }
            }

            private static bool ChangeBoardOwner_Prefix(int east, int north, ref ObjectId faction)
            {
                if (Net.IsClient || !TerritoryChanging.HasSubscribers) return true;
                var e = new TerritoryChangingEvent(SectionOf(east, north), east, north, OwnerOf(east, north), faction, _cause);
                TerritoryChanging.Raise(e);
                if (e.IsCancelled) return false;
                if (e.NewOwner != null) faction = e.NewOwner;
                return true;
            }

            private static void ChangeBoardOwnerLocal_Prefix(int east, int north, out ObjectId __state)
            {
                __state = TerritoryChanged.HasSubscribers ? OwnerOf(east, north) : null;
            }

            private static void ChangeBoardOwnerLocal_Postfix(int east, int north, ObjectId faction, ObjectId __state)
            {
                if (!TerritoryChanged.HasSubscribers) return;
                if (__state != null && __state == faction) return; // owner did not actually change
                TerritoryChanged.Raise(new TerritoryChangedEvent(SectionOf(east, north), east, north, __state, faction, _cause));
            }

            private static readonly HashSet<int> _eliminated = new HashSet<int>();

            /// <summary>Forgets per-game state (called when the game changes mode).</summary>
            internal static void ResetGameState()
            {
                lock (_eliminated) _eliminated.Clear();
                lock (_blockades) _blockades.Clear();
            }

            private static void NeutralizeEverything_Prefix(InstanceFaction faction)
            {
                _cause = TerritoryChangeCause.FactionEliminated;
                if (faction == null || !FactionEliminated.HasSubscribers) return;
                int territories = 0;
                int craft = 0;
                try
                {
                    var assets = faction.Control?.TerritorialAssets;
                    territories = assets != null ? assets.Territories.Count : 0;
                    var forces = ForceManager.Instance.forces;
                    for (int i = 0; i < forces.Count; i++)
                        if (forces[i] != null && forces[i].FactionId == faction.Id) craft++;
                }
                catch
                {
                }
                // The game repeats this every check while the faction has no HQ; report it once, while there is something left.
                if (territories == 0 && craft == 0) return;
                lock (_eliminated)
                {
                    if (!_eliminated.Add(faction.Id.Id)) return;
                }
                FactionEliminated.Raise(new FactionEliminatedEvent(faction, territories, craft));
            }

            // ---- relations ---------------------------------------------------------------------------------

            private static void Relation_Prefix(FactionRelations __instance, int team1, int team2, out float __state)
            {
                __state = float.NaN;
                if (!RelationsChanged.HasSubscribers) return;
                try
                {
                    __state = __instance.R[team1, team2];
                }
                catch
                {
                }
            }

            private static void Relation_Postfix(FactionRelations __instance, int team1, int team2, float __state)
            {
                if (float.IsNaN(__state) || !RelationsChanged.HasSubscribers) return;
                float now;
                ObjectId a, b;
                try
                {
                    now = __instance.R[team1, team2];
                    a = __instance.FactionIds[team1];
                    b = __instance.FactionIds[team2];
                }
                catch
                {
                    return;
                }
                if (Math.Abs(now - __state) < 0.001f || team1 == team2) return;
                RelationsChanged.RaiseOnMainThread(new RelationsChangedEvent(a, b, __state, now));
            }

            // ---- battles -----------------------------------------------------------------------------------

            // 0 = not ending, 1 = normal end, 2 = ended early.
            [ThreadStatic] private static int _ending;

            private static void BeginSetup_Postfix(ObjectId teamStartingFight, Vector3 gp)
            {
                if (BattlePreparing.HasSubscribers) BattlePreparing.Raise(new BattlePreparingEvent(teamStartingFight, gp));
            }

            private static void BeginBattle_Postfix()
            {
                if (BattleStarted.HasSubscribers) BattleStarted.Raise(new BattleStartedEvent(SafeTeamStartingFight()));
            }

            private static void EndBattle_Prefix() => _ending = 1;

            private static void EndBattleEarly_Prefix() => _ending = 2;

            private static void EndBattle_Finalizer() => _ending = 0;

            private static void PerformTheBattleEnd_Postfix(EndBattleInfo __instance)
            {
                if (_ending == 0 || __instance == null || !BattleEnded.HasSubscribers) return;
                BattleEnded.Raise(new BattleEndedEvent(__instance, _ending == 2, SafeTeamStartingFight()));
            }

            private static void CancelBattle_Prefix()
            {
                if (!BattleCancelled.HasSubscribers) return;
                bool active;
                try
                {
                    active = !BattleOrchestrator.Idle;
                }
                catch
                {
                    active = true;
                }
                if (active) BattleCancelled.Raise(new BattleCancelledEvent(SafeTeamStartingFight()));
            }

            private static ObjectId SafeTeamStartingFight()
            {
                try
                {
                    return BattleOrchestrator.Instance.TeamStartingFight;
                }
                catch
                {
                    return null;
                }
            }

            // ---- blockades ---------------------------------------------------------------------------------

            // The blockader each fleet had at the last check. A blockade also ends silently when its blockader is deleted
            // (the game clears the link on read), so the previous state is kept here rather than read before each check.
            private static readonly Dictionary<IBlockadeFleet, KeyValuePair<IBlockadingFleet, string>> _blockades = new Dictionary<IBlockadeFleet, KeyValuePair<IBlockadingFleet, string>>();

            private static void Blockade_Postfix(IEnumerable<IBlockadeFleet> fleets)
            {
                if (fleets == null || (!BlockadeStarted.HasSubscribers && !BlockadeEnded.HasSubscribers)) return;
                try
                {
                    if (BattleOrchestrator.SetupPhase) return; // checked every 0.1 s then; blockades are frozen for the battle
                }
                catch
                {
                }
                var raise = new List<BlockadeEvent>();
                lock (_blockades)
                {
                    var seen = new HashSet<IBlockadeFleet>();
                    try
                    {
                        foreach (var fleet in fleets)
                        {
                            if (fleet == null || fleet.Deleted) continue;
                            seen.Add(fleet);
                            var info = fleet.BlockadeInfo;
                            var now = info != null && info.IsBlockaded ? info.FleetBlockadedBy : null;
                            _blockades.TryGetValue(fleet, out var before);
                            if (before.Key == null && now != null)
                            {
                                // Keep the blockader's name: it may be deleted (and unreadable) by the time the blockade ends.
                                before = new KeyValuePair<IBlockadingFleet, string>(now, EventHelpers.FleetName(now as Fleet));
                                raise.Add(new BlockadeEvent(fleet as Fleet, now as Fleet, before.Value, true));
                            }
                            else if (before.Key != null && now == null)
                            {
                                raise.Add(new BlockadeEvent(fleet as Fleet, before.Key as Fleet, before.Value, false));
                            }
                            if (now == null) _blockades.Remove(fleet);
                            else if (!ReferenceEquals(before.Key, now)) _blockades[fleet] = new KeyValuePair<IBlockadingFleet, string>(now, EventHelpers.FleetName(now as Fleet));
                            else _blockades[fleet] = before;
                        }
                    }
                    catch
                    {
                        return;
                    }
                    // Fleets that are gone: forget them (no event; FleetRemoved covers that).
                    if (_blockades.Count > seen.Count)
                    {
                        var gone = new List<IBlockadeFleet>();
                        foreach (var k in _blockades.Keys)
                            if (!seen.Contains(k)) gone.Add(k);
                        foreach (var k in gone) _blockades.Remove(k);
                    }
                }
                foreach (var e in raise)
                {
                    if (e.Started) BlockadeStarted.Raise(e);
                    else BlockadeEnded.Raise(e);
                }
            }


            // ---- off-screen battles ------------------------------------------------------------------------

            private static void OffscreenBegin_Postfix(IOffscreenBattleFleet attacker, IOffscreenBattleFleet defender)
            {
                if (attacker == null || defender == null || attacker == defender || !OffscreenBattleStarted.HasSubscribers) return;
                OffscreenBattleStarted.RaiseOnMainThread(new OffscreenBattleEvent(new[] { attacker as Fleet, defender as Fleet }));
            }

            private static void OffscreenEnd_Prefix(OffscreenFactionBattleCoordinator __instance, out List<OffscreenFactionBattle> __state)
            {
                __state = null;
                if (__instance == null || !OffscreenBattleEnded.HasSubscribers) return;
                try
                {
                    if (__instance.HasBattles) __state = new List<OffscreenFactionBattle>(__instance.Battles);
                }
                catch
                {
                }
            }

            private static void OffscreenEnd_Postfix(OffscreenFactionBattleCoordinator __instance, List<OffscreenFactionBattle> __state)
            {
                if (__state == null) return;
                HashSet<OffscreenFactionBattle> now;
                try
                {
                    now = new HashSet<OffscreenFactionBattle>(__instance.Battles);
                }
                catch
                {
                    return;
                }
                foreach (var battle in __state)
                {
                    if (battle == null || now.Contains(battle)) continue;
                    var fleets = new List<Fleet>();
                    foreach (var f in battle.FleetsInvolved)
                        if (f is Fleet fleet && !fleet.Deleted) fleets.Add(fleet);
                    OffscreenBattleEnded.RaiseOnMainThread(new OffscreenBattleEvent(fleets));
                }
            }

            private static void OnKilled_Prefix(IOffscreenBattleForce target)
            {
                if (target == null || !OffscreenForceKilled.HasSubscribers) return;
                OffscreenForceKilled.RaiseOnMainThread(new OffscreenForceKilledEvent(target as Force));
            }

            // ---- AI shipyards ------------------------------------------------------------------------------

            private sealed class PendingBuild
            {
                public Fleet Builder;
                public WorldSpecificationFactionDesign Design;
                public float Cost;
                public bool Dead;
            }

            [ThreadStatic] private static PendingBuild _pending;

            private static bool ExecuteBuildOrder_Prefix(ref BuildOrder buildOrder)
            {
                _pending = null;
                if (Net.IsClient || (!DesignBuilding.HasSubscribers && !DesignBuilt.HasSubscribers)) return true;
                Fleet builder;
                WorldSpecificationFactionDesign design = null;
                float cost = 0f;
                bool dead;
                try
                {
                    var unit = buildOrder.BuilderUnit;
                    var item = buildOrder.ItemToBuild;
                    if (unit == null || item == null) return true;
                    builder = FleetManager.Instance.GetFleetByID(unit.Id);
                    if (builder == null) return true; // the game skips this order too
                    dead = buildOrder.Dead;
                    cost = item.ResourceCostPlusResourceProcurementLevels?.m ?? 0f;
                    // The game skips orders it can't afford every AI cycle; don't report those.
                    if (!dead && builder.GetMaterial() < cost) return true;
                    design = WorldSpecificationFactionDesigns.GetDesignOfFaction(item.DesignId);
                }
                catch
                {
                    return true;
                }
                if (DesignBuilding.HasSubscribers)
                {
                    var e = new DesignBuildingEvent(builder, design, cost, dead);
                    DesignBuilding.Raise(e);
                    if (e.IsCancelled) return false;
                }
                _pending = new PendingBuild { Builder = builder, Design = design, Cost = cost, Dead = dead };
                return true;
            }

            private static void ExecuteBuildOrder_Finalizer()
            {
                _pending = null;
            }

            private static void SpawnExactThing_Postfix(Fleet __result)
            {
                var p = _pending;
                if (p == null || __result == null) return;
                _pending = null;
                if (DesignBuilt.HasSubscribers) DesignBuilt.Raise(new DesignBuiltEvent(p.Builder, p.Design, p.Cost, p.Dead, __result));
            }
        }
    }

    // ---- event classes -------------------------------------------------------------------------------------

    /// <summary>A fleet was created or is being removed.</summary>
    public sealed class FleetEvent : GameEvent
    {
        internal FleetEvent(Fleet fleet)
        {
            Fleet = fleet;
        }

        /// <summary>The fleet. Game object.</summary>
        public Fleet Fleet { get; }

        public string Name => EventHelpers.FleetName(Fleet);

        public ObjectId Faction => Fleet?.FactionId;

        public string FactionName => EventHelpers.FactionName(Faction);
    }

    /// <summary>Why a map square changed owner.</summary>
    public enum TerritoryChangeCause
    {
        /// <summary>Not known (map editor, cheats, a multiplayer client applying the host's change).</summary>
        Other,
        /// <summary>A fleet finished capturing it.</summary>
        Captured,
        /// <summary>Cut off from its faction's HQ, so it became neutral.</summary>
        CutOff,
        /// <summary>Its faction lost its last HQ.</summary>
        FactionEliminated
    }

    /// <summary>Shared members of the territory events.</summary>
    public abstract class TerritoryEventBase : CancellableEvent
    {
        internal TerritoryEventBase(MapBoardSection section, int east, int north, ObjectId oldOwner, ObjectId newOwner, TerritoryChangeCause cause)
        {
            Section = section;
            East = east;
            North = north;
            OldOwner = oldOwner;
            NewOwner = newOwner;
            Cause = cause;
        }

        /// <summary>The map square. Game object.</summary>
        public MapBoardSection Section { get; }

        /// <summary>The square's map coordinates.</summary>
        public int East { get; }

        public int North { get; }

        /// <summary>The faction that owned it (not valid when it was neutral).</summary>
        public ObjectId OldOwner { get; }

        /// <summary>The faction that gets it (not valid when it becomes neutral).</summary>
        public ObjectId NewOwner { get; protected set; }

        public string OldOwnerName => EventHelpers.FactionName(OldOwner);

        public string NewOwnerName => EventHelpers.FactionName(NewOwner);

        /// <summary>True when the square becomes neutral.</summary>
        public bool BecomesNeutral => NewOwner == null || !NewOwner.AppearsValid;

        public TerritoryChangeCause Cause { get; }

        /// <summary>True when the square has a resource zone.</summary>
        public bool IsResourceZone
        {
            get
            {
                try
                {
                    return Section != null && Section.IsResourceZone;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>The name of the area the square belongs to, if any.</summary>
        public string AreaName
        {
            get
            {
                try
                {
                    return Section?.Area?.Name?.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    /// <summary>A map square is about to change owner. Give it to someone else with <see cref="SetNewOwner"/>, or cancel.</summary>
    public sealed class TerritoryChangingEvent : TerritoryEventBase
    {
        internal TerritoryChangingEvent(MapBoardSection section, int east, int north, ObjectId oldOwner, ObjectId newOwner, TerritoryChangeCause cause)
            : base(section, east, north, oldOwner, newOwner, cause)
        {
        }

        /// <summary>Gives the square to another faction instead (an invalid id makes it neutral).</summary>
        public void SetNewOwner(ObjectId faction) => NewOwner = faction ?? ObjectId.NoLinkage;
    }

    /// <summary>A map square changed owner.</summary>
    public sealed class TerritoryChangedEvent : GameEvent
    {
        internal TerritoryChangedEvent(MapBoardSection section, int east, int north, ObjectId oldOwner, ObjectId newOwner, TerritoryChangeCause cause)
        {
            Section = section;
            East = east;
            North = north;
            OldOwner = oldOwner;
            NewOwner = newOwner;
            Cause = cause;
        }

        public MapBoardSection Section { get; }

        public int East { get; }

        public int North { get; }

        public ObjectId OldOwner { get; }

        public ObjectId NewOwner { get; }

        public string OldOwnerName => EventHelpers.FactionName(OldOwner);

        public string NewOwnerName => EventHelpers.FactionName(NewOwner);

        public bool BecameNeutral => NewOwner == null || !NewOwner.AppearsValid;

        /// <summary>True when the local player's faction gained or lost this square.</summary>
        public bool InvolvesPlayer
        {
            get
            {
                try
                {
                    var me = GAME_STATE.MyTeam;
                    return me != null && (me == OldOwner || me == NewOwner);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Why it changed. Only known on the host.</summary>
        public TerritoryChangeCause Cause { get; }
    }

    /// <summary>A faction is being eliminated.</summary>
    public sealed class FactionEliminatedEvent : GameEvent
    {
        internal FactionEliminatedEvent(InstanceFaction faction, int territories, int craft)
        {
            InstanceFaction = faction;
            Territories = territories;
            Craft = craft;
        }

        /// <summary>The faction. Game object.</summary>
        public InstanceFaction InstanceFaction { get; }

        public ObjectId Faction => InstanceFaction?.Id;

        public string FactionName => EventHelpers.FactionName(Faction);

        /// <summary>Map squares it still held (they become neutral).</summary>
        public int Territories { get; }

        /// <summary>Craft it still had (they are deleted).</summary>
        public int Craft { get; }
    }

    /// <summary>Two factions' relationship changed.</summary>
    public sealed class RelationsChangedEvent : GameEvent
    {
        internal RelationsChangedEvent(ObjectId a, ObjectId b, float oldValue, float newValue)
        {
            FactionA = a;
            FactionB = b;
            OldValue = oldValue;
            NewValue = newValue;
        }

        public ObjectId FactionA { get; }

        public ObjectId FactionB { get; }

        public string FactionAName => EventHelpers.FactionName(FactionA);

        public string FactionBName => EventHelpers.FactionName(FactionB);

        /// <summary>Relationship before (-100..100).</summary>
        public float OldValue { get; }

        /// <summary>Relationship after (-100..100).</summary>
        public float NewValue { get; }

        public float Change => NewValue - OldValue;

        /// <summary>At war now (below 0).</summary>
        public bool AtWar => NewValue < 0f;

        /// <summary>This change started a war.</summary>
        public bool WarStarted => OldValue >= 0f && NewValue < 0f;

        /// <summary>This change ended a war.</summary>
        public bool WarEnded => OldValue < 0f && NewValue >= 0f;

        /// <summary>This change made them allies (above 50).</summary>
        public bool AllianceFormed => OldValue <= 50f && NewValue > 50f;

        /// <summary>This change ended their alliance.</summary>
        public bool AllianceEnded => OldValue > 50f && NewValue <= 50f;

        /// <summary>True when one side is the local player's faction.</summary>
        public bool InvolvesPlayer
        {
            get
            {
                try
                {
                    var me = GAME_STATE.MyTeam;
                    return me != null && (me == FactionA || me == FactionB);
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    /// <summary>A battle's setup phase began.</summary>
    public sealed class BattlePreparingEvent : GameEvent
    {
        internal BattlePreparingEvent(ObjectId startedBy, Vector3 position)
        {
            StartedBy = startedBy;
            Position = position;
        }

        /// <summary>The faction that started the fight.</summary>
        public ObjectId StartedBy { get; }

        public string StartedByName => EventHelpers.FactionName(StartedBy);

        /// <summary>Where (game world position).</summary>
        public Vector3 Position { get; }
    }

    /// <summary>A battle began.</summary>
    public sealed class BattleStartedEvent : GameEvent
    {
        internal BattleStartedEvent(ObjectId startedBy)
        {
            StartedBy = startedBy;
        }

        public ObjectId StartedBy { get; }

        public string StartedByName => EventHelpers.FactionName(StartedBy);
    }

    /// <summary>A battle ended.</summary>
    public sealed class BattleEndedEvent : GameEvent
    {
        internal BattleEndedEvent(EndBattleInfo info, bool early, ObjectId startedBy)
        {
            Info = info;
            EndedEarly = early;
            StartedBy = startedBy;
            try
            {
                Duration = info?.BattleTimer?.Since ?? 0f;
            }
            catch
            {
                Duration = 0f;
            }
        }

        /// <summary>The game's end-of-battle info (retreats, resets, merits). Game object.</summary>
        public EndBattleInfo Info { get; }

        /// <summary>True when the player ended it early (retreat) instead of it finishing.</summary>
        public bool EndedEarly { get; }

        public ObjectId StartedBy { get; }

        public string StartedByName => EventHelpers.FactionName(StartedBy);

        /// <summary>The merits calculation (force sizes at start and end). Game object.</summary>
        public MeritsCalculation Merits => Info?.MeritsAwarded;

        /// <summary>How long the battle lasted, in game seconds.</summary>
        public float Duration { get; }

        /// <summary>Fleets that had to retreat.</summary>
        public int RetreatCount => Info?.ToRetreat?.Count ?? 0;
    }

    /// <summary>A battle was cancelled during setup.</summary>
    public sealed class BattleCancelledEvent : GameEvent
    {
        internal BattleCancelledEvent(ObjectId startedBy)
        {
            StartedBy = startedBy;
        }

        public ObjectId StartedBy { get; }

        public string StartedByName => EventHelpers.FactionName(StartedBy);
    }

    /// <summary>A fleet became blockaded or stopped being blockaded.</summary>
    public sealed class BlockadeEvent : GameEvent
    {
        internal BlockadeEvent(Fleet fleet, Fleet by, string byName, bool started)
        {
            Fleet = fleet;
            By = by;
            ByName = byName ?? EventHelpers.FleetName(by);
            Started = started;
        }

        /// <summary>The blockaded fleet. Game object.</summary>
        public Fleet Fleet { get; }

        public string Name => EventHelpers.FleetName(Fleet);

        public ObjectId Faction => Fleet?.FactionId;

        /// <summary>The enemy fleet blockading it (for an ended blockade, the last one; it may have been destroyed). Game object.</summary>
        public Fleet By { get; }

        /// <summary>The blockader's name (remembered from when the blockade started, in case it has since been destroyed).</summary>
        public string ByName { get; }

        public ObjectId ByFaction => By?.FactionId;

        /// <summary>True for BlockadeStarted, false for BlockadeEnded.</summary>
        public bool Started { get; }
    }

    /// <summary>An AI-vs-AI battle started or ended.</summary>
    public sealed class OffscreenBattleEvent : GameEvent
    {
        internal OffscreenBattleEvent(IReadOnlyList<Fleet> fleets)
        {
            Fleets = fleets;
        }

        /// <summary>Started: the attacker then the defender. Ended: the fleets still in the battle (the winners). Game objects.</summary>
        public IReadOnlyList<Fleet> Fleets { get; }
    }

    /// <summary>A craft was destroyed in an AI-vs-AI battle.</summary>
    public sealed class OffscreenForceKilledEvent : GameEvent
    {
        internal OffscreenForceKilledEvent(Force force)
        {
            Force = force;
        }

        /// <summary>The craft (out of play). Game object.</summary>
        public Force Force { get; }

        public string Name => Force?.Name;

        public ObjectId Faction => Force?.FactionId;
    }

    /// <summary>An AI faction is about to build a design. Cancellable.</summary>
    public sealed class DesignBuildingEvent : CancellableEvent
    {
        internal DesignBuildingEvent(Fleet builder, WorldSpecificationFactionDesign design, float cost, bool deadBlueprint)
        {
            Builder = builder;
            Design = design;
            Cost = cost;
            IsDeadBlueprint = deadBlueprint;
        }

        /// <summary>The fleet with the shipyard (usually the faction HQ). Game object.</summary>
        public Fleet Builder { get; }

        public ObjectId Faction => Builder?.FactionId;

        public string FactionName => EventHelpers.FactionName(Faction);

        /// <summary>The design to build. Game object; may be null.</summary>
        public WorldSpecificationFactionDesign Design { get; }

        public string DesignName => Design?.Name;

        /// <summary>Material cost (including the materials it is built with).</summary>
        public float Cost { get; }

        /// <summary>
        /// True when the design is placed as a zero-health wreck ("dead blueprint") that the faction then repairs. Placing it
        /// costs nothing up front (no material check); <see cref="Cost"/> is still the design's full cost.
        /// </summary>
        public bool IsDeadBlueprint { get; }
    }

    /// <summary>An AI faction built a design.</summary>
    public sealed class DesignBuiltEvent : GameEvent
    {
        internal DesignBuiltEvent(Fleet builder, WorldSpecificationFactionDesign design, float cost, bool deadBlueprint, Fleet fleet)
        {
            Builder = builder;
            Design = design;
            Cost = cost;
            IsDeadBlueprint = deadBlueprint;
            Fleet = fleet;
        }

        public Fleet Builder { get; }

        public ObjectId Faction => Builder?.FactionId;

        public string FactionName => EventHelpers.FactionName(Faction);

        public WorldSpecificationFactionDesign Design { get; }

        public string DesignName => Design?.Name;

        public float Cost { get; }

        /// <summary>True when it was placed as a zero-health wreck the faction repairs (see DesignBuildingEvent).</summary>
        public bool IsDeadBlueprint { get; }

        /// <summary>The new fleet. Game object.</summary>
        public Fleet Fleet { get; }
    }
}
