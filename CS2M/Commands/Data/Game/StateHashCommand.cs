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

        // v55: transport lines — count + a fingerprint over each line's number and waypoint positions.
        // Routes were fully invisible to the radar, so a reroute/create/delete that failed to sync (e.g.
        // the save-loaded-line reroute gap) produced no drift warning. Waypoint positions are world coords,
        // identical across machines like every other fingerprint here.
        public int Routes { get; set; }
        public long RouteHash { get; set; }

        // v55: City ServiceFee buffer fingerprint — a fee-only divergence moves no entity, so it was
        // invisible before; fees drive consumption/happiness/income so a drift here is a real sim desync.
        public long FeeHash { get; set; }

        // v55: tax rates (per-category) — a tax desync would only surface slowly via money before.
        public long TaxHash { get; set; }

        // v55: City policy buffer (active + adjustment by prefab name) — policies drove the sim invisibly
        // to the radar; a policy toggled on one PC but not the other now shows as drift.
        public long PolicyHash { get; set; }

        // v55: water source POSITIONS — the count alone missed a relocated source (water-move).
        public long WaterHash { get; set; }

        // v59: service-budget sliders (per service prefab NAME) — budget sync existed but the radar
        // never observed it, so a missed BudgetCommand diverged the sim's money flow silently.
        public long BudgetHash { get; set; }

        // v59: Loan.m_Amount on the City — same blind spot as budget (m_LastModified is a per-machine
        // frame index and is deliberately NOT folded).
        public long LoanHash { get; set; }

        // v59: coarse heightmap fingerprint (32×32 samples, 2 m quantum) — terrain was classified
        // WorldContract yet fully invisible to the radar (dossier terrain.md §6.6). Terrain replay is
        // best-effort by design, so the quantum is deliberately coarse: sub-2 m residue stays quiet,
        // a mountain that never crossed over lights the /resync alert.
        public long TerrainHash { get; set; }
    }
}
