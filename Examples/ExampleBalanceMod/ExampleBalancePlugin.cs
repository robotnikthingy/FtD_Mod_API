using System;
using System.Threading;
using BrilliantSkies.Modding;
using FtdModApi;

namespace ExampleBalanceMod
{
    /// <summary>
    /// Example mod built on FtdModApi. No Harmony and no copied game code: just modifiers.
    ///
    ///  - APS shells launch with 20% more kinetic damage.
    ///  - Explosions do 10% more damage.
    ///  - Friendly fire does half damage.
    ///  - Logs a running count of destroyed blocks.
    ///  - Adds a "Dense solid body" APS shell part, and a "Cluster charge" part with its own payload.
    ///  - Adds "Dense HE pellets" for CRAM cannons (block in ItemDup/, strength registered here).
    ///  - CRAM shells get 10% more shell health.
    ///  - Adds two missile parts: "Heavy HE warhead" (data) and "Light reinforced body" (code, ExampleLightBody.cs).
    ///  - APS shells of 300 mm and more reload 15% slower; 8m autoloaders load 25% faster.
    ///  - Raises the APS gauge cap to 1000 mm.
    ///  - Registers its own "ExampleBalance" system, so players and other mods can change its numbers.
    ///  - Heavy armour blocks get 25% more health.
    ///  - Light fittings are left out of prefab pastes (Content.PreventPaste).
    ///  - Keeps a log on every craft (spawns, blocks lost) saved with the craft (ExampleCraftLog.cs).
    ///  - Adds two breadboard components, "Scale and limit" and "Craft status" (ExampleBreadboardComponents.cs).
    ///  - Uses the event system: logs craft spawning and removal, auto-scrap causes and build mode, counts destroyed
    ///    blocks, and refuses to place Rubber Blocks (a visible test of a cancellable event).
    /// </summary>
    public class ExampleBalancePlugin : GamePlugin_PostLoad
    {
        private const string ModId = "ExampleBalanceMod";

        private static int _blocksDestroyed;
        private static int _eventBlocksDestroyed;
        private static int _eventBlocksReported;
        private static readonly System.Collections.Generic.Dictionary<string, int> _combatTally = new System.Collections.Generic.Dictionary<string, int>();

        private static int _firingChecks;
        private static int _shieldHits;

        private static readonly System.Collections.Generic.Dictionary<string, float> _economyTally = new System.Collections.Generic.Dictionary<string, float>();

        private static void Economy(string key, float amount)
        {
            _economyTally.TryGetValue(key, out float n);
            _economyTally[key] = n + amount;
        }

        private static readonly System.Collections.Generic.Dictionary<string, int> _aiTally = new System.Collections.Generic.Dictionary<string, int>();

        private static void AiTally(string key)
        {
            _aiTally.TryGetValue(key, out int n);
            _aiTally[key] = n + 1;
        }

        private static string DescribeFocus(object focus)
        {
            switch (focus)
            {
                case Block b: return $"block '{b.item?.ComponentId.Name}'";
                case MainConstruct c: return $"craft '{c.GetName()}'";
                default: return focus.GetType().Name;
            }
        }

        private static void Tally(string key, int amount = 1)
        {
            _combatTally.TryGetValue(key, out int n);
            _combatTally[key] = n + amount;
        }

        public string name => ModId;

        public Version version => new Version(0, 1, 0);

        public void OnLoad()
        {
            // Fails this mod with a clear message if an older, incompatible API is installed.
            FtdApi.Require(new Version(0, 28, 0), ModId);
            var log = FtdApi.GetLog(ModId);

            // This mod's own settings, published as a system. Players and other mods change them like game values,
            // e.g. "ExampleBalance.FriendlyFireFactor": 0.25 in any mod's FtdModApi.tunables.json.
            var system = Tunables.RegisterSystem(ModId, "ExampleBalance", "Settings of the FtdModApi example balance mod.");
            var friendlyFire = system.Float("FriendlyFireFactor", 0.5f, min: 0f, max: 1f, description: "Damage multiplier for friendly fire.", units: "x");
            var apsKinetic = system.Float("ApsKineticMultiplier", 1.2f, min: 0f, description: "Multiplier on APS shell kinetic damage at launch.", units: "x");

            // Shell output: changes the projectile itself, so it penetrates further too.
            DamageApi.ProjectileLaunch.Add(ModId, ctx =>
            {
                if (ctx.Source == ProjectileSource.AdvancedCannon)
                {
                    ctx.KineticDamage *= apsKinetic.Value;
                }
            }, Priority.Multiply, "APS kinetic +20%");

            // Every explosion in the game: HE shells, missiles, ammo racks, ...
            DamageApi.Explosion.Add(ModId, ctx => ctx.Damage *= 1.1f, Priority.Multiply, "Explosions +10%");

            // Per-block damage, any weapon type. Runs on worker threads: keep it simple and thread-safe.
            DamageApi.Block.Add(ModId, ctx =>
            {
                if (ctx.IsFriendlyFire)
                {
                    ctx.Damage *= friendlyFire.Value;
                }
            }, Priority.Multiply, "Friendly fire -50%");

            DamageApi.BlockDamaged.Subscribe(ModId, args =>
            {
                if (!args.Destroyed) return;
                int total = Interlocked.Increment(ref _blocksDestroyed);
                if (total % 500 == 0) log.Info($"{total} blocks destroyed so far this session.");
            });

            // A new APS shell part. It behaves like the game's solid warhead body (so the game treats it as a
            // kinetic body everywhere), with more kinetic damage, less speed and a higher ammo cost.
            // The Id is permanent: it becomes the number saved in blueprints.
            ShellParts.Register(ModId, new ShellPartDefinition
            {
                Id = "ExampleBalanceMod.DenseSolidBody",
                Name = "Dense solid body",
                Description = "A heavier solid body: more kinetic damage, slower, more expensive. (Example part from FtdModApi.)",
                BehavesAs = "SolidWarhead",
                KineticDamage = 1.3f,
                Speed = 0.9f,
                AmmoCost = 1.5f
            });

            // A shell part with a payload of its own (ExampleClusterPayload.cs). It sits where an HE body can and
            // carries no HE itself: when the shell detonates, the payload sets off four smaller blasts instead.
            ShellParts.Register(ModId, new ShellPartDefinition
            {
                Id = "ExampleBalanceMod.ClusterCharge",
                Name = "Cluster charge",
                Description = "Four smaller HE blasts in a line instead of one. (Example payload from FtdModApi.)",
                BehavesAs = "HeWarhead",
                Payload = new ExampleClusterPayload()
            });

            // A new CRAM pellet. The block is a copy of the game's HE pellets, made by the game's own
            // "item duplicate and modify" file in this mod's ItemDup folder. Here we say how it differs.
            CramPellets.Register(ModId, new CramPelletDefinition
            {
                Id = "ExampleBalanceMod.DenseHePellets",
                Block = "07e074c0-24cb-4e2e-b65b-8e3c2cc8c60c",
                Description = "Denser filling: +30% HE power, -20% shell health per pellet. (Example pellet from FtdModApi.)",
                Payload = 1.3f,
                Health = 0.8f
            });

            // Every CRAM shell, whatever its pellets. Also used for the gun's tooltip, so keep it a pure calculation.
            CramApi.ShellStats.Add(ModId, ctx => ctx.Health *= 1.1f, Priority.Multiply, "CRAM shell health +10%");

            // A data missile part: a copy of the game's explosive warhead that counts as 1.25 warheads of HE
            // but is more fragile. Everything else (model, behaviour, settings) comes from the warhead.
            MissileParts.Register(ModId, new MissilePartDefinition
            {
                Id = "ExampleBalanceMod.HeavyHeWarhead",
                Name = "Heavy HE warhead",
                Description = "Packs 25% more explosive than a standard warhead, but is more fragile. (Example part from FtdModApi.)",
                BehavesAs = "ExplosiveWarhead",
                PayloadVolume = 1.25f,
                HealthModifier = 0.07f
            });

            // A code missile part: our own class (ExampleLightBody.cs), derived from the game's reinforced body.
            MissileParts.Register<ExampleLightBody>(ModId, "ExampleBalanceMod.LightBody");

            // Every APS shell design: big shells reload more slowly. Shown in the ammo customiser.
            ApsApi.ShellStats.Add(ModId, ctx =>
            {
                if (ctx.Diameter >= 0.3f) ctx.ReloadTime *= 1.15f;
            }, Priority.Multiply, "APS 300mm+ reload +15%");

            // One loader block: the 8m autoloader loads 25% faster (factor 0.75 on its loading time).
            ApsApi.SetLoaderTimeFactor(ModId, "Autoloader (8m shells)", 0.75f);

            // Raise the APS gauge cap from 500 mm to 1000 mm (needs about 19 gauge increasers on one barrel).
            ApsGauge.RequestMaxGauge(ModId, 1000f);

            // One block type: heavy armour gets 25% more maximum health (applies to blocks placed or loaded from now on).
            BlockApi.SetHealthMultiplier(ModId, "Heavy Armour", 1.25f);

            // With "FtdModApi.VerboseLogging": true, the API logs the first health change per block type itself.

            // Light fittings are left out when a prefab is pasted with the build tool (they can still be placed by
            // hand, and craft that already have them keep them). A balance mod would usually HideItem the block too.
            Content.PreventPaste(ModId, "Light fitting");

            // ---- Events ----
            // Craft lifecycle. Spawned/Despawned run on every machine; Defeating only on the host.
            Events.Constructs.Spawned.Subscribe(ModId, e => log.Info($"Craft spawned: '{e.Name}' (id {e.UniqueId})."));
            Events.Constructs.Despawned.Subscribe(ModId, e => log.Info($"Craft removed: '{e.Name}' ({e.Reason})."));
            Events.Constructs.Defeating.Subscribe(ModId, e => log.Info($"Craft '{e.Name}' is being auto-scrapped: {e.Cause}."), EventPriority.Monitor);

            // A cancellable event: refuse to place Rubber Blocks, and say why.
            Events.Blocks.Placing.Subscribe(ModId, e =>
            {
                if (e.ItemName == "Rubber Block")
                {
                    e.Cancel();
                    log.Hud("ExampleBalanceMod: Rubber Blocks are disabled by the example mod.");
                }
            });

            // Count blocks destroyed by damage, and report the total every 5 seconds when it changed.
            Events.Blocks.Destroyed.Subscribe(ModId, e =>
            {
                _eventBlocksDestroyed++;
                CraftModules.Get<ExampleCraftLog>(e.MainConstruct)?.BlockLost();
            });

            // A craft module on every craft: spawn and block-loss counts saved with the craft (ExampleCraftLog.cs).
            CraftModules.Register<ExampleCraftLog>(ModId, "craft-log");
            Events.Ticks.EveryFiveSeconds.Subscribe(ModId, e =>
            {
                if (_eventBlocksDestroyed == _eventBlocksReported) return;
                _eventBlocksReported = _eventBlocksDestroyed;
                log.Info($"Blocks destroyed so far: {_eventBlocksDestroyed}.");
            });

            // Weapon events: tally shots and missile detonations, and report them every 5 seconds while fighting.
            // Firing runs on the game's AI worker threads: only thread-safe work here (Interlocked), no game objects.
            // A mod could call e.Cancel() to hold fire; this one only counts.
            Events.Weapons.Firing.Subscribe(ModId, e => Interlocked.Increment(ref _firingChecks));
            Events.Weapons.Fired.Subscribe(ModId, e => Tally("fired " + e.Kind));
            Events.Aps.ShellFired.Subscribe(ModId, e => Tally($"APS shell {e.Diameter * 1000f:0}mm"));
            Events.Cram.Packed.Subscribe(ModId, e => Tally("CRAM packed"));
            Events.Missiles.Launched.Subscribe(ModId, e => Tally("missile launched"));
            Events.Missiles.Detonated.Subscribe(ModId, e => Tally("missile " + e.Cause + (e.IsArmed ? "" : " (dud)")));
            // Projectile events (queued to the main thread), plus ShieldHit on the worker thread.
            Events.Projectiles.Hit.Subscribe(ModId, e => { Tally("hits"); if (e.IsKill) Tally(e.IsMunitionTarget ? "munitions killed by hits" : "blocks killed by hits"); });
            Events.Projectiles.Detonated.Subscribe(ModId, e => Tally($"{e.Kind} detonated {e.Reason}"));
            Events.Projectiles.Damaged.Subscribe(ModId, e => { if (e.Destroyed) Tally($"{e.Kind} intercepted"); });
            Events.Projectiles.ShieldHit.Subscribe(ModId, e => Interlocked.Increment(ref _shieldHits));

            Events.Ticks.EveryFiveSeconds.Subscribe(ModId, e =>
            {
                int shields = Interlocked.Exchange(ref _shieldHits, 0);
                if (shields > 0) Tally("shield hits", shields);
                int firing = Interlocked.Exchange(ref _firingChecks, 0);
                if (_combatTally.Count == 0 && firing == 0) return;
                var parts = new System.Collections.Generic.List<string> { $"firing checks: {firing}" };
                foreach (var kv in _combatTally) parts.Add($"{kv.Key}: {kv.Value}");
                _combatTally.Clear();
                log.Info("Combat (last 5 s): " + string.Join(", ", parts));
            });

            // Session events, and per-save mod data: count how many times this save was loaded.
            Events.Session.ModeChanged.Subscribe(ModId, e => log.Info($"Mode changed: {e.Mode} ({e.SequenceName})."));
            Events.Session.Paused.Subscribe(ModId, e => log.Info("Game paused."));
            Events.Session.Unpaused.Subscribe(ModId, e => log.Info("Game unpaused."));
            Events.Session.TimeScaleChanged.Subscribe(ModId, e => log.Verbose(() => $"Game speed {e.OldTimeScale:0.##} -> {e.NewTimeScale:0.##}."));
            Events.Session.Saving.Subscribe(ModId, e =>
            {
                int loads = int.TryParse(ModSaveData.Get(ModId), out int n) ? n : 0;
                e.Write(ModId, loads.ToString());
                log.Info($"Saving '{e.SaveName}' (slot {e.Slot}{(e.IsAutosave ? ", autosave" : "")}); stored load count {loads}.");
            });
            Events.Session.Saved.Subscribe(ModId, e => log.Info($"Saved; mod data written: {e.ModDataWritten}."));
            Events.Session.Loaded.Subscribe(ModId, e =>
            {
                int loads = (int.TryParse(e.Read(ModId), out int n) ? n : 0) + 1;
                ModSaveData.Set(ModId, loads.ToString());
                log.Info($"Loaded slot {e.Slot}; this save has now been loaded {loads} time(s) with the example mod.");
            });
            Events.Session.GameFinished.Subscribe(ModId, e => log.Info($"Scenario finished: {(e.PlayerWon ? "won" : "lost")}."));

            // Player events.
            Events.Players.Added.Subscribe(ModId, e => log.Info($"Player added: {e.Name}{(e.IsLocal ? " (local)" : "")}."));
            Events.Players.AvatarSpawned.Subscribe(ModId, e => log.Info($"{e.Name} spawned."));
            Events.Players.AvatarDied.Subscribe(ModId, e => log.Info(e.Killed ? $"{e.Name} died." : $"{e.Name} removed their avatar."));
            Events.Players.SeatEntered.Subscribe(ModId, e => log.Info($"{e.Name} sat in a seat on '{e.Construct?.GetName()}'."));
            Events.Players.SeatLeft.Subscribe(ModId, e => log.Info($"{e.Name} left a seat on '{e.Construct?.GetName()}'."));
            Events.Players.PrefabPlaced.Subscribe(ModId, e => log.Info($"Prefab '{e.PrefabName ?? "(unnamed selection)"}' placed ({e.BlocksPlaced} blocks)."));
            Events.Players.ChatReceived.Subscribe(ModId, e => log.Info($"Chat from {e.Name}: {e.Message}"));

            // Resource events: add up salvage, dumps, harvesting and supply transfers, and report them every 5 seconds.
            Events.Resources.Gained.Subscribe(ModId, e => Economy(e.Source == ResourceGainSource.DumpPickup ? "dump pickups" : "salvage", e.Material));
            Events.Resources.Dumped.Subscribe(ModId, e => Economy(e.IsNew ? "new dumps" : "added to dumps", e.Material));
            Events.Resources.Harvested.Subscribe(ModId, e => Economy($"harvested by '{e.Name}'", e.Material));
            Events.Resources.Transferred.Subscribe(ModId, e =>
            {
                foreach (var c in e.Changes)
                    if (c.Material > 0) Economy("supply transfers", (float)c.Material);
            });
            Events.Ticks.EveryFiveSeconds.Subscribe(ModId, e =>
            {
                if (_economyTally.Count == 0) return;
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in _economyTally) parts.Add($"{kv.Key}: {kv.Value:0.#}");
                _economyTally.Clear();
                log.Info("Materials (last 5 s): " + string.Join(", ", parts));
            });

            // Campaign events.
            Events.Campaign.FleetCreated.Subscribe(ModId, e => log.Verbose(() => $"Fleet created: '{e.Name}' ({e.FactionName})."));
            Events.Campaign.FleetRemoved.Subscribe(ModId, e => log.Verbose(() => $"Fleet removed: '{e.Name}' ({e.FactionName})."));
            Events.Campaign.TerritoryChanged.Subscribe(ModId, e =>
                log.Info($"Territory ({e.East},{e.North}) changed from {e.OldOwnerName ?? "neutral"} to {e.NewOwnerName ?? "neutral"} ({e.Cause})."));
            Events.Campaign.FactionEliminated.Subscribe(ModId, e => log.Info($"Faction eliminated: {e.FactionName} ({e.Territories} squares, {e.Craft} craft left)."));
            Events.Campaign.RelationsChanged.Subscribe(ModId, e =>
            {
                if (e.WarStarted) log.Info($"War: {e.FactionAName} and {e.FactionBName} ({e.NewValue:0.#}).");
                else if (e.WarEnded) log.Info($"Peace: {e.FactionAName} and {e.FactionBName} ({e.NewValue:0.#}).");
                else if (e.AllianceFormed) log.Info($"Alliance: {e.FactionAName} and {e.FactionBName}.");
                else if (e.InvolvesPlayer) log.Verbose(() => $"Relations {e.FactionAName} / {e.FactionBName}: {e.OldValue:0.#} -> {e.NewValue:0.#}.");
            });
            Events.Campaign.BattlePreparing.Subscribe(ModId, e => log.Info($"Battle setup started by {e.StartedByName}."));
            Events.Campaign.BattleStarted.Subscribe(ModId, e => log.Info("Battle started."));
            Events.Campaign.BattleEnded.Subscribe(ModId, e => log.Info($"Battle ended{(e.EndedEarly ? " early" : "")} after {e.Duration:0} s; {e.RetreatCount} fleet(s) retreating."));
            Events.Campaign.BattleCancelled.Subscribe(ModId, e => log.Info("Battle cancelled."));
            Events.Campaign.BlockadeStarted.Subscribe(ModId, e => log.Info($"Fleet '{e.Name}' blockaded by '{e.ByName}'."));
            Events.Campaign.BlockadeEnded.Subscribe(ModId, e => log.Info($"Fleet '{e.Name}' is no longer blockaded by '{e.ByName}'."));
            Events.Campaign.OffscreenBattleStarted.Subscribe(ModId, e => log.Info($"Off-screen battle: {string.Join(" vs ", System.Linq.Enumerable.Select(e.Fleets, f => $"'{f?.Name}'"))}."));
            Events.Campaign.OffscreenBattleEnded.Subscribe(ModId, e => log.Info($"Off-screen battle ended; still standing: {string.Join(", ", System.Linq.Enumerable.Select(e.Fleets, f => $"'{f?.Name}'"))}."));
            Events.Campaign.OffscreenForceKilled.Subscribe(ModId, e => log.Info($"'{e.Name}' was destroyed off screen."));
            Events.Campaign.DesignBuilt.Subscribe(ModId, e => log.Info($"{e.FactionName} {(e.IsDeadBlueprint ? $"placed '{e.DesignName}' as a wreck to repair (design cost {e.Cost:0})" : $"built '{e.DesignName}' for {e.Cost:0} materials")}."));

            // AI and detection events: count the frequent ones, and report them every 5 seconds; log mode changes.
            Events.Ai.TargetSpotted.Subscribe(ModId, e => AiTally(e.TargetConstruct != null ? "targets spotted" : "missiles targeted"));
            Events.Ai.TargetLost.Subscribe(ModId, e => AiTally(e.Destroyed ? "targets destroyed" : "targets lost"));
            Events.Ai.PrimaryTargetChanged.Subscribe(ModId, e => AiTally("primary target changes"));
            Events.Detection.ContactMade.Subscribe(ModId, e => AiTally(e.IsOtherTeam ? "enemy contacts" : "friendly contacts"));
            Events.Detection.IncomingProjectile.Subscribe(ModId, e => AiTally($"incoming {e.Kind}"));
            Events.Campaign.FleetOrdered.Subscribe(ModId, e =>
            {
                if (e.IsPlayerFleet && !e.ByAi) log.Info($"Fleet '{e.Name}' ordered to {(e.Follow != null ? $"follow '{e.FollowName}'" : "move")}{(e.Queued ? " (queued)" : "")}.");
                else AiTally("AI fleet orders");
            });
            Events.Campaign.FleetWaypointReached.Subscribe(ModId, e =>
            {
                if (e.IsPlayerFleet) log.Info($"Fleet '{e.Name}' reached a waypoint{(e.HasMoreOrders ? "" : " (its last)")}.");
                else AiTally("AI waypoints reached");
            });
            Events.Ai.MovementChanged.Subscribe(ModId, e => log.Info($"'{e.Name}' mainframe '{e.MainframeName}': movement {e.OldMode} -> {e.NewMode}."));
            Events.Ai.FiringChanged.Subscribe(ModId, e => log.Info($"'{e.Name}' mainframe '{e.MainframeName}': firing {e.OldMode} -> {e.NewMode}."));
            Events.Ai.RoutineChanged.Subscribe(ModId, e => log.Info($"'{e.Name}' mainframe '{e.MainframeName}': {e.Kind} {e.OldName ?? "none"} -> {e.NewName ?? "none"}."));
            Events.Ticks.EveryFiveSeconds.Subscribe(ModId, e =>
            {
                if (_aiTally.Count == 0) return;
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in _aiTally) parts.Add($"{kv.Key}: {kv.Value}");
                _aiTally.Clear();
                log.Info("AI and detection (last 5 s): " + string.Join(", ", parts));
            });

            // A mod keybind (rebindable in the FtdModApi settings window, Keybinds tab): Ctrl+K reports the craft count.
            var report = KeyBinds.Register(ModId, "report", "Report craft count", new KeyCombo(UnityEngine.KeyCode.K, KeyModifiers.Ctrl),
                "Shows how many craft are in play.");
            report.OnPressed(e => log.Hud($"ExampleBalanceMod: {StaticConstructablesManager.Constructables.Count} craft in play ({e.Combo})."));

            // Mod functions for players' Lua scripts: Mods.ExampleBalanceMod.Hello("name") and .BlocksDestroyed().
            LuaFunctions.Register(ModId, "Hello", call => $"Hello {call.String(0, "captain")} from '{call.Construct?.GetName()}'!",
                "Returns a greeting. Lua: I:Log(Mods.ExampleBalanceMod.Hello(\"Bob\"))");
            LuaFunctions.Register(ModId, "BlocksDestroyed", call => _eventBlocksDestroyed, "Blocks destroyed this session.");
            // ModNet: Ctrl+L sends a ping to the host, the host answers with a pong. In single player (or hosting alone)
            // the ping arrives on this machine; turn on "ModNetLoopback" in the FtdModApi settings to get the pong too.
            var ping = ModNet.Channel(ModId, "ping");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var me = Guid.NewGuid(); // so a machine only shows the pongs for its own pings
            ping.OnReceive(m =>
            {
                var kind = m.Reader.ReadString();
                var from = m.Reader.ReadGuid();
                var sentAt = m.Reader.ReadDouble();
                var how = m.IsLoopback ? " (loopback)" : "";
                if (kind == "ping" && !m.FromHost)
                {
                    log.Info($"ModNet: ping from {m.SenderName}{how}; answering.");
                    var pong = new ModNetWriter().Write("pong").Write(from).Write(sentAt);
                    if (m.Sender != null) ping.SendTo(m.Sender, pong);              // a client pinged: answer it
                    else if (ModNet.LoopbackClients) ping.SendToClients(pong);     // this machine pinged itself: loopback test
                    else log.Info("ModNet: the ping came from this machine; turn on ModNetLoopback to get a pong here.");
                }
                else if (kind == "pong" && m.FromHost && from == me)
                {
                    log.Hud($"ExampleBalanceMod: pong from {m.SenderName}{how} after {(clock.Elapsed.TotalSeconds - sentAt) * 1000:0} ms.");
                }
            });
            KeyBinds.Register(ModId, "ping", "ModNet ping", new KeyCombo(UnityEngine.KeyCode.L, KeyModifiers.Ctrl),
                "Sends a test message to the host (FtdModApi ModNet).")
                .OnPressed(e =>
                {
                    bool sent = ping.SendToHost(new ModNetWriter().Write("ping").Write(me).Write(clock.Elapsed.TotalSeconds));
                    log.Hud($"ExampleBalanceMod: ping {(sent ? "sent" : "not sent")} ({(ModNet.IsClient ? "to the host" : ModNet.IsMultiplayer ? "hosting" : "single player")}).");
                });
            ModNet.PeerJoined.Subscribe(ModId, e => log.Info($"ModNet: {e.Peer.Name} connected (FtdModApi {e.Peer.ApiVersion}, {(e.Peer.HasMod(ModId) ? "has" : "doesn't have")} this mod)."));
            ModNet.PeerLeft.Subscribe(ModId, e => log.Info($"ModNet: {e.Peer.Name} left."));

            // Breadboard components: "Scale and limit" on every breadboard, "Craft status" on vehicle and AI breadboards.
            BreadboardComponents.Register<ScaleAndLimit>(ModId, "scale-and-limit", "Scale and limit",
                "Multiplies the input by a factor and can keep the result between a minimum and a maximum.", BreadboardKinds.All);
            BreadboardComponents.Register<CraftStatus>(ModId, "craft-status", "Craft status",
                "The craft's speed, altitude and top speed.");

            Events.Lua.ScriptError.Subscribe(ModId, e => log.Info($"Lua error on '{e.Name}' ({(e.WhileLoading ? "loading" : "running")}): {e.Message}"));
            Events.Lua.Logged.Subscribe(ModId, e => log.Verbose(() => $"Lua log from '{e.Name}'{(e.ToHud ? " (HUD)" : "")}: {e.Message}"));

            // UI events: log windows opening and closing (with what they show), and block interactions.
            Events.Ui.WindowOpened.Subscribe(ModId, e => log.Info($"Window opened: {e.TypeName}{(e.Focus != null ? $" for {DescribeFocus(e.Focus)}" : "")}."));
            Events.Ui.WindowClosed.Subscribe(ModId, e => log.Info($"Window closed: {e.TypeName}."));
            Events.Ui.BlockInteracted.Subscribe(ModId, e => log.Info($"Interacted with '{e.ItemName}' on '{(e.Construct as MainConstruct)?.GetName() ?? e.Construct?.Main?.GetName()}'."));

            Events.BuildMode.Entered.Subscribe(ModId, e => log.Info($"Build mode entered on '{e.Name}'."));
            Events.BuildMode.Exited.Subscribe(ModId, e => log.Info($"Build mode left on '{e.Name}'."));

            // Tell the player if a game update switched off something this mod relies on.
            Lifecycle.WhenMainMenuReady(ModId, () =>
            {
                foreach (var feature in new[] { Features.ProjectileLaunch, Features.Explosion, Features.BlockDamage, Features.ShellPartRegistry, Features.ShellPartBehaviour, Features.CramPellets, Features.CramShellStats, Features.MissilePartRegistry, Features.MissilePartBehaviour, Features.ApsShellStats, Features.ApsLoaderTiming, Features.ApsGaugeCap, Features.BlockHealth, Features.EventConstructLifecycle, Features.EventBlockBuild, Features.EventBlockDestroyed, Features.EventWeapons, Features.EventMissiles, Features.EventProjectiles, Features.EventSession, Features.EventPlayers, Features.EventResources, Features.EventCampaign, Features.EventAi, Features.EventDetection, Features.EventLua, Features.LuaFunctions, Features.Keybinds, Features.EventUi, Features.BreadboardComponents, Features.ModNet, Features.CraftData, Features.ContentPaste, Features.ApsPayloads })
                {
                    if (!Features.IsAvailable(feature))
                    {
                        log.Hud($"{ModId}: '{feature}' is unavailable in this game version, so part of this mod is inactive.");
                    }
                }
            });

            // Verbose lines only appear when verbose logging is on; use them for detail that helps when debugging.
            log.Verbose(() => $"ExampleBalance settings: friendly fire x{friendlyFire.Value}, APS kinetic x{apsKinetic.Value}.");
            log.Info("Loaded.");
        }

        public void OnSave()
        {
        }

        public bool AfterAllPluginsLoaded() => true;
    }
}
