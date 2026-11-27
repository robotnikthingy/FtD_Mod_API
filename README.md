# FtdModApi

A shared modding API for From The Depths. FtdModApi owns the Harmony patches. Other mods register
**modifiers** and **event handlers** instead of patching the game themselves, so:

- Several mods can change the same value (for example kinetic damage) without overwriting each other.
- A game update breaks one patch in one place, and the API switches that feature off cleanly
  instead of every mod crashing or silently doing nothing.
- Mods don't copy whole game methods into prefixes or ship their own Harmony.

Built against FtD **4.3.4**.

## Layout

```
src/
  Directory.Build.props      shared settings: game path, target framework, game references
  FtdModApi.sln
  FtdModApi/                 the API mod
    FtdApi.cs                entry point: version, logging, Require()
    Core/                    plugin entry, assembly resolver, features, lifecycle events
    Patching/                PatchSpec + PatchManager (self-checking Harmony patches)
    Pipelines/               ModifierPipeline / ApiEvent (priority-ordered, fault-isolated)
    Damage/                  DamageApi + the patches behind it
    Blocks/                  BlockApi (armour class)
    Tunables/                the game's *Constants balance values, from code or JSON
    Content/                 hide or replace blocks, prefabs and shell parts; stable numeric ids for modded content
    Cram/                    CRAM shell stats hook and modded CRAM pellets
    Missiles/                modded missile parts
    Aps/                     APS shell stats and loader timing hooks
    lib/0Harmony.dll         Harmony 2.2.2 (net45), MIT licence
    ModFiles/                plugin.json + header.header deployed with the DLL
  Examples/ExampleBalanceMod a mod that uses only the API (no Harmony)
  Examples/ExampleDataOnlyMod a balance mod with no code: one JSON file (copy the folder into Mods to try it)
  TestMod/FtdModApiTest      the API test mod: checks every feature and writes a pass/fail report
  Tools/PatchChecker         offline patch checker (RunPatchChecker.ps1 / .cmd)
```

## Building

Requires the .NET SDK (6 or later).

```
cd src
dotnet build -c Release
```

The build copies the API into `Documents\From The Depths\Mods\FtdModApi`. The example mods are only
copied when you add `-p:DeployExamples=true`, and the test mod when you add `-p:DeployTestMod=true`. If the game is installed somewhere
else, pass `-p:FtdManaged="<game>\From_The_Depths_Data\Managed"`. Pass `-p:FtdDeploy=false` to build
without copying.

After launching the game, open `Mods\FtdModApi\FtdModApi_status.txt` to see which features are
working, every patch result, and any other mods patching the same game methods. The mod list also
shows a one-line status.

## Writing a mod on the API

1. Reference `FtdModApi.dll` (`Private=false`, so it is not copied), plus the game DLLs as usual.
2. In `plugin.json`, set `"depends": ["FtdModApi"]`. FtD then loads the API first.
3. Don't ship `0Harmony.dll`. If you need your own patches, reference the API's copy.
4. Register in `OnLoad`:

```csharp
FtdApi.Require(new Version(0, 1, 0), "MyMod");

DamageApi.ProjectileLaunch.Add("MyMod", ctx =>
{
    if (ctx.Source == ProjectileSource.AdvancedCannon) ctx.KineticDamage *= 1.2f;
});

DamageApi.Explosion.Add("MyMod", ctx => ctx.Radius *= 1.1f);

DamageApi.Block.Add("MyMod", ctx =>
{
    if (ctx.DamageType == ApiDamageType.Laser) ctx.Damage *= 0.8f;
});
```

See `Examples/ExampleBalanceMod` for a complete mod.

### Priorities

Modifiers run in priority order. When two modifiers share a priority, they run in mod-id order, so
the load order of mods never changes the result.

| Priority | Use for |
|---|---|
| `Priority.Add` (100) | flat additions |
| `Priority.Multiply` (200) | percentage changes; these stack multiplicatively |
| `Priority.Override` (300) | replacing a value outright |
| `Priority.Clamp` (400) | limits applied after everything else |
| `Priority.Observe` (1000) | reading the final value without changing it |

A modifier that throws is logged. After 5 failures it is switched off for the session, and the
rest keep running.

## Features (v0.28)

| Feature id | API | Game hook (4.3.4) | Notes |
|---|---|---|---|
| `Damage.ProjectileLaunch` | `DamageApi.ProjectileLaunch` | prefix on `ProjectileImpactState.Reset` | Starting kinetic damage and AP of APS, CRAM and simple-weapon shells and fragments. Affects penetration. |
| `Damage.ProjectileSource` | `ctx.Source` | prefixes on the four pooled projectiles' `ActivateHere` | Optional. If it fails, `Source` is `Unknown` and launch modifiers still run. |
| `Damage.Explosion` | `DamageApi.Explosion` | prefixes on both `ExplosionCreator.Explosion` overloads, with a depth guard | Every explosion: shells, missiles, PACs, ammo and fuel cook-offs, boilers, block deaths. |
| `Damage.Block` | `DamageApi.Block` | transpiler on `Block.ApplyDamage(IDamageDescription, float)` after `CalculateDamage`, plus a prefix for precalculated (fire) damage | Every damage type, per block. Changes health removed only, not penetration or HUD numbers. |
| `Damage.BlockDamagedEvent` | `DamageApi.BlockDamaged` | prefix and postfix on the same method | Notification after a block loses health. |
| `Blocks.ArmourClass` | `BlockApi.ArmourClass` | postfix on `Block.GetArmourClass` (no overrides in 4.3.4) | Every block. Also changes what the block tooltip shows. Clamped to at least 1, as the game does. |
| `Blocks.Health` | `BlockApi.MaxHealth`, `BlockApi.SetHealthMultiplier` / `SetArmourMultiplier`, `"blockHealth"` / `"blockArmour"` in `FtdModApi.content.json` | transpiler on `Block.InitialiseCommon` and `Block.ProcessPossibleMaximumHealthChange` (passes the block to the health calculation) | Per block, when it is placed or loaded. Current health scales with it. |
| `Settings.Menu` | `SettingsMenu.Open()`, `PlayerValues` | hotkey via `GameEvents.UpdateEvent` (no patch); optional prefix on `MainMenuAbstractTab.Done` adds Settings > Mod settings | In-game window: Game values / Mod values / Changed tabs with Default, Current and Player Value columns. Player values override everything and are saved to `Documents/From The Depths/FtdModApi.player.json`. Tooltips show a plain-language description of each value (`Tunables.Describe`, `FtdModApi.descriptions.json`). |
| `Tunables` | `Tunables.Set/Add/Multiply`, `Tunables.RegisterSystem`, `FtdModApi.tunables.json` | none: the game's `*Constants` classes are plain static fields it never writes | 397 game values in 4.3.4 (APS, missile, CRAM, laser, PAC, plasma, steam, fuel, propulsion, jets, campaign and more), plus every value mods register in their own systems. |
| `Tunables.ReadOnly` | same as `Tunables` (keys like `Explosion.AirBleedPower`) | transpilers on every game method that reads the getters (raw IL scan of the reader classes), one optional patch per group | 57 values in the Explosion, Detection, SharedWeapon and FuelRefinery groups that the game declares as read-only getters. Only values whose readers were redirected are listed. |
| `Content.Items` | `Content.HideItem/HidePrefab/ReplaceItem`, `FtdModApi.content.json` | postfix on `Configured.RunReplacements` (when FtD builds its content, after plugins load) | Hide removes from build menu and search; replace converts every existing copy in blueprints, saves and campaign designs (the game's own item-replacement system). |
| `Content.PreventPaste` | `Content.PreventPaste/PreventPasteFromModFolder`, JSON `preventPaste`, `preventPasteModFolders` | prefix on the `PrefabCommand` constructor (filtered copy of the prefab); optional prefix/finalizer on `cBuild.PerformPrefabOperation` for the player message | Blocks left out of build-tool prefab pastes; blueprints, saves, retrofits and undo are unchanged. |
| `Content.ShellParts` | `Content.HideShellPart` | postfix on `ShellModuleDictionary.GetAllModulesTypes` | Removes parts from the ammo customiser; existing shells keep working. |
| `Content.ShellPartRegistry` | `ShellParts.Register` | postfix on `ShellModuleDictionary.GenerateDictionary` | New APS shell parts, saved under a permanent number derived from their id. |
| `Content.ShellPartBehaviour` | (automatic) | transpilers on the `ShellModel_*` stat classes, projectile flight, firing, ejection and the part settings UI | Modded parts act as their `BehavesAs` game part wherever the game checks part types. |
| `Cram.ShellStats` | `CramApi.ShellStats` | postfix on `RailwayGunStats.GetStatsOfShell` | A CRAM shell's kinetic, AP, health, HE, frag, EMP and fire values. Also used for the gun's tooltip and fire control, so modifiers must be pure calculations. |
| `Content.CramPellets` | `CramPellets.Register`, `"cramPellets"` in `FtdModApi.content.json` | prefix/postfix on `CannonPackerAddon.UpdateConnectedTypeInfo`, postfixes on `CannonAutoLoader.SeeConnections` and `CramNode.PriorToSendingOutFeelers`, optional tooltip postfix | New pellet blocks: a copy of a game pellet (ItemDup file) with its own per-pellet multipliers. |
| `Content.MissilePartRegistry` | `MissileParts.Register` / `Register<T>`, `"missileParts"` in `FtdModApi.content.json` | adds to the game's missile part tables (`R_Missiles`) at the main menu; postfixes on the part property getters and `MissileBlueprint.GetLoadVolume`; optional prefix on `MissileBlueprintPersistence.UnserializeComponent` | New missile parts saved under a permanent number. Missing modded parts load as a body instead of breaking the missile. |
| `Content.MissilePartBehaviour` | (automatic) | transpilers on the missile classes that check part numbers (blueprint, warheads, explosion, triggering, movement, detection, mesh, launchpad, designer stats) | Modded parts count as their game part. Saving, copying and designer selection keep the real number. |
| `Aps.ShellStats` | `ApsApi.ShellStats` | postfix on the `ShellModel` constructor | Per shell design: reload, intake and cooldown time, speed, kinetic and AP modifiers, payload multiplier, health. Shown in the ammo customiser. |
| `Aps.Payloads` | `ShellPartDefinition.Payload` (`ApsPayload`), `ApsPayloads` helpers | prefix/postfix on `AdvPooledProjectile.Explode`; payload-code part types redirected in the shell-part transpiler; optional postfix on the ammo customiser `StatsTab.Build` | Modded shell parts with their own detonation code; parts carry no game payload unless `KeepGamePayload`. |
| `Aps.LoaderTiming` | `ApsApi.LoaderTiming`, `ApsApi.SetLoaderTimeFactor`, `"apsLoaders"` in `FtdModApi.content.json` | postfixes on `AdvCannonAutoloader.ComponentStart` (and the belt feed's), `ShellRackModelAbstract.CalculateLoadingTimeFactor`, `BeltFeedShellRackModel.SetDiameter` | Per loader block: the factor on reload time (lower = faster). |
| `Aps.GaugeCap` | `ApsGauge.RequestMaxGauge`, `"apsMaxGaugeMm"` in `FtdModApi.content.json` | transpilers on `AdvCannonNode.CheckGauge` and `AdvCannonFiringPiece.GaugeIncreasesToProvideShellDiameter`; postfixes on the `AdvCannonData`/`ApsShellUiData` constructors; raises the cached slider limits used when designs load; tooltip, ammo customiser and (at the main menu) `ApsTab.Build` | Highest request wins, up to 3000 mm. Above 500 mm the game's gauge increaser formula continues (1000 mm needs ~19 increasers on one barrel). |
| `Events.Ticks` | `Events.Ticks.Frame` / `PhysicsStep` / `EveryQuarterSecond` ... `EveryMinute` | none: the game's own `GameEvents` timers | Named by their real interval. |
| `Events.BuildMode` | `Events.BuildMode.Entered` / `Exited` | none: `GameEvents.OnEnterBuildMode` / `OnExitBuildMode` | Local player only. |
| `Events.Constructs.Lifecycle` | `Events.Constructs.Spawned` / `Despawned` | postfix on `BlueprintInitialisation.Run`, prefix on `MainConstruct.DestroyCompletely` | Every way a craft appears or is removed. |
| `Events.Constructs.Play` | `Playing` / `Played`, `Pulling` / `Pulled` | prefixes on `ForcePlayingAndPulling.PlayForce` / `PullForce`; `Force.MakeInPlay` / `MakeOutOfPlay` | Campaign in and out of play. The "...ing" events are cancellable. |
| `Events.Constructs.Scrap` | `Scrapping` / `Scrapped`, `Defeating` | `MainConstructScrapping.StartScrap`, prefix on `EnemyConstructableStateChecker.DisposeOf` | `Defeating` has the clean-up rule (AI dead, sunk, ...); cancelling resets the countdown. |
| `Events.Constructs.Team` | `TeamChanging` / `TeamChanged` | prefix on `FleetForceChangeSync._ChangeForceTeam`, `Force.SetTeam` | Captures. |
| `Events.Constructs.Subconstructs` | `SubconstructAdded` / `SubconstructRemoved` | postfix on `ConstructBasics.InitialiseSubConstructable`, prefix on `SubConstruct.DestroyCompletely` | Turrets, spin blocks, pistons. |
| `Events.Blocks.Build` | `Events.Blocks.Placing` / `Placed`, `Removing` / `Removed` | prefixes on `mBuild.Server_PlaceBlockOnConstructable` / `Server_ReplaceBlock` / `Server_PlaceSubConstructableOnConstructable` / `Server_RemoveBlock`; `ConstructBasics.AddBlock` / `PermanentlyRemoveBlock` | Placing (blocks, replacements, turrets) and Removing are cancellable (host). Placing runs before the game's own fit and cost checks. |
| `Events.Blocks.Destroyed` | `Events.Blocks.Destroyed` | `Block.MainThreadHealthIsZeroSoKill` | With the damage and the gunner. |
| `Events.Blocks.Repaired` | `Events.Blocks.Repaired` | postfix on `AllConstruct.BlockRepairedSoPerformAllActions` | Any repair. |
| `Events.Weapons` | `Events.Weapons.Firing` / `Fired` | transpiler on `ConstructableWeapon.Fire`: its `WeaponFire` call goes through the API | Every weapon type, after the game's readiness checks. `Firing` is cancellable and runs on the calling thread (often an AI worker thread). |
| `Events.Aps` | `Events.Aps.ShellFired` | postfix on `AdvCannonFiringPiece.MainThreadFireCallback` | Shell design, projectile, rail draw. |
| `Events.Cram` | `Events.Cram.ShellFired` / `Packed` | `CannonFiringPiece.MainThreadFire`, `RunPacking` | |
| `Events.Missiles` | `Events.Missiles.Launched` / `Reloaded` / `Detonated` | `LaunchpadAbstract.PrepareAndFireMissile`, `MissileTube.Reload`, `MissileExplosionHandler.ExplodeNow` (+ optional prefixes on fuse, collision, interceptor and breadboard callers for the cause) | Detonated has the cause (impact, fuse, shot down, expired, interceptor, scripted) and whether it was armed. |
| `Events.Projectiles` | `Events.Projectiles.Hit` / `Detonated` / `Damaged` / `ShieldHit` | `ProjectileDamage.ProcessOne`; `Explode` on the APS, CRAM and simple-weapon shell pools; `Projectile.ReduceHealth`; postfix on `ShieldProjector.Reflect` | Hit has health before/after (kill detection). Damaged covers missiles and shells being shot at. ShieldHit runs on the projectile thread and can change whether the shot is reflected. |
| `Events.Session` | `Events.Session.ModeChanging` / `ModeChanged`, `Saving` / `Saved` / `Loaded`, `Paused` / `Unpaused` / `TimeScaleChanged`, `GameFinished`, `Quitting`; `ModSaveData` | `ModeChanger.DoPrior` / `DoStart`, `PlanetFileSaveLoad.SaveToSlot` / `OpenSaveSlot`, `GameSpeedManager.SetTimeScale`, `VictoryAssessment._Client_GameFinished`, native `GameEvents.PreGameQuit` | Mod save data is one string per mod per save, stored in `FtdModApi.moddata.json` in the save's folder. |
| `Events.Players` | `Events.Players.Added` / `Removed`, `AvatarSpawned` / `AvatarDied`, `SeatEntering` / `SeatEntered` / `SeatLeft`, `PrefabPlacing` / `PrefabPlaced`, `ChatSending` / `ChatReceived` | `StaticPlayers.Register/UnregisterPlayer`, `bAvatar.DoTheSpawn`, `sHealth.ForceDie`, the chair methods of `s/bTeleportAndHold`, `PrefabSaveAndLoad.PlacePrefab(Local)`, `ChatLog.Send` / `_AddChatEntry` | Chat exists in multiplayer only. ChatSending can change or cancel a message. |
| `Events.Resources` | `Events.Resources.Gaining` / `Gained`, `Dumping` / `Dumped`, `Harvested`, `Transferred` | `ResourceStoreAbstract.GiveResourceAndExp` (+ `DumpPickupManager.PickupDumpedResources` for the source), `ResourceDumpSync.CreateNewDump` / `_CreateNewDump`, `ResourceGathererAbstract.Run` + `MapResourceZone.HowMuchResourceCanITake`, `SupplyGroupProcessingFtdSingleton.MakeEvents` | Gaining and Dumping can change the amounts or cancel. Harvested is added up per craft per second (craft in play only). |
| `Events.Campaign` | `Events.Campaign.FleetCreated` / `FleetRemoved`, `FleetOrdered` / `FleetWaypointReached`, `TerritoryChanging` / `TerritoryChanged`, `FactionEliminated`, `RelationsChanged`, `BattlePreparing` / `BattleStarted` / `BattleEnded` / `BattleCancelled`, `BlockadeStarted` / `BlockadeEnded`, `OffscreenBattleStarted` / `OffscreenBattleEnded` / `OffscreenForceKilled`, `DesignBuilding` / `DesignBuilt` | `FleetManager.AddFleetToList` / `RemoveFleet`, `CampaignSync.(_)ChangeBoardOwner`, `AreaNeutralizer`, `FactionRelations`, `BattleOrchestratorSync` + `EndBattleInfo.PerformTheBattleEnd`, `BlockadeManager.RunBlockadeChecks`, `OffscreenFactionBattleCoordinator`, `BattleStarterCore.OnKilled`, `HandleBuilding.ExecuteBuildOrder` + `AiUnitSpawner.SpawnExactThing` | TerritoryChanging can give the square to another faction or cancel; DesignBuilding can stop an AI build. |
| `Events.Ai` | `Events.Ai.TargetSpotted` / `TargetLost` / `PrimaryTargetChanged`, `MovementChanged` / `FiringChanged` / `RoutineChanged` | `AITargetManager.TargetDetected` / `Update` (detection thread), each mainframe's settings compared once a frame (`AiNode.OnUpdate`) | Raised once per AI mainframe. Mode changes skip blueprint loading. |
| `Events.Detection` | `Events.Detection.ContactMade`, `IncomingProjectile` | `DetectionParametersAndMaths.ProvideADetectionOnThisConstruct`, `MissileWarningManager.MissileDetected` | Works without a mainframe. IncomingProjectile reports a projectile once per craft (again after 5 s out of sight). |
| Mod keybinds | `KeyBinds.Register(modId, id, name, new KeyCombo(KeyCode.K, KeyModifiers.Ctrl))`, `bind.OnPressed(...)`, `Events.Input.KeyPressed` / `KeyReleased` | none (polled on the game's frame update) | Players rebind them in the settings window's Keybinds tab; saved to `FtdModApi.keybinds.json`. Not fired while typing in a text box. |
| Lua | `LuaFunctions.Register(modId, name, call => ...)` (Lua: `Mods.<ModId>.<Name>(...)`), `Events.Lua.ScriptError` / `Logged` | `LuaMaster.CreateSandbox`, `LuaBox.RunLuaUpdate` / `SetLuaCode`, `LuaBinding.Log` / `LogToHud` | Lua runs on the host only. An exception in a mod function stops that Lua box with the message, like a Lua error. |
| `Events.Ui` | `Events.Ui.WindowOpening` / `WindowOpened` / `WindowClosed`, `BlockInteracting` / `BlockInteracted` | `GuiDisplayBase.ActivateUi`, `GuiStore.AddRequest` / `Pop*`, `cInteract.PerformSecondary` | Window events carry the window's class name and what it shows (`Focus`, e.g. the block). Opening and interacting can be cancelled. |
| Breadboard components | `BreadboardComponents.Register<T>(modId, id, name, description, kinds)` with `T : ModBreadboardComponent` | `Board.MakeSystem` (prefix), `Board.OnPackageAddedToSystem`, `Board.Load`, the component editor titles | Vehicle, AI and missile breadboards. Components of a mod that isn't installed are kept (shown as missing) with their settings and links. |
| ModNet | `ModNet.Channel(modId, name)`, `channel.OnReceive(m => ...)`, `SendToHost` / `SendToClients` / `SendTo` / `SendToClientsExcept`, `ModNet.Peers`, `PeerJoined` / `PeerLeft` | none: the game's own network messages (identity 873) | Only machines running the API take part; a handshake says which channels each side has. In single player `SendToHost` arrives on this machine through the game's message code; `ModNetLoopback` delivers host-to-client messages locally too. |
| Craft data and modules | `CraftData.Get/Set(craft, modId, key, ...)`, `GetJson/SetJson`; `CraftModules.Register<T>(modId, id)` with `T : CraftModule` (`OnSpawned(saved)`, `OnSave()`, `OnUpdate()`, `OnRemoved()`) | `MainConstruct.InitialiseStage2`, `MainConstruct.Save`, `MainConstruct.DestroyCompletely` | Saved in the blueprint's own data dictionary, so it travels with blueprint files, campaign saves and craft going out of play and back. Values of mods that aren't installed are kept. |
| `Lifecycle.MainMenu` | `Lifecycle.WhenMainMenuReady`, `Lifecycle.MainMenuEntered` | postfix on `MainMenuChangeSequence.StartFromChange` | For code that needs UI styles loaded (the delayed-patching trick several mods copy). |

### Tunables

FtD keeps most balance numbers as static fields on classes like `AdvCannonConstants`, `MissileConstants`
and `CramConstants`, and never writes them itself. The API finds them all at startup, records the
defaults, and lists them in `Mods/FtdModApi/FtdModApi_tunables.txt`.

```csharp
Tunables.Multiply("MyMod", "AdvCannon.BaseReloadTime", 1.5);   // slower APS reloads
Tunables.Set("MyMod", "Missile.BaseHealth", 900);                  // tougher missiles
```

78 more values (Detection, Explosion, SharedWeapon, FuelRefinery, and some Game and Map values) are
read-only getters (`public static float X => 3f;`), not fields, so they aren't tunables. Changing them
would need getter patches, and Mono can inline such tiny methods into their callers, which bypasses the
patch. They'll be handled case by case with patches on the code that uses them.

A balance mod needs no code at all: put `FtdModApi.tunables.json` in a mod folder with a `header.header`
(see `Examples/ExampleDataOnlyMod`). Contributions from several mods combine in priority order,
just like modifiers. Change values in `OnLoad`: the game reads most of them when it computes stats,
so mid-battle changes may not reach things already built.

### Content

```csharp
Content.HideShellPart("MyMod", "GravitonRam");
Content.HideItem("MyMod", "Laser Combiner");            // name or guid
Content.ReplaceItem("MyMod", "Old Block", "New Block");  // every existing copy, everywhere
Content.HideModFolder("MyMod", "Core_Laser");            // every block and prefab from one folder
Content.PreventPaste("MyMod", "Laser Combiner");        // left out of prefab pastes (v0.27)
Content.PreventPasteFromModFolder("MyMod", "Core_Laser");
```

Hiding a block only takes it out of the build menu: a player can still capture a prefab from a craft that has
it and paste it. `PreventPaste` closes that gap. When a prefab is pasted with the build tool, the blocks it
names (and their mirror variants) are left out and the player is told which were left out. The rest of the
prefab is placed as normal, with its settings. The player's prefab is not changed, and spawning blueprints,
loading saves, retrofitting and undoing an erase are not affected, so existing designs never lose blocks.
In multiplayer the filtered prefab is what gets sent, so host and clients place the same blocks. Placing a
blueprint as a subobject and spawning a whole blueprint still include the block.

`Mods/FtdModApi/FtdModApi_content.txt` lists the rules and what they matched, every shell part and prefab,
and block counts per mod folder (`Core_APS`, `Core_Laser`, ...). `FtdModApi_blocks.txt` lists every
block (guid, name, class, tab) grouped by folder. The same rules work from a
`FtdModApi.content.json` in any mod folder (see `Examples/ExampleDataOnlyMod`).

`ContentIds.Register("ApsShellPart", "MyMod.Cordite")` gives a numeric id that is identical on every
machine and launch, for content the game saves as numbers. The shell-part registry will use it.

### New APS shell parts

```csharp
ShellParts.Register("MyMod", new ShellPartDefinition
{
    Id = "MyMod.DenseSolidBody",      // permanent: becomes the number saved in blueprints
    Name = "Dense solid body",
    Description = "Heavier, slower, more kinetic damage.",
    BehavesAs = "SolidWarhead",       // any game part name from FtdModApi_content.txt
    KineticDamage = 1.3f,             // unset stats are copied from the BehavesAs part
    Speed = 0.9f,
    AmmoCost = 1.5f
});
```

A modded part copies its `BehavesAs` part: placement, category, model, settings, and every place the
game asks "is this a casing / an HE body / a timed fuse?". On top of that it has its own name and stats
(kinetic, AP, speed, health, inaccuracy, ammo cost, length, detectability). For a new kind of payload,
give the part a `Payload` (below).

If a mod that added parts is removed, shells that used them load without those parts.

### Custom APS payloads (v0.28)

```csharp
public sealed class ClusterPayload : ApsPayload
{
    public override void Detonate(ApsPayloadHit hit)
    {
        float damage = ApsPayloads.HeDamage(hit.Strength / 4);       // game-equivalent HE damage
        float radius = ApsPayloads.ExplosionRadius(damage);
        for (int i = 0; i < 4; i++)
            hit.ExplosionAt(hit.Position + hit.Direction * (i * 1.5f), damage, radius);
    }

    public override IEnumerable<ApsPayloadStat> Describe(ApsPayloadShell shell)   // ammo customiser stats tab
    {
        yield return new ApsPayloadStat("Cluster blasts", $"4 x {ApsPayloads.HeDamage(shell.Strength / 4):0} damage");
    }
}

ShellParts.Register("MyMod", new ShellPartDefinition
{
    Id = "MyMod.ClusterCharge", Name = "Cluster charge",
    BehavesAs = "HeWarhead",              // placement, model and settings of an HE body
    Payload = new ClusterPayload()        // KeepGamePayload = false (default): no HE of its own
});
```

`Detonate` runs once per shell when the game would set off its own HE, frag or EMP (fuses, impact, ground hit,
shot down), on the host, on a game worker thread. A shell whose only payload is modded stops there, as an HE
shell does. `hit.Strength` uses the game's rules (gauge, number of these parts, sabot heads and `ApsApi.ShellStats`
payload multipliers), so `ApsPayloads.HeDamage/FlakDamage/EmpDamage/FragmentDamage(strength)` give what the
same number of game bodies would do. Helpers on the hit: `Explosion`, `ExplosionAt`, `HeExplosion`, `Emp`, `Fire`,
`Fragments` (all scaled by how intact the shell is, `DamageFactor`), plus `Craft`, `Block`, `Position`, `Direction`,
`Reason` and `OnMainThread` for anything else (visuals, spawning). Explosions go through the API's explosion
modifiers like any other. With `KeepGamePayload = true` the part also keeps its `BehavesAs` part's game payload.
In multiplayer only the host runs payloads; clients see what the game syncs (damage, explosions).

### New CRAM pellets

A CRAM pellet is an ordinary block, so the block itself is made with the game's own content files: an
`.itemduplicateandmodify` file in your mod's `ItemDup` folder that copies a game pellet (for example
"High Explosive Pellets") under a new guid, name and look. It keeps the game's pellet class, so it
connects, packs and shows in tooltips like the pellet it copies. Then tell the API how it differs:

```csharp
CramPellets.Register("MyMod", new CramPelletDefinition
{
    Id = "MyMod.DenseHePellets",
    Block = "07e074c0-24cb-4e2e-b65b-8e3c2cc8c60c",   // the duplicated block's guid (or name)
    Payload = 1.3f,                                   // +30% HE power per pellet
    Health = 0.8f                                     // -20% of its share of shell health
});
```

Multipliers: `Payload` (HE / frag / EMP / fire strength), `KineticDamage`, `ArmourPiercing` (hardener-based
pellets) and `Health`. Each is per pellet, relative to the pellet it copies. Data-only mods can do the
same with `"cramPellets": [{ "id": ..., "block": ..., "payload": 1.3, "health": 0.8 }]` in
`FtdModApi.content.json`. Blueprints save the block guid, so pellets survive saving like any block.

For changes to every CRAM shell (or effects that depend on which modded pellets are connected), use
`CramApi.ShellStats`; `ctx.ModdedPelletCount("MyMod.DenseHePellets")` tells you how many are on the gun.

### New missile parts

The game builds its missile part list from every `MissileComponent` class it finds, including in plugin DLLs,
so missile parts are classes. The API adds stable numbers, data-only parts and the "behaves as" redirection.

Data part (a copy of a game part with new numbers):

```csharp
MissileParts.Register("MyMod", new MissilePartDefinition
{
    Id = "MyMod.HeavyHeWarhead",          // permanent: becomes the number saved in missile designs
    Name = "Heavy HE warhead",            // must be unique in its category
    BehavesAs = "ExplosiveWarhead",       // class name (with or without "Missile") or number; see FtdModApi_content.txt
    PayloadVolume = 1.25f,                // counts as 1.25 warheads of HE
    HealthModifier = 0.07f                // unset values come from the BehavesAs part
});
```

Code part (your own class, full control):

```csharp
public class MyLightBody : MissileBody
{
    public new const enumMissileComponentType ComponentType = enumMissileComponentType.None; // the game's scan skips it
    public MyLightBody(MissileSize size, UIParameterBag bag) : base(size, bag) { }
    public override string Name => "Light reinforced body";
    protected override float MassModifier => 0.6f;
}

MissileParts.Register<MyLightBody>("MyMod", "MyMod.LightBody");
```

A code part behaves as the closest game class it derives from. Parts are added when the main menu first opens.

### APS shell stats and loader timing

```csharp
// Every APS shell design: big shells reload more slowly, HE-heavy shells hit a bit harder.
ApsApi.ShellStats.Add("MyMod", ctx =>
{
    if (ctx.Diameter >= 0.3f) ctx.ReloadTime *= 1.15f;
    if (ctx.CountOf("HeWarhead") >= 3) ctx.PayloadMultiplier *= 1.1f;
});

// One loader block (guid or name, duplicated blocks included): 25% faster loading.
ApsApi.SetLoaderTimeFactor("MyMod", "Autoloader (8m shells)", 0.75f);
```

Data-only: `"apsLoaders": { "Autoloader (8m shells)": 0.75 }` in `FtdModApi.content.json`. Shell stats are worked
out when the game builds a shell design, so the ammo customiser shows the changed numbers.

### Your own systems and values

A mod that adds its own weapon or mechanic publishes its numbers as a system. They then work exactly like the
game's tunables: other mods change them with `Tunables.Set/Add/Multiply`, players with `FtdModApi.tunables.json`,
and `FtdModApi_tunables.txt` lists them with owner, default, current value and limits.

```csharp
var dozers = Tunables.RegisterSystem("BulldozerMod", "Bulldozers", "Bulldozer blades and drive.");
var baseDamage = dozers.Float("BaseDamage", 500f, min: 0f, description: "Damage per blade hit.", units: "damage");
float damage = baseDamage.Value;   // cheap and thread-safe; always the current value

// Optional: hooks other mods can modify, with the same priority rules as the API's own.
public static readonly ModifierPipeline<DozerHit> Hit = dozers.CreatePipeline<DozerHit>("Hit");
Hit.Run(context);
```

Another mod or a JSON file can change `Bulldozers.BaseDamage` before the bulldozer mod has loaded, or when it is
not installed: the change waits and applies when the system registers (the report lists waiting changes).

### APS gauge cap

```csharp
ApsGauge.RequestMaxGauge("MyMod", 1000f);   // or "apsMaxGaugeMm": 1000 in FtdModApi.content.json
```

Every gauge limit follows the cap: the firing piece's gauge slider, saved designs, gauge increaser counting,
the "can't fire" check and tooltip, and the ammo customiser's stats slider. Designs above 500 mm load clamped
to 500 mm where no mod raises the cap. Players can set the cap directly in the settings menu
(`FtdModApi.ApsMaxGaugeMm`), which overrides every mod request.

### In-game settings menu

Press **Ctrl+Shift+M** anywhere (or main menu > Settings > Mod settings). Every tunable (the game's, the unlocked
read-only ones, and every mod system's) is listed with its **Default**, **Current** value and a **Player Value**
box. A Player Value overrides every mod and the game; clearing it hands control back. Player values are saved to
`Documents/From The Depths/FtdModApi.player.json` when the window closes, outside the Mods folder so Workshop
updates never touch them. The tooltip on each value starts with a plain-language description of what it does,
then lists which mods change it. The search box also matches descriptions.

The API's own settings are in **Mod values > FtdModApi**: `VerboseLogging` and `ApsMaxGaugeMm` (the APS gauge
cap; the highest mod request is applied there, and a Player Value overrides it).

#### Value descriptions

FtdModApi ships a description for every game value (embedded in the DLL). Descriptions are optional: a value
nobody described just shows no description line. Where they come from, strongest first:

1. the owning mod's own `description:` argument (`dozers.Float("BaseDamage", 500f, description: "...")`);
2. `Tunables.Describe("MyMod", "AdvCannon.BaseReloadTime", "...")` or a `FtdModApi.descriptions.json` file in
   your mod folder (`{ "Group.Name": "text" }`); the first mod to describe a key keeps it;
3. the built-in descriptions.

Write them for players: what changes in game when the number goes up or down, and the unit. Technical notes
(such as "read-only in the game; N reads redirected") are shown on a separate line.

### Events

Spigot-style events: subscribe to `Events.<Area>.<Event>` instead of writing Harmony patches.

```csharp
// Cancellable "...ing" events run before the game acts.
Events.Blocks.Placing.Subscribe("MyMod", e =>
{
    if (e.ItemName == "Rubber Block") e.Cancel();
});

// "...ed" events report what happened.
Events.Constructs.Spawned.Subscribe("MyMod", e => log.Info($"{e.Name} spawned"));
Events.Blocks.Destroyed.Subscribe("MyMod", e => kills++, EventPriority.Monitor);
Events.Ticks.EverySecond.Subscribe("MyMod", e => UpdateHud());
```

- **Pre and post:** "...ing" events (`Placing`, `Scrapping`, `Defeating`, ...) are raised before the game acts and can be
  cancelled with `e.Cancel()`; no matching "...ed" event follows. "...ed" events are raised after, or just before an
  object goes away while it can still be read (`Despawned`, `Removed`).
- **Priorities:** `EventPriority.Lowest` (-200), `Low`, `Normal` (0), `High`, `Highest` (200), `Monitor` (1000). Lower
  runs first, so `Highest` has the final say. `Monitor` handlers only watch: a cancel there is ignored.
- **Cancelled events:** by default a handler is skipped for an event an earlier handler cancelled. Pass
  `ignoreCancelled: false` to see them (the opposite of Spigot's default).
- **Side:** every event says where it is raised (`channel.Side`): **Host** (host or single player), **All** (every machine;
  clients spawn their own copies of craft and blocks), or **Local** (the local player's machine). Cancellable events
  are host-only, so a client can't veto the host.
- **Threads:** events run on the main thread (`channel.Thread == EventThread.Main`), except `Weapons.Firing` and
  `Projectiles.ShieldHit`, which must be answered immediately and run on whatever thread the game is on, usually an AI worker thread (`EventThread.Any`): keep
  those handlers fast, thread-safe, and away from Unity objects. Events whose game code runs on a worker thread but that
  can't be cancelled (`Weapons.Fired`) are queued to the main thread and arrive at most a frame later.
- **Cost:** the patch behind an event is applied the first time any mod subscribes, so unused events cost nothing and
  can't break on a game update. `FtdModApi_status.txt` lists every event, its side, its handler count and whether it
  is active.
- **Game objects:** events expose the game's objects (`MainConstruct`, `Block`, `Force`, ...) plus stable shortcuts
  (`Name`, `ItemName`, `Team`, ...). Prefer the shortcuts; the game's own members can change between versions.
- Don't keep event objects after your handler returns (tick events reuse one object).
- `Constructs.Despawned` gives a `Reason`: `Wiped` (destroyed, scrapped), `OutOfPlay` (back to the strategic map),
  `Deleted` (the force was deleted, e.g. "destroy all vehicles") or `DeadBlueprint` (campaign craft kept as a dead
  blueprint). `Pulled` is only raised for real out-of-play moves.
- `BuildMode.Entered` is raised once the game has picked the craft to build on (a moment after the key press), and
  not at all if there was nothing to build on.
- Handlers are fault-isolated like modifiers: one that throws 5 times is switched off.

| Area | Events |
|---|---|
| `Events.Constructs` | `Spawned`, `Despawned`, `Playing`/`Played`, `Pulling`/`Pulled`, `Scrapping`/`Scrapped`, `Defeating`, `TeamChanging`/`TeamChanged`, `SubconstructAdded`, `SubconstructRemoved` |
| `Events.Blocks` | `Placing`/`Placed`, `Removing`/`Removed`, `Destroyed`, `Repaired` |
| `Events.Weapons` | `Firing` (cancellable, any thread), `Fired` |
| `Events.Aps` | `ShellFired` |
| `Events.Cram` | `ShellFired`, `Packed` |
| `Events.Missiles` | `Launched`, `Reloaded`, `Detonated` |
| `Events.Projectiles` | `Hit`, `Detonated`, `Damaged`, `ShieldHit` (any thread, can change the reflection) |
| `Events.Session` | `ModeChanging`, `ModeChanged`, `Saving`, `Saved`, `Loaded`, `Paused`, `Unpaused`, `TimeScaleChanged`, `GameFinished`, `Quitting` |
| `Events.Players` | `Added`, `Removed`, `AvatarSpawned`, `AvatarDied`, `SeatEntering` (cancellable), `SeatEntered`, `SeatLeft`, `PrefabPlacing` (cancellable), `PrefabPlaced`, `ChatSending` (cancellable, editable), `ChatReceived` |
| `Events.Resources` | `Gaining` (cancellable, editable, any thread), `Gained`, `Dumping` (cancellable, editable, any thread), `Dumped`, `Harvested` (per craft per second), `Transferred` |
| `Events.Campaign` | `FleetCreated`, `FleetRemoved`, `FleetOrdered`, `FleetWaypointReached`, `TerritoryChanging` (cancellable, new owner settable), `TerritoryChanged`, `FactionEliminated`, `RelationsChanged`, `BattlePreparing`, `BattleStarted`, `BattleEnded`, `BattleCancelled`, `BlockadeStarted`, `BlockadeEnded`, `OffscreenBattleStarted`, `OffscreenBattleEnded`, `OffscreenForceKilled`, `DesignBuilding` (cancellable), `DesignBuilt` |
| `Events.Ai` | `TargetSpotted`, `TargetLost`, `PrimaryTargetChanged`, `MovementChanged`, `FiringChanged`, `RoutineChanged` |
| `Events.Detection` | `ContactMade`, `IncomingProjectile` |
| `Events.Lua` | `ScriptError`, `Logged` |
| `Events.Input` | `KeyPressed`, `KeyReleased` (mod keybinds) |
| `Events.Ui` | `WindowOpening` (cancellable), `WindowOpened`, `WindowClosed`, `BlockInteracting` (cancellable), `BlockInteracted` |
| `Events.Ticks` | `Frame`, `PhysicsStep`, `EveryQuarterSecond`, `EveryHalfSecond`, `EverySecond`, `EveryTwoSeconds`, `EveryFourSeconds`, `EveryFiveSeconds`, `EveryMinute` |
| `Events.BuildMode` | `Entered`, `Exited` |

More areas (breadboard components, networking) follow the catalog in the design notes.

#### Mod save data

Each mod can keep one string (JSON, CSV, anything) per campaign or adventure save:

```csharp
Events.Session.Saving.Subscribe("MyMod", e => e.Write("MyMod", JsonConvert.SerializeObject(state)));
Events.Session.Loaded.Subscribe("MyMod", e => state = Parse(e.Read("MyMod")));
```

It is written to `FtdModApi.moddata.json` in the save's folder when the game saves, read back before that save's world
loads (so `ModSaveData.Get` works while its craft spawn), and cleared when the game changes mode. The game's own
`GamePlugin.OnSave` is never called by FtD, so this is the way to store per-save data.

### Breadboard components

Mods can add their own components to players' breadboards (vehicle, AI and missile). Derive from
`ModBreadboardComponent`, declare pins and settings in the constructor, compute in `Compute`, and register in `OnLoad`:

```csharp
public class Scale : ModBreadboardComponent
{
    private readonly NumberSetting _factor;

    public Scale()
    {
        AddInput("Value");
        AddOutput("Scaled");
        _factor = AddNumberSetting("factor", "Factor", 2, -10, 10, 0.1);
    }

    protected override string Label => $"x{_factor.Value:0.##}";   // text on the component

    protected override void Compute() => Out(0, InNumber(0) * _factor.Value);
}

BreadboardComponents.Register<Scale>("MyMod", "scale", "Scale", "Multiplies the input by a factor.",
    BreadboardKinds.All);   // default: VehicleAndAi
```

- **Inputs:** `InNumber`, `InBool` (above 0), `InVector`, `InRotation`, `InText`, `IsConnected`. **Outputs:** `Out(i, double/bool/Vector3/Quaternion/string)`, `OutInteger`.
- **Settings:** `AddNumberSetting` (slider), `AddToggleSetting`, `AddTextSetting`. They appear in the component's editor, are saved with the craft, copied in prefabs and synced in multiplayer. Keep every player choice in settings; don't add your own game `Var` properties.
- **Context:** `Construct` (the craft; for a missile breadboard, the launching craft), `BreadboardBlock`, `Missile` (missile breadboards), `Kind`, `Board`.
- **Memory:** `Remember(slot, value)` / `Recall(slot)` (slots 0-4). A missile breadboard runs one component once per missile, and this memory is kept per missile, so use it rather than fields for values that change while running.
- **Threads:** `Compute` runs on the game's worker threads like the game's own components. Reading the craft is fine; don't create or destroy objects or touch the UI. An exception is logged once and the component keeps running.
- **Ids:** saved crafts refer to the component by mod id + component id. Never change them; the name, description, settings and pins can change. A newer version can add pins at the end (older crafts get them), and setting keys must stay the same to keep players' values.
- **Missing mods:** if a craft is loaded without the mod, each of its components shows as "Missing: <name>" and outputs nothing, but keeps its position, settings and links, so saving the craft loses nothing and the component works again once the mod is back.
- Register in `OnLoad`: the AI breadboard copies the list of components when the block is placed.

### Craft data and craft modules

Mods can keep their own data on each craft (any MainConstruct: ship, plane, fortress, structure). It is saved with the
craft, in its blueprint file, in campaign saves, and when a craft goes out of play and comes back.

```csharp
// Simple values:
CraftData.Set(craft, "MyMod", "kills", "3");
var kills = CraftData.Get(craft, "MyMod", "kills");          // null when not set
CraftData.SetJson(craft, "MyMod", "stats", new Stats { Kills = 3 });
var stats = CraftData.GetJson(craft, "MyMod", "stats", new Stats());

// Or an object attached to every craft:
public class Veterancy : CraftModule
{
    private int _kills;
    protected override void OnSpawned(string saved) => int.TryParse(saved, out _kills);
    protected override string OnSave() => _kills.ToString();
    protected override void OnUpdate() { /* every frame, main thread (only if overridden) */ }
    protected override void OnRemoved() { }
    public void AddKill() => _kills++;
}
CraftModules.Register<Veterancy>("MyMod", "veterancy", c => !(c is Fortress));   // the filter is optional
CraftModules.Get<Veterancy>(craft)?.AddKill();
```

- **Saved:** with the craft whenever the game saves it: blueprint files, campaign saves, craft pulled out of play. Prefabs (block selections) don't carry it.
- **Belongs to the saved design:** every craft spawned from a saved blueprint file starts with the values saved in it (copies, and other players who download it, too). Retrofitting to another design takes that design's values. Each craft then keeps its own values from there (campaign saves, out of play and back).
- **Timing:** `OnSpawned` runs once the spawn is complete (blocks, force, name, position). `OnRemoved` runs while the craft is still intact; it is not called when the game quits.
- **Kept without the mod:** a craft saved while your mod is missing keeps your values, and your module's data comes back when the mod is installed again. Keep the mod id, keys and module ids the same between versions.
- **Multiplayer:** every machine loads the data the craft spawned with, and each machine has its own module. Later changes are not synced; send them with ModNet when clients need them.
- **Threads:** `CraftData` works from any thread. `OnSave` can run off the main thread, so only read your own fields there. `OnSpawned`, `OnUpdate` and `OnRemoved` run on the main thread.
- **Errors:** a module that throws is logged once. A failing `OnSave` keeps the previous save, and `OnUpdate` stops for that craft after 5 failures.

### ModNet (multiplayer messages)

Mods can send their own messages between the host and clients. Get a channel, handle what arrives, and send:

```csharp
var sync = ModNet.Channel("MyMod", "sync");          // same names on every machine
sync.OnReceive(m =>
{
    var value = m.Reader.ReadFloat();                // read in the order it was written
    if (m.FromHost) Apply(value);                    // on a client: the host's answer
    else sync.SendToClientsExcept(m.Sender, new ModNetWriter().Write(value)); // on the host: pass it on
});
sync.SendToHost(new ModNetWriter().Write(1.5f));     // from any machine
```

- **Who sends what:** anyone can `SendToHost`; only the host can `SendToClients`, `SendTo(client)` and `SendToClientsExcept(client)`. `m.Sender` is the client (on the host) or the host (on a client), and is what `SendTo` takes.
- **Only machines with the API:** when a client joins, the API on both sides says which channels it has. Messages only go to machines that have the channel, so a player without your mod gets nothing. `ModNet.Peers` lists the other machines (version, channels, `HasMod`); `ModNet.PeerJoined` / `PeerLeft` say when they come and go.
- **Single player and hosting:** this machine is the host, so `SendToHost` arrives here (next frame), going through the game's own message code. Your mod works the same without a second player.
- **Testing alone:** turn on `ModNetLoopback` (FtdModApi settings, or `"FtdModApi.ModNetLoopback": true` in a tunables file). Messages the host sends to clients are then also delivered here, with `m.FromHost` and `m.IsLoopback` true, so both directions of your code run on one machine. The example mod's Ctrl+L sends a ping and shows the pong.
- **Limits:** up to 256 KB per message, delivered reliably and in order. Handlers run on the main thread; sending works from any thread (it is queued to the main thread).
- **Values:** `ModNetWriter` / `ModNetReader` handle bool, byte, int, uint, long, ulong, float, double, string, byte[], Guid, Vector3, Vector3d, Quaternion and Color. Reading past the end throws `ModNetFormatException`, logged once per handler.
- **Not covered yet:** testing with a real second player. Everything up to the Steam connection itself runs in single player; the handshake and peer list are only exercised in a real multiplayer game. With verbose logging on, the API logs each connection and handshake, so testers' logs show what happened.

### Checking a new game version (offline patch checker)

After a From The Depths update, run the patch checker before launching the game:

```
.\RunPatchChecker.ps1        (PowerShell, in the src folder)
RunPatchChecker.cmd           (Command Prompt)
```

Add `--managed "<game>\From_The_Depths_Data\Managed"` for another install, `--all` to list OK patches too, `--verbose` to
see what the API logged while checking, `--no-transpilers` to skip running transpilers on the game's code.

It loads the game's DLLs without running the game and goes through every patch the API can apply: the startup ones,
the main-menu ones, every event hook (normally patched only when a mod first uses the event), and the Lua and breadboard
ones. For each it checks that:

- the game method still exists (same class, name and overload);
- every parameter our hook relies on still has the same name and type (`__instance`, `__result`, `__state`, `___field`
  and named parameters, by Harmony's rules);
- each transpiler still finds the code it rewrites, and the patch's own check passes.

It prints a summary and the problems, writes the full report to `FtdModApi_patchcheck.txt`, and exits with 0 when every
required patch fits (1 when a feature is broken, 2 when it couldn't run). It builds with the .NET SDK you already use and
is not part of the normal build. It confirms the patches will attach; it can't tell whether the game still behaves
the way a patch expects, so test in game too. Patches it can't inspect outside the game are listed as UNKNOWN
(for example, the Lua ones may be: the game's Lua library has a version quirk that only the game's own runtime accepts).
It runs on .NET 8 or newer.

### Testing the API in game (FtdModApiTest)

The test mod checks every feature it can by itself and watches the rest while you play. It changes no gameplay:
its hooks only watch, its test values are its own and put back, and its test parts behave exactly like the game
parts they copy. Build it into the game with:

```
dotnet build -c Release -p:DeployTestMod=true
```

It writes `Mods\FtdModApiTest\FtdModApiTest_report.txt` at the main menu, every 30 s while something changes, when the
game quits, and when you press **Ctrl+Shift+T** (which also shows the summary on screen). Each line is

- `PASS`: worked (with a count of how often, for things that happen in game);
- `FAIL`: didn't work: please report it with the report file and the game log;
- `....`: not seen yet, with what to do in game to see it.

What it covers:

- **API features**: every feature is Available (Pending ones start when a game is loaded).
- **Self-tests** (at the main menu): modifier priority order and removal, a failing modifier being switched off
  while the others keep running (the two errors about "expected-failure test" in the log are this test), API events,
  tunables combining Add/Multiply/Set and limits, game values, content ids, every ModNet value type and damaged
  messages, key combos, block lookups, the APS payload formulas.
- **Every API event**: found automatically and watched at Monitor priority, so each event's game patch is applied
  and checked; each passes the first time it happens.
- **Every modifier hook** (damage, explosions, launches, APS and CRAM stats, loaders, armour, block health), with
  sanity checks on the values.
- **Craft modules and CraftData** (a probe value is set and removed at once, so player designs only gain the test
  module's few bytes), **per-save data**, **ModNet** (a message to the host; in multiplayer the host answers),
  a **Lua function** (`Mods.FtdModApiTest.Ping("hi")`), a **breadboard component** ("[API test] Echo") and an
  **APS shell part with a payload** ("[API test] HE body", identical to the HE body).

Remove the test mod for normal play: watching every event costs a little performance. Shells or breadboards that
used its test parts keep working without it (the parts are dropped).

### Logging and verbose mode

`FtdApi.GetLog("MyMod")` gives a logger that prefixes every line with your mod id. Besides Info/Warning/Error
it has `Verbose(...)`: extra detail written only when verbose logging is on. One switch covers the API and every
mod using it:

```json
{ "FtdModApi.VerboseLogging": true }      // in any enabled mod's FtdModApi.tunables.json
```

With it on, the API logs every patch target, modifier registration, first run of each hook, tunable change
(including waiting ones), content rule outcome, modded part and pellet resolution, gauge cap changes, and the
first loader-timing and block-health change per block type. Use `log.Verbose(() => ...)` on hot paths so the
message is only built when needed, and `VerboseOnce(key, ...)` for things that happen thousands of times.

### Threading and multiplayer

- `Block` modifiers and `BlockDamaged` handlers can run on the game's worker threads. Don't touch Unity
  objects there, and don't keep the context object (it is reused to avoid garbage).
- Block damage and explosions are decided by the server. Projectile launch modifiers run on every machine.
  In multiplayer, every player needs the same mods and settings.

## How the patches protect themselves

Each patch is a `PatchSpec`: a target resolver, hook methods, an optional `Verify` check, and the
feature it belongs to. `PatchManager` applies them one at a time:

1. Target missing (type or method renamed): the patch fails, nothing is applied.
2. Harmony throws (for example, the signature changed): the patch is rolled back.
3. `Verify` fails (for example, the transpiler could not find the call it injects after): the patch is rolled back.

Failures mark only the owning feature `Unavailable` (required patch) or `Degraded` (optional patch).
Mods check `Features.IsAvailable(id)` and can tell the player.

## Roadmap

The API is shaped around three planned mods: content packs (new APS shell parts, missile parts and
CRAM shell types). The last step is a test mod that exercises the API and reports what works.

1. **Balance hooks**: armour class and tunables are done (v0.2). Still to do:
   - block health (v0.11: `BlockApi.MaxHealth`, plus per-block health and armour multipliers);
   - **mod-registered systems and tunables** (done, v0.9): a mod that adds its own weapon or mechanic (its own "system",
     the way APS, CRAM and lasers are separate systems in the game) registers it and its constants, e.g. a
     Bulldozers mod publishing `Bulldozers.BaseDamage` and `Bulldozers.DesignSpeedModifier`. Other mods,
     JSON files and players can then change them like the game's own tunables, and they show in the
     reports and the "Modded values" screen;
   - **read-only constants** (v0.10): Explosion, Detection, SharedWeapon and FuelRefinery (57 values) are tunable;
     Game/Fuel (resource conversions, read in ~50 classes) can follow the same way if needed.
2. **Content registry core** (v0.3: stable ids, hide/replace blocks, prefabs and shell parts). Still to do:
   stopping hidden blocks being pasted in from prefabs (done, v0.27: `Content.PreventPaste`). Missile parts and CRAM
   pellets are ordinary blocks, so HideItem/ReplaceItem already cover disabling them.
3. **Content registries**: APS shell parts (v0.4: new parts that copy a game part's behaviour), CRAM pellets
   and the CRAM shell stats hook (v0.5), missile parts (v0.6: data copies and code parts).
4. **APS hooks** (v0.7): `ApsApi.ShellStats` (reload, cooldown, speed, kinetic, AP, payload, health per shell design) and `ApsApi.LoaderTiming` (per loader block).
5. **APS gauge cap** (v0.8, `ApsGauge.RequestMaxGauge`): one setting, applied
   everywhere the game hard-codes 500 mm (about 8 places, see the design notes), each place as its
   own verified patch. Blueprints that use more than 500 mm are clamped back to 500 mm when loaded
   without the mod.
6. **In-game settings menu** (v0.13): every game and mod value with Default / Current / Player Value columns. A Player Value
   overrides everything and is saved to `Documents/From The Depths/FtdModApi.player.json`. v0.14: plain-language
   descriptions for all 454 game values in the tooltips, and the APS gauge cap as a menu value.
6b. **Event system** (v0.15): framework (pre/post events, priorities, sides, lazy patching) plus craft, block, tick and
   build-mode events; v0.16-v0.19 add weapons, projectiles, session and player (with mod save data), resources and campaign; v0.20 AI and detection; v0.21 mod keybinds and Lua (events and mod functions); v0.22 UI windows; v0.23 mod breadboard components; v0.24 ModNet (mod multiplayer messages); v0.25 craft data and craft modules.
6c. **Before release (to do):**
   - **Offline patch checker** (v0.26, `RunPatchChecker.cmd`): see "Checking a new game version".
   - **API test mod** (done, `TestMod/FtdModApiTest`): see "Testing the API in game".
7. **Steam Workshop release**: publish only the API. Example mods stay out of the upload (and never
   deploy by default), so installing the API alone never changes gameplay.

### After the first release

- **Custom APS payloads**: done in v0.28 (`ShellPartDefinition.Payload`).
