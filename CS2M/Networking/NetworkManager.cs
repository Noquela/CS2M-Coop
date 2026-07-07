using CS2M.API;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands;
using CS2M.Commands.ApiServer;
using CS2M.Util;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using CS2M.Commands.Data.Internal;
using CS2M.Commands.Handler.Internal;
using Timer = System.Timers.Timer;

namespace CS2M.Networking
{
    public class NetworkManager
    {
        private const string ConnectionKey = "CSM";

        // v50: per-player latency as LiteNetLib reports it (playerId = peer.Id + 1, matching the
        // relay's SenderId convention). Feeds the host's ~1 Hz roster broadcast.
        private static readonly Dictionary<int, int> _latencyByPlayerId = new Dictionary<int, int>();
        private static readonly object _latencyLock = new object();

        public static List<KeyValuePair<int, int>> LatencySnapshot()
        {
            lock (_latencyLock)
            {
                return new List<KeyValuePair<int, int>>(_latencyByPlayerId);
            }
        }

        private readonly NetManager _netManager;
        private readonly ApiServer _apiServer;
        private ConnectionConfig _connectionConfig;
        private IPEndPoint _connectEndpoint;
        private Timer _timeout;
        private bool _pollNatEvent = false;

        public event OnNatHolePunchSuccessful NatHolePunchSuccessfulEvent;
        public event OnNatHolePunchFailed NatHolePunchFailedEvent;
        public event OnClientConnectSuccessful ClientConnectSuccessfulEvent;
        public event OnClientConnectFailed ClientConnectFailedEvent;
        public event OnClientDisconnect ClientDisconnectEvent;

        public NetworkManager()
        {
            // Set up network items
            EventBasedNetListener listener = new EventBasedNetListener();
            _netManager = new NetManager(listener)
            {
                NatPunchEnabled = true,
                UnconnectedMessagesEnabled = true,
                MtuDiscovery = true,
            };
            _apiServer = new ApiServer(_netManager);

            // Listen to events
            listener.NetworkReceiveEvent += ListenerOnNetworkReceiveEvent;
            listener.NetworkErrorEvent += ListenerOnNetworkErrorEvent;
            listener.PeerConnectedEvent += ListenerOnPeerConnectedEvent;
            listener.PeerDisconnectedEvent += ListenerOnPeerDisconnectedEvent;
            listener.NetworkLatencyUpdateEvent += ListenerOnNetworkLatencyUpdateEvent;
            listener.ConnectionRequestEvent += ListenerOnConnectionRequestEvent;
        }

        public bool InitConnect(ConnectionConfig connectionConfig)
        {
            Log.Trace("NetworkManager: InitConnect");

            if (connectionConfig.IsTokenBased())
            {
                Log.Info($"Initialize connect to server {connectionConfig.Token}...");
            }
            else
            {
                Log.Info($"Initialize connect to server at {connectionConfig.HostAddress}:{connectionConfig.Port}...");
            }

            _connectionConfig = connectionConfig;

            bool result = _netManager.Start();
            if (!result)
            {
                Log.Error("The client failed to start.");
                return false;
            }

            return true;
        }

        public bool SetupNatConnect()
        {
            Log.Trace("NetworkManager: Setting up NAT connect");

            IPEndPoint directEndpoint = null;
            if (!_connectionConfig.IsTokenBased())
            {
                // Given string to IP address (resolves domain names).
                try
                {
                    directEndpoint = IPUtil.CreateIPEndPoint(_connectionConfig.HostAddress, _connectionConfig.Port);
                }
                catch
                {
                    return false;
                }
            }

            _pollNatEvent = true;

            EventBasedNatPunchListener natPunchListener = new EventBasedNatPunchListener();
            _timeout = new Timer
            {
                Interval = 5000,
                AutoReset = false
            };
            _timeout.Elapsed += (sender, args) =>
            {
                Log.Debug("NAT hole punch failed, trying direct connect");
                _pollNatEvent = false;
                _connectEndpoint = directEndpoint;
                NatHolePunchFailedEvent?.Invoke();
            };

            // Callback on for each possible IP address to connect to the server.
            // Can potentially be called multiple times (local and public IP address).
            natPunchListener.NatIntroductionSuccess += (point, type, token) =>
            {
                Log.Debug($"NAT hole punch successful ({point.Address}:{point.Port})");
                _pollNatEvent = false;
                _connectEndpoint = point;
                bool? eventResult = NatHolePunchSuccessfulEvent?.Invoke();
                if (eventResult != null && eventResult.Value)
                {
                    _timeout.Enabled = false;
                }
            };

            string connect = "";
            if (_connectionConfig.IsTokenBased())
            {
                connect = "token:" + _connectionConfig.Token;
            }
            else if (directEndpoint != null)
            {
                connect = "ip:" + directEndpoint.Address;
            }

            // Register listener and send request to global server
            _netManager.NatPunchModule.Init(natPunchListener);
            try
            {
                Log.Trace("NetworkManager: Start NAT hole punch");
                _netManager.NatPunchModule.SendNatIntroduceRequest(
                    IPUtil.CreateIP4EndPoint(Mod.Instance.Settings.ApiServer, Mod.Instance.Settings.GetApiServerPort()),
                    connect);
                _timeout.Start();
            }
            catch (Exception e)
            {
                Log.Warn(
                    $"Could not send NAT introduction request to API server at {Mod.Instance.Settings.ApiServer}:{Mod.Instance.Settings.ApiServerPort}: {e}");
            }

            return true;
        }

        public bool Connect()
        {
            if (_connectEndpoint == null)
            {
                Log.Error("No valid endpoint to connect to.");
                return false;
            }

            Log.Debug($"Connect to {_connectEndpoint.Address}:{_connectEndpoint.Port}");

            try
            {
                _timeout = new Timer
                {
                    Interval = 5000,
                    AutoReset = false
                };
                _timeout.Elapsed += (sender, args) =>
                {
                    Log.Debug($"Connect to client ({_connectEndpoint.Address}:{_connectEndpoint.Port}) failed");
                    ClientConnectFailedEvent?.Invoke();
                };

                _netManager.Connect(_connectEndpoint, ConnectionKey);
                _timeout.Start();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to connect to {_connectEndpoint.Address}:{_connectEndpoint.Port}", ex);
                return false;
            }

            return true;
        }

        public string GetConnectionPassword()
        {
            return _connectionConfig.Password;
        }

        private void ListenerOnNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel,
            DeliveryMethod deliveryMethod)
        {
            // Issue #2: per-packet guard. Without it a single malformed payload (or an exception
            // inside a handler's Parse) threw out of PollEvents — the packet (and the rest of that
            // frame's queue) was silently lost, which is itself a desync. Log and drop instead;
            // law 9 ("a bad command must never kill a system") applied to the outermost gate.
            try
            {
                ReceiveOne(peer, reader);
            }
            catch (Exception ex)
            {
                Log.Error($"NetworkManager: dropping packet from peer {peer.Id}: {ex}");
            }
        }

        private void ReceiveOne(NetPeer peer, NetPacketReader reader)
        {
            CommandBase command = CommandInternal.Instance.Deserialize(reader.GetRemainingBytes());
            // v52 wire-tap IN: the single point every received packet funnels through, with the peer
            // id so the host can attribute who sent what. Inert unless CS2M_WIRETAP=1; never throws.
            if (WireTap.Enabled)
            {
                WireTap.Record("IN", command, peer.Id);
            }

            CommandHandler handler = CommandInternal.Instance.GetCommandHandler(command.GetType());
            // Issue #2: unknown command type (protocol skew that slipped past the version handshake)
            // returned a silent null and NRE'd below — reject loudly instead.
            if (handler == null)
            {
                Log.Error($"NetworkManager: no handler for command type {command.GetType()} " +
                          $"[PeerId: {peer.Id}] — dropping (protocol mismatch?)");
                return;
            }

            Log.Trace($"NetworkManager: OnNetworkReceiveEvent [PeerId: {peer.Id}] {command.GetType()}");
            if (command is PreconditionsCheckCommand)
            {
                ((PreconditionsCheckHandler)handler).HandleOnServer((PreconditionsCheckCommand)command, peer);
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER &&
                !NetworkInterface.Instance.IsPeerConnected(peer))
            {
                return;
            }

            //TODO: Check that only the relevant command could be sent in connected, not joined state

            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                // v43: stamp a stable per-connection identity (0 = the host itself). Without this
                // every remote player was SenderId 0 — with 3+ players their cursors/identities
                // collided. Stamped BEFORE the relay so all clients agree on who did what.
                command.SenderId = peer.Id + 1;

                // v43: the missing star-topology relay. Clients only talk to the host, so the host
                // must rebroadcast gameplay commands to every OTHER client — with only 2 players
                // this was invisible, with 3+ nothing a client did ever reached the other clients.
                if (handler.RelayOnServer)
                {
                    byte[] relayBytes = CommandInternal.Instance.Serialize(command);
                    foreach (NetPeer other in _netManager.ConnectedPeerList)
                    {
                        if (other != peer)
                        {
                            other.Send(relayBytes, DeliveryMethod.ReliableOrdered);
                        }
                    }
                }
            }

            handler.Parse(command);
        }

        private void ListenerOnPeerConnectedEvent(NetPeer peer)
        {
            Log.Trace($"NetworkManager: OnPeerConnectedEvent [PeerId: {peer.Id}]");
            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.CLIENT)
            {
                _timeout.Enabled = false;
                ClientConnectSuccessfulEvent?.Invoke();
            }
            else if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                _timeout = new Timer
                {
                    Interval = 5000,
                    AutoReset = false
                };
                _timeout.Elapsed += (sender, args) =>
                {
                    if (NetworkInterface.Instance.GetPlayerByPeer(peer) == null)
                    {
                        Log.Warn(
                            $"Client peer {peer.Id} did not register within {_timeout.Interval / 1000} seconds. Disconnecting peer.");
                        peer.Disconnect();
                    }
                };
                _timeout.Start();
            }
        }

        private void ListenerOnPeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Log.Trace($"NetworkManager: OnPeerDisconnectedEvent [PeerId: {peer.Id}]");
            lock (_latencyLock)
            {
                _latencyByPlayerId.Remove(peer.Id + 1);
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.CLIENT)
            {
                // TODO: Use disconnect info
                ClientDisconnectEvent?.Invoke();
            }
            else if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                NetworkInterface.Instance.GetPlayerByPeer(peer)?.HandleDisconnect();
                // Issue #3: remove the player from the connected/joined lists so the username can
                // reconnect and ResyncAll stops iterating dead peers.
                NetworkInterface.Instance.PlayerDisconnected(peer);
            }
        }

        private void ListenerOnNetworkErrorEvent(IPEndPoint endpoint, SocketError socketError)
        {
            string source = endpoint != null ? $"{endpoint.Address}:{endpoint.Port}" : "<Unconnected>";
            Log.Error($"Received an error from {source}. Code: {socketError}");
        }

        private void ListenerOnNetworkLatencyUpdateEvent(NetPeer peer, int latency)
        {
            Log.Trace($"NetworkManager: OnNetworkLatencyUpdateEvent [PeerId: {peer.Id}, Latency: {latency}]");
            lock (_latencyLock)
            {
                _latencyByPlayerId[peer.Id + 1] = latency;
            }
        }

        private void ListenerOnConnectionRequestEvent(ConnectionRequest request)
        {
            Log.Trace("NetworkManager: OnConnectionRequestEvent");
            request.AcceptIfKey(ConnectionKey);
        }

        public delegate bool OnNatHolePunchSuccessful();

        public delegate bool OnNatHolePunchFailed();

        public delegate bool OnClientConnectSuccessful();

        public delegate bool OnClientConnectFailed();

        public delegate bool OnClientDisconnect();

        public void ProcessEvents()
        {
            // Poll for new events
            if (_pollNatEvent)
            {
                _netManager.NatPunchModule.PollEvents();
            }

            _netManager.PollEvents();
            // Trigger keepalive to api server
            _apiServer.KeepAlive(_connectionConfig);
        }

        public void SendToAllClients(CommandBase message)
        {
            _netManager.SendToAll(CommandInternal.Instance.Serialize(message), DeliveryMethod.ReliableOrdered);

            Log.Debug($"Sending {message.GetType().Name} to all clients");
        }

        public void SendToClient(NetPeer peer, CommandBase message)
        {
            peer.Send(CommandInternal.Instance.Serialize(message), DeliveryMethod.ReliableOrdered);

            if (message is WorldTransferCommand { NewTransfer: false })
            {
                // Due to performance reasons, log "WorldTransferCommand" only on trace level (after logging once)
                Log.Trace($"Sending {message.GetType().Name} to client at {peer.Address}:{peer.Port}");
            }
            else
            {
                Log.Debug($"Sending {message.GetType().Name} to client at {peer.Address}:{peer.Port}");
            }
        }

        public void SendToServer(CommandBase message)
        {
            // Guard the empty peer list: PlayerCursorSystem (and any per-frame sender) calls this every
            // Rendering tick, so the instant a client drops — or during the reconnect window — the peer
            // list is empty and ConnectedPeerList[0] threw ArgumentOutOfRangeException, taking the whole
            // process down mid-session (host crash observed 06/07 when the client blinked). Nothing to
            // send to = no-op, not a crash.
            System.Collections.Generic.List<NetPeer> peers = _netManager.ConnectedPeerList;
            if (peers == null || peers.Count == 0)
            {
                return;
            }

            peers[0].Send(CommandInternal.Instance.Serialize(message), DeliveryMethod.ReliableOrdered);

            Log.Debug($"Sending {message.GetType().Name} to server");
        }

        public void SendToApiServer(ApiCommandBase message)
        {
            _apiServer.SendCommand(message);
            Log.Debug($"Sending {message.GetType().Name} to api server");
        }

        public bool StartServer(ConnectionConfig connectionConfig)
        {
            _connectionConfig = connectionConfig;

            // Let the user know that we are trying to start the server
            Log.Info($"Attempting to start server on port {_connectionConfig.Port}...");

            // Attempt to start the server
            bool result = _netManager.Start(_connectionConfig.Port);

            // If the server has not started, tell the user and return false.
            if (!result)
            {
                Log.Error("The server failed to start.");
                Stop(); // Make sure the server is fully stopped
                return false;
            }

            //TODO: NAT UPnP open Port
            //TODO: Check if port is reachable

            // Update the console to let the user know the server is running
            Log.Info("The server has started.");
            Chat.Instance.PrintGameMessage("CS2M.NetworkManager.ServerStarted".Translate());

            return true;
        }

        public void Stop()
        {
            _netManager.Stop();
            // Issue #3: on an abrupt shutdown the per-peer removal in the disconnect listener never
            // ran — the static map would leak ghost latencies into the next session's roster.
            lock (_latencyLock)
            {
                _latencyByPlayerId.Clear();
            }

            Log.Info("NetworkManager stopped.");
        }
    }
}
