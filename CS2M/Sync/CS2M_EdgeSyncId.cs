using System.Collections.Generic;
using System.Runtime.InteropServices;
using Game.Common;
using Game.Net;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     A stable, cross-PC identity for a NET EDGE — the arête analogue of <see cref="CS2M_NodeSyncId"/>.
    ///     The incremental road sync addresses edges only by their endpoint node pair (see
    ///     <see cref="NetBatchApplySystem.FindEdgeById"/>); that is enough while the two nodes agree, but the
    ///     NetSet host-authoritative reconcile (<see cref="NetSetAuthoritySystem"/> /
    ///     <see cref="NetSetApplySystem"/>) needs to say "THIS specific edge of the region survived / was
    ///     replaced / must be deleted" independently of whichever nodes it currently names — so it carries a
    ///     stable edge id the same way <see cref="CS2M_NodeSyncId"/> carries a stable node id. The receiver
    ///     reconciles by that identity: an id it already knows is healed/kept, a host id it lacks is created,
    ///     and a local id-bearing edge the host's set no longer contains is a phantom to delete.
    ///
    ///     Runtime-only (like <see cref="CS2M_SyncId"/> / <see cref="CS2M_NodeSyncId"/>): the map is a cache
    ///     keyed by the shipped id; a miss falls back safely (the caller re-resolves by node-pair identity or
    ///     re-creates), so a stale entry is self-correcting and the id is never persisted to the save.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct CS2M_EdgeSyncId : IComponentData, IQueryTypeParameter
    {
        public ulong m_Id;
    }

    /// <summary>Cross-PC edge-id ↔ local-entity cache. Ids come from <see cref="CS2M_SyncIdSystem.Allocate"/>
    /// (the same globally-unique space as objects and nodes), so host and client never collide.</summary>
    public static class CS2M_EdgeSyncIds
    {
        public static readonly Dictionary<ulong, Entity> Map = new Dictionary<ulong, Entity>();

        /// <summary>Stamp an edge with an id and cache it. Safe to call twice (idempotent).</summary>
        public static void Register(EntityManager em, Entity edge, ulong id)
        {
            if (id == 0 || !em.Exists(edge))
            {
                return;
            }

            if (em.HasComponent<CS2M_EdgeSyncId>(edge))
            {
                em.SetComponentData(edge, new CS2M_EdgeSyncId { m_Id = id });
            }
            else
            {
                em.AddComponentData(edge, new CS2M_EdgeSyncId { m_Id = id });
            }

            Map[id] = edge;
        }

        /// <summary>Sender: return this edge's id, allocating+stamping one if it has none yet.</summary>
        public static ulong Ensure(EntityManager em, Entity edge)
        {
            if (!em.Exists(edge))
            {
                return 0;
            }

            if (em.HasComponent<CS2M_EdgeSyncId>(edge))
            {
                return em.GetComponentData<CS2M_EdgeSyncId>(edge).m_Id;
            }

            ulong id = CS2M_SyncIdSystem.Allocate();
            em.AddComponentData(edge, new CS2M_EdgeSyncId { m_Id = id });
            Map[id] = edge;
            return id;
        }

        /// <summary>Resolve an id to a LIVE edge, or false. A stale/destroyed entry fails the guard and the
        /// caller falls back to node-pair identity — so the cache never needs an explicit rebuild.</summary>
        public static bool TryResolve(EntityManager em, ulong id, out Entity edge)
        {
            edge = Entity.Null;
            if (id == 0)
            {
                return false;
            }

            if (Map.TryGetValue(id, out edge)
                && em.Exists(edge)
                && em.HasComponent<Edge>(edge)
                && !em.HasComponent<Deleted>(edge))
            {
                return true;
            }

            edge = Entity.Null;
            return false;
        }

        public static void Clear()
        {
            Map.Clear();
        }
    }
}
