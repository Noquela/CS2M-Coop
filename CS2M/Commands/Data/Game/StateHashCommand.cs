using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Cheap world-state fingerprint the host broadcasts every ~10 s. Clients compare against
    ///     their own counts; persistent drift means the worlds diverged (a missed command, a
    ///     mid-session join edge case…) and the player is told to run "/resync" in chat. Only
    ///     player-action-driven counts are compared — growables/emergent sim differ by design.
    /// </summary>
    public class StateHashCommand : CommandBase
    {
        public int Edges { get; set; }
        public int SyncedObjects { get; set; }
        public int Districts { get; set; }
        public int WaterSources { get; set; }
    }
}
