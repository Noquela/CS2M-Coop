using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Applies remote net-delete: finds the local edge whose two endpoint node positions match the
    ///     command (order-independent, ~3 m tolerance) and tags it <c>Deleted</c> — the game's own net
    ///     cleanup removes it and any orphaned nodes/lanes. Marks the segment hash first so our detector
    ///     doesn't echo the delete back.
    /// </summary>
    public partial class NetEditApplySystem : GameSystemBase
    {
        // Selftest observability: how many deletes resolved by node-pair IDENTITY (not proximity). The
        // single-instance selftest asserts this increments, proving the identity path end-to-end without a
        // flaky 2-sim. Not synced, no gameplay effect.
        internal static int DeleteByIdCount;

        private EntityQuery _edges;
        private EntityQuery _nodes;

        protected override void OnCreate()
        {
            base.OnCreate();
            _edges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[NetEdit] NetEditApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteNetDeleteQueue.TryDequeue(out NetDeleteCommand cmd))
            {
                ApplyDelete(cmd);
            }

            while (RemoteNetUpgradeQueue.TryDequeue(out NetUpgradeCommand up))
            {
                ApplyUpgrade(up);
            }
        }

        private void ApplyUpgrade(NetUpgradeCommand cmd)
        {
            var start = new float3(cmd.StartX, cmd.StartY, cmd.StartZ);
            var end = new float3(cmd.EndX, cmd.EndY, cmd.EndZ);

            if (cmd.IsNode)
            {
                ApplyNodeUpgrade(cmd, start);
                return;
            }

            if (!FindEdgeById(cmd.StartNodeId, cmd.EndNodeId, out Entity edge) && !FindEdge(start, end, out edge))
            {
                CS2M.Log.Info($"[NetEdit] upgrade SKIP noMatch start=({start.x:F0},{start.z:F0}) end=({end.x:F0},{end.z:F0})");
                return;
            }

            Edge ed = EntityManager.GetComponentData<Edge>(edge);
            float3 s = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
            float3 en = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(s, en, "upg"));

            var up = new Upgraded
            {
                m_Flags = new CompositionFlags
                {
                    m_General = (CompositionFlags.General) cmd.General,
                    m_Left = (CompositionFlags.Side) cmd.Left,
                    m_Right = (CompositionFlags.Side) cmd.Right,
                },
            };

            if (EntityManager.HasComponent<Upgraded>(edge))
            {
                EntityManager.SetComponentData(edge, up);
            }
            else
            {
                EntityManager.AddComponentData(edge, up);
            }

            if (!EntityManager.HasComponent<Updated>(edge))
            {
                EntityManager.AddComponent<Updated>(edge);
            }

            if (!EntityManager.HasComponent<BatchesUpdated>(edge))
            {
                EntityManager.AddComponent<BatchesUpdated>(edge);
            }

            CS2M.Log.Info($"[NetEdit] APPLIED upgrade edge={edge.Index} g={cmd.General} l={cmd.Left} r={cmd.Right}");
        }

        /// <summary>Junction upgrade (traffic lights / stop signs / roundabout / crosswalks). The vanilla
        /// tool writes these as <c>Upgraded</c> General flags on the NODE, not an edge — the edge detector
        /// never saw them, so they didn't sync. Find the node by position and write the same flags; the
        /// net-update pipeline recomputes the junction (and its connected edges) from there.</summary>
        private void ApplyNodeUpgrade(NetUpgradeCommand cmd, float3 pos)
        {
            if (!(cmd.NodeId != 0 && CS2M_NodeSyncIds.TryResolve(EntityManager, cmd.NodeId, out Entity node))
                && !FindNode(pos, out node))
            {
                CS2M.Log.Info($"[NetEdit] node-upgrade SKIP noMatch pos=({pos.x:F0},{pos.z:F0})");
                return;
            }

            float3 np = EntityManager.GetComponentData<Node>(node).m_Position;
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(np, np, "upgNode"));

            var up = new Upgraded
            {
                m_Flags = new CompositionFlags
                {
                    m_General = (CompositionFlags.General) cmd.General,
                    m_Left = (CompositionFlags.Side) cmd.Left,
                    m_Right = (CompositionFlags.Side) cmd.Right,
                },
            };

            if (EntityManager.HasComponent<Upgraded>(node))
            {
                EntityManager.SetComponentData(node, up);
            }
            else
            {
                EntityManager.AddComponentData(node, up);
            }

            if (!EntityManager.HasComponent<Updated>(node))
            {
                EntityManager.AddComponent<Updated>(node);
            }

            if (!EntityManager.HasComponent<BatchesUpdated>(node))
            {
                EntityManager.AddComponent<BatchesUpdated>(node);
            }

            // Re-evaluate the connected edges too: their node-adjacent geometry (light poles, stop lines,
            // roundabout caps) is derived from the junction flags, so without this the other sim keeps the
            // stale end caps until the edges are touched.
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                DynamicBuffer<ConnectedEdge> ce = EntityManager.GetBuffer<ConnectedEdge>(node, true);
                for (int i = 0; i < ce.Length; i++)
                {
                    Entity ne = ce[i].m_Edge;
                    if (!EntityManager.Exists(ne) || EntityManager.HasComponent<Deleted>(ne))
                    {
                        continue;
                    }

                    if (!EntityManager.HasComponent<Updated>(ne)) { EntityManager.AddComponent<Updated>(ne); }
                    if (!EntityManager.HasComponent<BatchesUpdated>(ne)) { EntityManager.AddComponent<BatchesUpdated>(ne); }
                }
            }

            CS2M.Log.Info($"[NetEdit] APPLIED node-upgrade node={node.Index} g={cmd.General} pos=({np.x:F0},{np.z:F0})");
        }

        private void ApplyDelete(NetDeleteCommand cmd)
        {
            var start = new float3(cmd.StartX, cmd.StartY, cmd.StartZ);
            var end = new float3(cmd.EndX, cmd.EndY, cmd.EndZ);

            bool byId = FindEdgeById(cmd.StartNodeId, cmd.EndNodeId, out Entity edge);
            if (!byId && !FindEdge(start, end, out edge))
            {
                CS2M.Log.Info($"[NetEdit] delete SKIP noMatch start=({start.x:F0},{start.z:F0}) end=({end.x:F0},{end.z:F0})");
                return;
            }

            Edge ed = EntityManager.GetComponentData<Edge>(edge);
            float3 s = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
            float3 en = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(s, en, "del"));
            if (byId) { DeleteByIdCount++; }
            CS2M.Log.Info($"[NetEdit] delete resolve via={(byId ? "id" : "pos")}");

            if (!EntityManager.HasComponent<Deleted>(edge))
            {
                EntityManager.AddComponent<Deleted>(edge);
            }

            // The game's real delete CASCADES: a junction that loses one road rebuilds to a dead-end /
            // lower-order intersection, and a node left with no edges is removed. The mod's raw
            // "add Deleted to the edge" skips that, so the client kept the old junction shape (the flat
            // cut ends Bruno saw) and stale orphan nodes (the +nodes radar drift). Do the cascade here:
            // rebuild the two end nodes + their remaining edges, and delete any node left orphaned.
            RebuildAfterDelete(ed.m_Start, edge);
            RebuildAfterDelete(ed.m_End, edge);

            CS2M.Log.Info($"[NetEdit] APPLIED delete edge={edge.Index} start=({s.x:F0},{s.z:F0}) end=({en.x:F0},{en.z:F0})");
        }

        /// <summary>After an edge is deleted, converge the geometry the game would rebuild at
        /// <paramref name="node"/>: if it still has live edges, mark them (and the node) Updated so the
        /// end geometry re-caps (junction -> dead-end); if it is now orphaned, delete it too.</summary>
        private void RebuildAfterDelete(Entity node, Entity deletedEdge)
        {
            if (!EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node)
                || !EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                return;
            }

            DynamicBuffer<ConnectedEdge> ce = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            int live = 0;
            for (int i = 0; i < ce.Length; i++)
            {
                Entity e = ce[i].m_Edge;
                if (e == deletedEdge || !EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e))
                {
                    continue;
                }

                live++;
                MarkUpdated(e);
            }

            if (live == 0)
            {
                // No road left on this node — the host removed it; match, so counts converge.
                EntityManager.AddComponent<Deleted>(node);
            }
            else
            {
                MarkUpdated(node);
            }
        }

        private void MarkUpdated(Entity e)
        {
            if (!EntityManager.HasComponent<Updated>(e)) { EntityManager.AddComponent<Updated>(e); }
            if (!EntityManager.HasComponent<BatchesUpdated>(e)) { EntityManager.AddComponent<BatchesUpdated>(e); }
        }

        /// <summary>IDENTITY-first edge resolution: both endpoint nodes carry the SAME cross-PC
        /// <c>CS2M_NodeSyncId</c> on both machines (stamped at placement), so the edge is the one connecting
        /// those two exact nodes — immune to the ~10 m proximity mis-pick that let a couple of deletes land on
        /// the wrong road ("deletei várias e 2 ficaram no outro"). Returns false for legacy/save content
        /// (id 0 or unresolvable) so the caller falls back to <see cref="FindEdge"/>.</summary>
        private bool FindEdgeById(ulong aId, ulong bId, out Entity edge)
        {
            edge = Entity.Null;
            if (aId == 0 || bId == 0)
            {
                return false;
            }

            if (!CS2M_NodeSyncIds.TryResolve(EntityManager, aId, out Entity a)
                || !CS2M_NodeSyncIds.TryResolve(EntityManager, bId, out Entity b)
                || a == b || !EntityManager.HasBuffer<ConnectedEdge>(a))
            {
                return false;
            }

            DynamicBuffer<ConnectedEdge> ce = EntityManager.GetBuffer<ConnectedEdge>(a, true);
            for (int i = 0; i < ce.Length; i++)
            {
                Entity e = ce[i].m_Edge;
                if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e)
                    || !EntityManager.HasComponent<Edge>(e))
                {
                    continue;
                }

                Edge ed = EntityManager.GetComponentData<Edge>(e);
                if ((ed.m_Start == a && ed.m_End == b) || (ed.m_Start == b && ed.m_End == a))
                {
                    edge = e;
                    return true;
                }
            }

            return false;
        }

        private bool FindEdge(float3 start, float3 end, out Entity edge)
        {
            edge = Entity.Null;
            NativeArray<Entity> arr = _edges.ToEntityArray(Allocator.Temp);
            try
            {
                // ~10 m per endpoint (sum of the two squared distances < 200). Was 3 m and MISSED deletes
                // when the target edge sat a few metres off on the receiver (a split/junction landed slightly
                // differently — Bruno's "deletei várias e 2 ficaram no outro"). Both endpoints must still be
                // the closest match, so widening is safe: two distinct edges rarely share both endpoints
                // within 10 m, and closest-wins picks the intended one.
                float best = 200f;
                foreach (Entity e in arr)
                {
                    Edge ed = EntityManager.GetComponentData<Edge>(e);
                    if (!EntityManager.HasComponent<Node>(ed.m_Start) || !EntityManager.HasComponent<Node>(ed.m_End))
                    {
                        continue;
                    }

                    float3 s = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
                    float3 en = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;
                    float d1 = math.distancesq(s, start) + math.distancesq(en, end);
                    float d2 = math.distancesq(s, end) + math.distancesq(en, start);
                    float d = math.min(d1, d2);
                    if (d < best)
                    {
                        best = d;
                        edge = e;
                    }
                }
            }
            finally
            {
                arr.Dispose();
            }

            return edge != Entity.Null;
        }

        /// <summary>Nearest live node within ~3 m of the target position (junction upgrades address a
        /// single node; both sims' junctions sit at the same coord).</summary>
        private bool FindNode(float3 pos, out Entity node)
        {
            node = Entity.Null;
            NativeArray<Entity> arr = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                float best = 9f; // 3 m²
                foreach (Entity n in arr)
                {
                    float d = math.distancesq(EntityManager.GetComponentData<Node>(n).m_Position, pos);
                    if (d < best)
                    {
                        best = d;
                        node = n;
                    }
                }
            }
            finally
            {
                arr.Dispose();
            }

            return node != Entity.Null;
        }
    }
}
