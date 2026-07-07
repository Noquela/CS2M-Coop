using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Colossal;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands;
using CS2M.Commands.ApiServer;
using CS2M.Commands.Data.Game;
using CS2M.Commands.Data.Internal;
using CS2M.Helpers;
using LiteNetLib;
using Unity.Entities;

namespace CS2M.Networking
{
    public class NetworkInterface
    {
        public delegate void OnPlayerConnected(Player player);

        public delegate void OnPlayerDisconnected(Player player);

        public delegate void OnPlayerJoined(Player player);

        public delegate void OnPlayerLeft(Player player);

        private static NetworkInterface _instance;

        public readonly LocalPlayer LocalPlayer = new();

        /// <summary>
        ///     List of all players, which are connected on network level
        /// </summary>
        public List<Player> PlayerListConnected = new();

        /// <summary>
        ///     List of all players, which are connected on game level
        /// </summary>
        public List<Player> PlayerListJoined = new();

        /// <summary>
        ///     Issue #14 (host side): every SyncId-namespace nonce already in the session (the host's
        ///     own + each admitted client's). PreconditionsCheckHandler assigns a fresh nonce to a
        ///     joining client whose draw collides. Reset with the player lists.
        /// </summary>
        public readonly HashSet<ulong> SessionNonces = new();

        public NetworkInterface()
        {
            PlayerListConnected.Add(LocalPlayer);
            PlayerListJoined.Add(LocalPlayer);
        }

        public static NetworkInterface Instance => _instance ??= new NetworkInterface();

        /// <summary>
        ///     Event is triggered, when a player is connected on the network level
        /// </summary>
        public event OnPlayerConnected PlayerConnectedEvent;

        /// <summary>
        ///     Event is triggered, when a player disconnects on the network level
        /// </summary>
        public event OnPlayerDisconnected PlayerDisconnectedEvent;

        /// <summary>
        ///     Event is triggered, when a player joins on the game level
        /// </summary>
        public event OnPlayerJoined PlayerJoinedEvent;

        /// <summary>
        ///     Event is triggered, when a player leaves on the game level
        /// </summary>
        public event OnPlayerLeft PlayerLeftEvent;

        public void OnUpdate()
        {
            LocalPlayer.OnUpdate();
        }

        public void Connect(ConnectionConfig connectionConfig)
        {
            LocalPlayer.GetServerInfo(connectionConfig);
        }

        public void UpdateLocalPlayerUsername(string username)
        {
            LocalPlayer.UpdateUsername(username);
        }

        public void StartServer(ConnectionConfig connectionConfig)
        {
            LocalPlayer.Playing(connectionConfig);
        }

        public void StopServer()
        {
            // User-initiated stop/leave — must not trigger the auto-reconnect cycle.
            LocalPlayer.UserDisconnect();
        }

        public void SendToAll(CommandBase message)
        {
            LocalPlayer.SendToAll(message);
        }

        public void SendToClient(Player player, CommandBase message)
        {
            if (player is RemotePlayer remotePlayer)
            {
                LocalPlayer.SendToClient(remotePlayer.NetPeer, message);
            }
            else
            {
                Log.Warn("Trying to send packet to non-csm player, ignoring.");
            }
        }

        public void SendToServer(CommandBase message)
        {
            LocalPlayer.SendToServer(message);
        }

        public void SendToApiServer(ApiCommandBase message)
        {
            LocalPlayer.SendToApiServer(message);
        }

        public void SendToClients(CommandBase message)
        {
            LocalPlayer.SendToClients(message);
        }

        public RemotePlayer GetPlayerByPeer(NetPeer peer)
        {
            return PlayerListConnected
                .Where(p => p is RemotePlayer)
                .Cast<RemotePlayer>()
                .FirstOrDefault(p => p.NetPeer.Id == peer.Id);
        }

        public bool IsPeerConnected(NetPeer peer)
        {
            return PlayerListConnected
                .Where(p => p is RemotePlayer)
                .Cast<RemotePlayer>()
                .Any(p => p.NetPeer.Id == peer.Id);
        }

        public bool IsPeerJoined(NetPeer peer)
        {
            return PlayerListJoined
                .Where(p => p is RemotePlayer)
                .Cast<RemotePlayer>()
                .Any(p => p.NetPeer.Id == peer.Id);
        }

        public void PlayerConnected(RemotePlayer player)
        {
            Log.Debug($"RemotePlayer '{player.Username}' connected.");
            PlayerListConnected.Add(player);
            PlayerConnectedEvent?.Invoke(player);
            SendWorldTo(player);
        }

        /// <summary>
        ///     Host-side cleanup when a client peer drops (issue #3): without this the ghost entry
        ///     blocked the same username from ever reconnecting (PreconditionsCheckHandler) and
        ///     ResyncAll kept streaming the world to a dead NetPeer.
        /// </summary>
        public void PlayerDisconnected(NetPeer peer)
        {
            RemotePlayer player = GetPlayerByPeer(peer);
            if (player == null)
            {
                return;
            }

            Log.Info($"RemotePlayer '{player.Username}' disconnected (peer {peer.Id}).");
            PlayerListConnected.Remove(player);
            PlayerListJoined.Remove(player);
            PlayerDisconnectedEvent?.Invoke(player);
        }

        /// <summary>
        ///     Session teardown (issue #3): the singleton (and its lists) outlives the session, so
        ///     drop every remote entry and keep only the local player. Called from LocalPlayer.Inactive().
        /// </summary>
        public void ResetPlayerLists()
        {
            PlayerListConnected.RemoveAll(p => !ReferenceEquals(p, LocalPlayer));
            PlayerListJoined.RemoveAll(p => !ReferenceEquals(p, LocalPlayer));
            SessionNonces.Clear();
        }

        // Issue #15: monotonically bumped on session teardown. The async world-transfer task captures
        // the value at start and aborts between slices when it changes — without this, a "stop server"
        // mid-transfer kept calling peer.Send on a stopped NetManager from a background task.
        private static int _transferEpoch;

        /// <summary>Abort every in-flight world transfer (session teardown).</summary>
        public static void CancelWorldTransfers()
        {
            _transferEpoch++;
        }

        /// <summary>Serializes the current world and streams it to one client as WorldTransferCommand slices.</summary>
        public void SendWorldTo(RemotePlayer player)
        {
            // Get max packet size from MTU discovery
            int maxPacketSize = player.NetPeer.GetMaxSinglePacketSize(DeliveryMethod.ReliableOrdered);
            maxPacketSize -= 25; // Maximum packet overhead as computed and tested in `PacketSizeOverhead` unit test

            int epoch = _transferEpoch; // issue #15: this transfer belongs to the CURRENT session
            TaskManager.instance.EnqueueTask("LoadMap", async () =>
            {
                try
                {
                    SaveLoadHelper saveLoadHelper =
                        World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SaveLoadHelper>();
                    SlicedPacketStream stream = await saveLoadHelper.SaveGame(maxPacketSize);
                    int remainingBytes = (int)stream.Length;
                    bool newTransfer = true;

                    var watch = new Stopwatch();
                    watch.Start();

                    Log.Debug($"Sending world with size of {stream.Length} bytes. Slice size: {maxPacketSize}");
                    foreach (byte[] slice in stream.GetSlices())
                    {
                        // Issue #15: abort between slices when the session ended or THIS peer dropped
                        // (a several-second transfer easily outlives either).
                        if (epoch != _transferEpoch)
                        {
                            Log.Info("[Transfer] aborted — session ended mid-transfer");
                            return;
                        }

                        if (player.NetPeer.ConnectionState != ConnectionState.Connected)
                        {
                            Log.Info($"[Transfer] aborted — peer {player.NetPeer.Id} disconnected mid-transfer");
                            return;
                        }

                        remainingBytes -= slice.Length;
                        var cmd = new WorldTransferCommand
                        {
                            WorldSlice = slice,
                            RemainingBytes = remainingBytes,
                            NewTransfer = newTransfer,
                        };

                        CommandInternal.Instance.SendToClient(player, cmd);

                        newTransfer = false;
                    }

                    Log.Debug($"[SaveGame] Save game packaging took {watch.ElapsedMilliseconds}ms");
                }
                catch (System.Exception ex)
                {
                    // Issue #15: an unguarded throw here died inside the task scheduler, invisibly.
                    Log.Error($"[Transfer] world transfer failed: {ex}");
                }
            });
        }

        /// <summary>
        ///     Host-only on-demand full resync: tell every client to prepare (back to DOWNLOADING_MAP)
        ///     and re-stream the current world to each. Reuses the exact, tested join transfer path — the
        ///     ResyncCommand is sent first (reliable-ordered), and the world slices arrive after the async
        ///     SaveGame, so each client is ready when they land. Reconciles accumulated drift; costs a
        ///     hitch proportional to city size, so it's a manual/occasional action ("/resync" in chat).
        /// </summary>
        public void ResyncAll()
        {
            if (Command.CurrentRole != MultiplayerRole.Server)
            {
                Log.Info("[Resync] ignored — only the host can trigger a resync");
                return;
            }

            Log.Info($"[Resync] host re-syncing {PlayerListConnected.Count - 1} client(s)");
            CommandInternal.Instance.SendToClients(new ResyncCommand());
            foreach (Player p in PlayerListConnected)
            {
                if (p is RemotePlayer rp)
                {
                    SendWorldTo(rp);
                }
            }
        }
    }
}
