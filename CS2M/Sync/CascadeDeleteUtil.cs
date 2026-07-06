using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     v51 FIELD FIX ("part of the building stays on the ground"): remotely-created buildings get
    ///     their sub-nets/sub-areas from OUR Permanent definitions, and those children aren't always
    ///     registered in the parent's Sub* buffers — so the game's own delete cascade misses them and
    ///     bulldozing the building on another PC leaves yards/paths/lights behind. Deleting through
    ///     here marks every live child (Owner == target, recursively) Deleted in the same frame.
    ///
    ///     v56: this is only ever called from remote-apply paths (<c>RemoteEditApplySystem.ApplyDelete</c>,
    ///     <c>RemotePlacementApplySystem</c>) — the target AND every cascaded child get stamped
    ///     <c>CS2M_RemoteDeleted</c> here (the delete-echo tag, same pattern the net path uses), otherwise
    ///     an entity that independently carries a <c>CS2M_SyncId</c> (e.g. an installed service-upgrade
    ///     extension wiped out because its owning building got deleted) would look like a fresh LOCAL
    ///     delete to <c>DeleteDetectorSystem</c> and get re-broadcast — an echo. Deliberately NOT
    ///     <c>CS2M_RemotePlaced</c>: that tag marks remote CREATION and lives for the entity's whole
    ///     life, so using it for delete-echo would swallow a local player's delete of anything a remote
    ///     player built (the exact conflation CS2M_RemoteDeleted.cs documents for nets).
    /// </summary>
    internal static class CascadeDeleteUtil
    {
        public static void DeleteWithChildren(EntityManager em, Entity target)
        {
            if (!em.HasComponent<Deleted>(target))
            {
                em.AddComponent<Deleted>(target);
            }

            if (!em.HasComponent<CS2M_RemoteDeleted>(target))
            {
                em.AddComponent<CS2M_RemoteDeleted>(target);
            }

            // One query pass; recurse via repeated sweeps (children of children) — depth is tiny.
            for (int depth = 0; depth < 3; depth++)
            {
                bool any = false;
                EntityQuery owned = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Owner>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
                });
                NativeArray<Entity> ents = owned.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity child in ents)
                    {
                        Entity owner = em.GetComponentData<Owner>(child).m_Owner;
                        if (owner == Entity.Null || !em.Exists(owner))
                        {
                            continue;
                        }

                        // child of the target (or of an already-cascaded child)
                        if (owner == target || (em.HasComponent<Deleted>(owner)
                                                && IsUnder(em, owner, target, 4)))
                        {
                            em.AddComponent<Deleted>(child);
                            if (!em.HasComponent<CS2M_RemoteDeleted>(child))
                            {
                                em.AddComponent<CS2M_RemoteDeleted>(child);
                            }

                            any = true;
                        }
                    }
                }
                finally
                {
                    ents.Dispose();
                }

                if (!any)
                {
                    break;
                }
            }
        }

        private static bool IsUnder(EntityManager em, Entity e, Entity root, int maxHops)
        {
            for (int i = 0; i < maxHops; i++)
            {
                if (e == root)
                {
                    return true;
                }

                if (!em.HasComponent<Owner>(e))
                {
                    return false;
                }

                e = em.GetComponentData<Owner>(e).m_Owner;
                if (e == Entity.Null || !em.Exists(e))
                {
                    return false;
                }
            }

            return false;
        }
    }
}
