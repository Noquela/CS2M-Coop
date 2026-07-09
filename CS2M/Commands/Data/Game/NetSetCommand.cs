using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     NetSet (CS2M_NETSET, host-authoritative): the SETTLED authoritative node+edge SET of one region of
    ///     the road graph, shipped by the host so a client can force its own graph to match BY IDENTITY. This
    ///     is the network analogue of <see cref="ZoneBlockAuthorityCommand"/> ("host owns the grid" for zone
    ///     blocks): the incremental road sync (NetBatchCommand) still runs, and NetSet is a CORRECTION LAYER
    ///     on top of it — the host periodically re-asserts the real, post-settle state of a dirty region and
    ///     the client reconciles (heal matched, create missing, delete phantoms) so complex junctions that the
    ///     incremental rebuild folds wrong converge instead of drifting.
    ///
    ///     Addressed by STABLE identity: nodes by <see cref="CS2M_NodeSyncId"/> (<see cref="NodeIds"/>), edges
    ///     by <see cref="CS2M_EdgeSyncId"/> (<see cref="EdgeIds"/>) plus the two endpoint node ids
    ///     (<see cref="EdgeStartId"/>/<see cref="EdgeEndId"/>). Positions/geometry are carried too so the
    ///     client can create a missing node/edge, but the SET membership decision is by id, never proximity.
    ///
    ///     Wire format matches every other command here: flat parallel primitive arrays (MessagePack, no
    ///     nested structs), indexed positionally. One command carries ONE region (its bbox is
    ///     <see cref="MinX"/>..<see cref="MaxZ"/>); a large region is sliced into several commands, each of
    ///     which is self-contained (it carries every endpoint node its edges reference).
    /// </summary>
    public class NetSetCommand : CommandBase
    {
        // Region bounding box (world XZ). The client scopes its conservative phantom-deletion to this box.
        public float MinX { get; set; }
        public float MinZ { get; set; }
        public float MaxX { get; set; }
        public float MaxZ { get; set; }

        // ---- NODES (parallel arrays, length = node count) --------------------------------------------
        // Stable node identity (CS2M_NodeSyncId). Every endpoint of every edge in this command appears here,
        // even endpoints that fall outside the bbox — otherwise the client couldn't resolve that edge's end.
        public ulong[] NodeIds { get; set; }

        // Node.m_Position (authoritative).
        public float[] NX { get; set; }
        public float[] NY { get; set; }
        public float[] NZ { get; set; }

        // ---- EDGES (parallel arrays, length = edge count) --------------------------------------------
        // Stable edge identity (CS2M_EdgeSyncId).
        public ulong[] EdgeIds { get; set; }

        // The two endpoint node ids (CS2M_NodeSyncId) — always present in NodeIds above.
        public ulong[] EdgeStartId { get; set; }
        public ulong[] EdgeEndId { get; set; }

        // Prefab identity (type + name; the wire hash is always zero, as with NetPlaceCommand/NetBatchCommand).
        public string[] EdgePrefabType { get; set; }
        public string[] EdgePrefab { get; set; }

        // Game.Net.Road.m_Flags (byte) per edge. Carries the StartHalfAligned/EndHalfAligned bits, which are a
        // per-machine LATCH computed at build from length parity + prior value (decomp GenerateEdgesSystem.cs:
        // 1061/1070) — so two machines can end with the SAME final curve but DIFFERENT half-aligned bits, which
        // shifts the zone half-cell (BlockSystem.cs:191-192/780-785 reads exactly these bits) even after the
        // geometry converges. Forcing the curve can't cure it; the client adopts these bits explicitly. The
        // client masks IN only the half-aligned bits and preserves its own lighting bits (IsLit/AlwaysLit/
        // LightsOff). 0 for a non-road net (no Road component) — the client guards HasComponent<Road>. Optional
        // (null on an older sender — receiver treats missing as "no flags to adopt").
        public byte[] EdgeRoadFlags { get; set; }

        // Curve.m_Bezier control points a,b,c,d (the client pins a/d onto the endpoint nodes on create).
        public float[] Ax { get; set; }
        public float[] Ay { get; set; }
        public float[] Az { get; set; }
        public float[] Bx { get; set; }
        public float[] By { get; set; }
        public float[] Bz { get; set; }
        public float[] Cx { get; set; }
        public float[] Cy { get; set; }
        public float[] Cz { get; set; }
        public float[] Dx { get; set; }
        public float[] Dy { get; set; }
        public float[] Dz { get; set; }
    }
}
