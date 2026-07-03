using System;
using System.Collections.Generic;
using CS2M.API;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Active map pings (thread-safe). PlayerCursorSystem renders them each frame.</summary>
    public static class MapPingSync
    {
        public const float LifetimeSeconds = 8f;

        public struct Ping
        {
            public float3 Position;
            public string Username;
            public int PlayerId;
            public DateTime ExpiresUtc;
        }

        private static readonly List<Ping> Pings = new List<Ping>();
        private static readonly object Lock = new object();

        public static void Add(int playerId, float3 position, string username)
        {
            lock (Lock)
            {
                Pings.Add(new Ping
                {
                    Position = position,
                    Username = username,
                    PlayerId = playerId,
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(LifetimeSeconds),
                });
            }
        }

        /// <summary>Live pings only — expired entries are dropped on read.</summary>
        public static List<Ping> Snapshot()
        {
            lock (Lock)
            {
                DateTime now = DateTime.UtcNow;
                Pings.RemoveAll(p => p.ExpiresUtc <= now);
                return new List<Ping>(Pings);
            }
        }

        public static void Clear()
        {
            lock (Lock) { Pings.Clear(); }
        }
    }

    /// <summary>Latest roster received from the host (or built locally on the host).</summary>
    public static class PlayerStatsSync
    {
        private static readonly object Lock = new object();
        private static PlayerStatsCommand _latest;

        public static void Set(PlayerStatsCommand cmd)
        {
            lock (Lock) { _latest = cmd; }
        }

        public static PlayerStatsCommand Get()
        {
            lock (Lock) { return _latest; }
        }

        public static void Clear()
        {
            lock (Lock) { _latest = null; }
        }
    }

    /// <summary>
    ///     HOST only: broadcasts the player roster (name + latency) ~1 Hz and refreshes the local
    ///     panel. Latency comes from LiteNetLib's per-peer ping (NetworkManager records it);
    ///     names come from the cursor stream (players broadcast their username at 20 Hz).
    /// </summary>
    public partial class PlayerStatsSenderSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 60;
        private int _frame;

        protected override void OnUpdate()
        {
            LocalPlayer local = NetworkInterface.Instance.LocalPlayer;
            if (local.PlayerStatus != PlayerStatus.PLAYING || local.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            if (++_frame < SendEveryNFrames)
            {
                return;
            }

            _frame = 0;

            var ids = new List<int> { 0 };
            var names = new List<string> { string.IsNullOrEmpty(local.Username) ? "Host" : local.Username };
            var pings = new List<int> { 0 };

            // One roster row per peer the host currently tracks a latency for.
            foreach (KeyValuePair<int, int> kv in NetworkManager.LatencySnapshot())
            {
                ids.Add(kv.Key);
                names.Add(ResolveName(kv.Key));
                pings.Add(kv.Value);
            }

            var cmd = new PlayerStatsCommand { Ids = ids.ToArray(), Names = names.ToArray(), Pings = pings.ToArray() };
            Command.SendToAll?.Invoke(cmd);
            PlayerStatsSync.Set(cmd);
            UI.ChatPanel.RefreshPlayerList();
        }

        private static string ResolveName(int playerId)
        {
            foreach (var kv in RemotePlayerCursors.Snapshot())
            {
                if (kv.Key == playerId && !string.IsNullOrEmpty(kv.Value.Username))
                {
                    return kv.Value.Username;
                }
            }

            return $"Player {playerId}";
        }
    }
}
