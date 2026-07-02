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
                _pausedByUs = false; // drop stale state if we leave the session mid-join
                return;
            }

            bool anyJoining = RemoteJoinState.AnyJoining;

            if (anyJoining)
            {
                if (!_pausedByUs)
                {
                    float cur = _sim.selectedSpeed;
                    if (cur > 0f)
                    {
                        _savedSpeed = cur; // never save 0 (focus auto-pause etc.)
                    }

                    _pausedByUs = true;
                    CS2M.Log.Info($"[Join] PAUSED (saved speed={_savedSpeed})");
                }

                // Enforce EVERY frame, exactly like the vanilla forced-pause (TimeUISystem rewrites
                // selectedSpeed=0 per frame while its pause barrier is active). A one-shot write loses
                // to the game's UI: SPACE / speed keys / focus-restore all rewrite selectedSpeed, which
                // is why the host's game visibly kept running in the first real 2-PC session.
                if (_sim.selectedSpeed != 0f)
                {
                    _sim.selectedSpeed = 0f;
                }
            }
            else if (_pausedByUs)
            {
                _pausedByUs = false;
                _sim.selectedSpeed = _savedSpeed > 0f ? _savedSpeed : 1f;
                CS2M.Log.Info($"[Join] RESUMED (speed={_sim.selectedSpeed})");
            }
        }
    }
}
