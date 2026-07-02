using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Custom name set/cleared on an entity (buildings and districts in v48; street/line names are
    ///     part of the transport-lines project). Target resolution mirrors scoped policies: SyncId for
    ///     synced buildings, prefab+position for natives, area center for districts.
    /// </summary>
    public class RenameCommand : CommandBase
    {
        /// <summary>1 = building, 2 = district.</summary>
        public byte TargetKind { get; set; }
        public ulong TargetSyncId { get; set; }
        public string TargetPrefabName { get; set; }
        public float TargetX { get; set; }
        public float TargetZ { get; set; }

        /// <summary>The new custom name; null/empty clears back to the generated name.</summary>
        public string Name { get; set; }
    }
}
