using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     ZoneBlockAuthority (CS2M_ZONEAUTH, ON by default since 2026-07-07): the host mirrors its own zone Block geometry+cells so a
    ///     client's locally-derived block (same road, but overlap desync tiebreak by BuildOrder/Entity.Index
    ///     picks a different width — see docs/zoneauth-spec.md) converges to the host's authoritative shape.
    ///     Blocks are addressed by (owning edge identity, side, ordinal) rather than raw position, because
    ///     the whole point is that positions/sizes can legitimately differ before the heal. Arrays are
    ///     indexed positionally, one entry per changed block (flat parallel arrays, MessagePack, no nested
    ///     structs — same wire style as NetBatchCommand/ZonePaintCommand).
    /// </summary>
    public class ZoneBlockAuthorityCommand : CommandBase
    {
        // Owning edge identity (CS2M_NodeSyncId of the edge's two nodes) — one pair per block entry.
        public ulong[] EdgeStartIds { get; set; }
        public ulong[] EdgeEndIds { get; set; }

        // Which side of the edge the block sits on (+1/-1) and its position among same-side blocks of the
        // same edge, ordered by t (projection of the block center onto the start->end axis).
        public sbyte[] Sides { get; set; }
        public int[] Ordinals { get; set; }

        // Block.m_Position (authoritative).
        public float[] PosX { get; set; }
        public float[] PosY { get; set; }
        public float[] PosZ { get; set; }

        // Block.m_Direction.
        public float[] DirX { get; set; }
        public float[] DirZ { get; set; }

        // Block.m_Size.
        public int[] SizeX { get; set; }
        public int[] SizeY { get; set; }

        // Window into CellZonePool for this block's cells (row-major, SizeX*SizeY entries).
        public int[] CellsOffset { get; set; }
        public int[] CellsCount { get; set; }

        // Unique zone names referenced by this command's cells ("" = None/Unzoned).
        public string[] ZonePool { get; set; }

        // Flattened cell zones for every block in this command; value = index into ZonePool.
        public int[] CellZonePool { get; set; }

        // v65 (flags issue, cell-overlap hysteresis): the host's Game.Zones.Cell.m_State for every cell in
        // this command, aligned 1:1 with CellZonePool (same CellsOffset/CellsCount window per block). Only
        // the STABLE bits of the game's overlap contest are meaningful cross-machine (Blocked=1, Shared=2,
        // Roadside=4, Visible=8, Overridden=0x10, Redundant=0x80, RoadLeft/Right/Back=0x200/0x400/0x800) —
        // the sender pre-masks out Occupied=0x20 (flips independently whenever a building spawns/despawns,
        // a channel this authority doesn't own and would otherwise re-trigger sends constantly), Selected=
        // 0x40 (pure per-machine UI state) and Updating=0x100 (a transient mid-recompute marker). See
        // ZoneFlagAssert / ZoneFlagAssertSystem (ZoneBlockAuthoritySystems.cs) for why flags need
        // CONTINUOUS re-assertion rather than a one-shot write: CellCheckSystem only recomputes flags when
        // a block/neighbor gets Updated (decomp CellCheckSystem.cs:186, CollectUpdatedBlocks:311-395), and
        // the contest itself has hysteresis (an already-resolved/Visible cell wins — decomp
        // CellOverlapJobs.cs:559-562,578-581), so a machine with a different local history keeps
        // re-deriving a different (but locally "stable") equilibrium even after geometry/cells/BuildOrder
        // converge. Optional (null on old senders — receiver treats missing/short as "no flags to adopt").
        public ushort[] CellStatePool { get; set; }

        // v63 (flags issue): Game.Zones.BuildOrder.m_Order of the block, the FINAL tiebreak the game's own
        // cell-overlap contest uses between overlapping blocks (decomp CellOverlapJobs.cs:582 — the higher
        // order wins). It comes from a per-machine local counter (GenerateEdgesSystem.cs:1556-1558) so two
        // machines with identical geometry can still tiebreak differently and end up with different
        // Visible/Blocked flags on the same cell. Shipping it lets the client adopt the host's order so
        // its local recompute breaks the tie the same way. Optional (null on old senders — receiver treats
        // missing/short as "no order to adopt").
        public uint[] BuildOrders { get; set; }

        // v64 (split-junction drift, 2-sim statediff 07/07): the owning edge's two node XZ positions, ALWAYS
        // filled in by the host — one entry per block, same value repeated for every block of one edge. A
        // node born from a road SPLIT never gets a CS2M_NodeSyncId, so EdgeStartIds/EdgeEndIds can be 0 for
        // such an edge; these positions are the fallback identity the client uses to find the edge and to
        // validate/re-pair its (side, ordinal) block match when it can't trust the id. Match tolerance on
        // the receiving end is +-4 m — split-junction nodes have been observed to land ~1-2 m apart between
        // host and client (root cause: NodePinSystem's node-recentre snap is DISABLED, see NodePinSystem.cs
        // — forcing a node's position back is a known dead end, so this authority TOLERATES the drift
        // instead of trying to erase it).
        public float[] EdgeStartXs { get; set; }
        public float[] EdgeStartZs { get; set; }
        public float[] EdgeEndXs { get; set; }
        public float[] EdgeEndZs { get; set; }

        // v66 ("host owns the grid", CS2M_ZONESET): true = this block belongs to a group the host shipped
        // COMPLETE (every block of one (edge, side) group, because SOME member changed since the last send).
        // The client then RECONCILES that whole set instead of healing block-by-block: it heals the blocks
        // that match, CREATES the ones that are missing locally (cloning a sibling block — never a hand-built
        // archetype, per the project law), and DELETES its local phantoms that the host's set has no slot for.
        // This is the only model that converges when the two machines' BlockSystem derivations disagree on the
        // SET of blocks (host 228 vs client 224 with bit-identical roads — the derivation is non-deterministic
        // cross-machine on curves/tangents/timing, so a delta-of-existing-blocks heal can never add a missing
        // block or remove a ghost). Per-block (parallel with every other array here). Optional: null on a v65
        // (or older) sender — the receiver then falls back to the v65 ordinal/position heal (no create/delete).
        public bool[] GroupComplete { get; set; }
    }
}
