> Copy of the claude.ai project doc `claude/ftd-modapi-design.md` (2026-11-05). Its Milestones list stops at v0.15; for v0.16 and later see the status log at the end of `catalog.md`.

# FtdModApi: design notes and game research (FtD 4.3.4)

Source: this repository (see README.md). v0.1–v0.6 are confirmed working in game (2026-10-10). All patches applied (including 28 + 4 shell-part behaviour methods), all 16 features are Available, there are 397 tunables, and the Core_* folders hold 1,950 blocks. Verified examples: the "Dense solid body" shell part (selectable, survives save/reload), Dense HE CRAM pellets, and the Heavy HE warhead and Light reinforced body missile parts. Test setup: only FtdModApi is in Mods; the other downloaded mods were moved to `Documents\From The Depths\Mods_Example` for reference.

## Target mods and requests (these drive the API's priorities)
1. **Content packs**: new APS shell parts (done in v0.4), new missile parts and new CRAM shell types. Mods add them through the API and do not patch the game's hard-coded lists.
2. **APS shell stats hook** (kept from the dropped loader system): `ApsApi.ShellStats`, the APS counterpart of `CramApi.ShellStats`. Mods change a shell's computed reload time, cooldown, muzzle velocity and damage values, and can see its parts and gauge. A postfix on the `ShellModel_*` calculations. Optional extra: a per-loader loading-time hook (`ShellRackModel.LoadingTimeFactor`) so mods can make specific loader types faster or slower. The manual reload mod and a full loader abstraction are out of scope.
3. **Raise the APS 500 mm gauge cap**: a very common player request.
4. **Mod-registered systems and tunables**: a mod that adds its own weapon or mechanic (its own "system", the way APS, CRAM, lasers and PACs are separate systems in the game) registers that system and its constants with the API. Example: a Bulldozers mod registers a "Bulldozers" system with Base Damage, Design Speed Modifier and so on. Those values then behave exactly like the game's own tunables: other mods change them with Set/Add/Multiply in priority order, players change them in the in-game settings menu (or JSON), and they appear in the tunables report with owner, default and current value. See "Mod-registered systems" below.
5. **Read-only constants**: unlock Detection, Explosion and SharedWeapon first (agreed priority).
6. **In-game settings menu** (replaces the read-only "Modded values" screen): players see and change every game and mod value from inside the game. See "Player settings menu" below.
7. **Steam Workshop release**: ship only the API. Example mods are never enabled or deployed by default, so the API alone never changes gameplay.
8. **After release**: custom APS shell payloads (new payload types, not only stat variants of existing parts).
9. **API test mod (last)**: a dedicated mod that exercises as much of the API as possible (damage pipelines, tunables, content hiding/replacing, shell parts, CRAM pellets, missile parts, APS/CRAM shell stats, gauge cap, mod-registered systems, player values) and reports pass/fail, so every release and game update can be verified quickly.

## Architecture
- **FtdModApi** is a normal FtD plugin (`plugin.json` filename `0Harmony.dll,FtdModApi.dll`, no depends). Consumer mods add `"depends": ["FtdModApi"]`, which makes PluginLoader load the API first.
- **Harmony**: it ships Harmony 2.2.2 (net45; the same binary LargeAPS uses on 4.3.x), so consumer mods don't bundle Harmony. An AppDomain.AssemblyResolve handler is installed in the plugin constructor, before any Harmony type is touched. It points `FtdModApi`/`0Harmony` references at the API's copies.
- **PatchSpec/PatchManager**: every patch is declared as data (target resolver or a multi-target `Targets` list, hooks, optional Verify) and applied in isolation. If a patch fails, all its targets are rolled back. Only its feature is affected: Unavailable for a required patch, Degraded for an optional one.
- **Features**: `Features.IsAvailable("Damage.Block")` etc. Status is written each launch to `Mods/FtdModApi/FtdModApi_status.txt`, along with other mods' Harmony owners on the same methods. A one-line status also appears in the mod list via ModProblems (non-error).
- **ModifierPipeline / ApiEvent**: priority-ordered (Add 100, Multiply 200, Override 300, Clamp 400, Observe 1000). Ties are broken by mod id, so mod load order never changes the result. Reads are lock-free copy-on-write, because damage runs on worker threads. A modifier that throws 5 times is switched off.
- **Tunables** (v0.2): 397 public static fields on the game's `*Constants` classes, which the game never writes.
  - Discovered by reflection with defaults captured. Set/Add/Multiply contributions are applied in priority order.
  - Also loaded from `FtdModApi.tunables.json` in any enabled mod folder, and listed in `FtdModApi_tunables.txt`.
- **Read-only constants**: 78 more values are expression-bodied getters (`=> 3f`). Mono can inline these, so patching the getter is unreliable; the plan is to transpile the calling methods instead.
  - The full list with callers is in `Documentation/ReadOnlyConstants.md`.
  - Explosion values pass through a second layer of getters in `CustomExplosion`, read by CustomExplosion and ExplosionDamageDescription.
- **Content** (v0.3): `Content.HideItem/HidePrefab/HideModFolder/ReplaceItem/HideShellPart` and `FtdModApi.content.json`.
  - `ContentIds` is an FNV-1a hash of "category\0id" into [1,000,000, int.Max), with collision detection. It needs no storage and is identical on every machine.
  - Reports: `FtdModApi_content.txt` (rules, shell parts, prefabs, per-folder block counts) and `FtdModApi_blocks.txt` (all blocks by folder).
- **APS shell-part registry** (v0.4): `ShellParts.Register(modId, ShellPartDefinition)` takes these fields:
  - Id, Name and Description.
  - BehavesAs: the game part whose behaviour it copies.
  - LooksLike: the part whose visuals it uses.
  - Optional stat overrides: KineticDamage, ArmourPiercing, Speed, Health, Inaccuracy, AmmoCost, MaximumLength, Detectability, Placement, Category and SortingIndex.

  How it works:
  - The saved number is `ContentIds` of the Id (e.g. "ExampleBalanceMod.DenseSolidBody" is #342145250). `ShellModuleType` is saved as Int32, so unknown values round-trip.
  - A `GenerateDictionary` postfix adds the modules. It reruns on language change.
  - BehavesAs is a transpiler. It rewrites every `ldfld ShellModule::Type` and `call/callvirt ShellModuleParams::get_Type` to `ShellParts.EffectiveModuleType` / `EffectiveParamsType`.
    - Required spec: the ShellModel_* stat classes and ShellModelParameters (28 methods; Verify rewrites > 0).
    - Optional spec: `AdvPooledProjectile.ActivateHere`, `AdvCannonFiringPiece.MainThreadFireCallback`, `ShellConstants.EjectShellList` and `PickerTab.ShowCustomisationOfParts` (4 methods; Verify rewrites ≥ 4).
  - Target methods are found by a raw IL scan of method bodies for opcodes 0x7B/0x28/0x6F followed by the field/getter metadata token.
  - Don't use `PatchProcessor.GetOriginalInstructions(m, (ILGenerator)null)`. Its ILGenerator lives in mscorlib, which netstandard2.1 projects can't reference (CS7069). The same applies to `CodeInstruction.labels`/`blocks` (List<Label>): transpilers must not touch them. To insert code, turn the matched instruction into the first new instruction (it keeps its labels) and yield the rest after it.
  - `Content.HideShellPart` accepts modded ids too.
- **Build**: the examples deploy only with `-p:DeployExamples=true`. The device file-copy tool sometimes delivers a stale version when the same staged path is re-sent, so stage changed files under fresh paths and verify file sizes on the PC after copying.

## Mod-registered systems (built in v0.9)
In the game, each weapon family is its own system: separate block classes, node/stat classes and a `*Constants` class (AdvCannon/APS, Cram, Laser, ParticleCannon, Missile, Plasma, SimpleWeapon). A mod that adds a new family should be able to publish the same kind of balance surface through the API, so it is as moddable as the game's own systems.

Planned shape:
```csharp
var dozers = Tunables.RegisterSystem("BulldozerMod", "Bulldozers", "Bulldozer blades and drive.");
var baseDamage  = dozers.Float("BaseDamage", 500f, min: 0f, description: "Damage per blade hit.");
var speedFactor = dozers.Float("DesignSpeedModifier", 1f, min: 0.1f, max: 5f);
// In the mod's own code, every time it is needed (cheap, always the current value):
float damage = baseDamage.Value;
```
- **Naming**: values are addressed as `System.Value` (e.g. `Bulldozers.BaseDamage`), the same form as game tunables (`CramConstants.KineticPerPayload`). Other mods and `FtdModApi.tunables.json` use that name.
- **Contributions**: other mods call Set/Add/Multiply on them exactly like game tunables, combined in priority order. The owning mod declares the default plus an optional min/max, units and description. Min/max clamp the final value.
- **Visibility**: registered values appear in `FtdModApi_tunables.txt` and in the in-game "Modded values" screen. They are grouped under their system, with owning mod, default, current value and which mods changed them.
- **Lifetime**: register in OnLoad. JSON overrides for a system that is not installed are kept and reported as "waiting for <system>" rather than errors, so a settings pack can cover optional mods.
- **Pipelines**: a system can also publish its own `ModifierPipeline<T>` (e.g. "Bulldozers.Damage") through the API, so other mods can hook its behaviour with the same priority/fault-isolation rules as the built-in damage pipelines.
- **Feature status**: systems show up in `FtdModApi_status.txt`, so players can see which mods registered which systems.

## Player settings menu (planned)
An in-game menu where players browse and change every value the API exposes:
- the game's tunables (397 `*Constants` fields, plus the read-only constants once unlocked);
- values registered by mod systems (e.g. `Bulldozers.BaseDamage`);
- later, other mod settings that fit the same model.

**What each row shows**
- The value's name, system/group, description and units.
- The game or mod default, and the current value.
- Which mods change it and how (e.g. "ExampleDataOnlyMod: x1.1").
- The player's own setting, if any.

**Columns**: Value (name, system, description, units) | Default | Current | Player Value.
- **Default**: the game's or owning mod's value.
- **Current**: the value in effect right now, after all mods and the player.
- **Player Value**: empty by default. A number entered there becomes the value outright. It has the highest priority and overrides every mod contribution and the game default. Clearing the cell hands control back to the mods/game.
- A tooltip or detail panel lists which mods change the value and how (e.g. "ExampleDataOnlyMod: x1.1").

**Player settings**
- **Storage**: one JSON file in the root of the game's user folder: `Documents/From The Depths/FtdModApi.player.json`. It lives outside the Mods folder, so Steam Workshop updates never overwrite it, and it survives reinstalling the API. Only values with a Player Value are written.
- **Priority**: player values are applied as a final override above every mod contribution, including clamps. Mods cannot outrank them.
- **Validation**: type and min/max (for mod-registered values) are enforced in the menu. Invalid or unknown entries in the file are reported in the status file and left in the file, not applied. This way a value from a mod that is temporarily disabled is kept.
- **Reset**: clear one Player Value, a whole system, or everything.

**UI**
- Built with the game's own UI system (BrilliantSkies.Ui), so it matches the game's look.
- Opened from the main menu and from a keybind in game.
- Grouped by system (APS, CRAM, Missile, Laser, ... then each mod's systems), with search and a "changed only" filter.
- Changes apply live where the game reads the value at use time (most `*Constants` fields). Values the game only reads at startup are flagged "applies after restart".
- **Multiplayer**: a later item is syncing the host's values to clients. Until then the menu shows a warning that all players need the same settings.

## Value descriptions (v0.14)
- `TunableInfo.Description` is player-facing text for the settings menu tooltip; `TunableInfo.Note` holds technical notes (e.g. "Read-only in the game; N read(s) redirected by FtdModApi.").
- Sources, strongest first: the owning mod's `description:` argument; `Tunables.Describe(modId, key, text)` or a `FtdModApi.descriptions.json` file in an enabled mod folder (first mod wins); the built-in descriptions.
- Built-in descriptions for all 454 game values are an embedded resource in FtdModApi.dll (`Resources/FtdModApi.descriptions.json`), so a Workshop update can't lose or desync them.
- They were written from the game's own usage of each constant (decompiled readers). Values with no reader in 4.3.4 say "Appears unused in the current game version". A value with no description shows no description line.
- The settings menu search also matches descriptions.
- `FtdModApi.ApsMaxGaugeMm` (Mod values > FtdModApi) is the gauge cap as a tunable. The highest `RequestMaxGauge`/`apsMaxGaugeMm` request is a Set contribution at Priority.Override; a Player Value overrides it. It is registered only when the gauge patches are active.

## Logging conventions (v0.12, applies to all future work)
- `ModLog.Verbose(...)` writes only when `FtdModApi.VerboseLogging` is on. It is one switch for the API and every mod using `FtdApi.GetLog`, and lines are tagged `[verbose]`.
- **Turning it on**: set it in any `FtdModApi.tunables.json`; later it will be a value in the settings menu. The API peeks at the JSON files before patching, so the patching itself is logged too.
- **Every new feature must add verbose lines for**:
  - each patch target it hooks (PatchManager does this automatically);
  - registrations and their resolution: parts, pellets, rules, systems, waiting tunables;
  - the first time a hook runs (CallbackList does this automatically);
  - the first change per type on hot paths, e.g. a block type or loader type, with `VerboseOnce`.
- **Hot paths**: use the `Func<string>` overloads so the message is only built when verbose logging is on, and never log per call.
- The status file shows whether verbose logging is on.

## Game UI notes (v0.13 research)
- **Windows**: `ConsoleUi<T>` (focus object T) overrides `BuildInterface`: `NewWindow(id, title, WindowSizing.GetCentralHuge())`, then `window.SetMultipleTabs(params SuperScreen[])`. Open with `ActivateGui(GuiActivateType)`; close with `DeactivateGui()`.
- **Tabs**: `SuperScreen<T>` with `Build()`, which creates segments: `CreateStandardSegment`, `CreateStandardHorizontalSegment`, `CreateTableSegment(cols, rows)` (filled row by row; `SetColumnHeadings`, `SetColumnFractionalWidths`).
- **Widgets**: `StringDisplay.Quick`, `SubjectiveDisplay<T>.Quick` (live text), `TextInput<T>.Quick`, `SubjectiveButton<T>.Quick`, `DropDown<T, TDrop>` with `DropDownMenuAlt`, `SubjectiveToggle<T>.Quick`. Getters come from `M.m<T>(...)`.
- **Rebuilds**: `TriggerScreenRebuild()` rebuilds the tab, and a text box being typed in loses focus. So search applies on a button, and the value boxes apply live without a rebuild.
- **Main menu**: `MainMenuUi` → `MainMenuSettingsTab.Build()` uses `B(...)` (public) and `Button<T>(...)` (protected), and ends with `Done(bool)`.
- **Per-frame hook without patching**: `GameEvents.UpdateEvent.RegWithEvent(Action<ITimeStep>)`. Keyboard input needs a reference to `UnityEngine.InputLegacyModule`.

## How FtD builds its content (v0.3 research)
- **Load order**:
  1. The `ConfigurationManager` ctor calls `Load()`. That reads mod headers, then `LoadScriptMods()` triggers `PluginLoader.Instance`, so **plugins' OnLoad runs here**.
  2. `BuildConfiguration()`: `new Configured()`, components set from enabled mods, `Configured.RunReplacements(linker)`, then `Connect()` (runtime ids, flush).
  3. `Done()`.

  So rules registered in OnLoad are in place before content is built; the API applies them in a `RunReplacements` postfix. `Configured.i` is null during plugin OnLoad / AfterAllPluginsLoaded.
- **Blocks**: blocks are `ItemDefinition`s (`ModificationComponentContainerItem`, `.item` files), with `Code.ClassName` naming the C# block class.
  - New blocks are already possible through the game's own system (.item files plus a plugin class).
  - Missile parts (`MissileBlockComponent`) and CRAM pellets (`CannonPackerAddon`) are ordinary blocks.
- **Hiding**: `InventoryItemBaseClass.DisplayOnInventory` is honoured by the inventory GUI, tab selection, block search and quick search. Setting it false hides a block or prefab.
- **Replacing**: built-in item replacement (`.itemreplace`, `AbstractContainer.Replace(linker, guid, withGuid)`) swaps a definition for a deep copy of another that keeps the old ComponentId. Every blueprint and design converts.
- **Blueprints**: they store an `ItemDictionary` (index→guid). **Unknown guids silently become block 9a0ae372-beb4-4009-b14e-36ed0715af73.** Never remove item definitions; hide or replace them instead.
- **Tab names**: `ItemDefinition.InventoryTabOrVariantId.Reference` often has only a guid. Resolve the tab name by looking that guid up among items.
- **Core content folders in 4.3.4** (shown/total):
  - Core_Laser 37/43, Core_Plasma 20/27, Core_RingShields 3/3, Core_WarpDrive 4/4.
  - Core_NuclearEngine 0/10 (already hidden).
  - Core_APS 92/93, Core_CRAM 41/43, Core_Missile 45/52, Core_SimpleWeapon 19/19.
  - There is no separate PAC folder (still to find). Some laser-related blocks live outside Core_Laser: the laser rangefinder tracker, the laser warner and the APS/CRAM laser targeters.
- **Code-defined content**: `IVirtualMod` / `VirtualMod` are plugin classes the game constructs during `LoadScriptMods`. They can contribute mod components (textures, materials, items) from code, which makes them a candidate for code-defined content later.
- **APS shell parts**: the customiser list comes from `ShellModuleDictionary.GetAllModulesTypes()`. Existing shells use `GetModule`, so filtering only `GetAllModulesTypes` hides a part without breaking saved shells.

## Damage and block hooks found
- **Block.ApplyDamage(IDamageDescription DD, float precalculatedDamage)**: every damage type removes block health here. Hooked by a transpiler after CalculateDamage, plus a prefix for the precalculated (fire) case. Client returns early, so it is server-authoritative.
- **ProjectileImpactState.Reset(...)**: called from `ActivateHere` on the APS, CRAM, simple-weapon and fragment pools. The pool constructors call it with 0 damage.
- **ExplosionCreator.Explosion(DD)** / **(C, DD)**: every explosion; a depth guard prevents double modification.
- Never scale damage in the AbstractDamageDescription constructor: it would compound per block hit.
- **Block.GetArmourClass()** has no overrides in 4.3.4; a postfix is the armour hook.

## APS gauge cap (500 mm): where it lives
The cap is hard-coded in about 8 places. Each needs its own verified patch, all driven by a single `WeaponLimits.Aps.MaxGauge` setting:
- `AdvCannonData.DesiredGauge`: `VarFloatClamp(0.5f, 0.018f, 0.5f)` plus a slider attribute (0.018–0.5). `ProtoSync.LoadSlider` clamps saved values to the attribute.
- `AdvCannonNode.CheckGauge`: `Mathf.Min(0.5f, ...)`.
- `AdvCannonFiringPiece.GaugeIncreasesToProvideShellDiameter`: loops up to 8 gauge increasers.
- Other places: the `ApsTab.Build` slider (18f, 500f), a firing-piece tooltip, `ApsShellUiData.InspectionDiameter`, the ammo `StatsTab` and `ShellConstants`.
- Still to check: the stat curves above 0.5 m. Blueprints over 500 mm load clamped on installs without the mod.

## How the remaining content systems are built (first look)
- **Missile parts** (v0.6, built): parts are classes, not blocks.
  - `R_Missiles`'s static constructor scans every `MissileComponent` subclass the plugin loader knows, including plugin DLLs. Each class declares `public new const enumMissileComponentType ComponentType = (enumMissileComponentType)N`; `None` is skipped.
  - Parts are saved as that int, both in `.missile` files (`MissileBlueprintPersistence`) and in construct blueprints (gantry `ModuleType`).
  - An unknown number becomes `MissileBody` with a warning. In `.missile` files the parameter loop can then throw and lose the whole design; the API's prefix replaces unknown modded parts cleanly.
  - About 45 parts. Behaviour checks use both `componentType == N` (MissileBlueprint, MissileWarheads, explosion/impact handlers, movement, detection, mesh builder, launchpad, MainConstructInteraction, StatPanel) and `is` checks.
  - The API transpiles the number reads and leaves `Copy`/`CopyFrom`, persistence, `MissileBlueprintBuilder` and the editor alone, so they keep the real number.
  - Data parts are instances of the BehavesAs class. Name and stats come from postfixes on every declared getter; payload weight from a `GetLoadVolume` postfix.
  - Parts are added to `R_Missiles._componentTypes` / `_componentCategoryLookup` at the main menu. Never touch `R_Missiles` during plugin OnLoad: its static constructor uses `PluginLoader.Instance`, which is still being constructed then.
- **CRAM pellets**: `CannonPackerAddon` blocks bump fixed counters in `CramConnectedTypeInfo`.
- **APS reload timing** (for the shell stats hook): `ShellModel_Propellant` sets `ReloadTime = BaseReloadTime * volume modifiers * (2 + length + casing)`, `IntakeTime` and `CooldownTime`. Loaders use `ReloadTime * ShellRackModel.LoadingTimeFactor`; loader length capacity comes from the item's `Code.Variables` (`LengthCapacity`).

## Mod ecosystem observations
- Each existing mod bundles its own Harmony (2.0.4, 2.2.2, and a ~2.2 MB 2.3.x build).
- Common pain points:
  - Copying entire game methods into prefixes to change one constant (LargeAPS).
  - Patching `ProtoSync.LoadSlider`.
  - Delaying patches until the main menu.
- The old Huwa mods (AdvancedMimicUi, PrefabUIPlus) throw TypeLoadExceptions on 4.3.4 (removed `ClassHolder`).
- `plugin.json` gameversion: if it is older than "last compatible", the mod is not loaded. `6.6.6` bypasses the check.

## Milestones
1. (done, v0.1) Core, self-checking patches, damage pipelines, example mod.
2. (done, v0.2) Armour class pipeline, tunables (code + JSON).
3. (done, v0.3) Content core: ContentIds; hide/replace blocks, prefabs and whole mod folders; hide shell parts; reports.
4. (done, v0.4) APS shell-part registry with BehavesAs behaviour; verified in game (select, save, reload).
5. (done, v0.5) CRAM pellet registry and `CramApi.ShellStats`; verified in game (dense HE pellets raise HE power, lower shell health). 
6a. (done, v0.6) Missile parts: data copies and code parts, BehavesAs transpiler, missing-part fallback; verified in game (Heavy HE warhead and Light reinforced body change health, speed and explosive damage).
6. (done, v0.7, verified in game) APS hooks: `ApsApi.ShellStats` (postfix on the `ShellModel` constructor; payload via `_explosiveModifier` + `DeriveStatistics`) and `ApsApi.LoaderTiming` (rack→block map from `ComponentStart`; postfixes on `CalculateLoadingTimeFactor` and belt feed `SetDiameter`; `SetLoaderTimeFactor` and `"apsLoaders"` JSON).
7. (done, v0.8, verified in game: >500 mm works and survives save/reload) APS gauge cap: `ApsGauge.RequestMaxGauge` / `"apsMaxGaugeMm"`, highest request wins, max 3000 mm.
   - Patch sites: transpilers on `AdvCannonNode.CheckGauge` (0.5) and `GaugeIncreasesToProvideShellDiameter` (loop limit 8).
   - Postfixes on the `AdvCannonData(uint)` and `ApsShellUiData(uint)` constructors (raise `VarFloatClamp.Max`).
   - The slider attributes cached by `DataPackageSaveAndLoad.Gather` get their `Max`/`Middle` backing fields raised. In 4.3.4 the load-time clamp is `sliderAttribute.Clamp` inside `DataPackageSaveAndLoad.LoadAll`; LargeAPS's `ProtoSync.LoadSlider` patch targets an older build.
   - Optional transpilers on `AppendGaugeInfo`, `StatsTab.HandleDiam`, and `ApsTab.Build` (applied at the main menu, because UI styles must be loaded first).
8. (done, v0.9, verified in game: system registered, JSON change applied, WAITING entry reported) Mod-registered systems: `Tunables.RegisterSystem` → `TunableSystem.Float/Int/Bool` (lock-free reads, min/max clamp, `Changed` event), `CreatePipeline`/`CreateEvent` for mod-owned hooks; changes to unregistered systems wait (`WAITING` in the report). 
8a. (done, v0.10, verified in game: 57 values, 4 groups patched (19/13/31/5 methods), ProjectileArmor 35 shows in the CRAM tooltip) Read-only constants: Explosion, Detection, SharedWeapon, FuelRefinery (57 values). `ReadOnlyTunables` gives each a slot and transpiles every reader method (raw IL scan of the reader classes; `CustomExplosion`'s pass-through getters included) to `ldc.i4 slot; call GetFloat/GetInt/GetBool`. One optional patch per group; only values with redirected readers are registered (rolled-back groups excluded). Caveat: a tiny wrapper method outside the reader list could still inline an old copy.
8b. (done, v0.11, verified in game via log: Heavy Armour 1250 -> 1562.5, Metal Block 350 -> 700) Block health: `BlockApi.MaxHealth`; transpiler on `Block.InitialiseCommon` and `ProcessPossibleMaximumHealthChange` replaces `CalculateMaximumHealth(team, h)` (static, no block) with `CalculateMaximumHealthFor(team, h, this)`. Helpers `SetHealthMultiplier`/`SetArmourMultiplier` and JSON `"blockHealth"`/`"blockArmour"`.
8c. (done, v0.12, verified: 131 verbose lines, status shows on) Verbose logging switch and verbose lines across the API (see Logging conventions).
9. (done, v0.13, verified in game: menu opens) In-game settings menu: `SettingsUi` (`ConsoleUi` with Game values / Mod values / Changed / About tabs; group dropdown, search, table with Default / Current / Player Value / Clear). Opened by Ctrl+Shift+M (`GameEvents.UpdateEvent`, no patch) or Settings > Mod settings (prefix on `MainMenuAbstractTab.Done`). `PlayerValues` applies each value as a Set at priority int.MaxValue under owner "Player"; Default / Current / Player Value columns; player values override everything and are saved to `Documents/From The Depths/FtdModApi.player.json`.
9a. (done, v0.14, verified in game) Player-facing descriptions for all 454 game values in the menu tooltips (embedded resource, `Tunables.Describe`, `FtdModApi.descriptions.json`), `TunableInfo.Note`, and the APS gauge cap as menu value `FtdModApi.ApsMaxGaugeMm`.
9b. (built, v0.15, awaiting build and in-game test: framework, Constructs, Blocks, Ticks, BuildMode events) Event system, Spigot-style: Pre (cancellable) and Post events across constructs, blocks, weapons, projectiles, resources, campaign, session, player, AI, UI, scripting, plus construct modules, Lua functions, breadboard components and ModNet. Research and catalog: `claude/ftd-modapi-events-catalog.md`. Replaces the narrower "construct lifecycle events" item below.
10. Optional: block pasting hidden blocks from blueprints. Construct lifecycle events, offline patch-target checker.
11. Steam Workshop release (API only; examples excluded).
12. After release: custom APS shell payloads.
13. Last: API test mod (verifies the API's features in game and reports pass/fail).
