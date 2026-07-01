using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast for a terraforming brush application (raise/lower/level/slope). The receiver replays
    ///     the same brush via <c>TerrainSystem.ApplyBrush</c>. NOTE: terrain edits are continuous brush
    ///     strokes and the per-frame delta depends on each machine's frame time, so this is
    ///     <b>best-effort / approximate</b> — the on-demand full resync reconciles any accumulated
    ///     terrain drift.
    /// </summary>
    public class TerrainCommand : CommandBase
    {
        public int Type { get; set; }       // Game.Prefabs.TerraformingType (Shift/Level/Soften/Slope)
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float Size { get; set; }
        public float Strength { get; set; }
    }
}
