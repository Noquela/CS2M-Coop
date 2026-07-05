using System.Collections.Generic;
using Colossal.Mathematics;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     AtomicBatch APPLY (receiver). Drains ONE <see cref="NetBatchCommand"/> per frame and applies the
    ///     WHOLE batch in that single frame — a save/load in miniature. Runs at
    ///     <c>UpdateBefore(Modification1)</c> so the Created/Updated tags baked into the net archetype survive
    ///     into every consumer (References@Mod2B, CompositionSelect@Mod3, Geometry/Lane/Block@Mod4) this frame
    ///     and the derived pipeline re-computes from a COMPLETE, identical input set.
    ///
    ///     Steps: (1) resolve every boundary node by <see cref="CS2M_NodeSyncIds.TryResolve"/> — any miss parks
    ///     the ENTIRE batch (retry ~every 15 frames up to 300, then DROP; NEVER guess by proximity). (2) create
    ///     new nodes directly from the prefab's <c>NetData.m_NodeArchetype</c>. (3) create new edges from
    ///     <c>m_EdgeArchetype</c>, linking resolved (new|boundary) node entities by identity. (4) mark boundary
    ///     nodes Updated (+BatchesUpdated) so ReferencesSystem re-wires ConnectedEdge and NodeAlign re-centres
    ///     with the new arm. (5) apply the split's deletes by node-pair identity + cascade. Every created /
    ///     deleted entity is tagged <c>CS2M_RemotePlaced</c> (the by-component echo guard); nothing gets
    ///     <c>Temp</c> or <c>Applied</c> (Applied would re-trigger detectors).
    ///
    ///     The delete resolution (<see cref="FindEdgeById"/> / <see cref="RebuildAfterDelete"/>) intentionally
    ///     duplicates the identity-first logic from <see cref="NetEditApplySystem"/> rather than refactoring
    ///     that live system, so the legacy net path stays byte-for-byte untouched.
    /// </summary>
    public partial class NetBatchApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _liveNodes;

        // A batch whose boundary nodes are not all resolvable yet (a sibling batch that creates them may still
        // be in flight). Held until they resolve or it ages out — never applied partially.
        private NetBatchCommand _parked;
        private int _parkAge;
        private ulong _lastMissingId;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _liveNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[Batch] NetBatchApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // Hold a parked batch until its boundary resolves (keeps arrival order + atomicity intact).
            if (_parked != null)
            {
                _parkAge++;
                if (_parkAge % 15 == 0)
                {
                    try
                    {
                        if (TryApply(_parked))
                        {
                            _parked = null;
                            _parkAge = 0;
                        }
                        else if (_parkAge >= 300)
                        {
                            CS2M.Log.Info($"[Batch] DROP boundary-miss id={_lastMissingId} (parked {_parkAge}f)");
                            _parked = null;
                            _parkAge = 0;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        CS2M.Log.Info($"[Guard] apply failed in NetBatchApplySystem (parked): {ex.Message}");
                        _parked = null;
                        _parkAge = 0;
                    }
                }

                return;
            }

            if (RemoteNetBatchQueue.TryDequeue(out NetBatchCommand cmd))
            {
                try
                {
                    if (!TryApply(cmd))
                    {
                        _parked = cmd;
                        _parkAge = 0;
                        CS2M.Log.Info($"[Batch] PARK boundary-miss id={_lastMissingId} (retry up to 300f)");
                    }
                }
                catch (System.Exception ex)
                {
                    CS2M.Log.Info($"[Guard] apply failed in NetBatchApplySystem: {ex.Message}");
                }
            }
        }

        /// <summary>Apply the whole batch atomically. Returns false WITHOUT mutating anything when a boundary
        /// node can't be resolved yet (the caller parks the batch); <see cref="_lastMissingId"/> names it.
        /// Atomicity is real: prefabs are validated up front (a miss DROPS the whole batch — a missing
        /// prefab never heals by retrying), and boundary resolution PLANS first, mutating identity state
        /// only after every id resolved (a park leaves zero side effects).</summary>
        private bool TryApply(NetBatchCommand cmd)
        {
            _lastMissingId = 0;
            var idToEntity = new Dictionary<ulong, Entity>();
            var boundarySet = new HashSet<ulong>();

            // (0) Validate EVERY prefab/archetype up front (pure reads). Any miss → consume the batch as a
            // loud DROP instead of building a PARTIAL network (the adversarial review's atomicity finding).
            if (!ValidateAllPrefabs(cmd))
            {
                CS2M.Log.Info("[Batch] DROP prefab-missing (whole batch — see RESOLVE-FAIL above)");
                return true;
            }

            // (1) Resolve boundary — PLAN ONLY, no mutation, so a park has zero side effects. `claimed`
            // prevents two boundary ids adopting the SAME physical node (two save nodes <0.5 m apart —
            // the review's critical finding: it produced a degenerate self-loop edge and collapsed two
            // junctions into one).
            var adoptPlan = new List<KeyValuePair<ulong, Entity>>();
            var claimed = new HashSet<Entity>();
            if (cmd.BoundaryNodeIds != null)
            {
                bool hasPos = cmd.BoundaryPosX != null && cmd.BoundaryPosX.Length == cmd.BoundaryNodeIds.Length;
                for (int bi = 0; bi < cmd.BoundaryNodeIds.Length; bi++)
                {
                    ulong bid = cmd.BoundaryNodeIds[bi];
                    if (CS2M_NodeSyncIds.TryResolve(EntityManager, bid, out Entity be))
                    {
                        if (!claimed.Add(be))
                        {
                            // Two ids already bound to one entity = pre-existing map corruption. Park —
                            // never build a self-loop out of it.
                            _lastMissingId = bid;
                            return false;
                        }
                    }
                    // Id-miss fallback for SAVE-loaded boundary nodes (no id on either PC): save geometry
                    // is byte-identical on both machines (same file at join), so a STRICT <0.5 m match is
                    // exact resolution there, not guessing. Only BARE nodes (no CS2M_NodeSyncId) not yet
                    // claimed this call are valid adopt targets.
                    else if (hasPos && FindNodeStrict(
                                 new float3(cmd.BoundaryPosX[bi], cmd.BoundaryPosY[bi], cmd.BoundaryPosZ[bi]),
                                 claimed, out be))
                    {
                        claimed.Add(be);
                        adoptPlan.Add(new KeyValuePair<ulong, Entity>(bid, be));
                    }
                    else
                    {
                        _lastMissingId = bid;
                        return false;
                    }

                    idToEntity[bid] = be;
                    boundarySet.Add(bid);
                }
            }

            // Every boundary resolved — NOW commit the planned adoptions.
            foreach (KeyValuePair<ulong, Entity> adopt in adoptPlan)
            {
                CS2M_NodeSyncIds.Register(EntityManager, adopt.Value, adopt.Key);
                CS2M.Log.Info($"[Batch] boundary adopt-by-pos id={adopt.Key} node={adopt.Value.Index}");
            }

            // (2) Create new NODES.
            int nodeCount = cmd.NodeIds != null ? cmd.NodeIds.Length : 0;
            int createdNodes = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                Entity ne = CreateNode(cmd, i);
                if (ne != Entity.Null)
                {
                    idToEntity[cmd.NodeIds[i]] = ne;
                    createdNodes++;
                }
            }

            // (3) Create new EDGES, linking (new|boundary) nodes by identity.
            int edgeCount = cmd.EdgeStartNodeIds != null ? cmd.EdgeStartNodeIds.Length : 0;
            int createdEdges = 0;
            var boundaryTouched = new HashSet<Entity>();
            for (int i = 0; i < edgeCount; i++)
            {
                if (!idToEntity.TryGetValue(cmd.EdgeStartNodeIds[i], out Entity s)
                    || !idToEntity.TryGetValue(cmd.EdgeEndNodeIds[i], out Entity en))
                {
                    CS2M.Log.Info($"[Batch] SKIP edge {i} unresolved endpoint " +
                                  $"(startId={cmd.EdgeStartNodeIds[i]} endId={cmd.EdgeEndNodeIds[i]})");
                    continue;
                }

                // Defense in depth vs the double-claim class: never fabricate a self-loop.
                if (s == en)
                {
                    CS2M.Log.Info($"[Batch] SKIP edge {i} degenerate (start==end entity={s.Index})");
                    continue;
                }

                // Idempotency: this exact edge (by node-pair identity) already exists → duplicate delivery.
                if (FindEdgeById(cmd.EdgeStartNodeIds[i], cmd.EdgeEndNodeIds[i], out _))
                {
                    CS2M.Log.Info($"[Batch] SKIP dup edge {i} (already live)");
                    continue;
                }

                Entity ee = CreateEdge(cmd, i, s, en);
                if (ee != Entity.Null)
                {
                    createdEdges++;
                    if (boundarySet.Contains(cmd.EdgeStartNodeIds[i])) { boundaryTouched.Add(s); }
                    if (boundarySet.Contains(cmd.EdgeEndNodeIds[i])) { boundaryTouched.Add(en); }
                }
            }

            // (4) A pre-existing boundary node gained an arm: re-derive its junction (ReferencesSystem
            // re-wires ConnectedEdge, NodeAlign re-centres with the new connected set).
            foreach (Entity b in boundaryTouched)
            {
                MarkUpdated(b);
            }

            // (5) Apply the split's deletes (and any bulldozes bundled in) by node-pair identity + cascade.
            int delCount = cmd.DelStartNodeIds != null ? cmd.DelStartNodeIds.Length : 0;
            int appliedDels = 0;
            for (int i = 0; i < delCount; i++)
            {
                if (ApplyDelete(cmd, i))
                {
                    appliedDels++;
                }
            }

            CS2M.Log.Info($"[Batch] APPLIED nodes={createdNodes} edges={createdEdges} dels={appliedDels}");
            return true;
        }

        private Entity CreateNode(NetBatchCommand cmd, int i)
        {
            // Idempotency (duplicate/re-delivered batch, same guard class as RemotePlacementApplySystem's
            // SyncId check): if this id already maps to a LIVE node, reuse it — never fabricate a twin.
            if (CS2M_NodeSyncIds.TryResolve(EntityManager, cmd.NodeIds[i], out Entity existing))
            {
                CS2M.Log.Info($"[Batch] SKIP dup node id={cmd.NodeIds[i]} (already live)");
                return existing;
            }

            if (!ResolveNetPrefab(cmd.NodePrefabTypes[i], cmd.NodePrefabNames[i], out Entity netPrefab, out NetData netData))
            {
                return Entity.Null;
            }

            if (!netData.m_NodeArchetype.Valid)
            {
                CS2M.Log.Info($"[Batch] node RESOLVE-FAIL {cmd.NodePrefabNames[i]} invalid node archetype");
                return Entity.Null;
            }

            Entity node = EntityManager.CreateEntity();
            EntityManager.SetArchetype(node, netData.m_NodeArchetype);

            SetOrAdd(node, new PrefabRef(netPrefab));
            SetOrAdd(node, new Node
            {
                m_Position = new float3(cmd.NodePosX[i], cmd.NodePosY[i], cmd.NodePosZ[i]),
                m_Rotation = new quaternion(cmd.NodeRotX[i], cmd.NodeRotY[i], cmd.NodeRotZ[i], cmd.NodeRotW[i]),
            });
            SetOrAdd(node, new PseudoRandomSeed((ushort) cmd.NodeSeeds[i]));

            // Mirror the builder's conditional components blindly (zero heuristics).
            if (cmd.NodeHasStandalone[i] && !EntityManager.HasComponent<Standalone>(node))
            {
                EntityManager.AddComponent<Standalone>(node);
            }

            if (cmd.NodeHasElevation[i])
            {
                SetOrAdd(node, new Game.Net.Elevation(new float2(cmd.NodeElevX[i], cmd.NodeElevY[i])));
            }

            CS2M_NodeSyncIds.Register(EntityManager, node, cmd.NodeIds[i]);
            if (!EntityManager.HasComponent<CS2M_RemotePlaced>(node))
            {
                EntityManager.AddComponent<CS2M_RemotePlaced>(node);
            }

            return node;
        }

        private Entity CreateEdge(NetBatchCommand cmd, int i, Entity startNode, Entity endNode)
        {
            if (!ResolveNetPrefab(cmd.EdgePrefabTypes[i], cmd.EdgePrefabNames[i], out Entity netPrefab, out NetData netData))
            {
                return Entity.Null;
            }

            if (!netData.m_EdgeArchetype.Valid)
            {
                CS2M.Log.Info($"[Batch] edge RESOLVE-FAIL {cmd.EdgePrefabNames[i]} invalid edge archetype");
                return Entity.Null;
            }

            var bezier = new Bezier4x3(
                new float3(cmd.EdgeAX[i], cmd.EdgeAY[i], cmd.EdgeAZ[i]),
                new float3(cmd.EdgeBX[i], cmd.EdgeBY[i], cmd.EdgeBZ[i]),
                new float3(cmd.EdgeCX[i], cmd.EdgeCY[i], cmd.EdgeCZ[i]),
                new float3(cmd.EdgeDX[i], cmd.EdgeDY[i], cmd.EdgeDZ[i]));

            Entity edge = EntityManager.CreateEntity();
            EntityManager.SetArchetype(edge, netData.m_EdgeArchetype);

            SetOrAdd(edge, new PrefabRef(netPrefab));
            SetOrAdd(edge, new Edge { m_Start = startNode, m_End = endNode });
            SetOrAdd(edge, new Curve { m_Bezier = bezier, m_Length = MathUtils.Length(bezier) });
            // CRITICAL (I1): without a Composition entry CompositionSelectSystem@Mod3 falls into an
            // untraced fallback. The prefab is the composition source for a default (un-upgraded) edge.
            SetOrAdd(edge, new Composition { m_Edge = netPrefab, m_StartNode = netPrefab, m_EndNode = netPrefab });
            // Same BuildOrder on both PCs → deterministic lane/block ordering.
            SetOrAdd(edge, new BuildOrder { m_Start = cmd.EdgeBuildOrderStart[i], m_End = cmd.EdgeBuildOrderEnd[i] });
            SetOrAdd(edge, new PseudoRandomSeed((ushort) cmd.EdgeSeeds[i]));

            if (cmd.EdgeHasUpgraded[i])
            {
                SetOrAdd(edge, new Upgraded
                {
                    m_Flags = new CompositionFlags
                    {
                        m_General = (CompositionFlags.General) cmd.EdgeUpgradedG[i],
                        m_Left = (CompositionFlags.Side) cmd.EdgeUpgradedL[i],
                        m_Right = (CompositionFlags.Side) cmd.EdgeUpgradedR[i],
                    },
                });
            }

            if (cmd.EdgeHasElevation[i])
            {
                SetOrAdd(edge, new Game.Net.Elevation(new float2(cmd.EdgeElevX[i], cmd.EdgeElevY[i])));
            }

            if (!EntityManager.HasComponent<CS2M_RemotePlaced>(edge))
            {
                EntityManager.AddComponent<CS2M_RemotePlaced>(edge);
            }

            return edge;
        }

        /// <summary>Delete the edge the split removed, addressed by node-pair identity (never proximity). Tags
        /// <c>CS2M_RemotePlaced</c> BEFORE <c>Deleted</c> (delete echo guard) and cascades the junction rebuild.</summary>
        private bool ApplyDelete(NetBatchCommand cmd, int i)
        {
            ulong aId = cmd.DelStartNodeIds[i];
            ulong bId = cmd.DelEndNodeIds[i];
            if (!FindEdgeById(aId, bId, out Entity edge))
            {
                CS2M.Log.Info($"[Batch] delete SKIP noMatch startId={aId} endId={bId} " +
                              $"start=({cmd.DelStartX[i]:F0},{cmd.DelStartZ[i]:F0}) end=({cmd.DelEndX[i]:F0},{cmd.DelEndZ[i]:F0})");
                return false;
            }

            Edge ed = EntityManager.GetComponentData<Edge>(edge);
            // CS2M_RemoteDeleted (NOT RemotePlaced): the capture's removed-query must skip THIS delete
            // (echo) while still shipping a local player's legit bulldoze of a remote-BUILT edge.
            if (!EntityManager.HasComponent<CS2M_RemoteDeleted>(edge))
            {
                EntityManager.AddComponent<CS2M_RemoteDeleted>(edge);
            }

            if (!EntityManager.HasComponent<Deleted>(edge))
            {
                EntityManager.AddComponent<Deleted>(edge);
            }

            RebuildAfterDelete(ed.m_Start, edge);
            RebuildAfterDelete(ed.m_End, edge);
            CS2M.Log.Info($"[Batch] DELETE edge={edge.Index} startId={aId} endId={bId}");
            return true;
        }

        private bool ResolveNetPrefab(string type, string name, out Entity netPrefab, out NetData netData)
        {
            netPrefab = Entity.Null;
            netData = default;

            var prefabId = new PrefabID(type, name, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info($"[Batch] RESOLVE-FAIL type={type} name={name}");
                return false;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out netPrefab))
            {
                CS2M.Log.Info($"[Batch] RESOLVE-FAIL no prefab entity name={name}");
                return false;
            }

            if (!EntityManager.HasComponent<NetData>(netPrefab))
            {
                CS2M.Log.Info($"[Batch] RESOLVE-FAIL prefab {name} has no NetData (not a net?)");
                return false;
            }

            netData = EntityManager.GetComponentData<NetData>(netPrefab);
            return true;
        }

        /// <summary>Nearest BARE live node (no <see cref="CS2M_NodeSyncId"/> yet, not already claimed by an
        /// earlier boundary id of this batch) within 0.5 m of the builder's settled coord, or false.
        /// Intentionally STRICT: this is exact-resolution for byte-identical save geometry, not the loose
        /// proximity guessing (3.5–10 m radii) this architecture retired for session content. An id-bearing
        /// node is NEVER a valid adopt target — overwriting its id would silently reassign identity.</summary>
        private bool FindNodeStrict(float3 pos, HashSet<Entity> claimed, out Entity node)
        {
            node = Entity.Null;
            float best = 0.25f; // 0.5 m squared
            Unity.Collections.NativeArray<Entity> arr = _liveNodes.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity n in arr)
                {
                    if (claimed.Contains(n) || EntityManager.HasComponent<CS2M_NodeSyncId>(n))
                    {
                        continue;
                    }

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

        /// <summary>Pure-read pre-validation of every prefab (and its archetype) the batch references, so
        /// the apply is all-or-nothing: a resolve failure drops the WHOLE batch instead of committing a
        /// partial network (some arms present, others silently missing — a lasting desync).</summary>
        private bool ValidateAllPrefabs(NetBatchCommand cmd)
        {
            var seen = new HashSet<string>();

            int nodeCount = cmd.NodeIds != null ? cmd.NodeIds.Length : 0;
            for (int i = 0; i < nodeCount; i++)
            {
                string key = cmd.NodePrefabTypes[i] + "|" + cmd.NodePrefabNames[i];
                if (!seen.Add(key))
                {
                    continue;
                }

                if (!ResolveNetPrefab(cmd.NodePrefabTypes[i], cmd.NodePrefabNames[i], out _, out NetData nd)
                    || !nd.m_NodeArchetype.Valid)
                {
                    return false;
                }
            }

            int edgeCount = cmd.EdgeStartNodeIds != null ? cmd.EdgeStartNodeIds.Length : 0;
            for (int i = 0; i < edgeCount; i++)
            {
                string key = "E" + cmd.EdgePrefabTypes[i] + "|" + cmd.EdgePrefabNames[i];
                if (!seen.Add(key))
                {
                    continue;
                }

                if (!ResolveNetPrefab(cmd.EdgePrefabTypes[i], cmd.EdgePrefabNames[i], out _, out NetData ed)
                    || !ed.m_EdgeArchetype.Valid)
                {
                    return false;
                }
            }

            return true;
        }

        private void SetOrAdd<T>(Entity e, T data) where T : unmanaged, IComponentData
        {
            if (EntityManager.HasComponent<T>(e))
            {
                EntityManager.SetComponentData(e, data);
            }
            else
            {
                EntityManager.AddComponentData(e, data);
            }
        }

        // ---- Delete resolution (identity-first, duplicated from NetEditApplySystem so that live system
        //      stays untouched). See NetEditApplySystem for the rationale behind the exact logic. ---------

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
    }
}
