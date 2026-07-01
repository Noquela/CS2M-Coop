using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>Relocate a synced object on the other PCs to a new transform, named by <c>CS2M_SyncId</c>.</summary>
    public class MoveCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float RotW { get; set; }
    }
}
