using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     A network segment placed by a player (roads, rails, pipes, power lines, fences — all use
    ///     the same net pipeline). We ship the exact live <c>Curve.m_Bezier</c> (4 control points) so
    ///     the other PC rebuilds identical geometry with no curve-fitting, plus the prefab identity,
    ///     per-endpoint elevation and the random seed (so pylons/catenary/details regenerate the same).
    /// </summary>
    public class NetPlaceCommand : CommandBase
    {
        public ulong SyncId { get; set; }

        // Prefab identity.
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public uint Hash0 { get; set; }
        public uint Hash1 { get; set; }
        public uint Hash2 { get; set; }
        public uint Hash3 { get; set; }

        // Bezier4x3 control points a,b,c,d (world space, y = real height).
        public float Ax { get; set; }
        public float Ay { get; set; }
        public float Az { get; set; }
        public float Bx { get; set; }
        public float By { get; set; }
        public float Bz { get; set; }
        public float Cx { get; set; }
        public float Cy { get; set; }
        public float Cz { get; set; }
        public float Dx { get; set; }
        public float Dy { get; set; }
        public float Dz { get; set; }

        // Per-endpoint elevation (Game.Net.Elevation.m_Elevation is float2 per node).
        public float StartElevX { get; set; }
        public float StartElevY { get; set; }
        public float EndElevX { get; set; }
        public float EndElevY { get; set; }

        // Authoritative node positions: Node.m_Position of Edge.m_Start / m_End on the HOST — the
        // SNAPPED junction vertices, which can differ from the raw bezier ends. The receiver
        // shares/creates junction nodes at THESE coords instead of guessing topology by proximity
        // (the fix for junction drift: two triangle vertices getting fused by the 4 m snap radius).
        // HasNodes=false on legacy/relayed commands → receiver falls back to the old bezier path.
        public bool HasNodes { get; set; }
        public float StartNodeX { get; set; }
        public float StartNodeY { get; set; }
        public float StartNodeZ { get; set; }
        public float EndNodeX { get; set; }
        public float EndNodeY { get; set; }
        public float EndNodeZ { get; set; }

        // Stable cross-PC node identities (0 = unknown/legacy). Edges that share a node on the sender
        // carry the SAME id, so the receiver fuses them onto ONE node by identity instead of guessing
        // by proximity — the definitive fix for the "host forged a phantom node at a junction" drift.
        public ulong StartNodeId { get; set; }
        public ulong EndNodeId { get; set; }

        public int RandomSeed { get; set; }
    }
}
