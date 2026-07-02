using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Bulldoze an object on the other PCs. Synced objects are named by their cross-PC
    ///     <c>CS2M_SyncId</c>; NATIVE objects (from the save, no id — v42) are addressed by
    ///     prefab + position: when <c>SyncId == 0</c> the receiver deletes the nearest match.
    /// </summary>
    public class DeleteCommand : CommandBase
    {
        public ulong SyncId { get; set; }

        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
    }
}
