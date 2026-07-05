using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player upgrades a net segment (sidewalks, trees, sound barriers, lighting,
    ///     quays, elevation options). The edge is addressed by its two endpoint node positions; the
    ///     receiver finds the matching local edge and writes the same <c>CompositionFlags</c>
    ///     (General/Left/Right, as raw uints).
    ///
    ///     When <see cref="IsNode"/> is true this is a JUNCTION upgrade instead (traffic lights, stop
    ///     signs, roundabout, crosswalks…): the target is a single node addressed by <c>Start</c>, and
    ///     <c>End</c> is unused. The receiver finds the matching node and writes the same flags there.
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
        public bool IsNode { get; set; }

        // Cross-PC identity (CS2M_NodeSyncId). For an edge upgrade the receiver resolves the edge by the
        // endpoint node PAIR first; for a junction (IsNode) upgrade it resolves the node by NodeId. Position
        // stays as the fallback for legacy/save-loaded content (id 0). Same trick as NetDeleteCommand.
        public ulong StartNodeId { get; set; }
        public ulong EndNodeId { get; set; }
        public ulong NodeId { get; set; }
    }
}
