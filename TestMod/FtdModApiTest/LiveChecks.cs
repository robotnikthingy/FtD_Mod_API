using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FtdModApi;

namespace FtdModApiTest
{
    /// <summary>
    /// Checks that pass when something happens in game: every API event, every modifier hook, craft modules,
    /// per-save data and ModNet. All of them only watch.
    /// </summary>
    internal static class LiveChecks
    {
        private const string Hooks = "Modifier hooks (watched, unchanged)";

        // ---- events ------------------------------------------------------------------------------------------------

        /// <summary>What to do in game to see each group of events.</summary>
        private static readonly Dictionary<string, string> EventHints = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ai"] = "spawn an enemy and let AI craft (with an AI mainframe) fight",
            ["Blocks"] = "place and remove blocks in build mode; damage and repair a craft",
            ["Campaign"] = "play a campaign: move fleets, fight, capture territory, build designs",
            ["Constructs"] = "spawn and delete craft; in campaign put craft in and out of play; change a craft's team; let one be scrapped",
            ["Detection"] = "a craft with an AI mainframe and detection near an enemy (missiles for incoming warnings)",
            ["Ticks"] = "load any game or the designer",
            ["BuildMode"] = "enter and leave build mode",
            ["Lua"] = "use a Lua box: I:Log(\"x\") and a script with an error",
            ["Players"] = "spawn as the avatar, die, sit in a seat, place a prefab; chat in multiplayer",
            ["Projectiles"] = "fire APS, CRAM or simple weapons at an enemy; shields for shield hits",
            ["Weapons"] = "fire any weapon",
            ["Aps"] = "fire an APS",
            ["Cram"] = "fire a CRAM cannon",
            ["Missiles"] = "fire missiles at an enemy",
            ["Resources"] = "campaign: gather, transfer or dump resources",
            ["Session"] = "save, load, pause, change game speed, quit",
            ["Ui"] = "open and close windows; interact with a block (Q)",
            ["Input"] = "press a mod keybind (the report key counts)",
            ["ModNet"] = "multiplayer only: another player with FtdModApi joins or leaves"
        };

        private static string HintFor(string id)
        {
            var group = id.Split('.')[0];
            return EventHints.TryGetValue(group, out var h) ? h : null;
        }

        private static string GroupOf(string id) => "Events: " + id.Split('.')[0];

        /// <summary>Subscribes a watcher to every API event (found by reflection, so new events are covered too).</summary>
        internal static void WatchAllEvents()
        {
            var holders = new List<Type> { typeof(ModNet) };
            var queue = new Queue<Type>(new[] { typeof(Events) });
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                holders.Add(t);
                foreach (var n in t.GetNestedTypes(BindingFlags.Public)) queue.Enqueue(n);
            }

            var watch = typeof(LiveChecks).GetMethod(nameof(Watch), BindingFlags.NonPublic | BindingFlags.Static);
            int count = 0;
            foreach (var holder in holders)
            {
                foreach (var p in holder.GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    var type = p.PropertyType;
                    if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(EventChannel<>)) continue;
                    try
                    {
                        var channel = p.GetValue(null);
                        watch.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, new[] { channel });
                        count++;
                    }
                    catch (Exception e)
                    {
                        var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                        Report.Fail("Events", holder.Name + "." + p.Name, "could not subscribe: " + inner.Message);
                    }
                }
            }
            Report.Info("Setup", "Events watched", count + " API events (each one's game patch is applied when this mod subscribes)");
        }

        private static readonly List<Action> _availabilityChecks = new List<Action>();

        private static void Watch<T>(EventChannel<T> channel) where T : GameEvent
        {
            var id = channel.Id;
            var group = GroupOf(id);
            var side = channel.Side == EventSide.Host ? "host" : channel.Side == EventSide.Local ? "local" : "every machine";
            Report.Expect(group, id, HintFor(id));
            channel.Subscribe(Plugin.ModId, e => Report.Seen(group, id, $"seen ({side})"), EventPriority.Monitor, ignoreCancelled: false, name: "API test watcher");
            // The patch behind an event may be applied lazily; check that it worked once the game is up.
            _availabilityChecks.Add(() =>
            {
                if (!channel.IsAvailable) Report.Fail(group, id, "the event's game patch is not working (see FtdModApi_status.txt)");
            });
        }

        /// <summary>Fails events whose game patch could not be applied.</summary>
        internal static void CheckEventAvailability()
        {
            foreach (var check in _availabilityChecks)
            {
                try
                {
                    check();
                }
                catch
                {
                    // Reported elsewhere.
                }
            }
        }

        // ---- modifier hooks ----------------------------------------------------------------------------------------

        internal static void WatchModifierHooks()
        {
            Observe(DamageApi.Block, "DamageApi.Block (damage to one block)", "damage any block (fight an enemy)",
                c => Finite(c.Damage) && c.Damage >= 0 ? null : "damage " + c.Damage);
            Observe(DamageApi.ProjectileLaunch, "DamageApi.ProjectileLaunch (shell and bullet launch)", "fire APS, CRAM or simple weapons",
                c => Finite(c.KineticDamage) && c.KineticDamage >= 0 ? null : "kinetic " + c.KineticDamage);
            Observe(DamageApi.Explosion, "DamageApi.Explosion (every explosion)", "any explosion: HE shells, missiles, ammo racks",
                c => Finite(c.Damage) && c.Damage >= 0 ? null : "damage " + c.Damage);
            Report.Expect(Hooks, "DamageApi.BlockDamaged (after a block lost health)", "damage any block");
            DamageApi.BlockDamaged.Subscribe(Plugin.ModId, a => Report.Seen(Hooks, "DamageApi.BlockDamaged (after a block lost health)", "seen"), Priority.Observe, "API test watcher");
            Observe(ApsApi.ShellStats, "ApsApi.ShellStats (APS shell designs)", "open an APS ammo customiser or spawn a craft with APS");
            Observe(ApsApi.LoaderTiming, "ApsApi.LoaderTiming (APS loaders)", "spawn a craft with APS autoloaders");
            Observe(CramApi.ShellStats, "CramApi.ShellStats (CRAM shells)", "spawn a craft with a CRAM cannon and let it load");
            Observe(BlockApi.ArmourClass, "BlockApi.ArmourClass (block armour)", "spawn any craft");
            Observe(BlockApi.MaxHealth, "BlockApi.MaxHealth (block health)", "spawn any craft");
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static void Observe<T>(ModifierPipeline<T> pipeline, string name, string hint, Func<T, string> problem = null) where T : class
        {
            Report.Expect(Hooks, name, hint);
            pipeline.Add(Plugin.ModId, ctx =>
            {
                string bad = null;
                if (problem != null)
                {
                    try
                    {
                        bad = problem(ctx);
                    }
                    catch (Exception e)
                    {
                        bad = "could not read the values: " + e.Message;
                    }
                }
                if (bad != null) Report.Fail(Hooks, name, "unexpected value: " + bad);
                else Report.Seen(Hooks, name, "called");
            }, Priority.Observe, "API test watcher");
        }

        // ---- per-save data -----------------------------------------------------------------------------------------

        private const string SaveGroup = "Per-save data (ModSaveData)";
        private const string SavePrefix = "FtdModApiTest saved at ";

        internal static void WatchSaveData()
        {
            Report.Expect(SaveGroup, "Data written when the game saves", "save a campaign (or wait for an autosave)");
            Report.Expect(SaveGroup, "Data read back when that save loads", "save a campaign, then load that save");
            Events.Session.Saving.Subscribe(Plugin.ModId, e =>
            {
                e.Write(Plugin.ModId, SavePrefix + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Report.Seen(SaveGroup, "Data written when the game saves", $"slot {e.Slot}{(e.IsAutosave ? " (autosave)" : "")}");
            });
            Events.Session.Loaded.Subscribe(Plugin.ModId, e =>
            {
                var data = e.Read(Plugin.ModId);
                if (data != null && data.StartsWith(SavePrefix, StringComparison.Ordinal))
                    Report.Pass(SaveGroup, "Data read back when that save loads", $"slot {e.Slot}: \"{data}\"");
                else if (data != null)
                    Report.Fail(SaveGroup, "Data read back when that save loads", $"slot {e.Slot}: unexpected data \"{data}\"");
                // null: this save was made without the test mod; keep waiting.
            });
        }

        // ---- ModNet ------------------------------------------------------------------------------------------------

        private const string NetGroup = "ModNet";
        private static ModNetChannel _net;
        private static readonly Guid _me = Guid.NewGuid();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static double _sentAt = -1;
        private static bool _arrived;

        internal static void WatchModNet()
        {
            Report.Expect(NetGroup, "A message to the host arrives", "load any game or the designer (single player delivers it on this machine)");
            Report.Expect(NetGroup, "The host answers a client", "multiplayer only: join a host that runs this mod");
            _net = ModNet.Channel(Plugin.ModId, "selftest");
            _net.OnReceive(m =>
            {
                var kind = m.Reader.ReadString();
                var from = m.Reader.ReadGuid();
                var sentAt = m.Reader.ReadDouble();
                double ms = (_clock.Elapsed.TotalSeconds - sentAt) * 1000;
                if (kind == "ping" && from == _me)
                {
                    _arrived = true;
                    Report.Pass(NetGroup, "A message to the host arrives",
                        $"{(m.IsLoopback ? "on this machine (single player or hosting)" : "from " + m.SenderName)} after {ms:0} ms");
                }
                else if (kind == "ping" && m.Sender != null && !m.FromHost)
                {
                    _net.SendTo(m.Sender, new ModNetWriter().Write("echo").Write(from).Write(sentAt)); // a client's ping: answer it
                }
                else if (kind == "echo" && from == _me && m.FromHost)
                {
                    Report.Pass(NetGroup, "The host answers a client", $"{m.SenderName} answered after {ms:0} ms");
                }
            });
        }

        /// <summary>Called every few seconds: sends the test message once ModNet is up, and times it out.</summary>
        internal static void ModNetTick()
        {
            if (_net == null || _arrived) return;
            if (_sentAt < 0)
            {
                if (!Features.IsAvailable(Features.ModNet)) return; // not in a game yet
                if (_net.SendToHost(new ModNetWriter().Write("ping").Write(_me).Write(_clock.Elapsed.TotalSeconds)))
                {
                    _sentAt = _clock.Elapsed.TotalSeconds;
                    Plugin.Log.Verbose("ModNet test message sent.");
                }
                return;
            }
            if (_clock.Elapsed.TotalSeconds - _sentAt > 20)
            {
                Report.Fail(NetGroup, "A message to the host arrives", "sent 20 s ago and not received");
                _arrived = true;
            }
        }

        // ---- lifecycle ---------------------------------------------------------------------------------------------

        internal static void WatchLifecycle()
        {
            Report.Expect("Setup", "Lifecycle.MainMenuEntered", "reach the main menu");
            Lifecycle.MainMenuEntered.Subscribe(Plugin.ModId, a => Report.Seen("Setup", "Lifecycle.MainMenuEntered", "seen"), Priority.Observe, "API test watcher");
        }
    }
}
