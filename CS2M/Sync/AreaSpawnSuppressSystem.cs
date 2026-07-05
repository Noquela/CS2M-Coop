using CS2M.API.Networking;
using CS2M.Networking;
using Game;

namespace CS2M.Sync
{
    /// <summary>
    ///     Client side of host-authoritative AREAS (v56): while connected as a CLIENT, disable the local
    ///     <c>AreaSpawnSystem</c> — the sim that spawns/grows extractor+farm FIELDS and building sub-area
    ///     surfaces from local footprint/terrain. Each PC ran it independently, so the sub-areas diverged
    ///     (2-sim clean world: client 593 areas vs host 585, different triangulation) — the persistent
    ///     <c>areas(hash)</c> drift + the farm work-area not syncing. With it off on the client, the host's
    ///     areas arrive through the existing area sync (AreaEditDetector→ApplySystem, which now creates them
    ///     on retry-expiry). Restored the moment the session ends, so single-player is unaffected.
    ///
    ///     EXPERIMENTAL — validate on the autopilot 2-sim that (a) areas(hash) drift shrinks and (b) the
    ///     client still HAS its areas (a regression here = client missing fields → revert).
    /// </summary>
    public partial class AreaSpawnSuppressSystem : GameSystemBase
    {
        private Game.Simulation.AreaSpawnSystem _areaSpawn;
        private bool _suppressed;

        protected override void OnCreate()
        {
            base.OnCreate();
            _areaSpawn = World.GetOrCreateSystemManaged<Game.Simulation.AreaSpawnSystem>();
            CS2M.Log.Info("[Area] AreaSpawnSuppressSystem created");
        }

        protected override void OnUpdate()
        {
            // Gated OFF by default (env CS2M_AREASUPPRESS=1) until validated on a 2-sim WITH a farm: an
            // unvalidated suppression risks leaving the client with NO fields (visible regression) if the
            // host's area sync doesn't fully materialise them. Safe to ship dormant.
            bool enabled = System.Environment.GetEnvironmentVariable("CS2M_AREASUPPRESS") == "1";
            bool shouldSuppress = enabled
                                  && NetworkInterface.Instance.LocalPlayer.PlayerStatus == PlayerStatus.PLAYING
                                  && NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.CLIENT;

            if (shouldSuppress == _suppressed)
            {
                return;
            }

            _suppressed = shouldSuppress;
            _areaSpawn.Enabled = !shouldSuppress;
            CS2M.Log.Info(shouldSuppress
                ? "[Area] client AreaSpawnSystem SUPPRESSED (host-authoritative areas)"
                : "[Area] client AreaSpawnSystem restored");
        }
    }
}
