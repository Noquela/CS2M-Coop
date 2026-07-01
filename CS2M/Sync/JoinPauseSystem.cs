using CS2M.API.Networking;
using CS2M.Networking;
using Game;
using Game.Simulation;

namespace CS2M.Sync
{
    /// <summary>
    ///     Pauses the simulation for everyone already in-game while a remote player is joining
    ///     (loading in), then restores the previous speed once they're done — so the world doesn't
    ///     drift while the joiner catches up. Runs in the Rendering phase so it still ticks (and can
    ///     un-pause) while the sim is paused.
    /// </summary>
    public partial class JoinPauseSystem : GameSystemBase
    {
        private SimulationSystem _sim;
        private bool _pausedByUs;
        private float _savedSpeed = 1f;

        protected override void OnCreate()
        {
            base.OnCreate();
            _sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            CS2M.Log.Info("[Join] JoinPauseSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            bool anyJoining = RemoteJoinState.AnyJoining;

            if (anyJoining && !_pausedByUs)
            {
                _savedSpeed = _sim.selectedSpeed;
                _sim.selectedSpeed = 0f;
                _pausedByUs = true;
                CS2M.Log.Info($"[Join] PAUSED (saved speed={_savedSpeed})");
            }
            else if (!anyJoining && _pausedByUs)
            {
                _sim.selectedSpeed = _savedSpeed > 0f ? _savedSpeed : 1f;
                _pausedByUs = false;
                CS2M.Log.Info($"[Join] RESUMED (speed={_sim.selectedSpeed})");
            }
        }
    }
}
