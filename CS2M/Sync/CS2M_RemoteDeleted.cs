using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Zero-size tag added to an entity in the SAME frame a remote command deletes it, so the
    ///     batch capture's removed-edges query (<c>Deleted &amp; !Applied</c>) can skip it and not echo
    ///     the delete back. Deliberately distinct from <see cref="CS2M_RemotePlaced"/>: that tag marks
    ///     "was BUILT by a remote player" and lives for the entity's whole life — excluding it from the
    ///     removed query would also swallow a LOCAL player's legitimate bulldoze of a remote-built road
    ///     (the delete would never ship and the builder would keep a ghost). This tag only ever exists
    ///     on a dying entity (CleanUpSystem destroys Deleted entities at end of frame).
    /// </summary>
    public struct CS2M_RemoteDeleted : IComponentData
    {
    }
}
