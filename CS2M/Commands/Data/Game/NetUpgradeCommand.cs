using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player upgrades a net segment (sidewalks, trees, sound barriers, lighting,
    ///     quays, elevation options). The edge is addressed by its two endpoint node positions; the
    ///     receiver finds the matching local edge and writes the same <c>CompositionFlags</c>
    ///     (General/Left/Right, as raw uints).
    /// </summary>
    public class NetUpgradeCommand : CommandBase
    {
        public float StartX { get; set; }
        public float StartY { get; set; }
        public float StartZ { get; set; }
        public float EndX { get; set; }
        public float EndY { get; set; }
        public float EndZ { get; set; }
        public uint General { get; set; }
        public uint Left { get; set; }
        public uint Right { get; set; }
    }
}
