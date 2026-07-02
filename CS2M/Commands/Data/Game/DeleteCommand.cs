using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Bulldoze an object on the other PCs. Synced objects are named by their cross-PC
    ///     <c>CS2M_SyncId</c>; NATIVE objects (from the save, no id — v42) are addressed by
    ///     prefab + position: when <c>SyncId == 0</c> the receiver deletes the nearest match.
    ///     v49: <c>TargetKind == 1</c> deletes a TRANSPORT LINE — resolved by SyncId, falling back to
    ///     prefab name + RouteNumber (<see cref="Number"/>); the game cascades waypoints/segments.
    /// </summary>
    public class DeleteCommand : CommandBase
    {
        public ulong SyncId { get; set; }

        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }

        /// <summary>0 = object (default), 1 = transport line/route.</summary>
        public byte TargetKind { get; set; }

        /// <summary>RouteNumber for TargetKind 1 (save-loaded lines have no SyncId).</summary>
        public int Number { get; set; }
    }
}
