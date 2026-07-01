using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player bulldozes a net segment (road/rail/pipe/power). Edges have no
    ///     cross-PC id, so the segment is addressed by its two endpoint (node) world positions; the
    ///     receiver finds the local edge whose endpoints match (order-independent, within tolerance)
    ///     and deletes it.
    /// </summary>
    public class NetDeleteCommand : CommandBase
    {
        public float StartX { get; set; }
        public float StartY { get; set; }
        public float StartZ { get; set; }
        public float EndX { get; set; }
        public float EndY { get; set; }
        public float EndZ { get; set; }
    }
}
