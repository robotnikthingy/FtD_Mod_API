using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BrilliantSkies.Modding;
using FtdModApi.Damage;
using FtdModApi.Patching;
using HarmonyLib;

namespace FtdModApi.Core
{
    /// <summary>
    /// The plugin FtD's PluginLoader instantiates. Kept free of Harmony types so the assembly resolver
    /// is installed before anything asks for 0Harmony.
    /// </summary>
    public sealed class ApiPlugin : GamePlugin_PostLoad
    {
        public ApiPlugin()
        {
            AssemblyResolver.Install();
        }

        public string name => FtdApi.ModId;

        public Version version => FtdApi.Version;

        public void OnLoad()
        {
            Bootstrap.Run();
        }

        public void OnSave()
        {
        }

        public bool AfterAllPluginsLoaded()
        {
            Bootstrap.AfterAllPluginsLoaded();
            return true;
        }
    }

    internal static class Bootstrap
    {
        private const string StatusFileName = "FtdModApi_status.txt";

        internal static void Run()
        {
            var log = FtdApi.Log;
            log.Info($"FtdModApi {FtdApi.Version} starting on FtD {FtdApi.GameVersion}.");
            VerboseSetting.Peek(SafeModsRoot());
            MainThread.Initialise();

            RegisterFeatures();
            LogHarmony(log);

            PatchManager.Apply(StartupSpecs());
            CramPellets.AfterPatching();
            InitialiseEvents(log);

            InitialiseTunables(log);
            InitialiseContent(log);

            FtdApi.IsReady = true;

            foreach (var f in Features.All)
            {
                if (f.State == FeatureState.Available) log.Info("Feature " + f);
                else log.Warning("Feature " + f);
            }
        }

        /// <summary>The patches applied at startup (also read by the offline patch checker).</summary>
        internal static List<PatchSpec> StartupSpecs()
        {
            var specs = new List<PatchSpec>();
            foreach (var (_, build) in StartupSources()) specs.AddRange(build());
            return specs;
        }

        /// <summary>Each group of startup patches, by name (the patch checker gathers them one by one).</summary>
        internal static List<(string Name, Func<IEnumerable<PatchSpec>> Build)> StartupSources() => new List<(string, Func<IEnumerable<PatchSpec>>)>
        {
            ("Lifecycle", () => Lifecycle.Specs()),
            ("Damage", () => DamagePatches.Specs()),
            ("Blocks", () => BlockApi.Specs()),
            ("Content", () => Content.Specs()),
            ("APS shell parts", () => ShellParts.Specs()),
            ("CRAM pellets", () => CramPellets.Specs()),
            ("Missile parts", () => MissileParts.Specs()),
            ("APS", () => ApsApi.Specs()),
            ("APS gauge", () => ApsGauge.Specs()),
            ("APS payloads", () => ApsPayloads.Specs()),
            ("Read-only tunables", () => ReadOnlyTunables.Specs()),
            ("Settings menu", () => FtdModApi.Settings.SettingsMenu.Specs()),
            ("Craft data", () => CraftData.Specs()),
        };

        internal static void AfterAllPluginsLoaded()
        {
            WriteTunablesFile();
            WriteStatusFile();
            ReportToModList();
        }

        private static void RegisterFeatures()
        {
            Features.Register(Features.BlockDamage, "Modify damage dealt to individual blocks (all damage types).");
            Features.Register(Features.BlockDamagedEvent, "Event after a block loses health.");
            Features.Register(Features.ProjectileLaunch, "Modify kinetic damage and AP of launched shells, bullets and fragments.");
            Features.Register(Features.ProjectileSource, "Tells launch modifiers which weapon family fired (APS, CRAM, simple, fragment).");
            Features.Register(Features.Explosion, "Modify damage and radius of every explosion.");
            Features.Register(Features.MainMenuEvent, "Main menu event and WhenMainMenuReady.");
            Features.Register(Features.ArmourClass, "Modify the armour class of any block.");
            Features.Register(Features.BlockHealth, "Modify the maximum health of any block.");
            Features.Register(Features.ContentItems, "Hide or replace existing blocks and prefabs (code or JSON).");
            Features.Register(Features.ContentShellParts, "Hide APS shell parts from the ammo customiser.");
            Features.Register(Features.ContentPaste, "Leave chosen blocks out of prefab pastes (PreventPaste).");
            Features.Register(Features.ShellPartRegistry, "Add new APS shell parts that survive saving and loading.");
            Features.Register(Features.ShellPartBehaviour, "Modded shell parts act as the game part they are based on (stats, fusing, flight, settings UI).");
            Features.Register(Features.CramShellStats, "Modify CRAM shell stats (kinetic, AP, HE, frag, EMP, fire, health) from the gun's pellets.");
            Features.Register(Features.CramPellets, "Add new CRAM pellet blocks, copies of game pellets with their own strength.");
            Features.Register(Features.MissilePartRegistry, "Add new missile parts (data copies of game parts, or your own classes) that survive saving.");
            Features.Register(Features.MissilePartBehaviour, "Modded missile parts count as the game part they are based on (warheads, thrusters, guidance, ...).");
            Features.Register(Features.ApsShellStats, "Modify APS shell stats (reload, cooldown, speed, kinetic, AP, payload, health) per shell design.");
            Features.Register(Features.ApsLoaderTiming, "Modify how fast each APS autoloader or belt feed loads.");
            Features.Register(Features.ApsGaugeCap, "Raise the APS 500 mm gauge cap (highest mod request wins).");
            Features.Register(Features.ApsPayloads, "Modded APS shell parts with payloads of their own (code that runs when the shell detonates).");
            Features.Register(Features.ReadOnlyTunables, "Change the game's read-only constants (Explosion, Detection, SharedWeapon, FuelRefinery) by redirecting the code that reads them.");
            Features.Register(Features.SettingsMenu, "In-game settings window (" + FtdModApi.Settings.SettingsMenu.HotkeyText + ", or Settings > Mod settings) with Default / Current / Player Value columns.");
            Features.Register(Features.EventTicks, "Events.Ticks: timed ticks (every frame, physics step, 0.25 s ... 60 s).");
            Features.Register(Features.EventBuildMode, "Events.BuildMode: the local player entering and leaving build mode.");
            Features.Register(Features.EventConstructLifecycle, "Events.Constructs: Spawned and Despawned.");
            Features.Register(Features.EventConstructPlay, "Events.Constructs: Playing/Played and Pulling/Pulled (campaign in/out of play).");
            Features.Register(Features.EventConstructScrap, "Events.Constructs: Scrapping/Scrapped and Defeating (auto-scrap with cause).");
            Features.Register(Features.EventConstructTeam, "Events.Constructs: TeamChanging/TeamChanged (capture).");
            Features.Register(Features.EventSubconstructs, "Events.Constructs: SubconstructAdded/SubconstructRemoved (turrets, spin blocks).");
            Features.Register(Features.EventBlockBuild, "Events.Blocks: Placing/Placed and Removing/Removed.");
            Features.Register(Features.EventBlockDestroyed, "Events.Blocks: Destroyed (by damage, with the damage and gunner).");
            Features.Register(Features.EventBlockRepaired, "Events.Blocks: Repaired.");
            Features.Register(Features.EventWeapons, "Events.Weapons: Firing (cancellable, any thread) and Fired, for every weapon type.");
            Features.Register(Features.EventAps, "Events.Aps: ShellFired.");
            Features.Register(Features.EventCram, "Events.Cram: ShellFired and Packed.");
            Features.Register(Features.EventMissiles, "Events.Missiles: Launched, Reloaded, Detonated (with cause).");
            Features.Register(Features.EventProjectiles, "Events.Projectiles: Hit, Detonated, Damaged (munitions shot at), ShieldHit (can change reflection).");
            Features.Register(Features.EventSession, "Events.Session: mode changes, saving/loading with mod save data, pause and speed, victory/defeat, quit.");
            Features.Register(Features.EventPlayers, "Events.Players: joined/left, avatar spawned/died, seats, prefabs, chat.");
            Features.Register(Features.EventResources, "Events.Resources: salvage and dump pickups (editable), resource dumps, harvesting per craft, supply-group transfers.");
            Features.Register(Features.EventCampaign, "Events.Campaign: fleets, move orders and waypoints, territory (cancellable), diplomacy, battles, blockades, AI-vs-AI battles, AI shipyard builds.");
            Features.Register(Features.EventAi, "Events.Ai: AI mainframe targets spotted/lost, primary target changes, movement/firing mode and behaviour/manoeuvre changes.");
            Features.Register(Features.EventDetection, "Events.Detection: first contact between craft, incoming missiles and shells spotted.");
            Features.Register(Features.EventLua, "Events.Lua: Lua script errors and log lines.");
            Features.Register(Features.LuaFunctions, "Mod functions in players' Lua scripts (Mods.<ModId>.<Function>).");
            Features.Register(Features.EventUi, "Events.Ui: windows opening (cancellable) and closing, block interaction (Q, cancellable).");
            Features.Register(Features.BreadboardComponents, "Mod components on players' breadboards (vehicle, AI and missile); components of missing mods are kept.");
            Features.Register(Features.CraftData, "Mod data and modules on each craft (CraftData, CraftModules), saved with the craft; values of missing mods are kept.");
            Features.Register(Features.ModNet, "ModNet: messages between the copies of a mod in multiplayer (channels, host/client sends, peers that run the API).");
            Features.Register(Features.Keybinds, "Mod keybinds the player can rebind in the settings window (Keybinds tab); Events.Input.KeyPressed/KeyReleased.");
            Features.Register(Features.Tunables, "Change the game's balance constants (AdvCannon, Missile, Cram, Laser, ...) and values registered by mod systems, from code or JSON.");
        }

        private static void InitialiseEvents(ModLog log)
        {
            try
            {
                Events.Initialise();
                log.Verbose("Event channels created; their game patches are applied when a mod first subscribes.");
            }
            catch (Exception e)
            {
                log.Error("Setting up the event system failed.", e);
            }
        }

        private static void InitialiseTunables(ModLog log)
        {
            try
            {
                int count = Tunables.Count; // discovers and captures game defaults before any mod changes them
                if (count == 0)
                {
                    Features.SetState(Features.Tunables, FeatureState.Unavailable, "no *Constants classes found in the game assemblies");
                    return;
                }
                Features.SetState(Features.Tunables, FeatureState.Available);
                log.Info($"Found {count} tunable game values.");
                if (Features.IsAvailable(Features.ReadOnlyTunables))
                {
                    int readOnly = ReadOnlyTunables.Register();
                    log.Info($"Made {readOnly} read-only game values tunable.");
                }
            }
            catch (Exception e)
            {
                Features.SetState(Features.Tunables, FeatureState.Unavailable, e.GetType().Name + ": " + e.Message);
                log.Error("Tunables discovery failed.", e);
                return;
            }

            VerboseSetting.Register();

            try
            {
                TunableDescriptions.Load(BrilliantSkies.Core.Constants.Get.PermanentPaths.RootModDir().ToString());
                log.Verbose(() => $"{TunableDescriptions.Count} value descriptions available for the settings menu.");
            }
            catch (Exception e)
            {
                log.Error("Loading value descriptions failed.", e);
            }

            try
            {
                Tunables.LoadDataOnlyMods(BrilliantSkies.Core.Constants.Get.PermanentPaths.RootModDir().ToString());
            }
            catch (Exception e)
            {
                log.Error("Loading " + Tunables.JsonFileName + " files failed.", e);
            }

            try
            {
                var modsRoot = SafeModsRoot();
                var userFolder = string.IsNullOrEmpty(modsRoot) ? null : System.IO.Directory.GetParent(modsRoot.TrimEnd('/', '\\'))?.FullName;
                PlayerValues.Load(userFolder);
            }
            catch (Exception e)
            {
                log.Error("Loading " + PlayerValues.FileName + " failed.", e);
            }

            try
            {
                var modsRoot = SafeModsRoot();
                var userFolder = string.IsNullOrEmpty(modsRoot) ? null : System.IO.Directory.GetParent(modsRoot.TrimEnd('/', '\\'))?.FullName;
                KeyBinds.Load(userFolder);
            }
            catch (Exception e)
            {
                log.Error("Loading " + KeyBinds.FileName + " failed.", e);
            }

            try
            {
                BreadboardComponents.Initialise();
            }
            catch (Exception e)
            {
                log.Error("Setting up mod breadboard components failed.", e);
            }

            try
            {
                ModNet.Initialise();
            }
            catch (Exception e)
            {
                log.Error("Setting up ModNet failed.", e);
            }
        }

        private static void InitialiseContent(ModLog log)
        {
            try
            {
                Content.LoadDataOnlyMods(BrilliantSkies.Core.Constants.Get.PermanentPaths.RootModDir().ToString());
            }
            catch (Exception e)
            {
                log.Error("Loading " + Content.JsonFileName + " files failed.", e);
            }

            // Blocks only exist once FtD has built its content, which happens after all plugins load.
            Lifecycle.WhenMainMenuReady(FtdApi.ModId, () =>
            {
                CramPellets.ResolveAll();
                MissileParts.ActivateAll();
                MainThread.Install();
                FtdModApi.Settings.SettingsMenu.InstallHotkey();
                // UI screens need the game's styles, which exist from the main menu on.
                PatchManager.Apply(ApsGauge.MainMenuSpecs());
                ApsGauge.ApplySliderLimits();
                WriteStatusFile();
                WriteContentFile();
            });
        }

        private static void WriteContentFile()
        {
            var dir = AssemblyResolver.ApiDirectory;
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                File.WriteAllText(Path.Combine(dir, "FtdModApi_content.txt"), Content.BuildReport());
                File.WriteAllText(Path.Combine(dir, "FtdModApi_blocks.txt"), Content.BuildBlocksReport());
            }
            catch (Exception e)
            {
                FtdApi.Log.Warning("Could not write FtdModApi_content.txt: " + e.Message);
            }
        }

        private static void WriteTunablesFile()
        {
            var dir = AssemblyResolver.ApiDirectory;
            if (string.IsNullOrEmpty(dir) || !Features.IsAvailable(Features.Tunables)) return;
            try
            {
                File.WriteAllText(Path.Combine(dir, "FtdModApi_tunables.txt"), Tunables.BuildReport());
            }
            catch (Exception e)
            {
                FtdApi.Log.Warning("Could not write FtdModApi_tunables.txt: " + e.Message);
            }
        }

        private static void LogHarmony(ModLog log)
        {
            try
            {
                var asm = typeof(Harmony).Assembly;
                log.Info($"Using Harmony {asm.GetName().Version} from {SafeLocation(asm)}.");
                var copies = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "0Harmony");
                if (copies > 1)
                {
                    log.Info($"{copies} copies of Harmony are loaded (other mods bundle their own). Harmony 2 shares patch state between copies, so this is fine.");
                }
            }
            catch (Exception e)
            {
                log.Warning("Could not inspect Harmony: " + e.Message);
            }
        }

        private static void WriteStatusFile()
        {
            var dir = AssemblyResolver.ApiDirectory;
            if (string.IsNullOrEmpty(dir)) return;

            var sb = new StringBuilder();
            sb.AppendLine("FtdModApi status report");
            sb.AppendLine("Written " + DateTime.Now.ToString("u") + " (regenerated every launch)");
            sb.AppendLine();
            sb.AppendLine("API version:  " + FtdApi.Version);
            sb.AppendLine("Game version: " + FtdApi.GameVersion);
            sb.AppendLine("Verbose log:  " + (ModLog.VerboseEnabled ? "on" : "off (turn on with \"FtdModApi.VerboseLogging\": true in a FtdModApi.tunables.json)"));
            try
            {
                var harmony = typeof(Harmony).Assembly;
                sb.AppendLine("Harmony:      " + harmony.GetName().Version + "  (" + SafeLocation(harmony) + ")");
            }
            catch
            {
                sb.AppendLine("Harmony:      unknown");
            }
            sb.AppendLine();

            sb.AppendLine("FEATURES");
            foreach (var f in Features.All)
            {
                sb.AppendLine($"  {f.State,-12} {f.Id}");
                sb.AppendLine($"               {f.Description}");
                if (!string.IsNullOrEmpty(f.Detail)) sb.AppendLine($"               Problem: {f.Detail}");
            }
            sb.AppendLine();

            sb.AppendLine("MOD SYSTEMS  (values and hooks registered by mods; full list in FtdModApi_tunables.txt)");
            var systems = Tunables.Systems;
            if (systems.Count == 0) sb.AppendLine("  None.");
            foreach (var s in systems)
            {
                var hooks = s.Pipelines;
                sb.AppendLine($"  {s.Name,-24} by {s.ModId}: {s.Values.Count} value(s){(hooks.Count > 0 ? ", hooks: " + string.Join(", ", hooks) : string.Empty)}");
            }
            sb.AppendLine();

            sb.AppendLine("EVENTS  (Host = host/single player only, All = every machine, Local = this player's machine)");
            foreach (var line in EventRegistry.Report()) sb.AppendLine(line);
            sb.AppendLine();

            sb.AppendLine("PATCHES");
            foreach (var r in PatchManager.Results) sb.AppendLine("  " + r);
            sb.AppendLine();

            sb.AppendLine("OTHER MODS PATCHING THE SAME GAME METHODS");
            sb.AppendLine("  (Not necessarily a problem; useful when two mods fight over the same behaviour.)");
            bool any = false;
            try
            {
                foreach (var original in PatchManager.Harmony.GetPatchedMethods())
                {
                    var info = Harmony.GetPatchInfo(original);
                    if (info == null) continue;
                    var others = info.Owners.Where(o => o != PatchManager.HarmonyId).ToList();
                    if (others.Count == 0) continue;
                    any = true;
                    sb.AppendLine("  " + PatchManager.Describe(original) + ": " + string.Join(", ", others));
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("  Could not read patch info: " + e.Message);
            }
            if (!any) sb.AppendLine("  None.");

            try
            {
                File.WriteAllText(Path.Combine(dir, StatusFileName), sb.ToString());
            }
            catch (Exception e)
            {
                FtdApi.Log.Warning("Could not write " + StatusFileName + ": " + e.Message);
            }
        }

        private static void ReportToModList()
        {
            var dir = AssemblyResolver.ApiDirectory;
            if (string.IsNullOrEmpty(dir)) return;

            var all = Features.All;
            var broken = all.Where(f => f.State == FeatureState.Unavailable).ToList();
            var degraded = all.Where(f => f.State == FeatureState.Degraded).ToList();

            string text;
            if (broken.Count == 0 && degraded.Count == 0)
            {
                text = $"v{FtdApi.Version} active, all {all.Count} features working";
            }
            else
            {
                text = $"v{FtdApi.Version} active, {all.Count - broken.Count}/{all.Count} features working"
                       + (broken.Count > 0 ? "; unavailable: " + string.Join(", ", broken.Select(f => f.Id)) : string.Empty)
                       + (degraded.Count > 0 ? "; degraded: " + string.Join(", ", degraded.Select(f => f.Id)) : string.Empty)
                       + ". See " + StatusFileName;
            }

            try
            {
                // Not an error: this is how mods show a status line in FtD's mod list.
                ModProblems.AddModProblem(FtdApi.ModId, dir, text, isError: false);
            }
            catch (Exception e)
            {
                FtdApi.Log.Warning("Could not add status to the mod list: " + e.Message);
            }
        }

        private static string SafeModsRoot()
        {
            try
            {
                return BrilliantSkies.Core.Constants.Get.PermanentPaths.RootModDir().ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string SafeLocation(System.Reflection.Assembly a)
        {
            try
            {
                return string.IsNullOrEmpty(a.Location) ? "(in memory)" : a.Location;
            }
            catch
            {
                return "(unknown)";
            }
        }
    }
}
