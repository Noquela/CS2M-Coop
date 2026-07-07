using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v60 auto-heal: the host's COMPLETE authoritative water-source list (a city has dozens of
    ///     sources at most — a few KB). The client reconciles: creates the missing, deletes the extra,
    ///     rewrites diverged params in place. Idempotent — a client already in sync applies a no-op.
    ///     Host-originated broadcast (mirror of host-owned state), like ZoneBlockAuthorityCommand.
    /// </summary>
    public class WaterHealCommand : CommandBase
    {
        public float[] PosX { get; set; }
        public float[] PosY { get; set; }
        public float[] PosZ { get; set; }
        public float[] Radius { get; set; }
        public float[] Height { get; set; }
        public float[] Multiplier { get; set; }
        public float[] Polluted { get; set; }
        public int[] ConstantDepth { get; set; }

        /// <summary>v62 (issue #9): per-source cross-PC identity (0 = save-loaded source). Lets the
        /// client reconcile by ID with the SAME identity rules as WaterApplySystem (10 m fallback)
        /// instead of the old 2 m-only proximity match that could duplicate a drifted source.</summary>
        public ulong[] SyncIds { get; set; }
    }
}
