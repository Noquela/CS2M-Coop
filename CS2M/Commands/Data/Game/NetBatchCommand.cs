using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     AtomicBatch (CS2M_ATOMIC=1): the whole RESULT of one net-tool apply, shipped as ONE packet and
    ///     replayed atomically on the receiver — a save/load in miniature. Instead of re-running the tool
    ///     per-segment (which lets <c>NodeAlignSystem</c> re-centre junctions from a DIFFERENT connected set
    ///     on each PC and drift the nodes &lt;1 m→14 m apart, breaking zone-block matching), the builder sends
    ///     every new node, new edge, the edges the split removed, and the ids of the pre-existing boundary
    ///     nodes that gained an arm. The receiver creates them directly from the prefab archetype in a single
    ///     frame, so the whole net pipeline (align/composition/geometry/lanes/zone blocks) re-derives from an
    ///     IDENTICAL, COMPLETE input set and converges.
    ///
    ///     Wire format follows the existing commands: flat parallel arrays of primitives (MessagePack), no
    ///     nested structs (Opção A of the design freeze). Arrays are indexed positionally:
    ///     <c>NodeIds[i]</c> pairs with <c>NodePosX[i]</c>, etc.
    /// </summary>
    public class NetBatchCommand : CommandBase
    {
        // ---- NEW NODES (parallel arrays, length = node count) ------------------------------------
        // Stable cross-PC node identity allocated by the builder; the receiver stamps the same id onto
        // the node it creates so sibling edges (this batch or later) fuse by identity, not proximity.
        public ulong[] NodeIds { get; set; }

        // Node.m_Position (authoritative, read from the Node COMPONENT — never the bezier end).
        public float[] NodePosX { get; set; }
        public float[] NodePosY { get; set; }
        public float[] NodePosZ { get; set; }

        // Node.m_Rotation (quaternion).
        public float[] NodeRotX { get; set; }
        public float[] NodeRotY { get; set; }
        public float[] NodeRotZ { get; set; }
        public float[] NodeRotW { get; set; }

        // Prefab identity (type + name; the wire hash is always zero, as with NetPlaceCommand).
        public string[] NodePrefabTypes { get; set; }
        public string[] NodePrefabNames { get; set; }

        // Conditional components mirrored from the builder's real entity (presence + value).
        public bool[] NodeHasStandalone { get; set; }
        public bool[] NodeHasElevation { get; set; }
        public float[] NodeElevX { get; set; }
        public float[] NodeElevY { get; set; }

        // PseudoRandomSeed.m_Seed (ushort widened to int; deterministic pylons/catenary/details).
        public int[] NodeSeeds { get; set; }

        // ---- NEW EDGES (parallel arrays, length = edge count) ------------------------------------
        // Endpoint node identities: an id from NodeIds (a new node) OR a boundary node id.
        public ulong[] EdgeStartNodeIds { get; set; }
        public ulong[] EdgeEndNodeIds { get; set; }

        // Curve.m_Bezier control points a,b,c,d (world space). m_Length is DERIVED on apply.
        public float[] EdgeAX { get; set; }
        public float[] EdgeAY { get; set; }
        public float[] EdgeAZ { get; set; }
        public float[] EdgeBX { get; set; }
        public float[] EdgeBY { get; set; }
        public float[] EdgeBZ { get; set; }
        public float[] EdgeCX { get; set; }
        public float[] EdgeCY { get; set; }
        public float[] EdgeCZ { get; set; }
        public float[] EdgeDX { get; set; }
        public float[] EdgeDY { get; set; }
        public float[] EdgeDZ { get; set; }

        public string[] EdgePrefabTypes { get; set; }
        public string[] EdgePrefabNames { get; set; }

        // Conditional Upgraded (CompositionFlags: General / Side left / Side right), presence-flagged.
        public bool[] EdgeHasUpgraded { get; set; }
        public uint[] EdgeUpgradedG { get; set; }
        public uint[] EdgeUpgradedL { get; set; }
        public uint[] EdgeUpgradedR { get; set; }

        // Conditional Elevation (Game.Net.Elevation.m_Elevation is float2), presence-flagged.
        public bool[] EdgeHasElevation { get; set; }
        public float[] EdgeElevX { get; set; }
        public float[] EdgeElevY { get; set; }

        public int[] EdgeSeeds { get; set; }

        // BuildOrder{m_Start,m_End} (uint) — the SAME values on both PCs give deterministic lane/block order.
        public uint[] EdgeBuildOrderStart { get; set; }
        public uint[] EdgeBuildOrderEnd { get; set; }

        // ---- REMOVED EDGES (the split cut them; delete by node-pair identity) ---------------------
        public ulong[] DelStartNodeIds { get; set; }
        public ulong[] DelEndNodeIds { get; set; }
        // World positions of the removed edge's endpoints. Used for the SKIP/DROP log AND — since save-loaded
        // edges carry no CS2M_NodeSyncId on either PC (id=0/0) — as the receiver's position-based fallback
        // resolution when FindEdgeById can't match (see NetBatchApplySystem.FindEdgeByPosition). Y included so
        // that fallback compares the full 3D Node.m_Position, not just the XZ plan.
        public float[] DelStartX { get; set; }
        public float[] DelStartY { get; set; }
        public float[] DelStartZ { get; set; }
        public float[] DelEndX { get; set; }
        public float[] DelEndY { get; set; }
        public float[] DelEndZ { get; set; }

        // ---- BOUNDARY (pre-existing nodes referenced by new edges; NEVER their data) --------------
        public ulong[] BoundaryNodeIds { get; set; }
        // The builder's settled position of each boundary node. Used ONLY as the id-miss fallback for
        // SAVE-loaded nodes (no CS2M_NodeSyncId on either PC): save geometry is byte-identical on both
        // machines (same file at join), so a STRICT (<0.5 m) position match is sound there — unlike the
        // loose proximity guessing this architecture retired for session content.
        public float[] BoundaryPosX { get; set; }
        public float[] BoundaryPosY { get; set; }
        public float[] BoundaryPosZ { get; set; }
    }
}
