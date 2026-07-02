using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>Shared counting queries so host and clients fingerprint the world identically.
    /// Only player-action-driven counts — growables/emergent sim legitimately differ per PC.</summary>
    internal static class StateHash
    {
        public static EntityQueryDesc EdgeDesc()
        {
            return new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(), // building sub-nets are derived, not compared
                },
            };
        }

        public static EntityQueryDesc DistrictDesc()
        {
            return new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Areas.District>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            };
        }

        public static EntityQueryDesc WaterDesc()
        {
            return new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Simulation.WaterSourceData>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            };
        }
    }

    /// <summary>Host: broadcast the world fingerprint every ~10 s.</summary>
    public partial class StateHashSenderSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 600;

        private EntityQuery _edges;
        private EntityQuery _districts;
        private EntityQuery _water;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _edges = GetEntityQuery(StateHash.EdgeDesc());
            _districts = GetEntityQuery(StateHash.DistrictDesc());
            _water = GetEntityQuery(StateHash.WaterDesc());
            CS2M.Log.Info("[Hash] StateHashSenderSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING
                || NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            if (++_frame < SendEveryNFrames)
            {
                return;
            }

            _frame = 0;
            Command.SendToAll?.Invoke(new StateHashCommand
            {
                Edges = _edges.CalculateEntityCount(),
                SyncedObjects = CS2M_SyncIdSystem.Map.Count,
                Districts = _districts.CalculateEntityCount(),
                WaterSources = _water.CalculateEntityCount(),
            });
        }
    }

    /// <summary>
    ///     Clients: compare the host's fingerprint against local counts. Two consecutive mismatches
    ///     (≈20 s — rules out in-flight commands) trigger a chat warning suggesting "/resync".
    /// </summary>
    public partial class StateHashApplySystem : GameSystemBase
    {
        private const int EdgeTolerance = 3; // splits/merges can transiently differ by a couple

        private EntityQuery _edges;
        private EntityQuery _districts;
        private EntityQuery _water;
        private int _strikes;
        private double _lastWarnedAt;

        protected override void OnCreate()
        {
            base.OnCreate();
            _edges = GetEntityQuery(StateHash.EdgeDesc());
            _districts = GetEntityQuery(StateHash.DistrictDesc());
            _water = GetEntityQuery(StateHash.WaterDesc());
            CS2M.Log.Info("[Hash] StateHashApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                _strikes = 0;
                return;
            }

            if (!RemoteStateHashQueue.TryTake(out StateHashCommand cmd))
            {
                return;
            }

            int edges = _edges.CalculateEntityCount();
            int synced = CS2M_SyncIdSystem.Map.Count;
            int districts = _districts.CalculateEntityCount();
            int water = _water.CalculateEntityCount();

            int edgeDelta = System.Math.Abs(edges - cmd.Edges);
            int syncedDelta = System.Math.Abs(synced - cmd.SyncedObjects);
            int districtDelta = System.Math.Abs(districts - cmd.Districts);
            int waterDelta = System.Math.Abs(water - cmd.WaterSources);

            bool diverged = edgeDelta > EdgeTolerance || syncedDelta > 1 || districtDelta > 1 || waterDelta > 1;
            if (!diverged)
            {
                _strikes = 0;
                return;
            }

            _strikes++;
            CS2M.Log.Info($"[Hash] DRIFT strike={_strikes} edges {edges}vs{cmd.Edges} synced {synced}vs{cmd.SyncedObjects} " +
                          $"districts {districts}vs{cmd.Districts} water {water}vs{cmd.WaterSources}");

            if (_strikes < 2)
            {
                return;
            }

            _strikes = 0;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now - _lastWarnedAt < 300.0)
            {
                return; // warn at most once per 5 min
            }

            _lastWarnedAt = now;
            try
            {
                CS2M.API.Chat.Instance?.PrintChatMessage("CS2M",
                    $"worlds drifting apart (roads Δ{edgeDelta}, objects Δ{syncedDelta}) — ask the host to type /resync");
            }
            catch
            {
            }
        }
    }
}
