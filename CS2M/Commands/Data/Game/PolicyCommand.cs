using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Toggle a policy. Identified by prefab type+name (cross-PC stable). The receiver raises the
    ///     same policy-change event the game's UI raises (<c>Event</c>+<c>Modify</c>).
    ///     v46: scoped targets — 0 = city (default), 1 = a specific BUILDING ("empty landfill",
    ///     school programs…), 2 = a DISTRICT. Buildings resolve by SyncId or position; districts by
    ///     their area center.
    /// </summary>
    public class PolicyCommand : CommandBase
    {
        public string PolicyType { get; set; }
        public string PolicyName { get; set; }
        public bool Active { get; set; }
        public float Adjustment { get; set; }

        public byte TargetKind { get; set; }
        public ulong TargetSyncId { get; set; }
        public float TargetX { get; set; }
        public float TargetZ { get; set; }
    }
}
