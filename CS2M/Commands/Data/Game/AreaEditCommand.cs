using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Edit of a building-owned work area (a farm's field, a mine's dig zone…): ships the full
    ///     polygon + the owner's identity. The receiver rewrites the matching owned area's nodes (or
    ///     creates it if missing) and the game's area systems re-triangulate the visual.
    /// </summary>
    public class AreaEditCommand : CommandBase
    {
        public ulong OwnerSyncId { get; set; }
        public string OwnerPrefabName { get; set; }
        public float OwnerX { get; set; }
        public float OwnerY { get; set; }
        public float OwnerZ { get; set; }

        public string PrefabType { get; set; }
        public string PrefabName { get; set; }

        public float[] Xs { get; set; }
        public float[] Ys { get; set; }
        public float[] Zs { get; set; }
        public float[] Els { get; set; }
    }
}
