using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Custom name set/cleared on an entity. Target resolution mirrors scoped policies: SyncId for
    ///     synced buildings, prefab+position for natives, area center for districts; transport lines
    ///     (v49) resolve by SyncId falling back to prefab name + RouteNumber (in <see cref="Number"/>).
    /// </summary>
    public class RenameCommand : CommandBase
    {
        /// <summary>1 = building, 2 = district, 3 = transport line.</summary>
        public byte TargetKind { get; set; }

        /// <summary>RouteNumber for TargetKind 3.</summary>
        public int Number { get; set; }
        public ulong TargetSyncId { get; set; }
        public string TargetPrefabName { get; set; }
        public float TargetX { get; set; }
        public float TargetZ { get; set; }

        /// <summary>The new custom name; null/empty clears back to the generated name.</summary>
        public string Name { get; set; }
    }
}
