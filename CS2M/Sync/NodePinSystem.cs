using System;
using CS2M.API.Networking;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     HOST-AUTHORITATIVE NODE GEOMETRY (04/07). CS2 is not deterministic across machines, so a junction
    ///     re-centres ~&lt;1 m differently on the host vs the client even when both build from the identical
    ///     shipped bezier. That sub-metre drift is invisible on the road itself, but it changes the SIZE of
    ///     the zone blocks the road generates, so the client can't match the host's zone-paint command and
    ///     drops it ("[Zone] DROP noBlock") — Bruno's "zones don't sync". It also keeps areas(nodes/roads)
    ///     hash drifting forever.
    ///
    ///     Fix: on the CLIENT, snap each identified node (<see cref="CS2M_NodeSyncId"/>) back to the host's
    ///     authoritative coord (<see cref="CS2M_NodeSyncIds.AuthPos"/>, the shipped bezier endpoint) and mark
    ///     it + its edges Updated so the game re-derives the edge geometry and zone blocks from the host's
    ///     position — making roads byte-identical and zones matchable. The host is untouched (it IS the
    ///     authority). Only pins a node that has drifted &gt; 0.2 m, throttled, so a settled junction is left
    ///     alone once converged.
    ///
    ///     Gated OFF by default (env <c>CS2M_NODEPIN=1</c>) until validated on a 2-sim — forcing node
    ///     positions could fight the game if a junction is still being actively re-shaped, so it ships dormant.
    /// </summary>
    public partial class NodePinSystem : GameSystemBase
    {
        private int _counter;
        private int _diagCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            CS2M.Log.Info("[Pin] NodePinSystem created");
        }

        protected override void OnUpdate()
        {
            // DISABLED (04/07). Pinning the node position OSCILLATES: we snap the node to the builder's
            // coord and mark it+edges Updated, but the game's GenerateNodesSystem re-centres the junction
            // from the connected edges on the next frame, so the node drifts back and we snap again every
            // ~2 s — and the churn spawned EXTRA nodes on the host (609 vs 600). The game owns the junction
            // centre computation (non-deterministic ~<1 m), and we cannot override it durably from here.
            // Kept for reference; the real fix for the <1 m road drift is to make ZONE-block matching tolerant
            // of it (match by the owning road's identity), not to force node coords. See NEXT-STEP note.
            if (true)
            {
                return;
            }

            LocalPlayer lp = NetworkInterface.Instance.LocalPlayer;
            if (lp.PlayerStatus != PlayerStatus.PLAYING || lp.PlayerType == PlayerType.NONE)
            {
                return;
            }

            // ~2 Hz: junctions settle in a frame or two, so we don't need per-frame enforcement; throttling
            // also bounds the structural-change cost.
            if (++_counter < 30)
            {
                return;
            }

            _counter = 0;

            // Heartbeat (~every 15 s) so we can SEE why the pin does/doesn't act: how many authoritative node
            // coords we hold. If this stays 0, no net command populated AuthPos (roads not via the HasNodes
            // path) — that is the thing to fix, not the pin.
            if (++_diagCounter >= 30)
            {
                _diagCounter = 0;
                CS2M.Log.Info($"[Pin] heartbeat authPos={CS2M_NodeSyncIds.AuthPos.Count} mapped={CS2M_NodeSyncIds.Map.Count}");
            }

            if (CS2M_NodeSyncIds.AuthPos.Count == 0)
            {
                return;
            }

            int pinned = 0;
            // Snapshot the ids first: we do structural changes (Updated) inside the loop, and we must not
            // enumerate the shared dictionary while another system could mutate it.
            var ids = new NativeList<ulong>(CS2M_NodeSyncIds.AuthPos.Count, Allocator.Temp);
            try
            {
                foreach (System.Collections.Generic.KeyValuePair<ulong, float3> kv in CS2M_NodeSyncIds.AuthPos)
                {
                    ids.Add(kv.Key);
                }

                for (int i = 0; i < ids.Length; i++)
                {
                    ulong id = ids[i];
                    if (!CS2M_NodeSyncIds.AuthPos.TryGetValue(id, out float3 auth))
                    {
                        continue;
                    }

                    if (!CS2M_NodeSyncIds.TryResolve(EntityManager, id, out Entity node))
                    {
                        continue;
                    }

                    Node n = EntityManager.GetComponentData<Node>(node);
                    // Only the XZ plane matters for zone blocks; ignore tiny drift so we don't churn.
                    float dx = n.m_Position.x - auth.x;
                    float dz = n.m_Position.z - auth.z;
                    if (dx * dx + dz * dz < 0.04f) // < 0.2 m
                    {
                        continue;
                    }

                    // Keep the local Y (terrain height is deterministic from the same heightmap); snap XZ.
                    n.m_Position = new float3(auth.x, n.m_Position.y, auth.z);
                    EntityManager.SetComponentData(node, n);
                    MarkUpdated(node);

                    if (EntityManager.HasBuffer<ConnectedEdge>(node))
                    {
                        DynamicBuffer<ConnectedEdge> ce = EntityManager.GetBuffer<ConnectedEdge>(node, true);
                        for (int e = 0; e < ce.Length; e++)
                        {
                            Entity edge = ce[e].m_Edge;
                            if (EntityManager.Exists(edge) && !EntityManager.HasComponent<Deleted>(edge))
                            {
                                MarkUpdated(edge);
                            }
                        }
                    }

                    pinned++;
                }
            }
            finally
            {
                ids.Dispose();
            }

            if (pinned > 0)
            {
                CS2M.Log.Info($"[Pin] snapped {pinned} node(s) to host coord (zone blocks re-derive)");
            }
        }

        private void MarkUpdated(Entity e)
        {
            if (!EntityManager.HasComponent<Updated>(e)) { EntityManager.AddComponent<Updated>(e); }
            if (!EntityManager.HasComponent<BatchesUpdated>(e)) { EntityManager.AddComponent<BatchesUpdated>(e); }
        }
    }
}
