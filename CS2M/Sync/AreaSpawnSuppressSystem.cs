using CS2M.API.Networking;
using CS2M.Networking;
using Game;

namespace CS2M.Sync
{
    /// <summary>Global toggle for client-side AreaSpawnSystem suppression (see
    /// <see cref="AreaSpawnSuppressSystem"/>). ON by default since 2026-07-07 — this is the derive-once
    /// half of the host-authoritative AREAS design: each PC's <c>Game.Simulation.AreaSpawnSystem</c> grows
    /// extractor/farm FIELDS with its OWN local RNG (decomp AreaSpawnSystem.cs:308-384
    /// <c>TryGetObjectPrefab</c> keys a <c>Random</c> off the per-machine chunk index; <c>:188</c>
    /// <c>CalculateExtractorObjectArea</c> then grows the lot with usage), so two PCs deriving the same
    /// field independently NEVER agree on its shape. With the client's copy suppressed, only the host
    /// derives; the host's <see cref="AreaEditDetectorSystem"/> (host-only scanner, see its
    /// <c>ScanWorkAreaEdits</c> AUTHORITY comment) ships the derived polygon and the client adopts it via
    /// <see cref="AreaEditApplySystem"/>. Set env <c>CS2M_AREASUPPRESS=0</c> to disable.</summary>
    public static class AreaSuppressGate
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_AREASUPPRESS") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     Client side of host-authoritative AREAS (v56): while connected as a CLIENT, disable the local
    ///     <c>AreaSpawnSystem</c> — the sim that spawns/grows extractor+farm FIELDS and building sub-area
    ///     surfaces from local footprint/terrain. Each PC ran it independently, so the sub-areas diverged
    ///     (2-sim clean world: client 593 areas vs host 585, different triangulation) — the persistent
    ///     <c>areas(hash)</c> drift + the farm work-area not syncing. With it off on the client, the host's
    ///     areas arrive through the existing area sync (AreaEditDetector→ApplySystem, which now creates them
    ///     on retry-expiry). Restored the moment the session ends, or the local player becomes/reverts to
    ///     host, so single-player is unaffected.
    ///
    ///     DERIVE-ONCE (no longer experimental): the client suppresses its own AreaSpawnSystem instead of
    ///     letting it run and diverge; the host's AreaEditDetectorSystem (host-only, see its scanner's
    ///     AUTHORITY comment) transmits the derived polygon over the existing AreaEdit sync, so the client's
    ///     field is never missing — it is adopted from the host instead of grown locally (decomp
    ///     AreaSpawnSystem.cs:308-384: <c>TryGetObjectPrefab</c>'s per-chunk-index <c>Random</c> and
    ///     <c>CalculateExtractorObjectArea</c>'s usage-driven growth are exactly what made every PC's
    ///     derivation diverge from every other's).
    ///
    ///     KNOWN RISK: this ALSO suppresses AreaSpawnSystem's growth of non-farm Storage areas (mine/cargo
    ///     yard piles) on the client, which used to grow locally too — now covered the same way, by the
    ///     host's AreaEditApplySystem/AreaEditDetectorSystem for owned resource-field areas
    ///     (<c>Game.Areas.Extractor</c>), not a client-local guess.
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
            // CS2M_AREAGROW (option A, default ON): do NOT suppress — let the client grow its own field
            // locally, targeting the size implied by the host-mirrored Extractor (ExtractorSyncSystems). The
            // crop/surface mirrors stand down in that mode (see AreaObjGate / AreaSurfaceGate) so nothing
            // double-creates. CS2M_AREAGROW=0 restores the legacy suppress-and-mirror path.
            bool shouldSuppress = AreaSuppressGate.Enabled
                                  && !ExtractorGrowGate.Enabled
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
