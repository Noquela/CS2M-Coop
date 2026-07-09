using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Host-authoritative mirror of the <c>Game.Areas.Extractor</c> component of every farm / forestry /
    ///     ore work area. The Extractor is the DRIVER of the field's tilled-soil / crop coverage: the game's
    ///     <c>Game.Simulation.AreaSpawnSystem</c> grows sub-objects until the covered area reaches
    ///     <c>AreaUtils.CalculateExtractorObjectArea(geometry, extractor, …)</c> — a function of
    ///     <c>m_TotalExtracted</c> / <c>m_ExtractedAmount</c> (decomp AreaSpawnSystem.cs:182-188). That
    ///     extraction sim runs INDEPENDENTLY on each machine, so the field grows to a different size on host
    ///     vs client. Mirroring the Extractor (host → client, host wins) makes the client's field TARGET the
    ///     host's size; combined with un-suppressing the client's AreaSpawnSystem (CS2M_AREAGROW) the client
    ///     field then fills to that same size locally (crop POSITIONS use per-chunk RNG so they differ, but
    ///     the mancha SIZE matches — the only thing that matters, and invisible to the radar: crops are
    ///     Owner-ed sub-objects/surfaces, excluded from the StateHash contract — decomp StateHashSystems
    ///     AreaInContract).
    /// </summary>
    /// <remarks>
    ///     Batch of areas as flat parallel primitive arrays (index-aligned, MessagePack-friendly — same shape
    ///     as AreaSubObjectCommand / AreaSurfaceCommand). Each area is addressed by its stable host-minted
    ///     <c>CS2M_SyncId</c> (<see cref="AreaIds"/>), with prefab name + polygon centroid as the one-time
    ///     fallback matcher (a client that resolved the area through the world transfer / area sync rather
    ///     than this command). The seven per-area floats/int are the whole <c>Extractor</c> struct.
    /// </remarks>
    public class ExtractorSyncCommand : CommandBase
    {
        // --- Per-area anchor (same identity scheme as AreaSurfaceCommand's owner anchor). ---
        /// <summary>Stable id of each Extractor AREA (host-minted <c>CS2M_SyncId</c>).</summary>
        public ulong[] AreaIds { get; set; }

        /// <summary>Prefab name of each area — the one-time fallback matcher on the client.</summary>
        public string[] AreaPrefabNames { get; set; }

        /// <summary>Area polygon centroid (fallback position when the id is not yet known).</summary>
        public float[] CenterX { get; set; }
        public float[] CenterZ { get; set; }

        // --- The Game.Areas.Extractor struct, field-per-array (index-aligned with AreaIds). ---
        public float[] ResourceAmount { get; set; }
        public float[] MaxConcentration { get; set; }
        public float[] ExtractedAmount { get; set; }
        public float[] WorkAmount { get; set; }
        public float[] HarvestedAmount { get; set; }
        public float[] TotalExtracted { get; set; }

        /// <summary><c>Game.Vehicles.VehicleWorkType</c> as int.</summary>
        public int[] WorkType { get; set; }
    }
}
