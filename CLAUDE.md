# FtdModApi: notes for Claude Code

FtdModApi is a stable modding API for **From The Depths 4.3.4** (Unity, Mono, .NET Framework 4.x). It is a normal
FtD plugin built on Harmony. Other mods depend on it instead of writing their own Harmony patches. When a game update
breaks a patch, only that one feature turns off, and the API reports which.

The API is documented in `README.md`. The history, game research and per-version status are in `docs/` (see below).

PC-specific notes (local paths, how the user tests) go in `CLAUDE.local.md`, which is git-ignored. This file is public.

## Paths

| What | Where |
|---|---|
| Game DLLs | Steam: `steamapps\common\From The Depths\From_The_Depths_Data\Managed` (default in `Directory.Build.props`; override with `-p:FtdManaged=<path>`) |
| Game user folder | `Documents\From The Depths\` |
| Mods (deploy target) | `...\From The Depths\Mods\` (`FtdModApi`, `ExampleBalanceMod`, `FtdModApiTest`) |
| Game logs | `...\From The Depths\Logs\4.3.4.1_<date>--<time>.txt` (newest = last run) |
| API reports (each launch) | `Mods\FtdModApi\FtdModApi_status.txt`, `_tunables.txt`, `_content.txt`, `_blocks.txt` |
| Test mod report | `Mods\FtdModApiTest\FtdModApiTest_report.txt` |
| Player settings | `...\From The Depths\FtdModApi.player.json`, `FtdModApi.keybinds.json` |

## Build, deploy, check

```powershell
dotnet build -c Release -p:DeployExamples=true -p:DeployTestMod=true   # API + example mod + test mod into Mods
dotnet build -c Release                                               # API only (what ships)
.\RunPatchChecker.ps1                                                 # offline: every patch vs installed game DLLs
```

- **Patch checker:** writes `FtdModApi_patchcheck.txt` in this folder. Exit codes: 0 OK, 1 broken, 2 couldn't run, 3 build failed.
  - The last result was 173/173 OK on v0.28.1. The test mod adds no new API patches.
  - Run it after every API change that adds or changes patches, and after every game update.
- **In-game testing:** build, play through the feature, then read the newest log in `Logs\`, plus the status and report files.
- **Multiplayer:** single player and hosting alone cover everything except a real second player. ModNet peers and
  handshakes need community testing; make the verbose logs good enough for that.

## Layout

- `FtdModApi/`: the API (netstandard2.1, references the game DLLs via `Directory.Build.props`, ships Harmony 2.2.2 net45 in `lib/`).
  - Folders: `Core` (plugin, features, lifecycle, ModLog, FieldRefs), `Patching` (PatchSpec, PatchManager, PatchChecker), `Pipelines`,
    `Damage`, `Blocks`, `Tunables`, `Settings` (in-game menu), `Content` (hide/replace/prevent paste, ContentIds, ShellParts),
    `Cram`, `Missiles`, `Aps` (shell stats, loaders, gauge cap, payloads), `Events` (Spigot-style events), `Input` (keybinds),
    `Lua`, `Breadboards`, `Net` (ModNet), `Craft` (CraftData, CraftModules).
- `Examples/ExampleBalanceMod`: uses only the API, deployed with `-p:DeployExamples=true`.
- `Examples/ExampleDataOnlyMod`: JSON only.
- `TestMod/FtdModApiTest`: the API test mod, deployed with `-p:DeployTestMod=true`.
- `Tools/PatchChecker`: net8.0 console app, not in the sln. Loads game DLLs via an AssemblyLoadContext resolver.
- `docs/catalog.md`: event research and **the running status log**: what each version added, what was verified in game,
  and the gotchas found.
- `docs/design.md`: architecture, game research and target list. Its milestones stop at v0.15.

## Rules (keep these)

- **Player blueprints must never become unusable or corrupted.** This is the user's top concern.
  - Never remove item definitions: unknown guids silently become a placeholder block. Hide or replace instead.
  - Saved modded numbers come from `ContentIds` (a stable hash). Never change an id or a hash.
  - A missing mod must leave designs loadable. Test that round trip (remove the mod, load, re-save, add it back) for anything that saves data.
- **The API alone must never change gameplay.** Examples and the test mod are opt-in builds. The Workshop release ships the API only.
- **Patches are data**: one `PatchSpec` per patch (`Id`, `FeatureId`, `Required`, `Target`/`Targets`, hooks, `Verify`).
  - A failing patch only turns off its feature (Unavailable for required, Degraded for optional).
  - Event hooks are patched lazily, on first subscribe.
  - Every new spec must be reachable by the patch checker: add it to a startup group in `ApiPlugin` or to an existing `Specs()` list.
- **Never call `AccessTools.FieldRefAccess` directly**; use `FieldRefs.Get<T,F>(name)`. Harmony 2.2.2 can't build those
  accessors on .NET 8, so the patch checker would report false failures.
- **netstandard2.1 + Harmony net45 pitfalls:**
  - Don't bind Harmony methods with an `ILGenerator` parameter (error CS7069); call them via reflection.
  - Transpilers must not touch `CodeInstruction.labels`/`blocks`. To insert code, turn the matched instruction into the first new one and yield the rest after it.
  - Inside the checker, don't use Harmony APIs that start MonoMod's detour engine. On .NET 8 that's a fatal CLR crash (0x80131506).
- **Threads:** damage, projectiles, detection, AI and payloads run on worker threads.
  - Post events are queued to the main thread.
  - Keep worker-thread hooks fast and thread-safe.
  - The host decides all damage (`Net.IsClient` returns early).
- **Verbose logging** (`ModLog.Verbose`, `VerboseOnce`, `Func<string>` overloads on hot paths): every feature logs its
  registrations, the first time each hook runs, and the first change per type. Never log per call.
  - The game's log file drops a line repeated within 0.1 s and caps any one line at 51 writes; `ModLog` adds " (repeat N)" for those.
- **Versions:** bump on every change, in all three places: `FtdModApi/FtdApi.cs`, `FtdModApi/FtdModApi.csproj` and
  `FtdModApi/ModFiles/plugin.json`.
  - Bump the example's `FtdApi.Require` only when it uses new API.
  - The current API version is **0.28.1**.
- **Docs:** after each step, update `README.md` (feature section + table) and append a short dated entry to `docs/catalog.md`.
- **Never commit** game DLLs or decompiled game source.

## Game facts that cost time to find

- **Load order:** plugin `OnLoad` runs inside `ConfigurationManager.Load`, before content is built (`Configured.i` is null then).
  Content rules apply in a `Configured.RunReplacements` postfix. Never touch `R_Missiles` in OnLoad.
- **Plugin manifest:** `plugin.json` `gameversion` older than "last compatible" means the mod isn't loaded. Consumer mods use `"depends": ["FtdModApi"]`.
- **Chat:** in multiplayer, Ctrl+M and Ctrl+J type a newline that opens the chat behind other windows. The API patches `ChatGUI.CheckForInput` to stop that.
- **Known game errors (not ours):** a `Force.GetBoundingBox` NRE with resource gatherers, and a FleetMovement NRE.
  "Change sync not enabled" warnings come from the game's breadboard code.

## Status (2026-11-05)

Verified in game: everything up to and including **v0.28.1**:
- the event system (constructs, blocks, weapons, projectiles, session, players, resources, campaign, AI, detection, UI, input, Lua);
- breadboard components, ModNet (single player and hosting alone), craft data and modules (including campaign save/reload);
- the offline patch checker, prevent-paste and custom APS payloads.

**Next steps, in order:**
1. **API test mod (FtdModApiTest 1.0.0)** builds cleanly (2026-11-07: 0 errors, 0 warnings) but has **not been run in game yet**.
   1. Build with `-p:DeployExamples=true -p:DeployTestMod=true`.
   2. Launch to the main menu, then the designer: spawn Trenton plus an enemy, fight, enter and leave build mode, open an APS ammo customiser, press Ctrl+Shift+T, quit.
   3. Read `FtdModApiTest_report.txt` and the log.
   - The two "expected-failure test" errors in the log are intended.
   - Remaining "not seen yet" items should be ones that only campaign or multiplayer can trigger.
2. **Steam Workshop release, API only.** Check `plugin.json`/`header.header`, the README for players, and that the examples and test mod are excluded.
3. **Untested leftovers:** Campaign BattleCancelled and FactionEliminated, cancel paths of cancellable events, simple weapons.
   Also load an API-saved blueprint with FtdModApi disabled, to confirm the extra SerialisedInfo keys are ignored by vanilla.
