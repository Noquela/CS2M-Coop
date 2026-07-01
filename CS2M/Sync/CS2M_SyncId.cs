using System.Runtime.InteropServices;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     A stable, cross-PC identity for a synced object/net. DOTS <c>Entity{Index,Version}</c>
    ///     is machine-local, so to sync delete/move/upgrade of a specific thing both PCs must agree
    ///     on WHICH thing. We stamp this id (identical on both PCs) on every entity we create from a
    ///     remote command, and on the sender's own entity when its placement is detected.
    ///
    ///     Runtime-only (deliberately NOT ISerializable): a serializer would change the chunk stride
    ///     and the runtime map would be stale after a save/load anyway — we rebuild the map from the
    ///     query instead.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct CS2M_SyncId : IComponentData, IQueryTypeParameter
    {
        public ulong m_Id;
    }
}
