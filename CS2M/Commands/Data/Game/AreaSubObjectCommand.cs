using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Host-authoritative mirror of the SUB-OBJECTS the game's <c>Game.Simulation.AreaSpawnSystem</c>
    ///     grows inside an Extractor / Storage work area (a farm field's crops/animals, a mine or cargo
    ///     yard's resource piles). That system keys its RNG off the per-machine chunk index
    ///     (decomp AreaSpawnSystem.cs:148) so it NEVER produces the same objects on two PCs, which is why
    ///     the client suppresses it entirely (<see cref="CS2M.Sync.AreaSpawnSuppressSystem"/>). Without
    ///     this command the suppressed client's field stays visually empty. The host detects what it grew
    ///     and ships it here as a batch of create/delete ops the client materialises via the SAME
    ///     definition path the game uses (CreationDefinition + ObjectDefinition), so the objects are real
    ///     game entities, not hand-built ones.
    /// </summary>
    /// <remarks>
    ///     Anchor scheme mirrors <see cref="AreaEditCommand"/>: the owner is the AREA entity itself
    ///     (the sub-objects' <c>Owner.m_Owner</c>). It is addressed by a stable, host-minted
    ///     <c>CS2M_SyncId</c> (<see cref="OwnerAnchorId"/>) once known, and by prefab name + centroid as
    ///     the one-time fallback for a client that resolved the area through the world transfer / area
    ///     sync rather than this command. Per-op fields are flat parallel primitive arrays
    ///     (MessagePack-friendly, same shape as NetBatchCommand / ZoneBlockAuthorityCommand).
    /// </remarks>
    public class AreaSubObjectCommand : CommandBase
    {
        // --- Owner AREA anchor (same identity scheme as AreaEditCommand). ---
        /// <summary>Stable id of the owning Extractor/Storage AREA (host-minted <c>CS2M_SyncId</c>).</summary>
        public ulong OwnerAnchorId { get; set; }

        /// <summary>Prefab name of the owning area — the one-time fallback matcher on the client.</summary>
        public string OwnerAnchorPrefabName { get; set; }

        /// <summary>Owning area polygon centroid (fallback position when the id is not yet known).</summary>
        public float OwnerX { get; set; }
        public float OwnerY { get; set; }
        public float OwnerZ { get; set; }

        /// <summary>Stable id of the building the area belongs to, 0 if none — a secondary hint only.</summary>
        public ulong BuildingSyncId { get; set; }

        // --- Per-op arrays (index-aligned). ---
        /// <summary>0 = create, 1 = delete.</summary>
        public byte[] Ops { get; set; }

        /// <summary>Host-minted stable id for each sub-object (primary key for a later delete).</summary>
        public ulong[] Ids { get; set; }

        public string[] PrefabTypes { get; set; }
        public string[] PrefabNames { get; set; }

        // World transform (Game.Objects.Transform) of each sub-object.
        public float[] PosX { get; set; }
        public float[] PosY { get; set; }
        public float[] PosZ { get; set; }
        public float[] RotX { get; set; }
        public float[] RotY { get; set; }
        public float[] RotZ { get; set; }
        public float[] RotW { get; set; }

        /// <summary>ObjectDefinition.m_Elevation for each sub-object (usually 0 — on-ground).</summary>
        public float[] Elevation { get; set; }

        /// <summary>CreationDefinition.m_RandomSeed → PseudoRandomSeed, so mesh/colour variation matches.</summary>
        public int[] Seeds { get; set; }
    }
}
