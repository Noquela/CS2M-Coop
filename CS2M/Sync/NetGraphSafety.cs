using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Invariant-preserving graph mutation helpers shared by every net apply path.
    ///
    ///     The game's Burst net jobs (NodeAlign, GeometrySystem, EdgeIterator) dereference — with NO null
    ///     guard — everything reachable from a node's <see cref="ConnectedEdge"/> buffer, and they ASSUME the
    ///     road graph is geometrically consistent: the curve of every edge connected to a node must start
    ///     (<c>m_Bezier.a</c>) or end (<c>m_Bezier.d</c>) exactly at that node's position (call this
    ///     invariant I4, guaranteed by the game at creation in
    ///     <c>Game.Net.GenerateEdgesSystem.cs:1319-1348</c>). Marking an edge <c>Updated</c> does NOT
    ///     re-fit its curve (<c>UpdateNodeConnections</c>, GenerateEdgesSystem.cs:538/564-596), so moving a
    ///     node while leaving its connected edges' bezier endpoints on the OLD coordinate produces a graph
    ///     the game then null-derefs on inside Burst → whole-process c0000005 abort (proven from native
    ///     dumps). Every place that repositions an already-connected node MUST therefore drag its edge
    ///     curves with it. This helper is the single, audited implementation of that.
    /// </summary>
    internal static class NetGraphSafety
    {
        /// <summary>
        ///     Move <paramref name="node"/> to <paramref name="newPos"/> AND keep every connected edge's
        ///     curve consistent (I4): each connected edge has its <c>m_Bezier.a</c> (when the node is the
        ///     edge's start) or <c>m_Bezier.d</c> (when the node is its end) set to <paramref name="newPos"/>,
        ///     and its two interior control points translated with a linear falloff so the tangents stay
        ///     plausible instead of kinking: the control point NEAR the moved end takes 2/3 of the shift,
        ///     the far one 1/3. <c>m_Length</c> is recomputed (<see cref="MathUtils.Length"/>, exactly how
        ///     the game recomputes it on deserialize — Curve.cs:23). The node and every touched edge are
        ///     marked <c>Updated</c>+<c>BatchesUpdated</c>.
        ///
        ///     Ordering: the node's own <c>Node</c> component is a plain <c>SetComponentData</c> (no
        ///     structural change), and the <see cref="ConnectedEdge"/> entities are copied into a local array
        ///     BEFORE any <c>AddComponent</c>, so the structural marks below (which move edges/node to new
        ///     chunks) never invalidate a buffer handle mid-iteration.
        ///
        ///     Guards: an entry whose edge is missing <see cref="Edge"/> or <see cref="Curve"/> (or is
        ///     deleted/dead) is skipped (logged <c>[Net] MOVE-GUARD missing-comp</c>); a node with no
        ///     <see cref="ConnectedEdge"/> buffer is moved as a plain reposition.
        /// </summary>
        public static void MoveNodeWithCurves(EntityManager em, Entity node, float3 newPos)
        {
            if (!em.Exists(node) || !em.HasComponent<Node>(node))
            {
                return;
            }

            Node cur = em.GetComponentData<Node>(node);
            float3 oldPos = cur.m_Position;
            float3 delta = newPos - oldPos;

            // 1) Node position — preserve every other field (rotation). Non-structural: safe first.
            em.SetComponentData(node, new Node { m_Position = newPos, m_Rotation = cur.m_Rotation });

            // No connected edges → nothing to keep consistent; just a plain reposition.
            if (!em.HasBuffer<ConnectedEdge>(node))
            {
                MarkUpdated(em, node);
                return;
            }

            // Snapshot edge entities before any structural change (AddComponent on an edge below would
            // otherwise invalidate this buffer handle mid-iteration — same lesson as HealNodePosition).
            DynamicBuffer<ConnectedEdge> ce = em.GetBuffer<ConnectedEdge>(node, true);
            var edges = new Entity[ce.Length];
            for (int i = 0; i < ce.Length; i++)
            {
                edges[i] = ce[i].m_Edge;
            }

            foreach (Entity edge in edges)
            {
                if (edge == Entity.Null || !em.Exists(edge) || em.HasComponent<Deleted>(edge))
                {
                    continue; // dead/deleted arm — ReferencesSystem will drop it from the buffer
                }

                if (!em.HasComponent<Edge>(edge) || !em.HasComponent<Curve>(edge))
                {
                    CS2M.Log.Info($"[Net] MOVE-GUARD missing-comp edge={edge.Index} " +
                                  "(ConnectedEdge entry without Edge/Curve — skipped)");
                    continue;
                }

                Edge ed = em.GetComponentData<Edge>(edge);
                Curve curve = em.GetComponentData<Curve>(edge);
                Bezier4x3 bez = curve.m_Bezier;

                bool isStart = ed.m_Start == node;
                bool isEnd = ed.m_End == node;
                if (!isStart && !isEnd)
                {
                    // The buffer references an edge that no longer names this node (stale mid-rewrite).
                    // Touching its curve would violate I4 on the OTHER node, so leave it.
                    continue;
                }

                if (isStart)
                {
                    bez.a = newPos;
                    bez.b += delta * (2f / 3f);
                    bez.c += delta * (1f / 3f);
                }

                if (isEnd)
                {
                    // A degenerate self-loop (start==end==node) lands here after the isStart block too, so
                    // both endpoints and both control points get their share — consistent either way.
                    bez.d = newPos;
                    bez.c += delta * (2f / 3f);
                    bez.b += delta * (1f / 3f);
                }

                curve.m_Bezier = bez;
                curve.m_Length = MathUtils.Length(bez); // exactly how Curve.Deserialize recomputes it
                em.SetComponentData(edge, curve);        // non-structural
                MarkUpdated(em, edge);                   // structural — but `edges` was copied out already
            }

            MarkUpdated(em, node);
        }

        private static void MarkUpdated(EntityManager em, Entity e)
        {
            if (!em.HasComponent<Updated>(e)) { em.AddComponent<Updated>(e); }
            if (!em.HasComponent<BatchesUpdated>(e)) { em.AddComponent<BatchesUpdated>(e); }
        }
    }
}
