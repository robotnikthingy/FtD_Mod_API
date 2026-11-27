using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Timing;
using NetInfrastructure;

namespace FtdModApi
{
    /// <summary>
    /// Messages between the copies of a mod in a multiplayer game, carried by the game's own network messages.
    /// Get a channel with <see cref="Channel"/>, handle what arrives with <see cref="ModNetChannel.OnReceive"/>, and send
    /// with <see cref="ModNetChannel.SendToHost(ModNetWriter)"/> (from anyone) or
    /// <see cref="ModNetChannel.SendToClients(ModNetWriter)"/> (from the host).
    /// <para>
    /// Only machines running FtdModApi take part: when a client joins, the API on each side tells the other which
    /// channels it has, and messages are only sent to machines that have the channel (<see cref="Peers"/>). In single
    /// player this machine is the host, so <c>SendToHost</c> arrives here, through the game's own message code. Set
    /// <see cref="LoopbackClients"/> to also receive here what the host sends to clients, to test both directions alone.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// var ping = ModNet.Channel("MyMod", "ping");
    /// ping.OnReceive(m =>
    /// {
    ///     if (m.FromHost) log.Info("Host says " + m.Reader.ReadString());
    ///     else if (m.Sender != null) ping.SendTo(m.Sender, new ModNetWriter().Write("pong to " + m.SenderName));
    /// });
    /// ping.SendToHost(new ModNetWriter().Write("ping"));
    /// </code>
    /// </example>
    public static class ModNet
    {
        /// <summary>The game network identity the API uses (game ids under 1000 are fixed; the game uses 0 and 50-55).</summary>
        public const uint NetworkIdentityId = 873u;

        /// <summary>Largest message contents, in bytes.</summary>
        public const int MaxMessageBytes = 256 * 1024;

        internal const string ToHostMethod = "FtdModApi_ModNetToHost";
        internal const string ToClientMethod = "FtdModApi_ModNetToClient";
        private const string HelloId = "@FtdModApi/hello";
        private const byte FormatVersion = 1;

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, ModNetChannel> _channels = new Dictionary<string, ModNetChannel>(StringComparer.Ordinal);
        private static readonly Dictionary<uint, ModNetPeer> _peers = new Dictionary<uint, ModNetPeer>();
        private static readonly HashSet<string> _onceKeys = new HashSet<string>();
        private static readonly Endpoint _endpoint = new Endpoint();
        private static INetworkIdentity _identity;
        private static bool _installed;
        private static NetworkType _lastNetType = NetworkType.Disconnected;
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static double _nextHello;
        private static int _hellosSent;
        private static double _nextPeerCheck;
        private static bool _channelsChanged;

        private static bool _hostWithoutModNet;
        private static readonly List<byte[]> _pendingToHost = new List<byte[]>();
        private static readonly Queue<KeyValuePair<string, byte[]>> _localQueue = new Queue<KeyValuePair<string, byte[]>>();
        private static readonly MessageSerialise _serialise = new MessageSerialise();
        private static readonly MessageDeserialise _deserialise = new MessageDeserialise();

        [ThreadStatic] private static bool _localDelivery;

        // ---- public surface ----------------------------------------------------------------------------

        /// <summary>
        /// A channel for one kind of message of a mod. Call on the main thread, ideally in OnLoad; the same mod id and
        /// name give the same channel. Every copy of the mod must use the same names.
        /// </summary>
        public static ModNetChannel Channel(string modId, string name)
        {
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A channel name is required.", nameof(name));
            if (modId.StartsWith("@", StringComparison.Ordinal)) throw new ArgumentException("Mod ids can't start with '@'.", nameof(modId));
            var id = modId + "/" + name;
            lock (_gate)
            {
                if (_channels.TryGetValue(id, out var existing)) return existing;
                var channel = new ModNetChannel(modId, name, id);
                _channels[id] = channel;
                _channelsChanged = true;
                FtdApi.Log.Verbose(() => "ModNet channel " + id + " created.");
                return channel;
            }
        }

        /// <summary>Every channel this machine has.</summary>
        public static IReadOnlyList<ModNetChannel> Channels
        {
            get { lock (_gate) return _channels.Values.ToList(); }
        }

        /// <summary>True in a multiplayer game (as host or client).</summary>
        public static bool IsMultiplayer => Net.IsConnected;

        /// <summary>True on the host, and in single player.</summary>
        public static bool IsHost => !Net.IsClient;

        /// <summary>True on a client in a multiplayer game.</summary>
        public static bool IsClient => Net.IsClient;

        /// <summary>
        /// The other machines running FtdModApi: on the host, the clients that have it; on a client, the host (once it
        /// answered). Empty in single player.
        /// </summary>
        public static IReadOnlyList<ModNetPeer> Peers
        {
            get { lock (_gate) return _peers.Values.ToList(); }
        }

        /// <summary>
        /// For testing alone: on the host or in single player, what is sent to clients is also delivered here as if
        /// this machine were a client (<see cref="ModNetMessage.FromHost"/> true, <see cref="ModNetMessage.IsLoopback"/> true).
        /// Off by default; the tunable "FtdModApi.ModNetLoopback" turns it on too.
        /// </summary>
        public static bool LoopbackClients { get; set; }

        /// <summary>
        /// A machine running FtdModApi connected (after it said which channels it has). Every machine. On the host, this
        /// is the moment to send a new client its starting state: messages sent to a client before this are not delivered.
        /// </summary>
        public static EventChannel<ModNetPeerEvent> PeerJoined { get; } = new EventChannel<ModNetPeerEvent>(
            "Net.PeerJoined", EventSide.All, "A machine running FtdModApi connected.");

        /// <summary>A machine running FtdModApi left (or this machine left the game). Every machine.</summary>
        public static EventChannel<ModNetPeerEvent> PeerLeft { get; } = new EventChannel<ModNetPeerEvent>(
            "Net.PeerLeft", EventSide.All, "A machine running FtdModApi disconnected.");

        // ---- start-up ----------------------------------------------------------------------------------

        internal static void Initialise()
        {
            if (!TryInstall()) Lifecycle.WhenMainMenuReady("FtdModApi", () => TryInstall());
        }

        private static bool TryInstall()
        {
            if (_installed) return true;
            try
            {
                if (GameEvents.UpdateEvent == null) return false;
                GameEvents.UpdateEvent.RegWithEvent(Poll);
                _installed = true;
                EnsureIdentity();
                return true;
            }
            catch (Exception e)
            {
                FtdApi.Log.Warning("ModNet: could not hook the frame update yet (" + e.Message + ").");
                return false;
            }
        }

        private static bool EnsureIdentity()
        {
            if (_identity != null) return true;
            try
            {
                var ids = NetSys.System?.NetworkIdentities;
                if (ids == null) return false;
                var identity = ids.MakeOrGetUniqueIdentity(NetworkIdentityId);
                if (identity == null) return false;
                identity.MonitorThis(_endpoint);
                _identity = identity;
                Features.SetState(Features.ModNet, FeatureState.Available);
                FtdApi.Log.Verbose(() => $"ModNet: listening on network identity {NetworkIdentityId} ({(NetSys.System.IsReal ? "Steam" : "offline")} network system).");
                return true;
            }
            catch (Exception e)
            {
                Features.SetState(Features.ModNet, FeatureState.Unavailable, e.GetType().Name + ": " + e.Message);
                LogOnce("identity", () => FtdApi.Log.Error("ModNet: could not register with the game's network system; mod messages won't work.", e));
                return false;
            }
        }

        // ---- per frame ---------------------------------------------------------------------------------

        private static double Now => _clock.Elapsed.TotalSeconds;

        private static void Poll(ITimeStep step)
        {
            try
            {
                if (!EnsureIdentity()) return;
                DrainLocal();
                var type = Net.NetworkType;
                if (type != _lastNetType)
                {
                    var was = _lastNetType;
                    _lastNetType = type;
                    OnNetworkChanged(was, type);
                }
                if (type == NetworkType.Client)
                {
                    bool haveHost;
                    lock (_gate) haveHost = _peers.Count > 0;
                    if (!haveHost && !_hostWithoutModNet && Now >= _nextHello)
                    {
                        if (_hellosSent >= 10)
                        {
                            _hostWithoutModNet = true;
                            int dropped;
                            lock (_gate)
                            {
                                dropped = _pendingToHost.Count;
                                _pendingToHost.Clear();
                            }
                            FtdApi.Log.Info($"ModNet: the host did not answer; it is probably not running FtdModApi (or a version without ModNet). Mod messages to the host are off{(dropped > 0 ? $" ({dropped} waiting message(s) dropped)" : "")}.");
                        }
                        else
                        {
                            _hellosSent++;
                            _nextHello = Now + 3;
                            SendHello(null);
                        }
                    }
                    else if (haveHost && _channelsChanged)
                    {
                        _channelsChanged = false;
                        SendHello(null);
                    }
                }
                else if (type == NetworkType.Server)
                {
                    if (_channelsChanged)
                    {
                        _channelsChanged = false;
                        foreach (var p in Peers) SendHello(p.Connection);
                    }
                    if (Now >= _nextPeerCheck)
                    {
                        _nextPeerCheck = Now + 1;
                        foreach (var p in Peers)
                        {
                            bool gone;
                            try
                            {
                                gone = p.Connection == null || !p.Connection.IsStillAround;
                            }
                            catch
                            {
                                gone = true;
                            }
                            if (gone) RemovePeer(p);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                LogOnce("poll", () => FtdApi.Log.Error("ModNet: per-frame update failed.", e));
            }
        }

        private static void OnNetworkChanged(NetworkType was, NetworkType now)
        {
            FtdApi.Log.Verbose(() => $"ModNet: network changed from {was} to {now}.");
            foreach (var p in Peers) RemovePeer(p);
            _hellosSent = 0;
            _nextHello = Now + 1; // give the connection a moment
            _channelsChanged = false;
            _hostWithoutModNet = false;
            lock (_gate) _pendingToHost.Clear();
        }

        private static void RemovePeer(ModNetPeer peer)
        {
            bool removed;
            lock (_gate) removed = _peers.Remove(peer.Key);
            if (!removed) return;
            FtdApi.Log.Verbose(() => $"ModNet: {peer.Name} left.");
            if (PeerLeft.HasSubscribers) PeerLeft.Raise(new ModNetPeerEvent(peer));
        }

        // ---- hello -------------------------------------------------------------------------------------

        // Client: to the host (to == null). Host: to one client.
        private static void SendHello(IConnectionData to)
        {
            var w = new ModNetWriter();
            w.Write(FtdApi.Version.ToString());
            var ids = Channels.Select(c => c.Id).ToList();
            w.Write(ids.Count);
            foreach (var id in ids) w.Write(id);
            var payload = Wrap(HelloId, w.ToArray());
            if (to == null) SendRawToHost(payload);
            else SendRawToClient(to, payload);
        }

        private static void ReceiveHello(ModNetReader r, IConnectionData sender, bool fromHost)
        {
            var version = r.ReadString();
            int n = r.ReadInt();
            var channels = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++) channels.Add(r.ReadString());

            var key = KeyOf(sender, fromHost);
            ModNetPeer peer, stale = null;
            bool isNew;
            lock (_gate)
            {
                isNew = !_peers.TryGetValue(key, out peer);
                // The same player reconnected before the old connection was noticed as gone.
                if (!isNew && !fromHost && !ReferenceEquals(peer.Connection, sender))
                {
                    stale = peer;
                    isNew = true;
                }
                if (isNew)
                {
                    peer = new ModNetPeer(key, sender, fromHost);
                    _peers[key] = peer;
                }
                peer.Update(version, channels);
            }
            if (stale != null)
            {
                FtdApi.Log.Verbose(() => $"ModNet: {stale.Name} reconnected.");
                if (PeerLeft.HasSubscribers) PeerLeft.Raise(new ModNetPeerEvent(stale));
            }
            FtdApi.Log.Verbose(() => $"ModNet: {(isNew ? "connected to" : "update from")} {peer.Name} (FtdModApi {version}, {channels.Count} channel(s): {string.Join(", ", channels)}).");
            // The host answers each client's first hello with its own.
            if (!fromHost && isNew) SendHello(sender);
            if (fromHost && isNew) FlushPendingToHost(peer);
            if (isNew && PeerJoined.HasSubscribers) PeerJoined.Raise(new ModNetPeerEvent(peer));
        }

        private static uint KeyOf(IConnectionData sender, bool fromHost)
        {
            if (fromHost) return uint.MaxValue - 1; // the one host
            try
            {
                return sender?.UniqueId ?? uint.MaxValue;
            }
            catch
            {
                return uint.MaxValue;
            }
        }

        // ---- sending -----------------------------------------------------------------------------------

        internal static byte[] Wrap(string channelId, byte[] contents)
        {
            if (contents != null && contents.Length > MaxMessageBytes)
                throw new ArgumentException($"ModNet messages can be at most {MaxMessageBytes} bytes; this one is {contents.Length}.");
            var w = new ModNetWriter();
            w.Write(FormatVersion);
            w.Write(channelId);
            w.Write(contents ?? Array.Empty<byte>());
            return w.ToArray();
        }

        internal static ModNetPeer HostPeer
        {
            get
            {
                lock (_gate) return _peers.TryGetValue(uint.MaxValue - 1, out var p) ? p : null;
            }
        }

        internal static bool SendToHost(ModNetChannel channel, byte[] contents)
        {
            var payload = Wrap(channel.Id, contents);
            if (!MainThread.IsCurrent)
            {
                MainThread.Post(() => SendToHostNow(channel, payload));
                return true;
            }
            return SendToHostNow(channel, payload);
        }

        private static bool SendToHostNow(ModNetChannel channel, byte[] payload)
        {
            if (!EnsureIdentity()) return false;
            if (!Net.IsClient)
            {
                // Host or single player: this machine is the host.
                DeliverLocally(ToHostMethod, payload);
                return true;
            }
            var host = HostPeer;
            if (host == null)
            {
                if (_hostWithoutModNet) return false;
                // Held until the host answers the handshake (then sent if it has the channel).
                lock (_gate)
                {
                    if (_pendingToHost.Count >= 200)
                    {
                        LogOnce("pending-full", () => FtdApi.Log.Warning("ModNet: over 200 messages are waiting for the host to answer; dropping new ones."));
                        return false;
                    }
                    _pendingToHost.Add(payload);
                }
                return true;
            }
            if (!host.Has(channel.Id))
            {
                LogOnce("nohost-" + channel.Id, () => FtdApi.Log.Verbose("ModNet: the host doesn't have channel " + channel.Id + "; not sending."));
                return false;
            }
            return SendRawToHost(payload);
        }

        private static void FlushPendingToHost(ModNetPeer host)
        {
            List<byte[]> pending;
            lock (_gate)
            {
                pending = _pendingToHost.ToList();
                _pendingToHost.Clear();
            }
            foreach (var payload in pending)
            {
                try
                {
                    var r = new ModNetReader(payload);
                    r.ReadByte();
                    if (host.Has(r.ReadString())) SendRawToHost(payload);
                }
                catch
                {
                }
            }
        }

        private static bool SendRawToHost(byte[] payload)
        {
            if (!EnsureIdentity()) return false;
            try
            {
                NetSys.System.SendMessageToHost(_identity, ToHostMethod, payload);
                return true;
            }
            catch (Exception e)
            {
                LogOnce("send-host", () => FtdApi.Log.Error("ModNet: sending to the host failed.", e));
                return false;
            }
        }

        private static bool SendRawToClient(IConnectionData client, byte[] payload)
        {
            if (!EnsureIdentity() || client == null) return false;
            try
            {
                NetSys.System.SendMessageFromHostToOnePlayer(_identity, ToClientMethod, client, payload);
                return true;
            }
            catch (Exception e)
            {
                LogOnce("send-client", () => FtdApi.Log.Error("ModNet: sending to a client failed.", e));
                return false;
            }
        }

        // Host only. except: a client to leave out; only: the one client to send to.
        internal static int SendToClients(ModNetChannel channel, byte[] contents, IConnectionData only, IConnectionData except)
        {
            var payload = Wrap(channel.Id, contents);
            if (!MainThread.IsCurrent)
            {
                MainThread.Post(() => SendToClientsNow(channel, payload, only, except));
                return 0;
            }
            return SendToClientsNow(channel, payload, only, except);
        }

        private static int SendToClientsNow(ModNetChannel channel, byte[] payload, IConnectionData only, IConnectionData except)
        {
            if (Net.IsClient)
            {
                LogOnce("client-send-" + channel.Id, () => FtdApi.Log.Warning($"ModNet: {channel.Id}: only the host can send to clients; use SendToHost on a client."));
                return 0;
            }
            if (!EnsureIdentity()) return 0;
            int sent = 0;
            if (Net.IsServer)
            {
                foreach (var peer in Peers)
                {
                    if (peer.IsHost || !peer.Has(channel.Id)) continue;
                    bool alive;
                    try
                    {
                        alive = peer.Connection != null && peer.Connection.IsStillAround;
                    }
                    catch
                    {
                        alive = false;
                    }
                    if (!alive)
                    {
                        RemovePeer(peer);
                        continue;
                    }
                    if (only != null && !SameConnection(peer.Connection, only)) continue;
                    if (except != null && SameConnection(peer.Connection, except)) continue;
                    if (SendRawToClient(peer.Connection, payload)) sent++;
                }
            }
            if (LoopbackClients && only == null) DeliverLocally(ToClientMethod, payload);
            return sent;
        }

        private static bool SameConnection(IConnectionData a, IConnectionData b)
        {
            if (ReferenceEquals(a, b)) return true;
            try
            {
                return a != null && b != null && a.UniqueId == b.UniqueId;
            }
            catch
            {
                return false;
            }
        }

        // Next frame, through the game's own message code: serialised by the game, then dispatched by it to our
        // identity like a message from the network. Everything but the actual Steam connection.
        private static void DeliverLocally(string method, byte[] payload)
        {
            lock (_gate)
            {
                if (_localQueue.Count >= 1000)
                {
                    LogOnce("local-full", () => FtdApi.Log.Warning("ModNet: over 1000 local messages are waiting; dropping new ones (is a handler answering itself in a loop?)."));
                    return;
                }
                _localQueue.Enqueue(new KeyValuePair<string, byte[]>(method, payload));
            }
        }

        // Once a frame: only what was queued before this frame, so a handler that answers itself waits a frame.
        private static void DrainLocal()
        {
            int n;
            lock (_gate) n = _localQueue.Count;
            for (int i = 0; i < n; i++)
            {
                KeyValuePair<string, byte[]> item;
                lock (_gate)
                {
                    if (_localQueue.Count == 0) return;
                    item = _localQueue.Dequeue();
                }
                DeliverNow(item.Key, item.Value);
            }
        }

        private static void DeliverNow(string method, byte[] payload)
        {
            _localDelivery = true;
            try
            {
                var bytes = _serialise.Serialise(_identity, method, new object[] { payload });
                double time = 0;
                try
                {
                    time = NetSys.System.Timing?.NetworkTime ?? 0;
                }
                catch
                {
                }
                IConnectionData me = null;
                try
                {
                    me = NetSys.System.MyConnection;
                }
                catch
                {
                }
                int before = _endpoint.Calls;
                _deserialise.DeserialiseAndInvoke(NetSys.System.NetworkIdentities, null, bytes, new MessageInfo(me, time));
                if (_endpoint.Calls == before)
                {
                    // The game didn't reach us (logged by the game as an info line); deliver directly.
                    LogOnce("local-path", () => FtdApi.Log.Warning("ModNet: the game's message code did not deliver a local message; delivering it directly. Multiplayer messages may not work."));
                    Receive(payload, null, method == ToClientMethod);
                }
            }
            catch (Exception e)
            {
                LogOnce("local", () => FtdApi.Log.Error("ModNet: delivering a local message failed.", e));
            }
            finally
            {
                _localDelivery = false;
            }
        }

        // ---- receiving ---------------------------------------------------------------------------------

        // The game calls these (by name) when a message for our identity arrives.
        private sealed class Endpoint
        {
            internal int Calls;

            [NetworkMethod]
            public void FtdModApi_ModNetToHost(byte[] data, MessageInfo info)
            {
                Calls++;
                if (Net.IsClient && !_localDelivery) return; // not for us
                Receive(data, info?.Sender, false);
            }

            [NetworkMethod]
            public void FtdModApi_ModNetToClient(byte[] data, MessageInfo info)
            {
                Calls++;
                if (!Net.IsClient && !_localDelivery) return; // not for us
                Receive(data, info?.Sender, true);
            }
        }

        private static void Receive(byte[] data, IConnectionData sender, bool fromHost)
        {
            string channelId = null;
            try
            {
                var r = new ModNetReader(data);
                var format = r.ReadByte();
                if (format != FormatVersion)
                {
                    LogOnce("format-" + format, () => FtdApi.Log.Warning($"ModNet: a message in format {format} arrived (this FtdModApi reads format {FormatVersion}); update FtdModApi on every machine."));
                    return;
                }
                channelId = r.ReadString();
                var contents = r.ReadBytes();
                bool loopback = _localDelivery;
                if (channelId == HelloId)
                {
                    if (!loopback) ReceiveHello(new ModNetReader(contents), sender, fromHost);
                    return;
                }
                ModNetChannel channel;
                lock (_gate) _channels.TryGetValue(channelId, out channel);
                if (channel == null)
                {
                    var id = channelId;
                    LogOnce("unknown-" + id, () => FtdApi.Log.Verbose("ModNet: a message for channel " + id + " arrived, but no mod here uses it."));
                    return;
                }
                channel.Dispatch(new ModNetMessage(channel, contents, loopback ? null : sender, fromHost, loopback));
            }
            catch (Exception e)
            {
                var id = channelId ?? "?";
                LogOnce("receive-" + id, () => FtdApi.Log.Error("ModNet: a message for " + id + " could not be read.", e));
            }
        }

        internal static void LogOnce(string key, Action log)
        {
            lock (_onceKeys)
            {
                if (_onceKeys.Count >= 500 || !_onceKeys.Add(key)) return;
            }
            log();
        }
    }

    /// <summary>One kind of message of a mod. Get it with <see cref="ModNet.Channel"/>.</summary>
    public sealed class ModNetChannel
    {
        private readonly List<KeyValuePair<string, Action<ModNetMessage>>> _handlers = new List<KeyValuePair<string, Action<ModNetMessage>>>();

        internal ModNetChannel(string modId, string name, string id)
        {
            ModId = modId;
            Name = name;
            Id = id;
        }

        public string ModId { get; }

        public string Name { get; }

        /// <summary>"ModId/Name".</summary>
        public string Id { get; }

        /// <summary>
        /// Runs the handler for every message that arrives on this channel, on the main thread. A handler that throws
        /// is logged (once) and the others still run.
        /// </summary>
        public void OnReceive(Action<ModNetMessage> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (_handlers) _handlers.Add(new KeyValuePair<string, Action<ModNetMessage>>(handler.Method.Name, handler));
        }

        internal void Dispatch(ModNetMessage message)
        {
            KeyValuePair<string, Action<ModNetMessage>>[] handlers;
            lock (_handlers) handlers = _handlers.ToArray();
            foreach (var h in handlers)
            {
                try
                {
                    message.ResetReader();
                    h.Value(message);
                }
                catch (Exception e)
                {
                    ModNet.LogOnce("handler-" + Id + "-" + h.Key, () => FtdApi.GetLog(ModId).Error($"ModNet {Id}: a message handler threw (reported once).", e));
                }
            }
        }

        /// <summary>
        /// Sends to the host. On the host and in single player it arrives on this machine (next frame). On a client that
        /// just joined, it waits until the host answered the handshake. False when it won't be sent (the host doesn't have
        /// this channel or doesn't run FtdModApi). True means handed to the game, not delivered. Any thread (off the main
        /// thread it is queued and returns true).
        /// </summary>
        public bool SendToHost(ModNetWriter message) => ModNet.SendToHost(this, message?.ToArray());

        public bool SendToHost(byte[] message) => ModNet.SendToHost(this, message);

        /// <summary>
        /// Host only: sends to every client that has this channel. Returns how many clients it was handed to (0 off the
        /// main thread, where it is queued). Clients that haven't finished the handshake are skipped: send starting
        /// state from <see cref="ModNet.PeerJoined"/>.
        /// </summary>
        public int SendToClients(ModNetWriter message) => ModNet.SendToClients(this, message?.ToArray(), null, null);

        public int SendToClients(byte[] message) => ModNet.SendToClients(this, message, null, null);

        /// <summary>Host only: sends to every client that has this channel except one (e.g. the one that sent it).</summary>
        public int SendToClientsExcept(IConnectionData except, ModNetWriter message) => ModNet.SendToClients(this, message?.ToArray(), null, except);

        /// <summary>Host only: sends to one client (e.g. <see cref="ModNetMessage.Sender"/>). True when sent.</summary>
        public bool SendTo(IConnectionData client, ModNetWriter message) => SendTo(client, message?.ToArray());

        /// <summary>Host only: sends raw bytes to one client.</summary>
        public bool SendTo(IConnectionData client, byte[] message)
        {
            if (client == null) throw new ArgumentNullException(nameof(client), "No client: a message from this machine has no Sender (check m.Sender != null).");
            return ModNet.SendToClients(this, message, client, null) > 0;
        }

        /// <summary>Host only: sends raw bytes to every client that has this channel except one.</summary>
        public int SendToClientsExcept(IConnectionData except, byte[] message) => ModNet.SendToClients(this, message, null, except);
    }

    /// <summary>A message that arrived on a channel.</summary>
    public sealed class ModNetMessage
    {
        internal ModNetMessage(ModNetChannel channel, byte[] contents, IConnectionData sender, bool fromHost, bool loopback)
        {
            Channel = channel;
            Bytes = contents;
            Reader = new ModNetReader(contents);
            Sender = sender;
            FromHost = fromHost;
            IsLoopback = loopback;
        }

        public ModNetChannel Channel { get; }

        /// <summary>Reads the contents (from the start for each handler).</summary>
        public ModNetReader Reader { get; private set; }

        /// <summary>The raw contents.</summary>
        public byte[] Bytes { get; }

        /// <summary>
        /// Who sent it: on the host, the client (use it with <see cref="ModNetChannel.SendTo(IConnectionData, ModNetWriter)"/> to answer); on a client,
        /// the host. Null when it came from this machine.
        /// </summary>
        public IConnectionData Sender { get; }

        /// <summary>The sender's player name, or "this machine".</summary>
        public string SenderName
        {
            get
            {
                try
                {
                    return Sender?.DisplayName ?? "this machine";
                }
                catch
                {
                    return "unknown";
                }
            }
        }

        /// <summary>True when the host sent it (it arrived on a client, or by <see cref="ModNet.LoopbackClients"/>).</summary>
        public bool FromHost { get; }

        /// <summary>True when it never left this machine (single player, the host's own SendToHost, or loopback).</summary>
        public bool IsLoopback { get; }

        internal void ResetReader() => Reader = new ModNetReader(Bytes);
    }

    /// <summary>Another machine running FtdModApi.</summary>
    public sealed class ModNetPeer
    {
        private readonly object _lock = new object();
        private HashSet<string> _channels = new HashSet<string>(StringComparer.Ordinal);

        internal ModNetPeer(uint key, IConnectionData connection, bool isHost)
        {
            Key = key;
            Connection = connection;
            IsHost = isHost;
        }

        internal uint Key { get; }

        /// <summary>The game's connection for the machine. Game object.</summary>
        public IConnectionData Connection { get; }

        /// <summary>True for the host (seen from a client).</summary>
        public bool IsHost { get; }

        public string Name
        {
            get
            {
                try
                {
                    return Connection?.DisplayName ?? (IsHost ? "the host" : "a client");
                }
                catch
                {
                    return IsHost ? "the host" : "a client";
                }
            }
        }

        /// <summary>Its FtdModApi version.</summary>
        public string ApiVersion { get; private set; }

        /// <summary>The channels it has ("ModId/Name").</summary>
        public IReadOnlyCollection<string> Channels
        {
            get { lock (_lock) return _channels.ToList(); }
        }

        /// <summary>True when it has the channel.</summary>
        public bool Has(string channelId)
        {
            lock (_lock) return _channels.Contains(channelId);
        }

        /// <summary>True when it has the channel.</summary>
        public bool Has(ModNetChannel channel) => channel != null && Has(channel.Id);

        /// <summary>True when it has any channel of the mod (a rough "has this mod" check).</summary>
        public bool HasMod(string modId)
        {
            var prefix = modId + "/";
            lock (_lock) return _channels.Any(c => c.StartsWith(prefix, StringComparison.Ordinal));
        }

        internal void Update(string version, HashSet<string> channels)
        {
            lock (_lock)
            {
                ApiVersion = version;
                _channels = channels;
            }
        }
    }

    /// <summary>A machine running FtdModApi connected or left.</summary>
    public sealed class ModNetPeerEvent : GameEvent
    {
        internal ModNetPeerEvent(ModNetPeer peer)
        {
            Peer = peer;
        }

        public ModNetPeer Peer { get; }
    }
}
