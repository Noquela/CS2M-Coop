using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Zero-size tag component added to every object that we create locally as a
    ///     result of a remote player's placement command. The placement detector puts
    ///     this in its query's <c>None</c> list, so a remotely-applied object is never
    ///     re-detected and echoed back to the other player (the loopback guard).
    /// </summary>
    public struct CS2M_RemotePlaced : IComponentData
    {
    }
}
