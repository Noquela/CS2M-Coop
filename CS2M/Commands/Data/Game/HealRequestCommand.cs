using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v60 auto-heal: a CLIENT whose radar confirmed a settled drift in a healable domain asks the
    ///     host for that domain's authoritative slice — no chat, no /resync, no full-world transfer.
    ///     The host answers with the domain-specific heal command (WaterHealCommand, TerrainPatchCommand,
    ///     or a re-broadcast of the current economy values through the EXISTING apply paths).
    ///     Host-consumed only (RelayOnServer=false): other clients never see the request.
    /// </summary>
    public class HealRequestCommand : CommandBase
    {
        /// <summary>Radar domain name: "water" | "terrain" | "fees" | "tax" | "policies" | "budget" | "loan".</summary>
        public string Domain { get; set; }

        /// <summary>Only for Domain=="terrain": the client's 32×32 sampled world heights (same grid as
        /// StateHash.SampleTerrainGrid), so the host can localize WHICH cells diverged and answer with
        /// small pixel patches instead of the whole heightmap (~4 KB up, a few KB down).</summary>
        public float[] TerrainHeights { get; set; }
    }
}
