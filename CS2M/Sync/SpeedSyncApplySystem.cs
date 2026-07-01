using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Networking;
using Game;
using Game.Simulation;

namespace CS2M.Sync
{
    /// <summary>
    ///     Clients: snap the local sim speed to the host's authoritative value. Runs in Rendering so it
    ///     can also un-pause a paused sim. The host doesn't apply (it IS the authority). This coexists
    ///     with <see cref="JoinPauseSystem"/>: during a join everyone (host included) is at 0, so the
    ///     broadcast agrees with the pause; outside joins the host's speed wins.
    /// </summary>
    public partial class SpeedSyncApplySystem : GameSystemBase
    {
        private SimulationSystem _sim;

        protected override void OnCreate()
        {
            base.OnCreate();
            _sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            CS2M.Log.Info("[Speed] SpeedSyncApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (Command.CurrentRole == MultiplayerRole.Server)
            {
                return; // host is authoritative; it doesn't take remote speed
            }

            if (!RemoteSpeedQueue.TryTake(out float speed))
            {
                return;
            }

            if (_sim.selectedSpeed != speed)
            {
                _sim.selectedSpeed = speed;
            }
        }
    }
}
