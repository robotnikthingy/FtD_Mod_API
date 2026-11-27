> Copy of the claude.ai project doc `claude/ftd-modapi-events-catalog.md` (2026-11-05). Sections 4 and later are the running status log: append a short entry after each change and test.

# FtdModApi event system: research and catalog (FtD 4.3.4)

Goal: a Spigot-style event system that covers most things that happen in the game, so mods subscribe to events instead of writing Harmony patches. This doc lists what the game offers, where each event would be raised, and a proposed build order. Paths are relative to the decompiled source (`Decompiled/Game/...`).

## 1. Design

### Event shape
- One class per event (`ApsShellFiredEvent`, `ConstructSpawnedEvent` ...), grouped under static hubs: `Events.Construct`, `Events.Block`, `Events.Weapon`, `Events.Missile`, `Events.Projectile`, `Events.Resource`, `Events.Campaign`, `Events.Session`, `Events.Player`, `Events.Ai`, `Events.Detection`, `Events.Ui`, `Events.Net`, `Events.Script`.
- Two kinds, as in Spigot:
  - **Pre events** (`...ing`, e.g. `MissileLaunching`) are raised before the game acts. They implement `ICancellable` (`Cancel()`), and some have settable fields (amounts, targets). Built on the existing `ModifierPipeline` (priority-ordered, fault-isolated).
  - **Post events** (`...ed`, e.g. `MissileLaunched`) are notifications after the fact. Built on `ApiEvent`.
- Priorities: the existing Priority constants. `Observe` (1000) is Spigot's MONITOR: it sees the final result and must not change it. Pre handlers can choose `ignoreCancelled` like Spigot.

### Threads
FtD runs weapon decisions, projectile casting, detection, AI targeting and some campaign maths on worker threads.
- **Post events are always delivered on the main thread.** If the source is a worker thread, the API copies the data into the event object and dispatches it with `GameEvents.Callbacks.DispatchToMainThread` (the game's own mechanism). Delivery is at most one frame late.
- **Pre events from worker threads** must run synchronously to be cancellable. They are marked `[WorkerThread]` in the docs, and handlers must be thread-safe and fast. Each event's doc says which thread it uses.

### Multiplayer side
Every event is tagged **Host** (server/single player only), **All** (every machine), or **Local** (this player's client only). Many game actions come as `Foo()` (request; prefix cancels at the source) and `_Foo()` (does the work on every machine), so the API can offer both a cancellable host Pre event and an All Post event.

### Cost and safety
- **Lazy patching**: each event's patch is applied the first time a mod subscribes (PatchManager can already apply specs later, as the main-menu UI patches do). Events nobody uses cost nothing and cannot break on a game update.
- Each event family is its own Feature (`Events.Weapon.Aps`, ...) with self-checking patches, like everything else in the API.
- **High-volume sources** (harvesting every physics tick, resource takes, per-frame laser fire) are aggregated into summary events (e.g. once per second per craft), never raised per call.
- **Native first**: where the game already has an event, the API subscribes to it instead of patching. See section 2.
- Verbose logging: first raise of each event is logged (follows the logging conventions).

## 2. What the game already offers (no patching)
- **`GameEvents`** (`Core/BrilliantSkies.Core.Timing/GameEvents.cs`), static `Callback`s with `RegWithEvent(fn, isPermanent)`:
  - `OnEnterBuildMode` / `OnExitBuildMode`, `UnitDefeated(Force)` (scrapped or captured), `SceneChange`, `InstanceChange`, `UniverseChange`, `BoardSectionCaptured` (no args), `ClearBattleEffects`, `ProfileChange`, `LanguageChanged`, `PreGameQuit`, `GameQuit`, `GameMenuDesired`, `GameSufficientlyBootedUp`, `OnPaintingStartedStopped`, C# events `SwapUiMode`, `FrameShifted`.
  - Timer ticks. **Names don't match the intervals**: `Once_Second` 1 s, `Twice_Second` 0.5 s, `Four_Second` 0.25 s, `Half_Second` 2 s, `Once_Minute` 60 s, `Twelve_Minute` ~5 s, `Fifteen_Minute` ~4 s. The API should expose correctly named ticks (`Events.Tick.EverySecond` ...).
  - Declared but never raised: `OnGameOver`, `ImportantObjectDestroyed`, `PlayerFocusChanged`, `OnViewTypeChanged`.
  - `GameEvents.TearDown` clears non-permanent subscribers on scene changes; the API re-registers as needed.
- **`Var<T>.SetChangeAction(old, new)`** is multicast and patch-free on any game `Var`. Covers AI behaviour/manoeuvre/movement/firing settings (`AiMaster.Pack`).
- **Avatar**: `bAvatar.AvatarSpawnedEvent` / `AvatarDestroyedEvent`.
- **Network**: `ConnectionDataCollection.OnPlayerConnectedEvent` / `OnPlayerDisconnectedEvent` (host, socket level), `NetSys.System.Status.OnStatusChange` / `OnFirstConnection` (Disconnected / Server / Client).
- **Per construct**: `construct.Scheduler.OnDestroyed`.
- **`MainConstruct.Events`** (22 `VehicleEvent`s: Spawn, BlockLost, Collision, ApsFired, CramFire, MissileFired, MissileReloaded, laser/plasma/flamer fired, TargetSpotted, TargetDead, PrimaryTargetDead, ProjectileWarning, OrderGiven, MaterialSent/Received, EnergySent/Received, MaterialDumpFound, MaterialsShotUp ...). These are counters polled by breadboards, not callbacks, but they mark exactly where each thing happens in the game code.
- **Construct modules** (big extension point): any class tagged `[ConstructModule(...)]` deriving from `MainConstructModule`/`AllConstructModule` gets an instance on every construct, with `BlockAdded(Block, Added/Repaired)`, `BlockRemoved(Block, Killed/RemovedFromDesign)`, `AfterEverythingLinkedUp` (spawn complete), `CleanUp` (destroy) and `Save/Load` (**custom per-craft data saved in the blueprint**). Plugin assemblies are preloaded, so mod modules should be found; to confirm in game.

## 3. Event catalog
Columns: event, hook (P = Pre/cancellable, E = Post), thread, side.

### 3.1 Construct (craft) lifecycle
| Event | Hook | Thread | Side |
|---|---|---|---|
| ConstructSpawned (all paths: designer, campaign, prefab spawn, split, drones) | postfix `BlueprintInitialisation.Run` / `BlueprintInitialisationDone` (Ftd/BrilliantSkies.Ftd.Persistence.Inits) | main | All |
| ConstructDespawned (any reason: wiped, out of play) | postfix `MainConstruct.DestroyCompletely(DestroyReason, bool)` or `Scheduler.OnDestroyed` | main | All |
| ConstructPlaying / Played, ConstructPulling / Pulled (in/out of play) | P `ForcePlayingAndPulling.PlayForce` / `PullForce` (source: player or game); E `Force.MakeInPlay` / `MakeOutOfPlay` | main | P Host, E All |
| ConstructScrapping / Scrapped | P/E `MainConstructScrapping.StartScrap(scrapType, dumpType)`; native `GameEvents.UnitDefeated` | main | All |
| ConstructDefeated (auto-scrap with cause: AI dead, sunk, too damaged, no heartstone, out of control) | P `EnemyConstructableStateChecker.DisposeOf`, cause from `C.Warning.State` | main | Host |
| ConstructTeamChanging / Changed (capture) | P `FleetForceChangeSync._ChangeForceTeam`; E `Force.SetTeam` | main | P Host, E All |
| ConstructSplit (debris) | `ConstructableDamage.StickBlocksIntoDebris` (private) | main | Host |
| ConstructSeparated (separator block) | `Separator.Separate` | main | Host |
| SubconstructAdded / Removed (turrets, spinblocks) | `ConstructBasics.AddSubConstructable`, `SubConstruct.DestroyCompletely` | main | All |
| ConstructCollided (ram, terrain) | `ConstructableCollision.DoConstructCollisionSingleContact` (private); throttle per pair | main | All |
| ConstructSinking (derived) | poll every second: below sea level + health fraction (same logic as `StateCheck.DamagedAndSunk`) | main | Host |
| BlueprintLoaded / Saving / Saved | `BlueprintFile.Load` (can be off-thread), P/E `BlueprintFile.Save` | any | Local |

### 3.2 Blocks
| Event | Hook | Thread | Side |
|---|---|---|---|
| BlockPlacing (player; mirror, fill, prefab) | P `mBuild.Server_PlaceBlockOnConstructable` (validates and charges cost) | main | Host |
| BlockPlaced | E `ConstructBasics.AddBlock` (or base `AllConstruct.ChangeWhenBlockAddedOrRepaired`) | main | All |
| BlockRemoving / Removed (player) | P `mBuild.Server_RemoveBlock`; E `ConstructBasics.PermanentlyRemoveBlock` | main | P Host, E All |
| BlockDestroyed (with damage cause and gunner) | E `Block.MainThreadHealthIsZeroSoKill(IDamageDescription)` | main | Host |
| BlockRepaired | E `AllConstruct.BlockRepairedSoPerformAllActions`; P `MainConstructRepairs.RepairBlock` | main | All |
| BlockDamaged | already built (`DamageApi.BlockDamaged`) | worker | Host |
| Avoid | `Block.StateChanged` (206 overrides), `set_CurrentHealth` (hot, inlinable) | | |
Blueprint spawns initialise blocks in bulk (`InitialiseBlocksInConstructable`) without `BlockPlaced`; that is covered by ConstructSpawned.

### 3.3 Weapons (all types)
| Event | Hook | Thread | Side |
|---|---|---|---|
| WeaponFiring (any weapon, cancellable) | P `ConstructableWeapon.Fire(FiredMunitionReturn)`; `Turrets.Fire` re-enters for child weapons | **worker** (AI) or main (player, Lua, breadboard) | Host |
| WeaponFired (generic) | postfix `Fire` when `FMR.GetFired()` | worker → dispatched | Host |
| WeaponCantFire (reason: no ammo, cooling, packing, no energy ...) | `FiredMunitionReturn.CantFire(reason)`; weapon captured in the `Fire` prefix; throttled (called every frame while blocked) | worker | Host |
| WeaponTargetChanged | `ConstructableWeapon.set_TargetObject` (compare old/new) | worker | Host |
| WeaponKill (material destroyed credited to a weapon) | `ConstructableWeapon.OnMaterialDestroyedByUs` | | Host |

### 3.4 APS
| Event | Hook | Thread | Side |
|---|---|---|---|
| ApsFiring | P `AdvCannonFiringPiece.WeaponFire` | worker | Host |
| ApsShellFired (firing piece, shell model, projectile, rail draw) | E `AdvCannonFiringPiece.MainThreadFireCallback` (private) | main | Host |
| ApsShellIntake (shell made and put in a rack) | `ShellRackModelAbstract.TakeShellIfPossible` (+ belt feed override) | main? | Host |
| ApsShellLoaded (clip → loader) | `ShellRackModel.LoadShellIfPossible` returns true | main? | Host |
| ApsBarrelCooling | `CannonBarrelSystem.Fired` | | Host |
| ApsAmmoShortage | intake `_errorCode == cannot_afford_shell` after `UpdateLoadNewShell`; throttled | | Host |
| ApsLoaderDestroyed (shells cooked off/ejected) | `AdvCannonAutoloader.WeveDiedSoExplodeOrEjectShells` | main | Host |

### 3.5 CRAM
| Event | Hook | Thread | Side |
|---|---|---|---|
| CramFiring | P `CannonFiringPiece.WeaponFire` | worker | Host |
| CramShellFired (shell stats, projectile) | E `CannonFiringPiece.MainThreadFire` (private) | main | Host |
| CramPackingComplete | postfix `CannonFiringPiece.RunPacking`, `PackTime` crosses `MaxPackTime` | main | Host |

### 3.6 Missiles
| Event | Hook | Thread | Side |
|---|---|---|---|
| MissileLaunching (per tube, cancellable) | P `MissileTube.TryFiring` | worker or main | Host |
| MissileLaunched (missile, launcher, MIRV parent) | E `LaunchpadAbstract.PrepareAndFireMissile` | main | All |
| MissileReloading / Reloaded | P/E `MissileTube.Reload` (false = no ammo) | main | Host |
| MissileDetonated (cause: impact, proximity/altitude fuse, expired, interceptor, missile collision, shot down) | E `MissileExplosionHandler.ExplodeNow`; cause from the caller (`ThumpAndExplode`, `CheckFuse`, `CheckSelfDestructTimer`, `MissileInterceptor.DistanceTrigger`, `MissilesHitEachOther`, `Missile.ApplyDamage`) | main | Host |
| MissileDamaged / ShotDown (by CIWS, LAMS, flak; who shot it) | `Missile.ApplyDamage` (Health < 1) | worker → dispatched | Host |
| MissileOutOfFuel | `Missile.set_Fuel` crossing zero | | Host |
| MissileRemoved | `MissileExplosionHandler.RPC_CustomMissile_Destroy` | main | All |
| FlareDeployed | `MissileStickyFlare.SpawnFlare` (private) | main | Host |

### 3.7 Lasers, PAC, plasma, flamer, simple weapons, melee
| Event | Hook | Thread | Side |
|---|---|---|---|
| LaserFired (beam, hit point) | `LaserDamageHelper.Fire` (CW: every frame while firing → aggregate per shot/second) | main | Host |
| LamsFired | `LamsJob.Fire` → `LaserDamageHelper.HitMissile` | worker | Host |
| ParticleCannonFired (arm, energy) | `ParticleCannon.MainThreadClientAndServerFire` | main | All |
| PlasmaFired | `PlasmaMantlet.MainThreadFire` (private) | main | Host |
| FlamerFired | `FlamerMain.ProcessPendingShot` (private) | main | Host |
| SimpleWeaponFired | `WorldWarCannonBase.ReallyFire` | main | Host |
| MeleeHit (ram, drill) | `Block.ReturnExtraCollisionDamage` / `MeleeBlockModule.RunMainThreadCollisionCheck` | main | Host |

### 3.8 Projectiles (APS, CRAM, simple)
| Event | Hook | Thread | Side |
|---|---|---|---|
| ProjectileHit (block/construct; health before/after → "last hit") | `ProjectileDamage.ProcessOne` (private) | worker → dispatched | Host |
| ProjectileDetonated (reason: ground hit, fuse, shell destroyed, out of energy, ERA ...) | `AdvPooledProjectile.Explode`, `PooledCramProjectile.Explode`, `PooledProjectile.Explode` | worker → dispatched | Host |
| ProjectileExpired / Missed | `*.Deactivate` (per pool) when not exploded | worker → dispatched | Host |
| ProjectileIntercepted (CIWS, flak, interceptors) | `Projectile.ReduceHealth` (shared by missiles, APS and CRAM shells) | worker | Host |
| ProjectileReflected (shields; cancellable/forceable) | P `ShieldProjector.Reflect` | worker | Host |
| ProjectileWaterSkim / Splash | `ObjectInteractionWithWater.OtherStuff` returns true | worker | Host |
| FragmentsCreated | `FragGenerator.CreateFrags` (per burst, not per fragment) | worker | Host |
Explosions and launches are already built (`DamageApi.Explosion`, `ProjectileLaunch`).

### 3.9 Resources and economy
| Event | Hook | Thread | Side |
|---|---|---|---|
| ResourcesSalvaged / DumpPickedUp (material, energy, source team) | P/E `ResourceStoreAbstract.GiveResourceAndExp` (amounts editable) | main | Host |
| DumpCreated (craft died, AI dump, overflow) | P `ResourceDumpSync.CreateNewDump` | main | Host |
| ResourcesHarvested (resource zones) | `MapResourceZone.HowMuchResourceCanITake` / `ResourceGathererAbstract.Run`; **aggregate per craft per second** | main + worker | Host |
| ResourcesTransferred (supply groups, AI transfers) | `Force.ReportExchanges`, P `ReturnHandler.ExecuteResourceTransferOrder` | worker / main | Host |
| AmmoUsed / FuelUsed (cancellable "not enough") | P `StorageSystem.TakeIfPossible` (per craft: `Storage.Ammo`, `Storage.Fuel`); hot → keep Pre handlers cheap | mixed | Host |
| RepairCost | `RepairStub.TryToRepairBlock` | main | Host |
| CommoditiesRefined | `FuelRefineryStill` give / `FtdAchievementAndStatListener.CommoditiesMade` (player team only) | main | Host |
| StorageFull / StorageEmpty (derived) | poll per second on `HeadRoom` / `Quantity` | main | Host |

### 3.10 Campaign
| Event | Hook | Thread | Side |
|---|---|---|---|
| FleetCreated / FleetRemoved | `FleetManager.CreateNewFleet` / `AddFleetToList`, `RemoveFleet` | main | All |
| DesignBuilding / Built (AI shipyard; builder, design, cost) | P/E `HandleBuilding.ExecuteBuildOrder` | main | Host |
| FleetMerged / FleetSplit | `FleetDistributionChangeSync._MergeForceIntoFleetAndTakeDrones` / `_MoveForceIntoNewFleetAndTakeDrones` | main | All |
| FleetMoveOrdered (player or AI) | P `AIWaypointControl._IssueFleetMoveOrder`, `MoveHandler.ExecuteMoveOrder` | main | Host |
| FleetArrived | `AIWaypointControl.FleetOrderWasReached` (player) / `Orders.AssessOrderReached` | main | Host |
| BlockadeStarted / Ended | `Fleet.OnBlockade`, `AbstractBlockadeInfo.RemoveBlockade` | main | Host |
| BattleSetup / Started / Ended (with merits, retreats) | `BattleOrchestratorSync._BeginSetupPhase`, `_BeginBattle`, `_EndBattle` / `_EndBattleEarly` / `_CancelBattle`, `EndBattleInfo.PerformTheBattleEnd` | main | All |
| OffscreenBattleStarted / Ended / Kill (AI vs AI) | `OffscreenFactionBattleCoordinator.BeginBattle` / `EndBattles`, `BattleStarterCore.OnKilled` | main | Host |
| TerritoryCaptured (old and new owner; cancellable) | P/E `CampaignSync._ChangeBoardOwner`; capture timer `AreaCapture.AttackerFound` | main | All |
| RelationsChanged / WarDeclared (bribes, insults, friendly fire) | `FactionRelations.ChangeRelationship` (+ `DeclareWar`, `BribeRelationship` ...), council `InstanceNewCampaignDiplomacy.ConfirmOptions` | main | Host |
| FactionEliminated | `AreaNeutralizer.Run` → `NeutralizeEverything` | main | Host |
| StrategicAiDecision (inspect/edit move, build, transfer, dump orders) | P `ReturnHandler.RetrieveFromAiProject(AiExecutionReturn)` | main | Host |
| Unlock check (override) | postfix `WorldUnlock.Check` | | Host |
Campaign has no "days"; time is `GameTimer.Instance.GameTimeOutOfBattle`.

### 3.11 Game session
| Event | Hook | Thread | Side |
|---|---|---|---|
| ModeChanging / ModeChanged (designer, campaign, adventure, story, multiplayer, menu, load) | `ModeChanger.DoPrior` / `DoStart` with the `ModeChangeSequence` type; native `SceneChange` | main | Local |
| ScenarioStarted (new vs loaded) | `PlanetInstanceLoad.LoadInstance(SpawnReason, ...)` | main | All |
| GameSaving / Saved (manual, autosave slot 29) | `PlanetFileSaveLoad.SaveToSlot` | main | Host |
| GameLoaded | `PlanetFileSaveLoad.OpenSaveSlot` → `LoadGameState` | main | Host |
| GamePaused / Unpaused | `GameSpeedManager.OnTryPause` / `OnTryUnPause` (private) | main | All |
| TimeScaleChanged | `GameSpeedManager.SetTimeScale` (filter changes); P `TimeSpeedChange` | main | All |
| Victory / Defeat | `VictoryAssessment._Client_GameFinished(ConditionHeader)` | main | All |
| GameQuitting | native `PreGameQuit` | main | Local |
Also: **mod save data**. `GamePlugin.OnSave` is never called by the game, so the API should offer `SaveData` hooks on GameSaving / GameLoaded (write a mod file next to the save).

### 3.12 Player
| Event | Hook | Thread | Side |
|---|---|---|---|
| PlayerJoined / Left (with name) | `LobbyConnectionAndBasicInfoManager.Server_GiveInfoToServer`; `_OnPlayerDisconnected`; native socket events | main | Host |
| AvatarSpawning / Spawned | P `RespawnManager.Server_SpawnRequested`; native `AvatarSpawnedEvent` | main | P Host, E All |
| AvatarDied | P `sHealth.ForceDie` | main | Host |
| SeatEntering / Entered, SeatLeaving / Left (craft) | P `sTeleportAndHold.unnetworked_Server_RequestToEnterChair` / `...LeaveChair`; E `bTeleportAndHold.ALL_ActuallyEnterTheFuckingChair` / `...Leave...` (real game names) | main | P Host, E All |
| ControlTaken (player drives a craft) | `cControl.ToggleControlMode`, `ConstructableControlModule.TryToTakeOverThisController` | main | Host |
| BuildModeEntered / Exited | native `OnEnterBuildMode` / `OnExitBuildMode` (+ `cBuild.C` for the craft) | main | Local |
| FleetViewOpened / Closed | `FleetControlGUI.OnActivateGui` / `OnDeactivateGui` | main | Local |
| RepairToolUsed | `sRepair.Server_ActivateRepairs`, `InformClientOfNewRepair` | main | Host |
| PrefabPlacing / Placed | P `PrefabSaveAndLoad.PlacePrefab`; E `PlacePrefabLocal` | main | P Host, E All |
| ChatSending / ChatReceived (filter, commands) | P `ChatLog.Send` (local), P `CoreRpcs.Server_AddChatEntry` (host), E `ChatLog._AddChatEntry` | main | as noted |

### 3.13 UI and input
| Event | Hook | Thread | Side |
|---|---|---|---|
| UiOpening / Opened / Closed (any window; focus object for block windows) | P `GuiAbstractBase.ActivateGui`, E `GuiStore.AddRequest` / `Pop*`; `ThrowAwayObjectGui<T>.ActivateGui(focus)` | main | Local |
| BlockInteracting (Q on a block) | P `cInteract.PerformSecondary` (private) | main | Local |
| Keybinds | no key events in the game. API helper: mod keybinds via `PlayerActionSetById.GetOrCreateDynamicAction(Guid)` (saved, rebindable) + `KeyPressed` raised from one UpdateEvent poll | main | Local |

### 3.14 AI and detection
| Event | Hook | Thread | Side |
|---|---|---|---|
| TargetSpotted / TargetLost / PrimaryTargetChanged | `AITargetManager.TargetDetected` (new-target branch), `CullOldTargets`, `_primaryTarget` compare around `Update` | **detection thread** → dispatched | Host |
| AiBehaviourChanged / ManoeuvreChanged / MovementChanged / FiringChanged | native `Var.SetChangeAction` on `AiMaster.Pack`; P `AiMaster.EnableBehaviour`, `SetOn/SetOff/SetPatrol/SetFleetMove` | main | All |
| AiOrderGiven (patrol, move) | `AIWaypointControl._Issue...` | main | Host |
| TrackCreated / TrackLost (per construct) | `MainConstructDetection.GetTrack` (create branch), `PurgeOld` | detection thread | Host |
| IncomingMissileWarning (first sighting) | `MissileWarningManager.MissileDetected` returns true | worker | Host |
ECM, smoke and jamming are continuous with no discrete events; threshold events would be derived.

### 3.15 Scripting (also new extension points)
| Item | Hook | Notes |
|---|---|---|
| LuaError (box, construct, message) | postfix `LuaBox.RunLuaUpdate` / `SetLuaCode` when `!Running && ErrorStack != ""` | host |
| LuaLog (`I:Log`) | postfix `LuaBinding.Log` | |
| **Mod Lua functions** | postfix `LuaMaster.CreateSandbox` and add a `Mod` table with callbacks (or `UserData.RegisterType`) | lets mods expose functions to players' Lua scripts |
| **Mod breadboard components** | add to `BoardTypes.FtdBreadboard` / `FtdAiBreadboard` / `MissileBreadboard` (public static lists) | saved by component Guid |

### 3.16 Networking (new extension point)
- Mods can send their own RPCs with almost no patching: `InstantComs` finds every `IRpcListener` type and registers its `[NetworkMethod]` methods; plugin assemblies appear to be included (to confirm in game).
- Method id = `methodName.GetHashCode()`: the API should enforce mod-prefixed names. Clients without the mod log an error and carry on.
- Arguments: primitives, string, byte[], int[], vectors, Quaternion, Guid, colours, ObjectId, connection; trailing `MessageInfo` gives the sender.
- Proposed `ModNet` API: `ModNet.Register("MyMod", "Sync", handler)`, `ModNet.SendToHost/SendToAll/SendToPlayer`. Also used to **sync the host's tunables and player values to clients** on join (the multiplayer item from the settings menu plan).

## 4. Status
- **v0.15 (built):** framework (`EventChannel<T>`, `GameEvent`/`CancellableEvent`, `EventPriority` Lowest..Monitor, `EventSide`, lazy patching per hook, per-hook availability, main-thread queue, EVENTS section in the status file); `Events.Constructs` (Spawned, Despawned, Playing/Played, Pulling/Pulled, Scrapping/Scrapped, Defeating, TeamChanging/TeamChanged, SubconstructAdded/Removed), `Events.Blocks` (Placing incl. replace and turrets, Placed, Removing/Removed, Destroyed, Repaired), `Events.Ticks` (Frame ... EveryMinute), `Events.BuildMode`.
- Design decisions: `ignoreCancelled` defaults to **true** (opposite of Spigot); Monitor handlers cannot change the cancel state; cancelling `Defeating` or an auto-scrap `Scrapping` resets the clean-up countdown; Placing runs before the game's own fit and cost checks.
- v0.15.1 fixes: `Despawned.Reason` adds `Deleted` (Force.Deleted set by ForceManager.DeleteForce before it goes out of play) and `DeadBlueprint` (flag around Force.MakeDeadBlueprint); `Pulled` no longer fires for those; `BuildMode.Entered` is deferred until the game has picked the craft (cBuild raises the enter callback before setting C, and reverts if there is none), and Exited carries the craft too.
- Verified in game (v0.15.0): Spawned/Despawned for 10 craft, Placing cancel (Rubber Block, 6 attempts), Destroyed counts, ticks, status EVENTS section. A `Force.GetBoundingBox` NullReferenceException after spawning craft with resource gatherers is a game bug (also appears with no mods).
- **v0.16 (built):** `Events.Weapons.Firing` (cancellable, EventThread.Any; transpiler routes ConstructableWeapon.Fire's single WeaponFire call through WeaponFireGate) and `Fired` (queued to main thread); `Events.Aps.ShellFired` (skips calls where no new projectile was created); `Events.Cram.ShellFired`, `Packed`; `Events.Missiles.Launched`, `Reloaded` (only when a missile was really loaded), `Detonated` (cause from args + optional caller tags; IsArmed). Channels now carry `EventThread`, shown in the status file. Per-tube cancellable launch skipped: TryFiring has no branch-free insertion point; cancel at Weapons.Firing (missile controller) instead. CantFire reasons deferred.
- **v0.17 (built):** `Events.Projectiles.Hit` (ProjectileDamage.ProcessOne prefix/postfix: health before/after with the game's death thresholds: blocks 0.005, shells <=1, missiles <1; includes fragments, players, balloons), `Detonated` (postfix on the three shell pools' Explode when it returns true; reason mapped from ExplosionReason), `Damaged` (Projectile.ReduceHealth: missiles, APS and CRAM shells being shot at), `ShieldHit` (Any thread; postfix on ShieldProjector.Reflect; Reflects settable, and forcing a reflection also triggers the shield-hit visual). Skipped: missed/expired (no reliable "exploded" flag on CRAM and simple pools), water skims.
- **v0.18 (built):** `Events.Session` (ModeChanging/ModeChanged via ModeChanger.DoPrior/DoStart with GameModeKind; Saving/Saved/Loaded via PlanetFileSaveLoad.SaveToSlot/OpenSaveSlot; Paused/Unpaused/TimeScaleChanged via GameSpeedManager.SetTimeScale with a 0.05 threshold; GameFinished via VictoryAssessment._Client_GameFinished and ConditionHeader.IsVictoryFor; Quitting native) and `ModSaveData` (one string per mod per save in FtdModApi.moddata.json in the save folder; read in the OpenSaveSlot prefix, before the world loads; cleared in DoPrior). `Events.Players` (Added/Removed via StaticPlayers; AvatarSpawned via sAvatar's explicit unnetworked_AvatarSpawned for single player/host and DoTheSpawn on sAvatar for remote players; AvatarDied via sHealth.ForceDie + _dead; SeatEntering/Entered/Left incl. seat switches; PrefabPlacing (skips undo) / PrefabPlaced; ChatSending (editable) / ChatReceived). Added the NetInfrastructure.dll reference.
- **v0.19 (built):** `Events.Resources` (Gaining/Gained via ResourceStoreAbstract.GiveResourceAndExp: salvage from destroyed blocks and dump pickups, source tagged by DumpPickupManager.PickupDumpedResources; Gaining amounts editable and cancellable, Gained reports what was actually added by quantity difference; Dumping/Dumped via ResourceDumpSync.CreateNewDump/_CreateNewDump with IsNew; Harvested summed per craft per second from ResourceGathererAbstract.Run + MapResourceZone.HowMuchResourceCanITake, in-play craft only; Transferred per supply-group run from SupplyGroupProcessingFtdSingleton.MakeEvents). `Events.Campaign` (FleetCreated/Removed; TerritoryChanging (host, cancellable, SetNewOwner) / TerritoryChanged (all) with cause Captured/CutOff/FactionEliminated/Other; FactionEliminated (AreaNeutralizer.NeutralizeEverything, only while something is left); RelationsChanged from FactionRelations.ChangeRelationship/ImposeUpperLimit/SetRelationship with war/alliance flags; BattlePreparing/Started/Ended/Cancelled via BattleOrchestratorSync underscore methods, Ended at EndBattleInfo.PerformTheBattleEnd (the running GUI also builds an EndBattleInfo for its preview, so BattleOrchestrator.EndBattle is not used); BlockadeStarted/Ended by diffing around BlockadeManager.RunBlockadeChecks; OffscreenBattleStarted/Ended/ForceKilled; DesignBuilding (cancellable; skips orders the game would skip for lack of material) / DesignBuilt via AiUnitSpawner.SpawnExactThing). Added the StrategicAi.dll reference.
- v0.19 verified in game (new campaign, ~40 min): DesignBuilt (98, incl. dead-blueprint wrecks), TerritoryChanged (47, all Captured), RelationsChanged war/alliance flags, BlockadeStarted, OffscreenBattleStarted/Ended/ForceKilled, BattlePreparing/Started/Ended (334 s), salvage, dump pickups, new/added dumps, harvesting, supply transfers.
- v0.19.1 fixes: Harvested skips craft destroyed or pulled out of play before the per-second flush (a handler reading the name threw); BlockadeEnded never fired because the game clears a blockade on read when the blockader is deleted, so the previous blockader per fleet is now kept between checks; Players.AvatarDied is now AvatarDiedEvent with Killed (damage, via sHealth.Die) vs removed by the player (spawn/remove key, map view); DesignBuilt/Building docs say dead blueprints are zero-health wrecks the faction repairs.
- **v0.20 (built):** `Events.Ai` (TargetSpotted: prefix/postfix on AITargetManager.TargetDetected, new key in _dictionaryOfTargetsAndScores; TargetLost and PrimaryTargetChanged: snapshot of keys and _primaryTarget around AITargetManager.Update; all on the detection thread, queued to main; MovementChanged/FiringChanged/RoutineChanged via Var.SetChangeAction on AIMainframe.Pack registered once per pack from AiNode's constructor (ConditionalWeakTable; nodes are rebuilt on relink), Initial/Load calls skipped). `Events.Detection` (ContactMade: first track creation in MainConstructDetection._constructDictionary around DetectionParametersAndMaths.ProvideADetectionOnThisConstruct, so UI-created tracks don't count; IncomingProjectile: MissileWarningManager.MissileDetected returning true, owner craft found by MWM reference and cached, repeats suppressed for 5 s per projectile per craft). `Events.Campaign.FleetOrdered` (FleetMovementOrders.IssueMoveOrder position/follow overloads with ObjectId ids; ByAi tagged from MoveHandler.ExecuteMoveOrder) and `FleetWaypointReached` (Orders.OrdersReachedMetric around FleetMovement.RunPositionFixedUpdate). Added the Ai.dll reference.
- v0.20.1 (after in-game test): mode events switched from Var.SetChangeAction (no mode changes were reported in game; cause not found) to comparing Pack.Movement/Firing/SelectedBehaviourId/SelectedManoeuvreId once a frame in an AiNode.OnUpdate postfix (first look is silent; routine changes between non-routine ids skipped). TargetLost is debounced: non-destroyed drops wait 2 s and are cancelled if the target is re-added (the game drops and re-adds some targets every update, e.g. salvage when AttackSalvage is off: 400 spotted/lost pairs per 5 s in test). Verified: TargetSpotted, PrimaryTargetChanged, ContactMade, IncomingProjectile (Aps/Missile/Cram), FleetOrdered (player, queued), FleetWaypointReached incl. last, AI order/waypoint counts.
- v0.20.2: RoutineChanged also fires when the selected routine is deleted (id no longer resolves; last known name kept). Verified in game (0.20.1 build): MovementChanged Automatic/Off/Manual/Fleet, FiringChanged On/Off, RoutineChanged delete and re-add of behaviour and manoeuvre, from the map AI window.
- **v0.21 (built):** mod keybinds (`KeyBinds.Register`, `KeyCombo`, `KeyBind.OnPressed/OnReleased/IsHeld`, `Events.Input.KeyPressed/KeyReleased`): the game's InControl dynamic actions are only saved for profile-switch keys and its rebinding screen lists only its enum inputs, so the API polls Unity Input on GameEvents.UpdateEvent, matches modifiers exactly, skips while GUIUtility.keyboardControl != 0, saves player choices to FtdModApi.keybinds.json (entries for absent mods kept) and adds a Keybinds tab (Set = capture next key, Reset, Unbind; conflicts between mod keys shown). Lua: `LuaFunctions.Register` adds callbacks to one shared `Mods` table put in every sandbox by a postfix on LuaMaster.CreateSandbox (later registrations reach running boxes; exceptions become ScriptRuntimeException so the box stops with the message); `Events.Lua.ScriptError` (Running true→false with ErrorStack around RunLuaUpdate; SetLuaCode failures) and `Logged` (LuaBinding.Log/LogToHud, host only). Added FtdLua.dll and MoonSharp.Interpreter.dll references.
- v0.21 verified in game: Ctrl+K keybind, rebinding to Ctrl+J in the Keybinds tab (saved to FtdModApi.keybinds.json, old key stops working), Mods.ExampleBalanceMod.Hello/BlocksDestroyed from a Lua box, Lua.Logged. Lua.ScriptError verified next session: runtime error (attempt to concatenate a nil value) once per run of the box, and a syntax error reported as loading. Note: after a failed load the box keeps the previous code's Update, so running it again repeats the old runtime error (game behaviour). Build note: FtdLua.dll references MoonSharp.Interpreter 0.0.0.0, so LuaMaster.Lua can't be used at compile time; the script is taken from the sandbox Table's OwnerScript.
- **v0.22 (built):** `Events.Ui`: WindowOpening (cancellable prefix on GuiDisplayBase's explicit IGuiDisplayerInternal.ActivateUi), WindowOpened (GuiStore.AddRequest postfix; BringToTop re-adds skipped), WindowClosed (before/after diff around GuiStore.PopAll/PopTop/PopSelected at the outermost call only, since closing a window can close others inside; PopInvalidSubject runs on every GUI event, so it only snapshots when a window has lost its subject), BlockInteracting/Interacted (cInteract.PerformSecondary, Q). Focus = the window's _focus field (captured before close). GetDefaultName avoided (ConsoleUi's override may recurse). Opportunistic GUIs (HUD, tooltips) are not in GuiStore and raise nothing.
- v0.20 review notes: Var change callbacks are (new, old, type); Paste/Reset/Initial/Load not reported; a new Fleet's constructor issues a 'stay here' order (skipped via a Fleet..ctor flag; its arrival is skipped too); FleetWaypointReached is host-only (clients complete orders through SimulateOrderReached); ContactMade needs a mainframe (DetectionMakers belong to AiNodes); mainframe destruction clears targets without TargetLost.
- Deferred from 3.9/3.10: ammo/fuel use (hot path), repair cost, refining, strategic AI decision inspection, capture-timer progress.
- Known gaps: drones raise `Spawned` before their mothership; first block of a brand-new craft (`Server_PlaceConstructable`) raises no Placing.
- Not yet: construct modules (per-craft data), collisions, sinking, blueprint save/load.

## 5. Proposed build order
1. **Event framework**: Pre/Post base classes, `ICancellable`, main-thread dispatcher, side tags, lazy patching, feature ids, verbose first-raise logs, correctly named tick events, and native `GameEvents` wrappers. Document the thread and side of every event.
2. **Construct and block events** (3.1, 3.2) plus **construct modules** (per-craft mod data saved in blueprints).
3. **Weapon events**: generic WeaponFiring/Fired/CantFire, then APS, CRAM, missiles; then lasers, PAC, plasma, flamer, simple, melee.
4. **Projectile events** (hit, detonated, intercepted, reflected).
5. **Session events** (mode, save/load with mod save data, pause, victory) and **player events**.
6. **Resource and campaign events.**
7. **AI, detection, UI/input** (mod keybinds), **scripting** (Lua functions, breadboard components) and **ModNet**.

Each step ships with verbose logging and, later, checks in the API test mod.

### v0.22.1 (UI events checked in game, 2026-10-18)
- Verified: Q on APS/range finder → BlockInteracted + ApsUi/TargetTrackingUi open/close; map → FleetControlGUI; AI button → AiUiExtension (one per mainframe, up to 4) + NavalAiUi routine editor; GameMenuUi, LoadVehicleUi, SettingsUi, MainMenuUi. Each window one open and one close.
- The apparent "AiUiExtension closed twice" was two AI windows (Trenton has two mainframes); the second "opened" line had been dropped by the game's log file.
- Finding: the game's log file (ConsumerWrittenLog, DecideWhenToPopUp(50, 0.1f)) drops a line identical to one less than 0.1 s of game time ago (TimeCache stops while paused) and writes any one line at most 51 times per session. ModLog now copies that rule and adds " (repeat N)" to lines the game would drop.
- Fix: WindowOpened is now raised parent-first. AddRequest prefix queues, the outermost finalizer raises windows still open. Before, a child opened inside the parent's OnActivateGui was reported first. A window opened and closed during the same opening reports neither.
- BringToTop suppression now applies only to the window being brought up, not to windows it opens.
- APS block menus show Focus = the firing piece of the cluster, not the block Q was pressed on. BlockInteracted is raised after the window opens.
- v0.22.1 verified in game: parent-first open order (FleetControlGUI, AiUiExtension, NavalAiUi, AiUiExtension (repeat 2)); closing with X (routine editor alone, then the map with both AI windows) and with Esc (all at once) reports each window once. "(repeat N)" works.
- v0.22.2: the verbose "Event hook for X installed" line now names the hook's patches, so several hooks of one feature don't print as repeats.
- UI events done. Next: breadboard components.

### v0.23.0 Breadboard components (written 2026-10-18, not yet built)
- API: `ModBreadboardComponent` (AddInput/AddOutput, AddNumberSetting/AddToggleSetting/AddTextSetting, InNumber/InBool/InVector/InRotation/InText, Out/OutInteger, Remember/Recall slots 0-4 via MyState (per missile on missile boards), Construct/BreadboardBlock/Missile/Kind/Board, Label, Compute on worker threads) + `BreadboardComponents.Register<T>(modId, id, name, desc, BreadboardKinds)`.
- Storage: one `VarString ModData` [Variable(7777)] holding key=value lines; identity (@guid,@mod,@id,@name) is its default with SaveDefault=true. Guid = SHA1(modId/id) with first 4 bytes "FtdM".
- Game lists: BoardTypes.FtdBreadboard (AI board copies it + FtdAiBreadboard at BlockStart → register in OnLoad), MissileBreadboard. Menu name via a LocalBoardComponentScraped subclass set on ComponentType.BoardAttribute.
- Hooks (always on): Board.MakeSystem prefix (creates mod components itself, guarded; unknown marker guid → MissingBreadboardComponent keeping pins/links/ModData, pins frozen), Board.OnPackageAddedToSystem postfix (attach board), Board.Load postfix (pad pins), editor segment UiEntry postfixes (titles).
- Known limits: breadboard prefabs find classes by type name (BreadboardPrefab) so a missing mod's component is dropped from a prefab; guid check guards same-name classes. Game's log file found: identical log lines limited (see v0.22.1).
- Example mod: "Scale and limit" (all boards), "Craft status" (vehicle+AI).
- v0.23.0 verified in game (2026-10-18): components in menu, links + settings survive save/respawn, copy/paste of a breadboard between craft, missile breadboard shows Scale and limit, missing-mod round trip (2 placeholders logged once each, resaved, components work again with settings). No API errors. "Change sync not enabled for <component>" warnings come from the game's own AddANewComponent (also for Printer/StabilityInput). Open question: no output on the designer's Mobile Drydock (not confirmed whether the game runs boards there).

### v0.24.0 ModNet (written 2026-10-18, not yet built)
- Game networking: Steam only (SteamInterface) or DummyInterface offline; NetSys.Configure runs after plugin load (BootTaskMultiplayer) → identity made lazily each frame. RPC: identity (MakeOrGetUniqueIdentity(id<1000); game uses 0, 50-55) + MonitorThis(obj) of [NetworkMethod] methods; method id = name.GetHashCode(). Client→host: SendMessageToHost; host→client: SendMessageFromHostToOnePlayer (reliable, ordered; Steam splits >512 KB).
- API: identity 873, Endpoint methods FtdModApi_ModNetToHost/ToClient(byte[], MessageInfo). Payload [format=1][channel id "mod/name"][bytes]. Hello handshake ("@FtdModApi/hello": version + channel ids): client resends every 3 s up to 10, host answers; peers keyed by Steam UniqueId; reconnect replaces stale peer; dead peers skipped/removed. Client messages wait for the handshake (max 200) and are dropped if the host has no ModNet.
- Local delivery (SP/host SendToHost, LoopbackClients): own queue drained once per frame in Poll, through the game's MessageSerialise + MessageDeserialise.DeserialiseAndInvoke (real RPC path). Tunable FtdModApi.ModNetLoopback.
- Testing: no second player available. SP/hosting alone exercises everything but the Steam connection; handshake/peers need community testing (verbose logs record connections).
- Example: Ctrl+L ping/pong with per-machine Guid.
- v0.24.0 tested (2026-10-18): SP ping (loopback), pong with ModNetLoopback (48 ms), hosting alone: "network changed from Disconnected to Server", ping/pong (37 ms). Bug: Net.ModNet stayed Pending (no patches) → example reported it unavailable; v0.24.1 sets it Available when the identity registers.
- OPEN: softlock while hosting after opening the FtdModApi settings window and pressing Clear on ModNetLoopback (no input to the window, Esc/pause menu dead, camera still moves). Nothing logged after "Settings window opened" (no Clear, no tunable change, no errors; log footer written on Alt+F4, so the main thread was alive). v0.24.1 adds verbose logs for Clear, Esc and window close to narrow it down.
- SOLVED (v0.24.2): the "softlock" was the multiplayer chat. Ctrl+M (and Ctrl+J) type a newline; ChatGUI.CheckForInput reads Event.character '\n' as Enter and focuses chat (only when ChatGUI mode is game, i.e. multiplayer), which then takes all keys behind our window. Fix: prefix on ChatGUI.CheckForInput skips newline KeyDown events with Ctrl held. Also protects mod keybinds on Ctrl+J/M.
- v0.24.2 verified in game (2026-10-18): chat patch applied, settings hotkey while hosting no longer opens chat; ModNet listening (Steam), host/disconnect transitions logged; example no longer reports ModNet unavailable. Startup summary still lists Net.ModNet as Pending (it registers a few frames later; cosmetic).
- Process note: two device commits silently did not land (PC kept old files). Always re-stage and cmp after committing.

### v0.25.0 Craft data and craft modules (written 2026-10-18, not yet built)
- Storage: Blueprint.SerialisedInfo.JsonDictionary (MiscDataSerialisation, string→string, saved in blueprint JSON and BinaryFormatter MP transfer). Keys "FtdModApi.data/<mod>/<key>" and "FtdModApi.module/<mod>/<id>". Unknown FtdModApi.* keys kept and rewritten.
- Hooks (all required; data inactive unless all applied): MainConstruct.InitialiseStage2 prefix (load), MainConstruct.BlueprintInitialisationDone postfix (attach modules: end of every spawn path via BlueprintInitialisation.Run), MainConstruct.Save postfix (OnSave + write; can run on autosave worker thread), MainConstruct.DestroyCompletely prefix (OnRemoved while intact; not called on quit).
- Semantics: values belong to the saved design (all spawns from a blueprint file inherit). Retrofit takes the new design's values (not carried over). MP: clients load spawn values, not synced afterwards; client keeps own copy after out-of-play.
- Module failing OnSpawned never saves (keeps data). OnUpdate only for modules overriding it; stops after 5 failures per craft.
- Example: ExampleCraftLog (spawns + blocks lost) + CraftData "firstSeen".
- v0.25.0 verified in game (2026-10-18): all 4 patches applied, Craft.Data Available. Designer: Trenton saved with 1 spawn / 4 blocks lost → respawn "time 2, 4 lost, same first seen". Every spawned craft (enemies, drydock) gets a module; OnRemoved runs with the name intact. Campaign: retrofit to the saved design → craft spawned with the design's values (as documented); campaign saves ran with no errors. Missing-mod round trip (step 5): values kept and restored. No API errors; only known game errors (Force.GetBoundingBox, FleetMovement NRE through our patched RunPositionFixedUpdate = game bug, MeshMaker collection modified).
- Not yet seen: reloading a campaign save and a craft coming back into play with its own values.
- v0.25.0 campaign round trip verified (2026-10-18): retrofit to "step 5" design → PO-I-16-M and BC - Victorious spawned time 2; campaign saved (slot 5); reloaded → both time 3 (own counts kept); Victorious pulled out of play (3) and back in → time 4; saved again. CheckIsValid passed for the blueprints; no API errors.
- TO DO before release: offline patch checker (README roadmap 6c), API test mod. Suggested extra check: load an API-saved blueprint with FtdModApi disabled (vanilla) to confirm extra SerialisedInfo keys are ignored.

### v0.26.x Offline patch checker (written 2026-10-18; first clean-up v0.26.1)
- FtdModApi/Patching/PatchChecker.cs (public PatchChecker.Run(offline, verbose) → PatchCheckReport.ToText). Gathers: Bootstrap.StartupSources() (12 named groups), ApsGauge.MainMenuSpecs, BreadboardComponents.Specs, LuaFunctions.Specs, every EventRegistry hook's BuildSpecs (after Events.TouchAll). Checks: targets resolve; Harmony hook params (__instance, __result, __state across hooks, ___fields, __n, named params + types); transpilers run offline on PatchProcessor.GetOriginalInstructions then Verify (not run in game). TypeLoad/Missing* = BROKEN; FileLoad/Security/DllNotFound etc = UNKNOWN; transpiler not runnable = UNKNOWN.
- Tool: src/Tools/PatchChecker (net8.0, RollForward Major, not in sln), RunPatchChecker.cmd (exit 0 ok, 1 broken, 2 couldn't run, 3 build failed). Report FtdModApi_patchcheck.txt. Game version read from FtdVersion statics offline. ModLog.Offline buffers log lines.
- Expected caveats: Lua (MoonSharp version quirk) may be UNKNOWN on .NET; transpiler IL generator on .NET 8 untested.
- Build fix (v0.26.0): netstandard compile can't bind Harmony net45 methods with an ILGenerator parameter (CS7069 mscorlib) → PatchChecker calls PatchProcessor.GetOriginalInstructions via reflection, generator kept as object.
- Process note: device_commit_files can silently write a stale cached copy when the same staged path was committed before. Commit from a fresh staging filename (e.g. outputs/staging/<name>.<time>.cs) and always re-stage + cmp.
- First real run (v0.26.0, game 4.3.4): 169 patches, 160 OK, 7 BROKEN, 2 WARN. All 9 failures were the checker, not the game: AccessTools.FieldRefAccess cannot build accessors on .NET 8 (needs MonoMod). Fix v0.26.1: Core/FieldRefs.Get<T,F>(name) — in game it is FieldRefAccess; offline it only checks the field exists, is an instance field and its type fits F. All 13 call sites switched (AI, campaign orders, detection, Lua log, avatar death, missile detonation, breadboard editor titles). Expected after fix: 169/169 OK. Rule for new code: never call AccessTools.FieldRefAccess directly, use FieldRefs.Get.
- v0.26.1 confirmed (2026-10-18): patch checker 169/169 OK on 4.3.4.

### v0.27.0 Prevent paste (hidden-block paste) (written and verified in game 2026-10-18)
- API: Content.PreventPaste(modId, guidOrName), Content.PreventPasteFromModFolder(modId, folder); JSON "preventPaste", "preventPasteModFolders". Feature Content.PreventPaste. Mirror variants (MirrorLaterial/VerticalFlipReplacementReference) included. Rules resolved to block guids after Configured.RunReplacements (and live when added/removed later); matching is by the definition the runtime id resolves to, so ReplaceItem is respected.
- Game path: cBuild.PerformPrefabOperation -> new PrefabCommand(prefab,...).Apply() -> PrefabSaveAndLoad.PlacePrefab -> PlacePrefabLocal (SP) or BlueprintTransfer + SpawnRequest (MP, host SpawnRequestManager / clients ClientsideLogic call PlacePrefabLocal on the transferred blueprint). EraseCommand/DisconnectionCommand undo also use PlacePrefab (with restoring prefabs) -> deliberately NOT filtered (would delete existing blocks).
- Hook: prefix on PrefabCommand ctor (Blueprint, AllConstruct, Vector3i, Quaternion, MirrorInfo) replaces `ref prefab` with a filtered copy (MemberwiseClone; arrays BlockIds/BLP/BLR/BCI/BP1/BP2/BlockState rebuilt; BlockData entries [3-byte index LE + SuperLoader packet] re-indexed; BEI [index,len,data] re-indexed; _resourceCost cache nulled). Undo/redo and MP use the copy, so host/clients agree; the player's BuildingWith.Prefab is untouched. Fallback on unexpected layout: copy with blocked BlockIds = -1 (FindUsingTheRuntimeId -> not found -> skipped; MP logs "Did not find guid for item number -1").
- Player message: optional prefix/finalizer on cBuild.PerformPrefabOperation sets a [ThreadStatic] flag; ModLog.Hud "N blocks left out of the pasted prefab (not allowed by <mod>): names". Host-side ctor (from a client's paste) logs Info instead.
- Not covered on purpose: spawning blueprints, subobject placement (BuildChecks.AddSubConstruct), save load, retrofit. Ghost preview still shows blocked blocks.
- Example mod: PreventPaste "Light fitting".
- v0.27.0 verified (2026-10-18): checker 171/171 OK. Designer on Trenton: paste left out 'Light fitting x2' (HUD), 3 blocks placed incl. spot lights with their own settings; undo/redo x4 reused the filtered copy (3 blocks each, no repeat message, as designed); hand placement fine; saved 'CA - Trenton - Light test' and respawned. No API warnings or errors.

### v0.28.x Custom APS payloads (written and verified in game 2026-10-18)
- API: ShellPartDefinition.Payload (abstract ApsPayload: Detonate(ApsPayloadHit), virtual Describe(ApsPayloadShell) -> ApsPayloadStat rows) and KeepGamePayload (default false). ShellPartInfo.Payload/KeepGamePayload captured at Register. Feature Aps.Payloads; startup group "APS payloads".
- Game: AdvPooledProjectile.Explode (host-only, worker thread) applies spalling/HEAT/HE+flak (HeAndThumpExplosion)/EMP/fire/frag/smoke from ShellModel_ExplosiveCharges getters; returns `exploded`, sets _explodedAlready. Callers deactivate the shell when true. Strength unit = _moduleVolumeModifier * count * _explosiveModifier (same factor the game multiplies by AdvCannonConstants.BaseXDamage).
- Hooks: prefix (__state = no payload parts || _explodedAlready) + postfix on Explode: if host and the shell (ConditionalWeakTable<ShellModel, plan>) has payload parts, run each Detonate (try/catch, ErrorOnce per part), set _explodedAlready = true and __result = true. Construct falls back to _pState.Fusing.LastBlockHit for delayed fuses (as the game's EMP/fire). DamageFactor = HealthDamageFactor(Health / shell.Health).
- "No game payload": ShellParts Behaviour_Transpiler now takes MethodBase original; inside ShellModel_ExplosiveCharges (and nested) it calls EffectivePayloadModuleType/ParamsType, which map no-game-payload parts to SolidWarhead.
- Customiser: optional postfix on StatsTab.Build adds one table per payload part (protected GetNewTableSegment/AddTableRow via reflection; live getters re-fetch the inspected shell).
- Helpers: Explosion/ExplosionAt/HeExplosion (ExplosionCreator.Explosion, API explosion modifiers apply), Emp (ProjectileContactEffectHandler.ApplyEmp), Fire (FireRestricted.DelayedCreateFire), Fragments (FragGenerator), OnMainThread (MainThread.PostLater). Static ApsPayloads.HeDamage/FlakDamage/EmpDamage/FragmentDamage/ExplosionRadius/FragmentCount.
- Postfix order note: Projectiles.Detonated event postfix reads __result; startup patches are applied before lazy event hooks, so the payload postfix runs first and payload-only shells are reported.
- Example: "Cluster charge" (BehavesAs HeWarhead, ExampleClusterPayload: 4 HE blasts 1.5 m apart, total strength split; logs first 3 detonations). Example requires 0.28.0.
- v0.28.0 first test (2026-10-18): checker 173/173 OK, both payload patches applied, Cluster charge registered, user reports customiser table and blasts OK; BUT the log has no 'Cluster charge ... went off' lines and no payload errors, so Detonate was not proven to run. v0.28.1 adds VerboseOnce lines on first plan and first detonation per payload part; retest pending.
- v0.28.1 verified (2026-10-18): only the 280 mm main gun with 3 Cluster charge parts. Plan: strength 1.056 at 280 mm. First detonation run_out_of_kinetic_energy on 'Brigand'; example logged 3 detonations of 4 blasts x 840 damage, radius 7.5 m. No API warnings/errors. (The first v0.28.0 test fired other guns' shells, which is why no payload lines appeared.)

### API test mod FtdModApiTest 1.0.0 (written 2026-10-18, not yet run)
- Project TestMod/FtdModApiTest (in the sln; deploys only with -p:DeployTestMod=true). Requires API 0.28.1. Header guid 10bd8a21-a5cc-4316-ab6f-af4f14395a6a; sln project guid {A3F6B5CF-BF10-44B9-AE54-5CF12B679B1A}.
- Report: Mods/FtdModApiTest/FtdModApiTest_report.txt (main menu, every 30 s when changed, on quit, Ctrl+Shift+T with HUD summary). Statuses PASS / FAIL (sticky) / .... (not seen yet + hint) / info.
- Self-tests at main menu: pipeline priority order + removal; failing modifier disabled after MaxFailures (logs 2 expected errors 'expected-failure test'); ApiEvent; tunables Add/Multiply/Set composition and undo, min/max, game key AdvCannon.BaseExplosiveDamage equals the field; ContentIds; ModNet writer/reader all types + damaged data; KeyCombo parse; Content lookups; ApsPayloads formulas.
- Live: every EventChannel found by reflection over Events nested types + ModNet, subscribed at Monitor (ignoreCancelled false), IsAvailable checked; observers (Priority.Observe) on all modifier pipelines with sanity checks; craft module (lifecycle, saved data round trip, CraftData set/get/keys/clear and JSON, probe cleared immediately); ModSaveData via Session.Saving/Loaded; ModNet ping to host (host echoes to clients); Lua Mods.FtdModApiTest.Ping; breadboard '[API test] Echo'; shell part '[API test] HE body' (BehavesAs HeWarhead, KeepGamePayload, CountingPayload); Features states each report.
