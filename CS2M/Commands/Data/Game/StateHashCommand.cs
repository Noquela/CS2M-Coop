using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     World-state fingerprint the host broadcasts every ~10 s. Clients compare against their own
    ///     world; a divergence that PERSISTS after both sides go quiet means the worlds drifted apart
    ///     (a missed command, a mid-session join edge case…) and the player is told to run "/resync".
    ///
    ///     v52: upgraded from bare COUNTS to position-based CONTENT hashes. Counts miss the field-bug
    ///     class Bruno actually hit — same number of roads, but in the wrong place / overlapping / not
    ///     connected. Each *Hash below is an order-independent fingerprint over world positions (world
    ///     coordinates are identical across machines, so no fragile prefab-index assumption): if the
    ///     two PCs' geometry differs at all, the hash differs. Only player-authored state is compared —
    ///     growables and emergent citizen/vehicle sim differ by design and are excluded.
    /// </summary>
    public class StateHashCommand : CommandBase
    {
        // Coarse counts (kept for continuity + a quick human-readable signal in the log).
        public int Edges { get; set; }
        public int SyncedObjects { get; set; }
        public int Districts { get; set; }
        public int WaterSources { get; set; }

        // v52 content fingerprints — the "same count, different geometry" catcher.
        public long EdgeHash { get; set; }
        public int Nodes { get; set; }
        public long NodeHash { get; set; }
        public int Buildings { get; set; }
        public long BuildingHash { get; set; }
        public int ZoneBlocks { get; set; }
        public long ZoneHash { get; set; }
        public long AreaHash { get; set; }
        public int Money { get; set; }
    }
}
