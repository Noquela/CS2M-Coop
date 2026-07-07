using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Simulation;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Host-only (~0.5 Hz): broadcasts weather scalars + the shared clock so both worlds LOOK like
    ///     the same place (rain/snow/clouds + day/night). ElapsedTimeFrames anchors the client's
    ///     Game.Common.TimeData to the host's elapsed simulation frames.
    /// </summary>
    public partial class EnvSyncSenderSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 120; // ~0.5 Hz

        private ClimateSystem _climate;
        private SimulationSystem _sim;
        private EntityQuery _timeDataQuery;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _climate = World.GetOrCreateSystemManaged<ClimateSystem>();
            _sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            _timeDataQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Common.TimeData>());
            CS2M.Log.Info("[Env] EnvSyncSenderSystem created");
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

            uint elapsed = 0;
            if (!_timeDataQuery.IsEmptyIgnoreFilter)
            {
                Game.Common.TimeData td = _timeDataQuery.GetSingleton<Game.Common.TimeData>();
                elapsed = _sim.frameIndex - td.m_FirstFrame;
            }

            Command.SendToAll?.Invoke(new EnvSyncCommand
            {
                Temperature = _climate.temperature.value,
                Precipitation = _climate.precipitation.value,
                Cloudiness = _climate.cloudiness.value,
                ElapsedTimeFrames = elapsed,
            });
        }
    }

    /// <summary>
    ///     Clients: mirror the host's weather via the ClimateSystem override properties (the game's
    ///     own debug/override mechanism — sampling consumes overrideValue when overrideState is on)
    ///     and realign Game.Common.TimeData.m_FirstFrame when the clock drifts. Overrides are released when the
    ///     session ends so single-player weather returns to normal.
    /// </summary>
    public partial class EnvSyncApplySystem : GameSystemBase
    {
        private const uint ClockDriftToleranceFrames = 60;

        private ClimateSystem _climate;
        private SimulationSystem _sim;
        private EntityQuery _timeDataQuery;
        private bool _overriding;

        protected override void OnCreate()
        {
            base.OnCreate();
            _climate = World.GetOrCreateSystemManaged<ClimateSystem>();
            _sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            _timeDataQuery = GetEntityQuery(ComponentType.ReadWrite<Game.Common.TimeData>());
            CS2M.Log.Info("[Env] EnvSyncApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                ReleaseOverrides();
                return;
            }

            // Issue #6: host-authoritative — the host's own climate/time IS the truth; never absorb
            // a client-authored EnvSyncCommand (mirrors SpeedSyncApplySystem's role guard).
            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                RemoteEnvQueue.TryTake(out _);
                return;
            }

            if (!RemoteEnvQueue.TryTake(out EnvSyncCommand cmd))
            {
                return;
            }

            _climate.temperature.overrideState = true;
            _climate.temperature.overrideValue = cmd.Temperature;
            _climate.precipitation.overrideState = true;
            _climate.precipitation.overrideValue = cmd.Precipitation;
            _climate.cloudiness.overrideState = true;
            _climate.cloudiness.overrideValue = cmd.Cloudiness;

            if (!_overriding)
            {
                _overriding = true;
                CS2M.Log.Info("[Env] weather override active (mirroring host)");
            }

            if (!_timeDataQuery.IsEmptyIgnoreFilter)
            {
                Game.Common.TimeData td = _timeDataQuery.GetSingleton<Game.Common.TimeData>();
                uint localElapsed = _sim.frameIndex - td.m_FirstFrame;
                long drift = (long)localElapsed - cmd.ElapsedTimeFrames;
                if (drift > ClockDriftToleranceFrames || drift < -(long)ClockDriftToleranceFrames)
                {
                    td.m_FirstFrame = _sim.frameIndex - cmd.ElapsedTimeFrames;
                    _timeDataQuery.SetSingleton(td);
                    CS2M.Log.Info($"[Env] clock realigned (drift={drift} frames)");
                }
            }
        }

        private void ReleaseOverrides()
        {
            if (!_overriding)
            {
                return;
            }

            _overriding = false;
            _climate.temperature.overrideState = false;
            _climate.precipitation.overrideState = false;
            _climate.cloudiness.overrideState = false;
            CS2M.Log.Info("[Env] weather override released");
        }
    }
}
