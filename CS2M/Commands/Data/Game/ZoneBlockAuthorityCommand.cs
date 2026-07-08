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

        // v63 (flags issue): Game.Zones.BuildOrder.m_Order of the block, the FINAL tiebreak the game's own
        // cell-overlap contest uses between overlapping blocks (decomp CellOverlapJobs.cs:582 — the higher
        // order wins). It comes from a per-machine local counter (GenerateEdgesSystem.cs:1556-1558) so two
        // machines with identical geometry can still tiebreak differently and end up with different
        // Visible/Blocked flags on the same cell. Shipping it lets the client adopt the host's order so
        // its local recompute breaks the tie the same way. Optional (null on old senders — receiver treats
        // missing/short as "no order to adopt").
        public uint[] BuildOrders { get; set; }
    }
}
