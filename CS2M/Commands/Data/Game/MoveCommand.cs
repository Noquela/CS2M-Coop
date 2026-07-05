using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Relocate an object on the other PCs to a new transform. Synced objects resolve by
    ///     <c>CS2M_SyncId</c>. v48: NATIVES (service buildings from the save, no id yet) resolve by
    ///     prefab + OLD position — captured while the move tool drags (<c>Temp.m_Original</c> cache) —
    ///     and both sides then REGISTER the shipped SyncId (first-touch identity), so every later
    ///     edit is id-addressed.
    /// </summary>
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

        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public float OldX { get; set; }
        public float OldY { get; set; }
        public float OldZ { get; set; }

        /// <summary>v55: relocating an installed service UPGRADE/extension (an Owner-bearing sub-object the
        /// SyncId/native paths both exclude). The upgrade carries no shared id, so the receiver resolves the
        /// OWNER (SyncId else prefab+position) then picks the child whose prefab is <see cref="PrefabName"/>
        /// nearest the OLD position, and sets its transform.</summary>
        public bool IsOwnedUpgrade { get; set; }

        public ulong OwnerSyncId { get; set; }
        public string OwnerPrefabName { get; set; }
        public float OwnerX { get; set; }
        public float OwnerY { get; set; }
        public float OwnerZ { get; set; }
    }
}
