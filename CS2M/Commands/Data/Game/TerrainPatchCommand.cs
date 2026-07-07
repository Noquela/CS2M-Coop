using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v60 auto-heal: an EXACT rectangular patch of the host's heightmap (raw R16 texels straight
    ///     from TerrainHeightData.heights), addressed in heightmap pixel space — both machines run the
    ///     same map at the same 4096² resolution, so pixel coords transfer as-is. Unlike the brush
    ///     replay (best-effort by design — per-frame strength), a patch is bit-exact: it's the same
    ///     data the save serializes, just cropped to the diverged region (a 32×32-grid cell ≈ 128×128
    ///     texels ≈ 32 KB) instead of the whole 32 MB heightmap that /resync ships.
    /// </summary>
    public class TerrainPatchCommand : CommandBase
    {
        /// <summary>Destination rect in heightmap pixels (row-major Data, W*H texels).</summary>
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public ushort[] Data { get; set; }
    }
}
