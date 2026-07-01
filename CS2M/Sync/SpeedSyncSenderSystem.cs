using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Simulation;

namespace CS2M.Sync
{
    /// <summary>
    ///     Host-only: broadcasts the authoritative simulation speed (immediately on change, plus a ~1 Hz
    ///     heartbeat) so clients advance the same number of ticks. Runs in Rendering so it ticks even
    ///     while the sim is paused (speed 0).
    /// </summary>
    public partial class SpeedSyncSenderSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 60;

        private SimulationSystem _sim;
        private float _lastSent = float.NaN;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            CS2M.Log.Info("[Speed] SpeedSyncSenderSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (Command.CurrentRole != MultiplayerRole.Server)
            {
                return; // only the host is authoritative over speed
            }

            float speed = _sim.selectedSpeed;
            bool changed = speed != _lastSent;
            if (!changed && ++_frame < SendEveryNFrames)
            {
                return;
            }

            _frame = 0;
            _lastSent = speed;
            Command.SendToAll?.Invoke(new SpeedCommand { Speed = speed });
            if (changed)
            {
                CS2M.Log.Info($"[Speed] SEND speed={speed}");
            }
        }
    }
}
