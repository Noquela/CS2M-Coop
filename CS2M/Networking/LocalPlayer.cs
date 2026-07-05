using System;
using System.Collections.Generic;
using System.Linq;
using Colossal;
using Colossal.PSI.Common;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands;
using CS2M.Commands.ApiServer;
using CS2M.Commands.Data.Internal;
using CS2M.Helpers;
using CS2M.Mods;
using CS2M.UI;
using CS2M.Util;
using LiteNetLib;
using Unity.Entities;

namespace CS2M.Networking
{
    public class LocalPlayer : Player
    {
        private SlicedPacketStream _packetStream;
        private readonly SaveLoadHelper _saveLoadHelper;
        private NetworkManager _networkManager;
        private UISystem _uiSystem;

        // v50 auto-reconnect: when an ESTABLISHED session drops without the user asking, retry the
        // same connection every few seconds. Rejoining re-runs the full join flow (preconditions +
        // world transfer), which doubles as an automatic resync.
        private const int ReconnectMaxTries = 24;      // × 5 s ≈ 2 minutes
        private const double ReconnectDelaySeconds = 5.0;
        private ConnectionConfig _lastClientConfig;
        private bool _intentionalDisconnect;
        private bool _reconnecting;
        private int _reconnectTriesLeft;
        private DateTime _nextReconnectUtc;

        public LocalPlayer()
        {
            PlayerStatusChangedEvent += PlayerStatusChanged;
            PlayerTypeChangedEvent += PlayerTypeChanged;
            _saveLoadHelper = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SaveLoadHelper>();
        }

        public bool GetServerInfo(ConnectionConfig connectionConfig)
        {
            if (PlayerStatus != PlayerStatus.INACTIVE)
            {
                return false;
            }

            _networkManager = new NetworkManager();

            _networkManager.NatHolePunchSuccessfulEvent += NatConnect;
            _networkManager.NatHolePunchFailedEvent += DirectConnect;
            _networkManager.ClientConnectSuccessfulEvent += ConnectionEstablished;
            _networkManager.ClientConnectFailedEvent += ConnectionFailed;
            _networkManager.ClientDisconnectEvent += OnClientDisconnected;

            _lastClientConfig = connectionConfig;
            _intentionalDisconnect = false;

            if (!_networkManager.InitConnect(connectionConfig))
            {
                _uiSystem.SetJoinErrors("CS2M.UI.JoinError.ClientFailed");
                return false;
            }

            if (!_networkManager.SetupNatConnect())
            {
                _uiSystem.SetJoinErrors("CS2M.UI.JoinError.InvalidIP");
                return false;
            }

            PlayerType = PlayerType.CLIENT;
            PlayerStatus = PlayerStatus.GET_SERVER_INFO;
            return true;
        }

        public bool NatConnect()
        {
            if (PlayerStatus != PlayerStatus.GET_SERVER_INFO)
            {
                return false;
            }

            if (!_networkManager.Connect())
            {
                _uiSystem.SetJoinErrors("CS2M.UI.JoinError.FailedToConnect");
                Inactive();
                return false;
            }

            PlayerStatus = PlayerStatus.NAT_CONNECT;
            return true;
        }

        public bool DirectConnect()
        {
            if (PlayerStatus != PlayerStatus.GET_SERVER_INFO &&
                PlayerStatus != PlayerStatus.NAT_CONNECT)
            {
                return false;
            }

            if (!_networkManager.Connect())
            {
                _uiSystem.SetJoinErrors("CS2M.UI.JoinError.FailedToConnect");
                Inactive();
                return false;
            }

            PlayerStatus = PlayerStatus.DIRECT_CONNECT;
            return true;
        }

        public bool ConnectionFailed()
        {
            if (_reconnecting)
            {
                // Host may still be coming back — keep the retry cycle alive.
                Inactive();
                ScheduleNextReconnect();
                return true;
            }

            _uiSystem.SetJoinErrors("CS2M.UI.JoinError.FailedToConnect");
            Inactive();
            return true;
        }

        /// <summary>v50: connection dropped by the network (NOT via the disconnect button). If we
        /// were in an established session, start the auto-reconnect cycle; rejoining re-runs the
        /// world transfer, so the client comes back fully resynced.</summary>
        private bool OnClientDisconnected()
        {
            bool wasEstablished = PlayerStatus == PlayerStatus.PLAYING
                                  || PlayerStatus == PlayerStatus.LOADING_MAP
                                  || PlayerStatus == PlayerStatus.DOWNLOADING_MAP
                                  || PlayerStatus == PlayerStatus.WAITING_TO_JOIN;

            Inactive();

            if (_intentionalDisconnect || !wasEstablished || _lastClientConfig == null)
            {
                _reconnecting = false;
                _reconnectTriesLeft = 0;
                return true;
            }

            _reconnecting = true;
            _reconnectTriesLeft = ReconnectMaxTries;
            _nextReconnectUtc = DateTime.UtcNow.AddSeconds(ReconnectDelaySeconds);
            Log.Info($"[Reconnect] connection lost — retrying every {ReconnectDelaySeconds:F0}s (max {ReconnectMaxTries})");
            API.Chat.Instance?.PrintGameMessage("Connection lost — auto-reconnecting…");
            return true;
        }

        private void ScheduleNextReconnect()
        {
            if (_reconnectTriesLeft <= 0)
            {
                _reconnecting = false;
                Log.Info("[Reconnect] giving up (no tries left)");
                API.Chat.Instance?.PrintGameMessage("Reconnect failed — use the join menu to retry.");
                return;
            }

            _nextReconnectUtc = DateTime.UtcNow.AddSeconds(ReconnectDelaySeconds);
        }

        /// <summary>v50: disconnect requested by the user/UI — never auto-reconnect after this.</summary>
        public bool UserDisconnect()
        {
            _intentionalDisconnect = true;
            _reconnecting = false;
            _reconnectTriesLeft = 0;
            return Inactive();
        }

        public bool ConnectionEstablished()
        {
            if (PlayerStatus != PlayerStatus.NAT_CONNECT &&
                PlayerStatus != PlayerStatus.DIRECT_CONNECT)
            {
                return false;
            }

            SendToServer(new PreconditionsCheckCommand
            {
                Username = Username,
                Password = GetConnectionPassword(),
                ModVersion = VersionUtil.GetModVersion(),
                GameVersion = VersionUtil.GetGameVersion(),
                Mods = ModSupport.Instance.RequiredModsForSync,
                DlcIds = DlcCompat.RequiredDLCsForSync,
            });

            PlayerStatus = PlayerStatus.CONNECTION_ESTABLISHED;
            return true;
        }

        public void PreconditionsError(PreconditionsErrorCommand command)
        {
            // Structural mismatch (version/mods/DLCs/password) never fixes itself — stop retrying.
            _reconnecting = false;
            _reconnectTriesLeft = 0;
            Inactive();
            var errors = new List<string>();
            PreconditionsUtil.Errors err = command.Errors;
            if (err.HasFlag(PreconditionsUtil.Errors.GAME_VERSION_MISMATCH))
            {
                errors.Add("precondition:GAME_VERSION_MISMATCH");
                errors.Add(command.GameVersion.ToString());
                errors.Add(VersionUtil.GetGameVersion().ToString());
            }

            if (err.HasFlag(PreconditionsUtil.Errors.MOD_VERSION_MISMATCH))
            {
                errors.Add("precondition:MOD_VERSION_MISMATCH");
                errors.Add(command.ModVersion.ToString());
                errors.Add(VersionUtil.GetModVersion().ToString());
            }

            if (err.HasFlag(PreconditionsUtil.Errors.USERNAME_NOT_AVAILABLE))
            {
                errors.Add("precondition:USERNAME_NOT_AVAILABLE");
            }

            if (err.HasFlag(PreconditionsUtil.Errors.PASSWORD_INCORRECT))
            {
                errors.Add("precondition:PASSWORD_INCORRECT");
            }

            if (err.HasFlag(PreconditionsUtil.Errors.DLCS_MISMATCH))
            {
                List<int> clientDLCs = DlcCompat.RequiredDLCsForSync;
                List<int> serverDLCs = command.DlcIds;

                IEnumerable<string> clientNotServer = clientDLCs.Where(mod => !serverDLCs.Contains(mod))
                    .Select(id => DlcCompat.GetDisplayName(new DlcId(id)));
                IEnumerable<string> serverNotClient = serverDLCs.Where(mod => !clientDLCs.Contains(mod))
                    .Select(id => DlcCompat.GetDisplayName(new DlcId(id)));

                errors.Add("precondition:DLCS_MISMATCH");
                errors.Add(string.Join(", ", serverNotClient));
                errors.Add(string.Join(", ", clientNotServer));
            }

            if (err.HasFlag(PreconditionsUtil.Errors.MODS_MISMATCH))
            {
                List<string> clientMods = ModSupport.Instance.RequiredModsForSync;
                List<string> serverMods = command.Mods;

                IEnumerable<string> clientNotServer = clientMods.Where(mod => !serverMods.Contains(mod));
                IEnumerable<string> serverNotClient = serverMods.Where(mod => !clientMods.Contains(mod));

                errors.Add("precondition:MODS_MISMATCH");
                errors.Add(string.Join(", ", serverNotClient));
                errors.Add(string.Join(", ", clientNotServer));
            }

            _uiSystem.SetJoinErrors(errors.ToArray());
        }

        public bool WaitingToJoin()
        {
            if (PlayerStatus != PlayerStatus.CONNECTION_ESTABLISHED)
            {
                return false;
            }

            //TODO: Implement JoinRequest

            PlayerStatus = PlayerStatus.WAITING_TO_JOIN;
            return DownloadingMap(); //TODO: Switch to 'return true;', when JoinRequest implemented
        }

        public bool DownloadingMap()
        {
            if (PlayerStatus != PlayerStatus.WAITING_TO_JOIN)
            {
                return false;
            }

            // Change state to downloading map, next step is to wait until all
            // map packets have been received by `SliceReceived` below.
            PlayerStatus = PlayerStatus.DOWNLOADING_MAP;
            _uiSystem.SetLoadProgress(0, 0);
            return true;
        }

        public void SliceReceived(WorldTransferCommand cmd)
        {
            if (PlayerStatus != PlayerStatus.DOWNLOADING_MAP)
            {
                Log.Warn("Received world slice, but not in downloading state");
                return;
            }

            if (cmd.NewTransfer)
            {
                _packetStream = new SlicedPacketStream(cmd.WorldSlice.Length);
            }
            else if (_packetStream == null)
            {
                Log.Warn("Received world slice without initialized packet stream");
                _uiSystem.SetJoinErrors("CS2M.JoinError.DownloadFailed");
                Inactive();
                return;
            }

            _packetStream.AppendSlice(cmd.WorldSlice);
            _uiSystem.SetLoadProgress((int)_packetStream.Length, cmd.RemainingBytes);

            if (cmd.RemainingBytes == 0)
            {
                LoadingMap();
            }
        }

        public void LoadingMap()
        {
            if (PlayerStatus != PlayerStatus.DOWNLOADING_MAP)
            {
                return;
            }

            PlayerStatus = PlayerStatus.LOADING_MAP;
            TaskManager.instance.EnqueueTask("LoadMap", async () =>
            {
                bool success = await _saveLoadHelper.LoadGame(_packetStream);
                if (success)
                {
                    _packetStream = null; // Clean up save game memory
                    Playing();
                }
                // TODO: Error handling
            });
        }

        public bool Playing()
        {
            if (PlayerStatus != PlayerStatus.LOADING_MAP)
            {
                return false;
            }

            PlayerStatus = PlayerStatus.PLAYING;
            return true;
        }

        /// <summary>
        ///     On-demand resync: put an already-PLAYING client back into DOWNLOADING_MAP so it accepts a
        ///     fresh world from the host (reuses the exact join download→load→playing flow). This is the
        ///     safety net that reconciles any accumulated simulation drift (population, traffic, terrain).
        /// </summary>
        public void PrepareResync()
        {
            if (PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            _packetStream = null;
            PlayerStatus = PlayerStatus.DOWNLOADING_MAP;
            _uiSystem.SetLoadProgress(0, 0);
            Log.Info("[Resync] client ready to receive a fresh world from host");
        }

        // INACTIVE -> PLAYING (Server)
        public bool Playing(ConnectionConfig connectionConfig)
        {
            if (PlayerStatus != PlayerStatus.INACTIVE)
            {
                return false;
            }

            _networkManager = new NetworkManager();

            bool serverStarted = _networkManager.StartServer(connectionConfig);
            if (!serverStarted)
            {
                return false;
            }

            //TODO: Setup server variables (player list, etc.)

            PlayerStatus = PlayerStatus.PLAYING;
            PlayerType = PlayerType.SERVER;

            return true;
        }

        public void Blocked()
        {
        }

        // PLAYING -> INACTIVE
        public bool Inactive()
        {
            // if (PlayerStatus != PlayerStatus.PLAYING)
            // {
            //     return false;
            // }

            if (PlayerType == PlayerType.SERVER)
            {
                //TODO: Clear server variables (player list, etc.)
            }
            else if (PlayerType == PlayerType.CLIENT)
            {
                //TODO: Clean-Up client
            }

            _networkManager?.Stop();

            // Drop any remote player cursors so they don't linger after leaving.
            Sync.RemotePlayerCursors.Clear();
            Sync.RemoteToolPreviews.Clear();
            Sync.SyncHealth.Clear();

            // Drop any queued remote object placements.
            Sync.RemotePlacementQueue.Clear();

            // Drop synced-entity id map + all queued sync state.
            Sync.CS2M_SyncIdSystem.Clear();
            Sync.CS2M_NodeSyncIds.Clear();
            Sync.RemoteMoneyQueue.Clear();
            Sync.RemoteEditQueue.Clear();
            Sync.RemoteNetQueue.Clear();
            Sync.RemoteNetBatchQueue.Clear();
            Sync.RemoteReplayQueue.Clear();
            Sync.RemoteNetEcho.Clear();
            Sync.RemoteProgressionQueue.Clear();
            Sync.RemoteZoneQueue.Clear();
            Sync.ZoneSync.Clear();
            Sync.ZoneEcho.Clear();
            Sync.RemoteJoinState.Clear();

            // Fork features: drop their queues + shared snapshots so a reconnect starts clean.
            Sync.RemoteTaxQueue.Clear();
            Sync.TaxSync.Clear();
            Sync.RemoteBudgetQueue.Clear();
            Sync.BudgetSync.Clear();
            Sync.RemotePolicyQueue.Clear();
            Sync.PolicySync.Clear();
            Sync.RemoteNetDeleteQueue.Clear();
            Sync.RemoteNetUpgradeQueue.Clear();
            Sync.RemoteDistrictQueue.Clear();
            Sync.RemoteWaterQueue.Clear();
            Sync.RemoteTerrainQueue.Clear();
            Sync.RemoteSpeedQueue.Clear();
            Sync.RemoteDevTreeQueue.Clear();
            Sync.DevTreeSync.Clear();
            Sync.RemoteEnvQueue.Clear();
            Sync.TileSync.Clear();
            Sync.RemoteAreaQueue.Clear();
            Sync.LoanSync.Clear();
            Sync.RenameSync.Clear();
            Sync.RouteSync.Clear();
            Sync.RemoteStateHashQueue.Clear();
            Sync.MapPingSync.Clear();
            Sync.PlayerStatsSync.Clear();
            Sync.FireSync.Clear();
            Sync.WaterSync.Clear();
            Sync.WorkAreaHash.Clear();
            Sync.DemandSync.Clear();
            Sync.RemoteFeeQueue.Clear();
            Sync.FeeSync.Clear();
            Sync.DistrictReshapeSync.Clear();
            UI.ChatPanel.RefreshPlayerList();

            PlayerStatus = PlayerStatus.INACTIVE;
            PlayerType = PlayerType.NONE;
            return true;
        }

        public void OnUpdate()
        {
            if (_uiSystem == null)
            {
                _uiSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<UISystem>();
            }

            if (PlayerStatus != PlayerStatus.INACTIVE)
            {
                _networkManager.ProcessEvents();
            }
            else if (_reconnecting && _reconnectTriesLeft > 0 && DateTime.UtcNow >= _nextReconnectUtc)
            {
                // v50 auto-reconnect tick.
                _reconnectTriesLeft--;
                int attempt = ReconnectMaxTries - _reconnectTriesLeft;
                Log.Info($"[Reconnect] attempt {attempt}/{ReconnectMaxTries}");
                API.Chat.Instance?.PrintGameMessage($"Reconnecting… (attempt {attempt}/{ReconnectMaxTries})");
                _nextReconnectUtc = DateTime.UtcNow.AddSeconds(ReconnectDelaySeconds);
                if (!GetServerInfo(_lastClientConfig))
                {
                    ScheduleNextReconnect();
                }
            }
        }

        public void UpdateUsername(string username)
        {
            if (PlayerStatus != PlayerStatus.INACTIVE)
            {
                //TODO: Print Warning
                return;
            }

            Username = username;
        }

        public void UpdatePlayerType(PlayerType playerType)
        {
            PlayerType = playerType;
        }

        public string GetConnectionPassword()
        {
            return _networkManager.GetConnectionPassword();
        }

        public void SendToAll(CommandBase message)
        {
            message.SenderId = PlayerId;
            if (PlayerType == PlayerType.SERVER)
            {
                _networkManager.SendToAllClients(message);
            }
            else
            {
                _networkManager.SendToServer(message);
            }
        }

        public void SendToClient(NetPeer peer, CommandBase message)
        {
            message.SenderId = PlayerId;
            _networkManager.SendToClient(peer, message);
        }

        public void SendToServer(CommandBase message)
        {
            if (PlayerType == PlayerType.CLIENT)
            {
                message.SenderId = PlayerId;
                _networkManager.SendToServer(message);
            }
        }

        public void SendToClients(CommandBase message)
        {
            if (PlayerType == PlayerType.SERVER)
            {
                message.SenderId = PlayerId;
                _networkManager.SendToAllClients(message);
            }
        }

        public void SendToApiServer(ApiCommandBase message)
        {
            _networkManager.SendToApiServer(message);
        }

        public void PlayerStatusChanged(PlayerStatus oldPlayerStatus, PlayerStatus newPlayerStatus)
        {
            Log.Debug($"LocalPlayer: changed player status from {oldPlayerStatus} to {newPlayerStatus}");

            // While WE join, ask everyone already in-game to pause + show a notice; resume when done.
            if (newPlayerStatus == PlayerStatus.WAITING_TO_JOIN)
            {
                API.Commands.Command.SendToAll?.Invoke(
                    new Commands.Data.Game.JoinNoticeCommand { Username = Username, Joining = true });
                Log.Info($"[Join] SEND joining=true user={Username}");
            }
            else if (newPlayerStatus == PlayerStatus.PLAYING && oldPlayerStatus == PlayerStatus.LOADING_MAP)
            {
                API.Commands.Command.SendToAll?.Invoke(
                    new Commands.Data.Game.JoinNoticeCommand { Username = Username, Joining = false });
                Log.Info($"[Join] SEND joining=false user={Username}");

                // v50: a completed (re)join ends any pending auto-reconnect cycle.
                if (_reconnecting)
                {
                    _reconnecting = false;
                    _reconnectTriesLeft = 0;
                    Log.Info("[Reconnect] SUCCESS — session re-established");
                    API.Chat.Instance?.PrintGameMessage("Reconnected ✔ (world re-synced)");
                }
            }
        }

        public void PlayerTypeChanged(PlayerType oldPlayerType, PlayerType newPlayerType)
        {
            Log.Debug($"LocalPlayer: changed player type from {oldPlayerType} to {newPlayerType}");

            // Keep the command-layer role in lockstep with the network-layer type. This was never
            // set before, so CurrentRole stayed None forever and every host-authoritative sender
            // (Money/Speed/Progression) plus ResyncAll silently gated itself off — confirmed in the
            // first real 2-PC session (zero [Money]/[Speed] SEND lines on a live host).
            if (newPlayerType == PlayerType.SERVER)
            {
                API.Commands.Command.CurrentRole = API.Commands.MultiplayerRole.Server;
            }
            else if (newPlayerType == PlayerType.CLIENT)
            {
                API.Commands.Command.CurrentRole = API.Commands.MultiplayerRole.Client;
            }
            else
            {
                API.Commands.Command.CurrentRole = API.Commands.MultiplayerRole.None;
            }

            Log.Info($"[Role] CurrentRole={API.Commands.Command.CurrentRole}");
        }
    }
}
