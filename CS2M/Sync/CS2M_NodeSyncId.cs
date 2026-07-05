using System.Collections.Generic;
using System.Runtime.InteropServices;
using Game.Common;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     A stable, cross-PC identity for a NET NODE. The junction-sync bug — the host forged phantom
    ///     dead-end nodes ~5-7 m from a junction that the client had fused into a single high-degree node
    ///     — was rooted in the receiver GUESSING which node to fuse an edge endpoint onto BY PROXIMITY.
    ///     That guess is order-dependent: a junction node re-centres as roads connect, so a later piece's
    ///     authoritative coord lands beyond the 3.5 m tight radius, and the 8 m fallback only fires for a
    ///     node that is ALREADY degree>=2 — which it is not yet, because the pieces that make it a junction
    ///     arrive on later frames (the receiver applies one net per frame).
    ///
    ///     Shipping a stable node id and fusing by IDENTITY removes the guess entirely: two edges that
    ///     share a node on the sender carry the SAME id, so the receiver reuses the one node regardless of
    ///     arrival order or how far it re-centred. Proximity (<see cref="Entity"/> lookups) remain only as
    ///     the fallback for legacy commands and pre-existing/save nodes that were never id-stamped.
    ///
    ///     Runtime-only (like <see cref="CS2M_SyncId"/>): the map is a cache keyed by the shipped id; a
    ///     miss falls back safely, so a stale entry is self-correcting.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct CS2M_NodeSyncId : IComponentData, IQueryTypeParameter
    {
        public ulong m_Id;
    }

    /// <summary>Cross-PC node-id ↔ local-entity cache. Ids come from <see cref="CS2M_SyncIdSystem.Allocate"/>
    /// (same globally-unique space as objects), so host and client never collide.</summary>
    public static class CS2M_NodeSyncIds
    {
        public static readonly Dictionary<ulong, Entity> Map = new Dictionary<ulong, Entity>();

        /// <summary>node-id → the HOST's authoritative world position for that node (the shipped bezier
        /// endpoint = the host's actual node coord at send time). CS2 is NOT deterministic across machines,
        /// so a junction re-centres ~&lt;1 m differently on each PC; that sub-metre drift changes zone block
        /// sizes and makes zone paint miss ("DROP noBlock"). NodePinSystem uses this to snap the client's
        /// node back to the host's coord (host-authoritative geometry), which re-derives edges+zone blocks
        /// identically. Populated on the client as net commands apply; cleared on session end.</summary>
        public static readonly Dictionary<ulong, float3> AuthPos = new Dictionary<ulong, float3>();

        /// <summary>Record the host's authoritative position for a node id (client side).</summary>
        public static void SetAuthPos(ulong id, float3 pos)
        {
            if (id != 0)
            {
                AuthPos[id] = pos;
            }
        }

        /// <summary>Stamp a node with an id and cache it. Safe to call twice (idempotent).</summary>
        public static void Register(EntityManager em, Entity node, ulong id)
        {
            if (id == 0 || !em.Exists(node))
            {
                return;
            }

            if (em.HasComponent<CS2M_NodeSyncId>(node))
            {
                em.SetComponentData(node, new CS2M_NodeSyncId { m_Id = id });
            }
            else
            {
                em.AddComponentData(node, new CS2M_NodeSyncId { m_Id = id });
            }

            Map[id] = node;
        }

        /// <summary>Sender: return this node's id, allocating+stamping one if it has none yet. Two edges
        /// sharing the node see the same id (first stamps, second reads it back).</summary>
        public static ulong Ensure(EntityManager em, Entity node)
        {
            if (!em.Exists(node))
            {
                return 0;
            }

            if (em.HasComponent<CS2M_NodeSyncId>(node))
            {
                return em.GetComponentData<CS2M_NodeSyncId>(node).m_Id;
            }

            ulong id = CS2M_SyncIdSystem.Allocate();
            em.AddComponentData(node, new CS2M_NodeSyncId { m_Id = id });
            Map[id] = node;
            return id;
        }

        /// <summary>Resolve an id to a LIVE node, or false. A stale/destroyed entry fails the guard and
        /// the caller falls back to proximity — so the cache never needs an explicit rebuild.</summary>
        public static bool TryResolve(EntityManager em, ulong id, out Entity node)
        {
            node = Entity.Null;
            if (id == 0)
            {
                return false;
            }

            if (Map.TryGetValue(id, out node)
                && em.Exists(node)
                && em.HasComponent<Node>(node)
                && !em.HasComponent<Deleted>(node))
            {
                return true;
            }

            node = Entity.Null;
            return false;
        }

        public static void Clear()
        {
            Map.Clear();
            AuthPos.Clear();
        }
    }
}
