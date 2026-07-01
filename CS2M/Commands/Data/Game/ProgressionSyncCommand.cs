using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Host broadcasts authoritative city progression (XP). Clients set XP and let their own
    ///     MilestoneSystem advance the level + apply unlocks/rewards — we never inject milestone events
    ///     (that would double-grant dev-tree points). Milestone thresholds are prefab data, identical
    ///     on both PCs, so they're not sent. <c>AchievedMilestone</c> is for validation/logging only.
    /// </summary>
    public class ProgressionSyncCommand : CommandBase
    {
        public int Xp { get; set; }
        public int MaxPopulation { get; set; }
        public int MaxIncome { get; set; }
        public byte XpRewardRecord { get; set; }
        public int AchievedMilestone { get; set; }
    }
}
