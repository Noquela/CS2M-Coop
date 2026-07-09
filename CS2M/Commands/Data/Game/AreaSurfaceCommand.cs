using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Host-authoritative mirror of the SURFACES (the brown "plowed / tilled soil" texture patch inside
    ///     a farm lot) the game's <c>Game.Simulation.AreaSpawnSystem</c> grows as sub-AREAS hanging off an
    ///     Extractor / Storage work area's <c>Game.Areas.SubArea</c> buffer. A Surface is a
    ///     <c>Game.Areas.Surface</c>-tagged area (a soil-textured polygon), NOT an object — so it flows
    ///     through NEITHER of the existing area channels: <c>AreaEditCommand</c> filters to
    ///     Any{Extractor,Storage} (a Surface carries neither) and <c>AreaSubObjectCommand</c> walks the
    ///     <c>Game.Objects.SubObject</c> object graph (a Surface lives in the <c>Game.Areas.SubArea</c> area
    ///     graph). Being sim-derived off per-process RNG (decomp AreaSpawnSystem.cs:148, seed = per-machine
    ///     chunk index) it NEVER matches across PCs, and the client's spawner is suppressed
    ///     (<see cref="CS2M.Sync.AreaSpawnSuppressSystem"/>) — so without this command the client's tilled
    ///     soil stays blank. The host detects each Surface and ships it here; the client materialises it via
    ///     the SAME definition path the game uses (CreationDefinition + a <c>Game.Areas.Node</c> polygon,
    ///     owned by the work area), so it is a real game entity, not a hand-built one.
    /// </summary>
    /// <remarks>
    ///     Anchor scheme mirrors <see cref="AreaSubObjectCommand"/>: the owner is the Extractor/Storage AREA
    ///     that holds the Surface in its <c>Game.Areas.SubArea</c> buffer, addressed by a stable host-minted
    ///     <c>CS2M_SyncId</c> (<see cref="OwnerAnchorId"/>) with prefab-name + centroid as the one-time
    ///     fallback. Per-op fields are flat parallel primitive arrays (MessagePack-friendly, same shape as
    ///     AreaSubObjectCommand). Because each Surface carries a variable-length polygon, the polygons are
    ///     flattened: <see cref="NodeCounts"/> gives the node count per op and <see cref="NodeX"/> …
    ///     <see cref="NodeEl"/> are the concatenation of every op's nodes in order.
    /// </remarks>
    public class AreaSurfaceCommand : CommandBase
    {
        // --- Owner AREA anchor (same identity scheme as AreaSubObjectCommand). ---
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

        /// <summary>Host-minted stable id for each Surface (primary key for a later delete).</summary>
        public ulong[] Ids { get; set; }

        public string[] PrefabTypes { get; set; }
        public string[] PrefabNames { get; set; }

        /// <summary>CreationDefinition.m_RandomSeed → PseudoRandomSeed for each Surface (0 if none).</summary>
        public int[] Seeds { get; set; }

        // --- Flattened polygons (Game.Areas.Node buffer per Surface). ---
        /// <summary>Number of polygon nodes for each op (index-aligned with <see cref="Ops"/>).</summary>
        public int[] NodeCounts { get; set; }

        /// <summary>Concatenation of every op's node positions/elevations, in op order.</summary>
        public float[] NodeX { get; set; }
        public float[] NodeY { get; set; }
        public float[] NodeZ { get; set; }
        public float[] NodeEl { get; set; }
    }
}
