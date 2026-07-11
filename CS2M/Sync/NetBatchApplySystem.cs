using System.Collections.Generic;
using Colossal.Mathematics;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     AtomicBatch APPLY (receiver), HYBRID model. Drains ONE <see cref="NetBatchCommand"/> per frame and
    ///     applies the WHOLE batch in that single frame — a save/load in miniature. Runs at
    ///     <c>UpdateBefore(Modification1)</c> so everything it emits is consumed by GenerateNodes@Mod1 /
    ///     GenerateEdges@Mod2 and the derived pipeline (References@Mod2B, CompositionSelect@Mod3,
    ///     Geometry/Lane/Block@Mod4) re-computes from a COMPLETE, identical input set this frame.
    ///
    ///     NODES are created DIRECTLY from the prefab's <c>NetData.m_NodeArchetype</c> — proven: direct nodes
    ///     render correctly. EDGES take the LEGACY vanilla definition path instead of the archetype: each is
    ///     emitted as a <c>CreationDefinition(Permanent)</c> + <c>NetCourse</c> (the exact pattern of
    ///     <see cref="NetPlaceApplySystem"/>.EmitCourse) whose two endpoints reference the already
    ///     created/resolved node entities by <c>m_Entity</c>. The direct-archetype edge was a hollow shell —
    ///     the pavement/lane mesh and terrain deformation never derived (this system runs before the net
    ///     consumers, but a raw archetype edge is not a definition, so GenerateEdges never (re)builds its
    ///     geometry) — whereas GenerateEdges FROM A DEFINITION builds the REAL edge (curve terrain-fit,
    ///     composition, geometry, lanes, zone blocks, mesh). ALL of a batch's node creations + edge
    ///     definitions happen in the SAME OnUpdate, so GenerateEdges consumes the complete set in one frame,
    ///     NodeAlign sees every arm at once, and ATOMICITY is preserved.
    ///
    ///     Steps: (1) resolve every boundary node by <see cref="CS2M_NodeSyncIds.TryResolve"/> — any miss parks
    ///     the ENTIRE batch (retry ~every 15 frames up to 300, then DROP; NEVER guess by proximity). (2) create
    ///     new nodes directly from <c>m_NodeArchetype</c> (tagged <c>CS2M_RemotePlaced</c>, the by-component
    ///     echo guard; no <c>Temp</c>/<c>Applied</c>). (3) emit one Permanent definition per new edge
    ///     (<see cref="EmitEdgeCourse"/>), linking resolved (new|boundary) node entities by identity. The edge
    ///     GenerateEdges then produces is born <c>Applied &amp; Created</c> and WITHOUT <c>CS2M_RemotePlaced</c>
    ///     (we never hold that entity), so — same contract as NetPlaceApplySystem — each course MARKS its seg
    ///     hash in <see cref="RemoteNetEcho"/> first and <see cref="NetBatchCaptureSystem"/> skips it
    ///     (reason=remoteEcho) instead of re-broadcasting. Any edge <c>Upgraded</c> flags are deferred to
    ///     <see cref="RemoteNetUpgradeQueue"/> and applied by identity in following frames by
    ///     <see cref="NetEditApplySystem"/> (existing mechanism, zero new apply code). (4) mark boundary nodes
    ///     Updated (+BatchesUpdated) so ReferencesSystem re-wires ConnectedEdge and NodeAlign re-centres with
    ///     the new arm. (5) apply the split's deletes by node-pair identity + cascade. Emitted definitions are
    ///     destroyed the NEXT frame once GenerateNodes/Edges have consumed them (<c>_pendingDefinitions</c>,
    ///     mirrors NetPlaceApplySystem).
    ///
    ///     The delete resolution (<see cref="FindEdgeById"/> / <see cref="RebuildAfterDelete"/>) intentionally
    ///     duplicates the identity-first logic from <see cref="NetEditApplySystem"/> rather than refactoring
    ///     that live system, so the legacy net path stays byte-for-byte untouched.
    /// </summary>
    public partial class NetBatchApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _liveNodes;
        private EntityQuery _liveEdges;

        // Edge definitions injected this frame (the vanilla path — see EmitEdgeCourse). Consumed by
        // GenerateNodes/Edges this same frame, then destroyed at the TOP of next OnUpdate. Exact mirror of
        // NetPlaceApplySystem._pendingDefinitions.
        private readonly List<Entity> _pendingDefinitions = new List<Entity>();

        // Per-edge Game.Net.BuildOrder correction queued by EmitEdgeCourse (see there for the "why"): the
        // edge doesn't exist yet when its course is emitted, so GenerateEdges@Mod2 assigns m_Start/m_End
        // from ITS OWN per-process counter this frame. Resolved by identity at the TOP of the FOLLOWING
        // OnUpdate (same slot as the _pendingDefinitions cleanup above), once the edge is live.
        private readonly List<PendingOrderFix> _pendingOrderFixes = new List<PendingOrderFix>();

        // Nodes that RebuildAfterDelete found orphaned (0 live connected edges) — their Deleted is
        // DEFERRED one frame instead of applied inside the apply frame. Deleting a node in the SAME frame
        // its junction is restructured opens a recycle window that BlockSystem.UpdateBlocksJob@Mod4 can
        // walk (crash #4). Drained at the TOP of OnUpdate (same slot as _pendingDefinitions), once
        // ReferencesSystem has revalidated the buffers: still-orphaned → Deleted, re-armed → keep.
        private readonly List<Entity> _pendingOrphanNodes = new List<Entity>();

        // FIX A — DEFERRED POSITION CORRECTOR. A node's position is DERIVED, not authored: NodeAlignSystem
        // re-centres node.m_Position from the SET of connected arms every time the node is marked Updated
        // (decomp Game/Net/NodeAlignSystem.cs:132 and :168). On the receiver, a boundary node picks up a
        // new arm from this batch and NodeAlign re-centres it from a DIFFERENT connected set than the
        // builder had at capture time → the node drifts metres from the builder's settled coordinate that
        // the batch actually carries (NodePos*/BoundaryPos*, captured POST-align). Blocks/lanes/zone cells
        // then derive off the wrong node coord and the radars hash-diff. This corrector runs AFTER the
        // native pipeline has settled (Age>=3) and nudges each node back to its wire-authoritative position
        // — small drifts via MoveNodeWithCurves (NodeAlign converges once both arm sets match), a large
        // relocation via detach-move-reattach (replaces the old permanent BOUND-SKIP-LARGE give-up).
        private readonly List<PendingPosFix> _pendingPosFixes = new List<PendingPosFix>();

        // Arms snapshotted by a large-drift relocation (DetachAndRelocate), re-emitted as vanilla edge
        // definitions on the NEXT drain — never the same frame as the deletes, so GenerateEdges'
        // ConnectionExists can't see the moribund arm still in the buffer and silently drop the definition
        // (I6). Mirrors _pendingDefinitions' one-frame-later contract.
        private readonly List<PendingReattach> _pendingReattach = new List<PendingReattach>();

        // FIX C — FOLD CEILING PARK. An edge whose captured bezier endpoint sits > FoldParkCeiling from the
        // WIRE position of the node id it names is NOT emitted immediately (the old FOLD-TRUST bent the edge
        // tens of metres onto the stale wire coord — the 32 m field bug). Instead it is parked here and
        // retried each frame: once a NodePosUpdate has brought the resolved node's LIVE position within
        // EndpointFoldTolerance of the bezier endpoint, it emits cleanly (pinned to the live coord); if it
        // never converges within FoldParkTtl frames it emits anyway as FOLD-TRUST-EXPIRED (pinned to the
        // node's CURRENT live coord, not the stale wire one). Retried at the top of OnUpdate, after the NPU
        // drain, so it sees this frame's position corrections.
        private readonly List<PendingFoldEdge> _pendingFoldEdges = new List<PendingFoldEdge>();

        /// <summary>A node whose local (NodeAlign-derived) position must be reconciled to the builder's
        /// wire-authoritative <see cref="WantPos"/>. <see cref="Age"/> gates the settle delay (>=3 frames)
        /// AND caps the unresolved-id retry (drop at 120); <see cref="Tries"/> caps the small-drift nudge
        /// (give up after 3, graph stays consistent); <see cref="Relocated"/> marks that a large-drift
        /// detach-move-reattach already fired for this id, so a second >10 m reading gives up instead of
        /// re-detaching (no relocation loop).</summary>
        private struct PendingPosFix
        {
            public ulong Id;
            public float3 WantPos;
            public int Age;
            public int Tries;
            public bool Relocated;
        }

        /// <summary>One arm snapshotted before a large-drift relocation deleted it, carrying exactly what
        /// <see cref="EmitEdgeCourse"/> transports (prefab, seed, elevation, Upgraded) plus the already
        /// 2/3-1/3-shifted bezier so the re-emit needs no recomputation. The moved node is one endpoint
        /// (<see cref="ThisIsStart"/>); <see cref="OtherNode"/> is the intact opposite endpoint.</summary>
        private struct ReattachArm
        {
            public Entity Prefab;
            public string PrefabName;
            public Bezier4x3 Bezier;
            public bool ThisIsStart;
            public ulong ThisNodeId;
            public ulong OtherNodeId;
            public Entity OtherNode;
            public int Seed;
            public bool HasElev;
            public float2 Elev;
            public bool HasUpgraded;
            public uint UpgG;
            public uint UpgL;
            public uint UpgR;
        }

        /// <summary>A relocated node's snapshotted arms, held one frame so the re-emit happens AFTER the
        /// deletes have been consumed (I6).</summary>
        private struct PendingReattach
        {
            public ulong NodeId;
            public Entity Node;
            public List<ReattachArm> Arms;
        }

        /// <summary>FIX C — an edge parked because a captured bezier endpoint was too far (&gt; FoldParkCeiling)
        /// from the wire position of the node id it names. Self-contained (everything <see cref="EmitEdgeCourse"/>
        /// transports) so the retry needs no wire command: it re-resolves the endpoints by id and, once their
        /// LIVE positions have converged (a NodePosUpdate arrived) or <see cref="Ttl"/> runs out, emits the
        /// course pinned to the live node coords.</summary>
        private struct PendingFoldEdge
        {
            public ulong StartId;
            public ulong EndId;
            public Entity Prefab;
            public string PrefabName;
            public Bezier4x3 Bezier;
            public int Seed;
            public bool HasElev;
            public float2 Elev;
            public bool HasUpgraded;
            public uint UpgG;
            public uint UpgL;
            public uint UpgR;
            public bool HasOrder;
            public uint OrderStart;
            public uint OrderEnd;
            public int Ttl;
        }

        /// <summary>One edge's wire-authoritative BuildOrder, queued until the edge it names exists.</summary>
        private struct PendingOrderFix
        {
            public ulong StartId;
            public ulong EndId;
            public uint OrderStart;
            public uint OrderEnd;
            public int Age;
        }

        /// <summary>A boundary id resolved by POSITION rather than a direct <see cref="CS2M_NodeSyncIds"/>
        /// hit — deferred (plan-then-commit) so the Map mutation happens only once every boundary id in the
        /// batch has resolved. <see cref="Wide"/> distinguishes <see cref="FindNodeStrict"/> (bare node,
        /// exact ≤0.5 m — safe to <c>Register</c>) from <see cref="FindNodeWide"/> (junction-scale match,
        /// candidate MAY already carry a different id — commit must <c>Remap</c> instead of clobbering it).</summary>
        private struct BoundaryAdopt
        {
            public ulong Id;
            public Entity Node;
            public bool Wide;
        }

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
                    ComponentType.ReadOnly<Owner>(), // building/extractor sub-nets grow locally by per-machine RNG — never ours to adopt/move
                },
            });
            // Delete's position-fallback scan (FindEdgeByPosition): every LIVE edge, id or no id. Same
            // None-set as the identity path (FindEdgeById skips Temp/Deleted transitively via ConnectedEdge).
            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(), // building/extractor sub-nets grow locally by per-machine RNG — never ours to adopt/move
                },
            });
            CS2M.Log.Info("[Batch] NetBatchApplySystem created");
        }

        protected override void OnUpdate()
        {
            // FIX B — NodePosUpdate drain (TOP of OnUpdate, before every other drain so the fold-park retry
            // below sees this frame's corrections). Each update reconciles a node BY IDENTITY to the builder's
            // newest settled coord via the SAME machinery the batch apply uses. When not PLAYING, drop the
            // queue instead (teardown-equivalent cleanup — LocalPlayer.cs is intentionally not touched).
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus == PlayerStatus.PLAYING)
            {
                while (RemoteNodePosUpdateQueue.TryDequeue(out NodePosUpdateCommand npu))
                {
                    try
                    {
                        ApplyNodePosUpdate(npu);
                    }
                    catch (System.Exception ex)
                    {
                        CS2M.Log.Info($"[Guard] NodePosUpdate apply failed: {ex.Message}");
                    }
                }
            }
            else
            {
                RemoteNodePosUpdateQueue.Clear();
                if (_pendingFoldEdges.Count > 0) { _pendingFoldEdges.Clear(); }
            }

            // Edge definitions injected last frame were consumed by GenerateNodes/Edges already — clean up.
            // (ToolClearSystem may have destroyed them first; the Exists guard makes this idempotent.)
            // Mirrors NetPlaceApplySystem; runs before the PLAYING gate so nothing ever leaks.
            for (int i = 0; i < _pendingDefinitions.Count; i++)
            {
                if (EntityManager.Exists(_pendingDefinitions[i]))
                {
                    EntityManager.DestroyEntity(_pendingDefinitions[i]);
                }
            }

            _pendingDefinitions.Clear();

            // BuildOrder corrector (SILENT WRITE): the edges queued last frame (EmitEdgeCourse) now exist
            // (GenerateEdges consumed their definitions before this OnUpdate ran again) — resolve by
            // node-pair identity and stamp the WIRE m_Start/m_End over the receiver's local-counter value
            // IN SILENCE. This converges FUTURE derivations to the SAME cell-overlap priority both machines
            // used (Zones.BuildOrder folds Game.Net.BuildOrder, see CellOverlapJobs.CheckPriority) instead
            // of each PC's own process-local GenerateEdgesSystem counter (decomp GenerateEdgesSystem.cs:2093)
            // — the root cause of the visibility split. It does NOT mark anything Updated/BatchesUpdated:
            // NEVER re-trigger a native block derive on a freshly-restructured junction. Two native dumps
            // (08/07 23:21 and 23:49) proved BlockSystem@Mod4 (UpdateBlocksJob) null-derefs in the frame of
            // that re-trigger EVEN with every quiescence guard passing clean — re-deriving is unsafe by
            // nature here. The CURRENT blocks converge instead via the host-authoritative same-size ZoneAuth
            // heal (ZoneBlockAuthoritySystems), which reconciles existing blocks' content/order; this silent
            // write only converges derivations yet to come. Runs before the PLAYING gate, same as the
            // _pendingDefinitions cleanup above, so a fix in flight never leaks past a disconnect.
            for (int i = _pendingOrderFixes.Count - 1; i >= 0; i--)
            {
                PendingOrderFix fix = _pendingOrderFixes[i];
                if (FindEdgeById(fix.StartId, fix.EndId, out Entity fixEdge))
                {
                    if (EntityManager.HasComponent<Game.Net.BuildOrder>(fixEdge))
                    {
                        Game.Net.BuildOrder localOrder = EntityManager.GetComponentData<Game.Net.BuildOrder>(fixEdge);
                        if (localOrder.m_Start != fix.OrderStart || localOrder.m_End != fix.OrderEnd)
                        {
                            // Silent write ONLY — stamp the wire-authoritative order and nothing else. No
                            // Updated/BatchesUpdated on the edge or its endpoint nodes: re-triggering the
                            // native block derive on a freshly-restructured junction crashes BlockSystem@Mod4
                            // (see block comment above). Future derivations read this converged order; the
                            // current blocks converge via the same-size ZoneAuth heal.
                            EntityManager.SetComponentData(fixEdge, new Game.Net.BuildOrder
                            {
                                m_Start = fix.OrderStart,
                                m_End = fix.OrderEnd,
                            });

                            CS2M.Log.Info($"[Batch] ORDER-FIX-SILENT edge={fixEdge.Index} " +
                                          $"order={localOrder.m_Start}-{localOrder.m_End}->{fix.OrderStart}-{fix.OrderEnd} " +
                                          "(no re-derive; converge via ZoneAuth heal)");
                        }
                    }

                    _pendingOrderFixes.RemoveAt(i);
                }
                else
                {
                    fix.Age++;
                    if (fix.Age >= 60)
                    {
                        CS2M.Log.Info($"[Batch] ORDER-DROP startId={fix.StartId} endId={fix.EndId} " +
                                      $"(unresolved {fix.Age}f)");
                        _pendingOrderFixes.RemoveAt(i);
                    }
                    else
                    {
                        _pendingOrderFixes[i] = fix;
                    }
                }
            }

            // Orphan-node deletes deferred from a previous frame's apply (RebuildAfterDelete). One frame
            // has now passed, so ReferencesSystem has revalidated the junction's buffers and the recycle
            // window BlockSystem@Mod4 could have walked is closed (crash #4). Delete the node only if it is
            // STILL orphaned (0 live connected edges); if it re-gained a live arm in the meantime, keep it
            // and just re-derive. Runs before the PLAYING gate like the cleanups above so nothing leaks.
            for (int i = _pendingOrphanNodes.Count - 1; i >= 0; i--)
            {
                Entity node = _pendingOrphanNodes[i];
                _pendingOrphanNodes.RemoveAt(i);

                if (!EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node))
                {
                    continue;
                }

                int liveArms = 0;
                if (EntityManager.HasBuffer<ConnectedEdge>(node))
                {
                    DynamicBuffer<ConnectedEdge> ce = EntityManager.GetBuffer<ConnectedEdge>(node, true);
                    for (int j = 0; j < ce.Length; j++)
                    {
                        Entity e = ce[j].m_Edge;
                        if (EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e))
                        {
                            liveArms++;
                        }
                    }
                }

                if (liveArms == 0)
                {
                    EntityManager.AddComponent<Deleted>(node);
                    CS2M.Log.Info($"[Batch] ORPHAN-DEL deferred node={node.Index}");
                }
                else
                {
                    MarkUpdated(node);
                    CS2M.Log.Info($"[Batch] ORPHAN-KEEP node={node.Index} live={liveArms}");
                }
            }

            // FIX A step 3d — REATTACH the arms a large-drift relocation snapshotted last frame. The
            // deletes it queued have now been consumed by GenerateEdges/CleanupSystem (a full frame has
            // passed), so re-emitting each arm as a vanilla CreationDefinition+NetCourse is safe: no
            // moribund arm lingers in the node's ConnectedEdge buffer for GenerateEdges' ConnectionExists
            // to trip over (I6). Runs BEFORE the pos-fix drain below so entries that drain just added this
            // frame wait until NEXT frame (they are appended after this loop finishes). Definitions land in
            // _pendingDefinitions and are destroyed at the top of the following OnUpdate like every other
            // emitted definition.
            for (int i = _pendingReattach.Count - 1; i >= 0; i--)
            {
                PendingReattach r = _pendingReattach[i];
                _pendingReattach.RemoveAt(i);

                if (!EntityManager.Exists(r.Node) || EntityManager.HasComponent<Deleted>(r.Node))
                {
                    CS2M.Log.Info($"[Batch] POS-REATTACH-SKIP id={r.NodeId} (node gone)");
                    continue;
                }

                int emitted = 0;
                foreach (ReattachArm arm in r.Arms)
                {
                    if (arm.Prefab == Entity.Null || arm.PrefabName == null
                        || arm.OtherNode == Entity.Null || !EntityManager.Exists(arm.OtherNode)
                        || EntityManager.HasComponent<Deleted>(arm.OtherNode))
                    {
                        continue;
                    }

                    Entity startNode = arm.ThisIsStart ? r.Node : arm.OtherNode;
                    Entity endNode = arm.ThisIsStart ? arm.OtherNode : r.Node;
                    if (startNode == endNode)
                    {
                        continue; // never fabricate a self-loop
                    }

                    EmitReattachCourse(arm, startNode, endNode);
                    emitted++;
                }

                CS2M.Log.Info($"[Batch] POS-REATTACH id={r.NodeId} arms={emitted}");
            }

            // FIX C — FOLD-PARK RETRY. Re-attempt each edge parked because its captured bezier endpoint was
            // too far from the wire position of the node id it named. This runs AFTER the NPU drain above, so
            // the resolved endpoints' LIVE positions already reflect this frame's corrections. Emit when both
            // endpoints have converged onto the captured bezier ends, or force-emit (FOLD-TRUST-EXPIRED,
            // pinned to the live coords) when the TTL runs out. Emits definitions like the reattach drain,
            // which is why it lives here (before the PLAYING gate); a post-teardown id simply fails to resolve
            // and ages out with no side effect.
            for (int i = _pendingFoldEdges.Count - 1; i >= 0; i--)
            {
                PendingFoldEdge fe = _pendingFoldEdges[i];
                fe.Ttl--;
                bool expired = fe.Ttl <= 0;

                bool sOk = CS2M_NodeSyncIds.TryResolve(EntityManager, fe.StartId, out Entity sN);
                bool eOk = CS2M_NodeSyncIds.TryResolve(EntityManager, fe.EndId, out Entity eN);
                if (!sOk || !eOk || sN == eN)
                {
                    if (expired)
                    {
                        CS2M.Log.Info($"[Batch] FOLD-PARK-DROP startId={fe.StartId} endId={fe.EndId} " +
                                      "(endpoint unresolved/degenerate at TTL expiry)");
                        _pendingFoldEdges.RemoveAt(i);
                    }
                    else
                    {
                        _pendingFoldEdges[i] = fe;
                    }

                    continue;
                }

                float3 sPos = EntityManager.GetComponentData<Node>(sN).m_Position;
                float3 ePos = EntityManager.GetComponentData<Node>(eN).m_Position;
                float gapS = math.distance(new float2(sPos.x, sPos.z), new float2(fe.Bezier.a.x, fe.Bezier.a.z));
                float gapE = math.distance(new float2(ePos.x, ePos.z), new float2(fe.Bezier.d.x, fe.Bezier.d.z));

                if ((gapS <= EndpointFoldTolerance && gapE <= EndpointFoldTolerance) || expired)
                {
                    EmitParkedFoldEdge(fe, sN, eN, sPos, ePos, expired, gapS, gapE);
                    _pendingFoldEdges.RemoveAt(i);
                }
                else
                {
                    _pendingFoldEdges[i] = fe;
                }
            }

            // FIX A steps 2-3 — DEFERRED POSITION CORRECTOR. Nudge each queued node back to the builder's
            // wire-authoritative coordinate once the native pipeline has settled (Age>=3). See the field
            // comment on _pendingPosFixes for the "why". Runs here (before the PLAYING gate, same slot as
            // the other drains) so a fix in flight never leaks past a disconnect.
            for (int i = _pendingPosFixes.Count - 1; i >= 0; i--)
            {
                PendingPosFix pf = _pendingPosFixes[i];

                if (!CS2M_NodeSyncIds.TryResolve(EntityManager, pf.Id, out Entity node))
                {
                    // Id not resolvable yet (a sibling batch may still be creating it) — age up to a cap
                    // then drop rather than retry forever.
                    pf.Age++;
                    if (pf.Age >= 120)
                    {
                        CS2M.Log.Info($"[Batch] POS-DROP id={pf.Id} (unresolved {pf.Age}f)");
                        _pendingPosFixes.RemoveAt(i);
                    }
                    else
                    {
                        _pendingPosFixes[i] = pf;
                    }

                    continue;
                }

                // Let GenerateEdges/NodeAlign/Geometry settle before measuring drift — the derive runs over
                // this batch's set across the next couple of frames; measuring too early would chase a
                // position that is still moving.
                if (pf.Age < 3)
                {
                    pf.Age++;
                    _pendingPosFixes[i] = pf;
                    continue;
                }

                float3 localPos = EntityManager.GetComponentData<Node>(node).m_Position;
                float drift = math.distance(localPos, pf.WantPos);

                if (drift <= 0.25f)
                {
                    // Inside the radar's quantum — converged.
                    _pendingPosFixes.RemoveAt(i);
                    continue;
                }

                if (drift <= 10f)
                {
                    // Small drift: drag the node (and its curves, I4) to the authoritative coord. NodeAlign
                    // may re-centre again next frame, but once both PCs' connected arm sets match it is
                    // deterministic and converges — so re-check on the next drain (Tries++) instead of
                    // removing. Cap at 3 attempts; a residual sub-metre wobble is harmless and the graph is
                    // consistent, so give up cleanly rather than churn forever.
                    NetGraphSafety.MoveNodeWithCurves(EntityManager, node, pf.WantPos);
                    pf.Tries++;
                    CS2M.Log.Info($"[Batch] POS-FIX id={pf.Id} drift={drift:F2} try={pf.Tries}");
                    if (pf.Tries >= 3)
                    {
                        CS2M.Log.Info($"[Batch] POS-GIVEUP id={pf.Id} drift={drift:F2} (>3 tries, graph consistent)");
                        _pendingPosFixes.RemoveAt(i);
                    }
                    else
                    {
                        _pendingPosFixes[i] = pf;
                    }

                    continue;
                }

                // Large drift (>10 m): the builder relocated this junction while drawing the batch (e.g. an
                // edge split fused two junctions). Teleporting a connected node this far corrupts the graph
                // and crashes the game's Burst net pass (the old BOUND-SKIP-LARGE gave up here and left a
                // permanent 44-74 m divergence). DETACH the arms, move the bare node, and re-emit the arms
                // next frame — the receiver's own safe del+create path.
                if (pf.Relocated)
                {
                    // Already relocated once and STILL >10 m off — do not detach again (loop guard). The
                    // graph is consistent; accept the residual.
                    CS2M.Log.Info($"[Batch] POS-GIVEUP id={pf.Id} drift={drift:F2} (post-reloc, no re-detach)");
                    _pendingPosFixes.RemoveAt(i);
                    continue;
                }

                int arms = DetachAndRelocate(node, pf.Id, pf.WantPos);
                CS2M.Log.Info($"[Batch] POS-RELOC id={pf.Id} drift={drift:F2} arms={arms}");
                // Re-arm the fix so any residual drift after the reattach settles is caught by the small-
                // drift nudge above; Relocated blocks a second detach.
                pf.Relocated = true;
                pf.Age = 0;
                pf.Tries = 0;
                _pendingPosFixes[i] = pf;
            }

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
            var adoptPlan = new List<BoundaryAdopt>();
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
                        adoptPlan.Add(new BoundaryAdopt { Id = bid, Node = be, Wide = false });
                    }
                    // JUNCTION-SCALE fallback: the strict 0.5 m match above assumes the save node never
                    // moved, but attaching THIS batch's new arm re-centres the junction on the builder's
                    // side (proven elsewhere in this exact codebase — NetPlaceApplySystem.FindJunctionNode:
                    // "a busy junction re-centres MORE than 3.5 m as roads join over time") BEFORE the
                    // client ever applies anything, so its still-untouched twin can sit meters from
                    // BoundaryPos. Same two radii as that proven legacy path (3.5 m any node / 8 m existing
                    // junction only) — see FindNodeWide for the ambiguity guard.
                    else if (hasPos && FindNodeWide(
                                 new float3(cmd.BoundaryPosX[bi], cmd.BoundaryPosY[bi], cmd.BoundaryPosZ[bi]),
                                 claimed, out be))
                    {
                        claimed.Add(be);
                        adoptPlan.Add(new BoundaryAdopt { Id = bid, Node = be, Wide = true });
                    }
                    else
                    {
                        _lastMissingId = bid;
                        return false;
                    }

                    idToEntity[bid] = be;
                    boundarySet.Add(bid);

                    if (EntityManager.HasComponent<Node>(be))
                    {
                        float3 bPos = EntityManager.GetComponentData<Node>(be).m_Position;
                        CS2M.Log.Info($"[Batch] BOUND-OK id={bid} entity={be.Index} pos=({bPos.x:F1},{bPos.z:F1})");
                    }
                }
            }

            // Every boundary resolved — NOW commit the planned adoptions.
            foreach (BoundaryAdopt adopt in adoptPlan)
            {
                if (adopt.Wide)
                {
                    // The junction-scale candidate may already answer to a DIFFERENT id (e.g. an earlier
                    // batch touching the same junction adopted it first). Remap only mutates the id↔entity
                    // Map — never the node's own CS2M_NodeSyncId component — so that existing identity
                    // keeps working exactly as SnapBoundaryNode's BOUND-MERGE branch already relies on.
                    // A bare node (no component yet) is safe to stamp directly instead.
                    if (EntityManager.HasComponent<CS2M_NodeSyncId>(adopt.Node))
                    {
                        CS2M_NodeSyncIds.Remap(adopt.Id, adopt.Node);
                    }
                    else
                    {
                        CS2M_NodeSyncIds.Register(EntityManager, adopt.Node, adopt.Id);
                    }

                    CS2M.Log.Info($"[Batch] boundary REMAP id={adopt.Id} -> node={adopt.Node.Index} " +
                                  "por pos (junction-scale match)");
                }
                else
                {
                    CS2M_NodeSyncIds.Register(EntityManager, adopt.Node, adopt.Id);
                    CS2M.Log.Info($"[Batch] boundary adopt-by-pos id={adopt.Id} node={adopt.Node.Index}");
                }
            }

            // (1b) SNAP a boundary node the builder MOVED after this batch's junction snapped two arms
            // together (proven live 06/07: node 9188895750254231569 slid ~70 m when a 2nd trace fused it
            // into another junction). The wire's BoundaryPos* is the builder's SETTLED post-move position;
            // if the receiver's local node is still at the OLD spot, the new edges below — whose bezier
            // endpoints are already captured at the NEW spot — would solder onto stale geometry and the
            // two worlds permanently diverge. Runs here, AFTER every boundary id resolved (any miss above
            // already returned false with ZERO mutation — the plan-then-commit contract this whole method
            // documents), so it is part of the SAME atomic commit as the adopt-by-pos loop just above, not
            // a separate mutation window; and it runs BEFORE (3) emits the new edges' CreationDefinition,
            // so GenerateEdges/NodeAlign see the ALIGNED node the instant they consume this frame's set.
            bool boundaryHasPos = cmd.BoundaryNodeIds != null && cmd.BoundaryPosX != null
                                   && cmd.BoundaryPosY != null && cmd.BoundaryPosZ != null
                                   && cmd.BoundaryPosX.Length == cmd.BoundaryNodeIds.Length
                                   && cmd.BoundaryPosY.Length == cmd.BoundaryNodeIds.Length
                                   && cmd.BoundaryPosZ.Length == cmd.BoundaryNodeIds.Length;
            if (boundaryHasPos)
            {
                for (int bi = 0; bi < cmd.BoundaryNodeIds.Length; bi++)
                {
                    SnapBoundaryNode(idToEntity[cmd.BoundaryNodeIds[bi]], cmd.BoundaryNodeIds[bi],
                        new float3(cmd.BoundaryPosX[bi], cmd.BoundaryPosY[bi], cmd.BoundaryPosZ[bi]));
                }
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

            // WIRE POSITION MAP (id -> builder's settled coord for that node), from the SAME wire fields the
            // pos-corrector trusts. EmitEdgeCourse cross-checks each edge endpoint's bezier point against the
            // wire position of the node id it names: a mismatch is the "folded id" signal (see the FOLD GUARD
            // in EmitEdgeCourse / EndpointFoldTolerance). Built here so both new-node and boundary coords are
            // present before any edge is emitted.
            var wirePos = new Dictionary<ulong, float3>();
            for (int i = 0; i < nodeCount; i++)
            {
                wirePos[cmd.NodeIds[i]] = new float3(cmd.NodePosX[i], cmd.NodePosY[i], cmd.NodePosZ[i]);
            }

            if (boundaryHasPos)
            {
                for (int bi = 0; bi < cmd.BoundaryNodeIds.Length; bi++)
                {
                    wirePos[cmd.BoundaryNodeIds[bi]] =
                        new float3(cmd.BoundaryPosX[bi], cmd.BoundaryPosY[bi], cmd.BoundaryPosZ[bi]);
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

                var bezier = new Bezier4x3(
                    new float3(cmd.EdgeAX[i], cmd.EdgeAY[i], cmd.EdgeAZ[i]),
                    new float3(cmd.EdgeBX[i], cmd.EdgeBY[i], cmd.EdgeBZ[i]),
                    new float3(cmd.EdgeCX[i], cmd.EdgeCY[i], cmd.EdgeCZ[i]),
                    new float3(cmd.EdgeDX[i], cmd.EdgeDY[i], cmd.EdgeDZ[i]));

                // Idempotency: this exact edge (by node-pair identity) already exists → duplicate delivery.
                // BUT node-pair identity alone can lie once a boundary id has been silently re-pointed onto
                // a different physical node by BOUND-MERGE above (or by any other id remap): FindEdgeById
                // would then match a LIVE edge that merely SHARES the (now-stale) id pair with the wire
                // command, not the edge the command actually describes. Cross-check the curve length before
                // trusting the id match — a real duplicate delivery ships byte-identical geometry, so a
                // >2 m difference means "different edge, id pair coincidentally collided", not "dup".
                if (FindEdgeById(cmd.EdgeStartNodeIds[i], cmd.EdgeEndNodeIds[i], out Entity liveEdge))
                {
                    float wantLen = MathUtils.Length(bezier);
                    float liveLen = EntityManager.HasComponent<Curve>(liveEdge)
                        ? EntityManager.GetComponentData<Curve>(liveEdge).m_Length
                        : wantLen;
                    if (math.abs(liveLen - wantLen) <= 2f)
                    {
                        CS2M.Log.Info($"[Batch] SKIP dup edge {i} (already live)");
                        continue;
                    }

                    CS2M.Log.Info($"[Batch] DUP-MISMATCH re-emitting edge {i} liveEdge={liveEdge.Index} " +
                                  $"liveLen={liveLen:F1} wantLen={wantLen:F1} " +
                                  "(node-pair id matched but geometry didn't — stale id after a merge, not a real dup)");
                }

                // Vanilla path: emit a Permanent definition (GenerateEdges builds the REAL edge this frame).
                if (EmitEdgeCourse(cmd, i, bezier, s, en, wirePos))
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

            // FIX A step 1 — queue EVERY node this batch touched (new + boundary) for the deferred position
            // corrector. The batch carries the builder's settled, post-NodeAlign coordinate for each; the
            // corrector reconciles the receiver's own NodeAlign-derived position to it once the native
            // pipeline has settled (see _pendingPosFixes). Only ids we actually resolved this frame are
            // enqueued (an unresolved id would just age out).
            for (int i = 0; i < nodeCount; i++)
            {
                if (idToEntity.ContainsKey(cmd.NodeIds[i]))
                {
                    EnqueuePosFix(cmd.NodeIds[i],
                        new float3(cmd.NodePosX[i], cmd.NodePosY[i], cmd.NodePosZ[i]));
                }
            }

            if (boundaryHasPos)
            {
                for (int bi = 0; bi < cmd.BoundaryNodeIds.Length; bi++)
                {
                    if (idToEntity.ContainsKey(cmd.BoundaryNodeIds[bi]))
                    {
                        EnqueuePosFix(cmd.BoundaryNodeIds[bi],
                            new float3(cmd.BoundaryPosX[bi], cmd.BoundaryPosY[bi], cmd.BoundaryPosZ[bi]));
                    }
                }
            }

            CS2M.Log.Info($"[Batch] APPLIED nodes={createdNodes} edges={createdEdges} dels={appliedDels}");
            return true;
        }

        /// <summary>Queue a node id for the deferred position corrector, refreshing the target of an
        /// existing entry rather than duplicating it (a later batch that touches the same node ships the
        /// newer settled coord; re-arming Age/Tries lets it re-converge).</summary>
        private void EnqueuePosFix(ulong id, float3 wantPos)
        {
            if (id == 0)
            {
                return;
            }

            for (int i = 0; i < _pendingPosFixes.Count; i++)
            {
                if (_pendingPosFixes[i].Id == id)
                {
                    _pendingPosFixes[i] = new PendingPosFix { Id = id, WantPos = wantPos, Age = 0, Tries = 0 };
                    return;
                }
            }

            _pendingPosFixes.Add(new PendingPosFix { Id = id, WantPos = wantPos, Age = 0, Tries = 0 });
        }

        /// <summary>FIX A step 3 (large drift) — DETACH-MOVE. Snapshot every live arm of <paramref name="node"/>
        /// (prefab/seed/elevation/Upgraded + a bezier already 2/3-1/3-shifted to <paramref name="wantPos"/>),
        /// mark each arm <see cref="Deleted"/> (+<see cref="CS2M_RemoteDeleted"/> so the capture never echoes
        /// the delete; components kept for I8), then move the now-detaching node directly to
        /// <paramref name="wantPos"/>. Direct <see cref="SetComponentData"/> is safe (I4): the arms are
        /// Deleted, so by end-of-frame the node is degree-0 and the native pass never runs its curves against
        /// the moved node. The re-emit is deferred to the next drain (<see cref="_pendingReattach"/>, step 3d).
        /// Returns the number of arms snapshotted. Deliberately does NOT route through RebuildAfterDelete: that
        /// would queue THIS node for an orphan-delete (it is momentarily degree-0), destroying the very node we
        /// are relocating.</summary>
        private int DetachAndRelocate(Entity node, ulong nodeId, float3 wantPos)
        {
            Node cur = EntityManager.GetComponentData<Node>(node);
            float3 delta = wantPos - cur.m_Position;

            var snap = new List<ReattachArm>();
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                // Snapshot the arm entities before any structural change (AddComponent<Deleted> below moves
                // chunks and would invalidate a live buffer handle mid-iteration).
                DynamicBuffer<ConnectedEdge> ce = EntityManager.GetBuffer<ConnectedEdge>(node, true);
                var armEdges = new Entity[ce.Length];
                for (int i = 0; i < ce.Length; i++)
                {
                    armEdges[i] = ce[i].m_Edge;
                }

                foreach (Entity edge in armEdges)
                {
                    if (edge == Entity.Null || !EntityManager.Exists(edge)
                        || EntityManager.HasComponent<Deleted>(edge)
                        || !EntityManager.HasComponent<Edge>(edge) || !EntityManager.HasComponent<Curve>(edge)
                        || !EntityManager.HasComponent<PrefabRef>(edge))
                    {
                        continue;
                    }

                    Edge ed = EntityManager.GetComponentData<Edge>(edge);
                    bool isStart = ed.m_Start == node;
                    bool isEnd = ed.m_End == node;
                    if (!isStart && !isEnd)
                    {
                        continue; // stale buffer entry — this edge no longer names this node
                    }

                    if (isStart && isEnd)
                    {
                        // Degenerate self-loop (both endpoints are this node). Never re-emit it (that would
                        // fabricate a self-loop), but it MUST be deleted before the move — leaving its curve
                        // pinned to the old coord while the node moves would violate I4. Delete, don't snap.
                        if (!EntityManager.HasComponent<CS2M_RemoteDeleted>(edge))
                        {
                            EntityManager.AddComponent<CS2M_RemoteDeleted>(edge);
                        }

                        if (!EntityManager.HasComponent<Deleted>(edge))
                        {
                            EntityManager.AddComponent<Deleted>(edge);
                        }

                        continue;
                    }

                    Entity other = isStart ? ed.m_End : ed.m_Start;
                    if (other == Entity.Null || !EntityManager.Exists(other) || other == node)
                    {
                        continue;
                    }

                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    if (!_prefabSystem.TryGetPrefab(new PrefabRef(prefab), out PrefabBase pb) || pb == null)
                    {
                        continue; // can't name it → can't echo-guard or rebuild it; leave it alone
                    }

                    // Shift the bezier the same way MoveNodeWithCurves/NetGraphSafety does: the moved
                    // endpoint takes the full shift, its near control point 2/3, the far one 1/3.
                    Bezier4x3 bez = EntityManager.GetComponentData<Curve>(edge).m_Bezier;
                    if (isStart)
                    {
                        bez.a = wantPos;
                        bez.b += delta * (2f / 3f);
                        bez.c += delta * (1f / 3f);
                    }
                    else
                    {
                        bez.d = wantPos;
                        bez.c += delta * (2f / 3f);
                        bez.b += delta * (1f / 3f);
                    }

                    var arm = new ReattachArm
                    {
                        Prefab = prefab,
                        PrefabName = pb.name,
                        Bezier = bez,
                        ThisIsStart = isStart,
                        ThisNodeId = nodeId,
                        OtherNodeId = EntityManager.HasComponent<CS2M_NodeSyncId>(other)
                            ? EntityManager.GetComponentData<CS2M_NodeSyncId>(other).m_Id : 0UL,
                        OtherNode = other,
                        Seed = EntityManager.HasComponent<PseudoRandomSeed>(edge)
                            ? EntityManager.GetComponentData<PseudoRandomSeed>(edge).m_Seed : 0,
                    };

                    if (EntityManager.HasComponent<Game.Net.Elevation>(edge))
                    {
                        arm.HasElev = true;
                        arm.Elev = EntityManager.GetComponentData<Game.Net.Elevation>(edge).m_Elevation;
                    }

                    if (EntityManager.HasComponent<Upgraded>(edge))
                    {
                        CompositionFlags f = EntityManager.GetComponentData<Upgraded>(edge).m_Flags;
                        arm.HasUpgraded = true;
                        arm.UpgG = (uint) f.m_General;
                        arm.UpgL = (uint) f.m_Left;
                        arm.UpgR = (uint) f.m_Right;
                    }

                    snap.Add(arm);

                    // Delete the arm (keep components — I8; tag so the capture skips the echo).
                    if (!EntityManager.HasComponent<CS2M_RemoteDeleted>(edge))
                    {
                        EntityManager.AddComponent<CS2M_RemoteDeleted>(edge);
                    }

                    if (!EntityManager.HasComponent<Deleted>(edge))
                    {
                        EntityManager.AddComponent<Deleted>(edge);
                    }

                    // The opposite endpoint loses (then regains) an arm — re-derive its junction. It keeps
                    // its other arms, so it never orphans here.
                    MarkUpdated(other);
                }
            }

            // Move the bare node (arms Deleted → degree-0 by end of frame; direct set does not violate I4).
            EntityManager.SetComponentData(node, new Node { m_Position = wantPos, m_Rotation = cur.m_Rotation });
            MarkUpdated(node);

            _pendingReattach.Add(new PendingReattach { NodeId = nodeId, Node = node, Arms = snap });
            return snap.Count;
        }

        /// <summary>FIX A step 3d — re-emit one snapshotted arm as a vanilla <c>CreationDefinition</c>+
        /// <c>NetCourse</c>, an exact mirror of <see cref="EmitEdgeCourse"/> (echo-guard, Permanent
        /// definition, deferred Upgraded via <see cref="RemoteNetUpgradeQueue"/>) but driven from a
        /// <see cref="ReattachArm"/> snapshot instead of a wire command. The bezier is already shifted, so
        /// GenerateEdges rebuilds the REAL edge touching the relocated node at its authoritative coord.</summary>
        private void EmitReattachCourse(ReattachArm arm, Entity startNode, Entity endNode)
        {
            Bezier4x3 bezier = arm.Bezier;

            // Echo guard (same as EmitEdgeCourse): the rebuilt edge is born Applied+Created WITHOUT
            // CS2M_RemotePlaced, so mark its seg hash first or NetBatchCaptureSystem would re-broadcast it.
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(bezier.a, bezier.d, arm.PrefabName));

            float2 elev = arm.HasElev ? arm.Elev : float2.zero;

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;

            course.m_StartPosition.m_Position = bezier.a;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(bezier));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_Elevation = elev;
            course.m_StartPosition.m_ParentMesh = -1;
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst;
            course.m_StartPosition.m_Entity = startNode;

            course.m_EndPosition.m_Position = bezier.d;
            course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(bezier));
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_Elevation = elev;
            course.m_EndPosition.m_ParentMesh = -1;
            course.m_EndPosition.m_Flags = CoursePosFlags.IsLast;
            course.m_EndPosition.m_Entity = endNode;

            // Both endpoints pinned by entity (Permanent → GenerateEdges.TryGetNode uses m_Entity directly,
            // decomp GenerateEdgesSystem.cs:1228; never position-resolves, so no fusion). Logged once per arm.
            CS2M.Log.Info($"[Batch] REATTACH-PIN start={startNode.Index} end={endNode.Index} name={arm.PrefabName}");

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = arm.Prefab,
                m_RandomSeed = arm.Seed,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponentData(def, course);
            EntityManager.AddComponent<Updated>(def);
            _pendingDefinitions.Add(def);

            // Upgraded flags: re-applied by identity via the existing net-edit pipeline (same as
            // EmitEdgeCourse), not carried on the definition.
            if (arm.HasUpgraded)
            {
                ulong startId = arm.ThisIsStart ? arm.ThisNodeId : arm.OtherNodeId;
                ulong endId = arm.ThisIsStart ? arm.OtherNodeId : arm.ThisNodeId;
                RemoteNetUpgradeQueue.Enqueue(new NetUpgradeCommand
                {
                    StartNodeId = startId,
                    EndNodeId = endId,
                    General = arm.UpgG,
                    Left = arm.UpgL,
                    Right = arm.UpgR,
                    StartX = bezier.a.x, StartY = bezier.a.y, StartZ = bezier.a.z,
                    EndX = bezier.d.x, EndY = bezier.d.y, EndZ = bezier.d.z,
                    IsNode = false,
                });
            }
        }

        /// <summary>Align a resolved boundary node to the builder's settled position when the two have
        /// drifted apart (junction snap moved the node on the builder's PC after/while this batch was
        /// captured). Sanity-gated: &lt;0.25 m is noise (float round-trip), &gt;=200 m means BoundaryPos is
        /// wrong/stale data, not a real snap — skip rather than teleport a node across the map. Structural
        /// change (AddComponent) always happens AFTER the plain SetComponentData, and the ConnectedEdge
        /// buffer handle is always taken AFTER that structural change (same ordering as the proven Heal
        /// path in ZoneBlockAuthoritySystems.cs) — any buffer fetched before an AddComponent on this same
        /// entity is invalidated by it.</summary>
        // Above this displacement, a "snap" is no longer plausibly the same junction settling a few
        // metres as arms attach — it is the builder having folded this id's node into a DIFFERENT one
        // (edge split / node re-use during the SAME batch's draw). See the BOUND-MERGE branch below.
        private const float SnapMergeDistance = 10f;

        // How close an ALREADY-REGISTERED node must sit to the builder's settled position for that node
        // to be treated as "the survivor of the merge" rather than "unrelated node that happens to be far
        // away". 2 m is generous vs the sub-metre cross-machine float drift NodePinSystem/BOUND-SNAP's own
        // <=0.25 m noise floor already accounts for, but tight enough that it won't misfire on two actually
        // distinct junctions that happen to be near each other.
        private const float SnapMergeSearchRadius = 2f;

        // FOLD GUARD (EmitEdgeCourse) — max horizontal gap allowed between an edge's captured bezier endpoint
        // and the WIRE-authoritative position of the node id that endpoint names. In a consistent batch these
        // are EQUAL: the builder captures curve.d == node.m_Position (net invariant I4 — a curve endpoint
        // always meets its node) and BoundaryPos/NodePos from that same node in the same snapshot, so the two
        // wire values coincide to float noise. A large gap means EITHER (fold-real) the id resolved to a
        // DIFFERENT physical node than the edge geometrically ends at — the builder folded/split this id onto
        // another node mid-draw (proven live 09/07 00:41:47: id ...817 named A@252/-446 but the edge from ...824
        // ended at B@259/-447, 7 m away; soldering the 4th arm onto A fused A+B into one junction → ~15 zone
        // blocks diverged) — OR (stale-curve) the id is CORRECT but the captured curve is old, snapped before
        // the builder's NodeAlign settled the junction (proven live 09/07 01:13: id ...371 gap=10.6 m, but the
        // builder had a SINGLE degree-3 node at the wire pos — the earlier "mint a node at bezierEnd" cure grew
        // a phantom degree-1 node and DIVERGED). ReanchorFoldedEndpoint disambiguates the two and cures each
        // correctly. 4 m sits far above the ~0 consistent-case gap, so it fires ONLY on genuine wire
        // self-inconsistency, never on junction re-centre (BOUND-SNAP already reconciled the node first).
        private const float EndpointFoldTolerance = 4f;

        // FIX C — FOLD CEILING. Above this wire gap the fold guard REFUSES to guess in-frame (neither
        // TRUST-pin nor SPLIT-attach): it PARKS the edge and waits for a NodePosUpdate to bring the resolved
        // node's LIVE position within EndpointFoldTolerance of the bezier endpoint. 8 m sits comfortably above
        // the (EndpointFoldTolerance, 8] band the in-frame TRUST/SPLIT logic still handles and well below the
        // 20-32 m drifts the field bug bent edges by (FOLD-TRUST gap=32.4 m). Only the pre-emit wire gap is
        // measured here; the retry re-measures against live positions.
        private const float FoldParkCeiling = 8f;

        // Frames a parked fold edge waits for a NodePosUpdate to converge it before force-emitting as
        // FOLD-TRUST-EXPIRED (pinned to the node's CURRENT live coord, not the stale wire coord).
        private const int FoldParkTtl = 120;

        // FOLD GUARD disambiguator — search radius (m, xz) for a REAL node at the edge's bezier endpoint. A hit
        // means the builder genuinely has a distinct node there (fold-real → attach); a miss means the curve is
        // merely stale (stale-curve → trust the id, do NOT mint). Tight (2 m) so it only adopts a node that truly
        // coincides with the endpoint, never an unrelated neighbour a few metres off.
        private const float FoldNodeMatchRadius = 2f;

        private void SnapBoundaryNode(Entity node, ulong id, float3 wantPos)
        {
            if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
            {
                return;
            }

            Node cur = EntityManager.GetComponentData<Node>(node);
            float dist = math.distance(cur.m_Position, wantPos);
            if (dist <= 0.25f || dist >= 200f)
            {
                return;
            }

            // Large snap = probably a MERGE, not a teleport. Proven live 06/07 (statediff on the
            // '-520/1451--420/1278' edge): a boundary id's settled position landed 100 m from where the
            // receiver's node for that id sits, because the BUILDER's own net editor re-used/folded that
            // node's identity into a DIFFERENT node while drawing a later piece of the same batch (an edge
            // split at the midpoint of an existing edge can hand the split node the ORIGINAL endpoint's
            // identity and mint a fresh one for the vacated endpoint — see the reconstructed host sequence
            // in the fix's companion report). If another node is ALREADY sitting at wantPos, that other
            // node — not a 100 m drag of THIS one — is what the id now names on the builder's side.
            if (dist > SnapMergeDistance
                && CS2M_NodeSyncIds.TryFindNearbyRegistered(EntityManager, wantPos, SnapMergeSearchRadius, node, out Entity survivor))
            {
                CS2M_NodeSyncIds.Remap(id, survivor);
                CS2M.Log.Info($"[Batch] BOUND-MERGE id={id} node={node.Index}->{survivor.Index} " +
                              $"dist={dist:F1}m settledPos=({wantPos.x:F1},{wantPos.z:F1}) " +
                              "(re-pointed id to the survivor instead of moving the old node)");
                return;
            }

            if (dist > SnapMergeDistance)
            {
                // v66.6 CRASH FIX (same root cause as NetPlaceApplySystem.HealNodePosition, proven from a
                // native full dump: c0000005 null-deref in a game Burst net job after a large node move).
                // Teleporting a node that already has connected edges by a large distance corrupts the road
                // graph and the game's GenerateEdges/NodeAlign Burst pass null-derefs → whole process abort.
                // No survivor to merge onto here, so DO NOT move — skip and accept the local position.
                CS2M.Log.Info($"[Batch] BOUND-SKIP-LARGE id={id} drift={dist:F1}m " +
                              $"({cur.m_Position.x:F1},{cur.m_Position.z:F1})->({wantPos.x:F1},{wantPos.z:F1}) " +
                              "— refusing to teleport a connected node (would corrupt the graph → Burst crash)");
                return;
            }

            float3 oldPos = cur.m_Position;

            // I4 FIX: move the node AND drag its connected edge curves with it atomically. The old code
            // set the Node position and marked the connected edges Updated but left each edge's
            // m_Bezier.a/.d pinned to the OLD coordinate — and marking an edge Updated does NOT re-fit its
            // curve (GenerateEdgesSystem.UpdateNodeConnections), so GenerateEdges/NodeAlign then ran over a
            // graph whose curve endpoints no longer met their node (I4 violated) and null-deref'd inside
            // Burst. Only sub-SnapMergeDistance drifts reach here (BOUND-SKIP-LARGE/BOUND-MERGE handled the
            // rest above), a plausible junction settle — exactly what MoveNodeWithCurves is safe to apply.
            // This runs BEFORE (3) emits the new edges' definitions, so GenerateEdges sees an aligned node.
            NetGraphSafety.MoveNodeWithCurves(EntityManager, node, wantPos);

            CS2M.Log.Info($"[Batch] BOUND-SNAP id={id} moved={dist:F1}m " +
                          $"({oldPos.x:F1},{oldPos.z:F1})->({wantPos.x:F1},{wantPos.z:F1})");
        }

        /// <summary>FIX B — apply one <see cref="NodePosUpdateCommand"/>: reconcile each node BY IDENTITY to
        /// the builder's newest settled coord. ≤0.25 m skip; ≤10 m drag via <see cref="NetGraphSafety"/>.
        /// MoveNodeWithCurves; &gt;10 m route through the existing deferred POS-RELOC detach-move-reattach path
        /// (<see cref="EnqueuePosFix"/> → the pos corrector, which carries the loop guard) rather than
        /// teleporting a connected node (Burst crash).</summary>
        private void ApplyNodePosUpdate(NodePosUpdateCommand cmd)
        {
            if (cmd?.Ids == null || cmd.X == null || cmd.Y == null || cmd.Z == null)
            {
                return;
            }

            int n = cmd.Ids.Length;
            if (cmd.X.Length < n || cmd.Y.Length < n || cmd.Z.Length < n)
            {
                return;
            }

            for (int i = 0; i < n; i++)
            {
                ulong id = cmd.Ids[i];
                if (!CS2M_NodeSyncIds.TryResolve(EntityManager, id, out Entity node))
                {
                    continue;
                }

                var want = new float3(cmd.X[i], cmd.Y[i], cmd.Z[i]);
                float3 local = EntityManager.GetComponentData<Node>(node).m_Position;
                float drift = math.distance(local, want);

                if (drift <= 0.25f)
                {
                    continue;
                }

                if (drift <= 10f)
                {
                    NetGraphSafety.MoveNodeWithCurves(EntityManager, node, want);
                    CS2M.Log.Info($"[Batch] NPU id={id} drift={drift:F2}");
                }
                else
                {
                    // Large move: reuse the proven POS-RELOC detach-move-reattach path (with its Relocated
                    // loop guard) via the deferred position corrector instead of teleporting a connected node.
                    EnqueuePosFix(id, want);
                    CS2M.Log.Info($"[Batch] NPU id={id} drift={drift:F2} (queued POS-RELOC)");
                }
            }
        }

        /// <summary>FIX C — the larger of the two endpoints' horizontal (xz) gaps between the captured bezier
        /// point and the WIRE position of the node id it names. Drives the fold-ceiling park decision.</summary>
        private float MaxFoldGap(NetBatchCommand cmd, int i, Bezier4x3 bezier, Dictionary<ulong, float3> wirePos)
        {
            float g = 0f;
            if (wirePos.TryGetValue(cmd.EdgeStartNodeIds[i], out float3 ws))
            {
                g = math.max(g, math.distance(new float2(ws.x, ws.z), new float2(bezier.a.x, bezier.a.z)));
            }

            if (wirePos.TryGetValue(cmd.EdgeEndNodeIds[i], out float3 we))
            {
                g = math.max(g, math.distance(new float2(we.x, we.z), new float2(bezier.d.x, bezier.d.z)));
            }

            return g;
        }

        /// <summary>FIX C — snapshot an edge whose fold gap exceeded <see cref="FoldParkCeiling"/> into a
        /// self-contained <see cref="PendingFoldEdge"/> (everything <see cref="EmitEdgeCourse"/> transports),
        /// so the retry drain can re-resolve and emit it once a NodePosUpdate converges the node.</summary>
        private void EnqueueFoldPark(NetBatchCommand cmd, int i, Bezier4x3 bezier, Entity netPrefab)
        {
            bool hasOrder = cmd.EdgeBuildOrderStart != null && cmd.EdgeBuildOrderEnd != null
                            && i < cmd.EdgeBuildOrderStart.Length && i < cmd.EdgeBuildOrderEnd.Length
                            && (cmd.EdgeBuildOrderStart[i] != 0 || cmd.EdgeBuildOrderEnd[i] != 0);

            _pendingFoldEdges.Add(new PendingFoldEdge
            {
                StartId = cmd.EdgeStartNodeIds[i],
                EndId = cmd.EdgeEndNodeIds[i],
                Prefab = netPrefab,
                PrefabName = cmd.EdgePrefabNames[i],
                Bezier = bezier,
                Seed = cmd.EdgeSeeds[i],
                HasElev = cmd.EdgeHasElevation[i],
                Elev = cmd.EdgeHasElevation[i] ? new float2(cmd.EdgeElevX[i], cmd.EdgeElevY[i]) : float2.zero,
                HasUpgraded = cmd.EdgeHasUpgraded[i],
                UpgG = cmd.EdgeUpgradedG[i],
                UpgL = cmd.EdgeUpgradedL[i],
                UpgR = cmd.EdgeUpgradedR[i],
                HasOrder = hasOrder,
                OrderStart = hasOrder ? cmd.EdgeBuildOrderStart[i] : 0u,
                OrderEnd = hasOrder ? cmd.EdgeBuildOrderEnd[i] : 0u,
                Ttl = FoldParkTtl,
            });
        }

        /// <summary>FIX C — emit a parked fold edge as a vanilla <c>CreationDefinition</c>+<c>NetCourse</c>
        /// (mirror of <see cref="EmitEdgeCourse"/>'s tail), but with both bezier endpoints PINNED onto the
        /// resolved nodes' CURRENT LIVE positions (2/3-1/3 falloff, the <see cref="NetGraphSafety"/> rule) —
        /// never the stale wire coord that made FOLD-TRUST bend the edge. Called from the retry drain either on
        /// convergence or (<paramref name="expired"/>) at TTL expiry.</summary>
        private void EmitParkedFoldEdge(PendingFoldEdge fe, Entity startNode, Entity endNode,
            float3 sPos, float3 ePos, bool expired, float gapS, float gapE)
        {
            if (EdgeAlreadyBuilt(startNode, endNode))
            {
                CS2M.Log.Info($"[Batch] FOLD-PARK-SKIP dup startId={fe.StartId} endId={fe.EndId} (already live)");
                return;
            }

            Bezier4x3 bezier = fe.Bezier;
            float3 dS = sPos - bezier.a; bezier.a = sPos; bezier.b += dS * (2f / 3f); bezier.c += dS * (1f / 3f);
            float3 dE = ePos - bezier.d; bezier.d = ePos; bezier.c += dE * (2f / 3f); bezier.b += dE * (1f / 3f);

            float2 elev = fe.HasElev ? fe.Elev : float2.zero;

            // Echo guard (same as EmitEdgeCourse): the rebuilt edge is born Applied+Created without
            // CS2M_RemotePlaced, so mark its seg hash first or NetBatchCaptureSystem re-broadcasts it.
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(bezier.a, bezier.d, fe.PrefabName));

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;

            course.m_StartPosition.m_Position = bezier.a;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(bezier));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_Elevation = elev;
            course.m_StartPosition.m_ParentMesh = -1;
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst;
            course.m_StartPosition.m_Entity = startNode;

            course.m_EndPosition.m_Position = bezier.d;
            course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(bezier));
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_Elevation = elev;
            course.m_EndPosition.m_ParentMesh = -1;
            course.m_EndPosition.m_Flags = CoursePosFlags.IsLast;
            course.m_EndPosition.m_Entity = endNode;

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = fe.Prefab,
                m_RandomSeed = fe.Seed,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponentData(def, course);
            EntityManager.AddComponent<Updated>(def);
            _pendingDefinitions.Add(def);

            if (fe.HasOrder)
            {
                _pendingOrderFixes.Add(new PendingOrderFix
                {
                    StartId = fe.StartId,
                    EndId = fe.EndId,
                    OrderStart = fe.OrderStart,
                    OrderEnd = fe.OrderEnd,
                    Age = 0,
                });
            }

            if (fe.HasUpgraded)
            {
                RemoteNetUpgradeQueue.Enqueue(new NetUpgradeCommand
                {
                    StartNodeId = fe.StartId,
                    EndNodeId = fe.EndId,
                    General = fe.UpgG,
                    Left = fe.UpgL,
                    Right = fe.UpgR,
                    StartX = bezier.a.x, StartY = bezier.a.y, StartZ = bezier.a.z,
                    EndX = bezier.d.x, EndY = bezier.d.y, EndZ = bezier.d.z,
                    IsNode = false,
                });
            }

            // The endpoint nodes gained an arm — re-derive their junctions (same as the batch's
            // boundaryTouched step; these are genuine new arms, not the block re-derive the silent order write
            // deliberately avoids).
            MarkUpdated(startNode);
            MarkUpdated(endNode);

            if (expired)
            {
                CS2M.Log.Info($"[Batch] FOLD-TRUST-EXPIRED startId={fe.StartId} endId={fe.EndId} " +
                              $"gapS={gapS:F1} gapE={gapE:F1} (never converged in {FoldParkTtl}f — pinned to live coords)");
            }
            else
            {
                CS2M.Log.Info($"[Batch] FOLD-PARK-RESOLVED startId={fe.StartId} endId={fe.EndId} " +
                              $"gapS={gapS:F1} gapE={gapE:F1} (NodePosUpdate brought nodes close — emitted)");
            }
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

            CS2M.Log.Info(
                $"[Batch] NODE-NEW id={cmd.NodeIds[i]} pos=({cmd.NodePosX[i]:F1},{cmd.NodePosZ[i]:F1}) entity={node.Index}");

            return node;
        }

        /// <summary>Emit ONE vanilla net DEFINITION for this edge — the LEGACY visual-correct path, an exact
        /// copy of <see cref="NetPlaceApplySystem"/>.EmitCourse (no pullback: node == curve endpoint). A
        /// <c>CreationDefinition(Permanent)</c> + <c>NetCourse</c> whose endpoints reference the already
        /// created/resolved node entities by <c>m_Entity</c>; GenerateNodes@Mod1/GenerateEdges@Mod2 then build
        /// the REAL edge THIS frame (curve terrain-fit, composition, geometry, lanes, zone blocks, mesh) — the
        /// derivation the direct-archetype edge never got. Returns true when a definition was emitted.</summary>
        private bool EmitEdgeCourse(NetBatchCommand cmd, int i, Bezier4x3 bezier, Entity startNode, Entity endNode,
            Dictionary<ulong, float3> wirePos)
        {
            // Prefab is pre-validated by ValidateAllPrefabs (incl. m_EdgeArchetype.Valid — kept as the
            // "is this a real net?" proxy even though the definition path no longer uses the archetype).
            if (!ResolveNetPrefab(cmd.EdgePrefabTypes[i], cmd.EdgePrefabNames[i], out Entity netPrefab, out NetData netData))
            {
                return false;
            }

            // FIX C — FOLD CEILING. When either endpoint's captured bezier point sits more than
            // FoldParkCeiling from the WIRE position of the node id it names, do NOT guess this frame (the old
            // FOLD-TRUST bent the edge tens of metres onto the stale wire coord — the 32 m field bug). PARK
            // the edge; the retry drain at the top of OnUpdate emits it once a NodePosUpdate has brought the
            // node close, or as FOLD-TRUST-EXPIRED after FoldParkTtl frames. The in-frame (tolerance, 8 m]
            // TRUST/SPLIT logic below is unchanged.
            float foldGap = MaxFoldGap(cmd, i, bezier, wirePos);
            if (foldGap > FoldParkCeiling)
            {
                EnqueueFoldPark(cmd, i, bezier, netPrefab);
                CS2M.Log.Info($"[Batch] FOLD-PARK edge {i} gap={foldGap:F1}m name={cmd.EdgePrefabNames[i]} " +
                              $"(>{FoldParkCeiling}m; await NodePosUpdate, retry {FoldParkTtl}f)");
                return false;
            }

            // FOLD GUARD — the edge's bezier endpoint (== node position on the builder, invariant I4) can
            // disagree with the wire position of the id it names, and that gap has TWO opposite root causes that
            // demand OPPOSITE cures (see ReanchorFoldedEndpoint for the full case split and the 00:41 fold-real
            // vs 01:13 stale-curve evidence): either the builder folded the id onto a DIFFERENT physical node
            // mid-draw (attach the arm to that distinct node — else it fuses two junctions), or the captured
            // curve is simply STALE (the id is correct — trust it and pin the emitted endpoint onto the node's
            // wire position, letting GenerateEdge re-pin the curve). Passed by ref so the stale-curve branch can
            // fix this course's chord in place before the NetCourse below is built.
            startNode = ReanchorFoldedEndpoint(cmd.EdgeStartNodeIds[i], startNode, ref bezier, true, wirePos);
            endNode = ReanchorFoldedEndpoint(cmd.EdgeEndNodeIds[i], endNode, ref bezier, false, wirePos);

            // FOLD-LIVE-MISMATCH — ReanchorFoldedEndpoint's LIVE cross-check (see LiveFoldMismatch) caught a
            // resolved node whose actual position disagrees with the wire id it's about to be welded to
            // (Entity.Null sentinel; never a legitimate return value otherwise). Same treatment as the
            // pre-emit FOLD-PARK ceiling above: park the whole edge and let the retry drain re-resolve once
            // a NodePosUpdate settles things, instead of soldering onto the wrong node.
            if (startNode == Entity.Null || endNode == Entity.Null)
            {
                EnqueueFoldPark(cmd, i, bezier, netPrefab);
                CS2M.Log.Info($"[Batch] FOLD-PARK edge {i} live-mismatch name={cmd.EdgePrefabNames[i]} " +
                              $"(await NodePosUpdate, retry {FoldParkTtl}f)");
                return false;
            }

            if (startNode == endNode)
            {
                // Re-anchoring must never collapse an edge to a self-loop (defense in depth).
                CS2M.Log.Info($"[Batch] SKIP edge {i} degenerate after fold re-anchor (start==end entity={startNode.Index})");
                return false;
            }

            // Idempotency after re-anchor: a re-anchored endpoint has no wire id, so the id-keyed dup check the
            // caller ran can't see the resulting edge. Skip if this exact node pair is already connected (a
            // re-delivered fold edge — FindReanchorNode reused the prior node, so this pair now matches).
            if (EdgeAlreadyBuilt(startNode, endNode))
            {
                CS2M.Log.Info($"[Batch] SKIP dup edge {i} (already live between resolved node pair)");
                return false;
            }

            // ECHO GUARD (same as NetPlaceApplySystem.EmitCourse line ~282): the edge GenerateEdges produces
            // from this definition is born Applied+Created and WITHOUT CS2M_RemotePlaced (we never hold that
            // entity), so NetBatchCaptureSystem would otherwise re-capture and re-broadcast it. Marking the
            // seg hash first makes the capture skip it (reason=remoteEcho). prefabName is cmd.EdgePrefabNames[i]
            // — the same prefab.name the capture hashes for the produced edge.
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(bezier.a, bezier.d, cmd.EdgePrefabNames[i]));

            // The edge's single Game.Net.Elevation float2 maps to BOTH per-endpoint course elevations —
            // exactly how NetDetectorSystem shipped it into NetPlaceCommand (el → startElev AND endElev).
            float2 elev = cmd.EdgeHasElevation[i]
                ? new float2(cmd.EdgeElevX[i], cmd.EdgeElevY[i])
                : float2.zero;

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;

            course.m_StartPosition.m_Position = bezier.a;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(bezier));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_Elevation = elev;
            course.m_StartPosition.m_ParentMesh = -1;
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst;
            course.m_StartPosition.m_Entity = startNode;

            course.m_EndPosition.m_Position = bezier.d;
            course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(bezier));
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_Elevation = elev;
            course.m_EndPosition.m_ParentMesh = -1;
            course.m_EndPosition.m_Flags = CoursePosFlags.IsLast;
            course.m_EndPosition.m_Entity = endNode;

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = netPrefab,
                m_RandomSeed = cmd.EdgeSeeds[i], // same seed both PCs → deterministic pylons/catenary/details
                m_Flags = CreationFlags.Permanent, // no Temp: GenerateEdge builds the REAL segment this frame
            });
            EntityManager.AddComponentData(def, course);
            EntityManager.AddComponent<Updated>(def); // required by GenerateEdgesSystem's definition query
            _pendingDefinitions.Add(def);

            // BuildOrder is NOT set here — GenerateEdges hasn't created the edge yet this frame (this is
            // only a definition), so there is nothing to stamp it on. The vanilla pipeline assigns it from
            // its OWN per-process counter (decomp GenerateEdgesSystem.cs:2093) the instant it materializes
            // the edge, and that counter diverges builder-vs-receiver — the confirmed root cause of the
            // cross-machine zone-visibility split (Zones.BuildOrder derives from it; CellOverlapJobs.
            // CheckPriority uses Zones.BuildOrder to break cell-overlap ties). Queue the WIRE-authoritative
            // order for the post-materialization corrector at the top of NEXT frame's OnUpdate instead —
            // by then the edge exists and FindEdgeById can resolve it by node-pair identity.
            if (cmd.EdgeBuildOrderStart != null && cmd.EdgeBuildOrderEnd != null
                && i < cmd.EdgeBuildOrderStart.Length && i < cmd.EdgeBuildOrderEnd.Length
                // 0/0 is the CAPTURE's own fallback for a builder edge that had no BuildOrder component —
                // stamping it over the receiver's real local order would be strictly worse than skipping.
                && (cmd.EdgeBuildOrderStart[i] != 0 || cmd.EdgeBuildOrderEnd[i] != 0))
            {
                _pendingOrderFixes.Add(new PendingOrderFix
                {
                    StartId = cmd.EdgeStartNodeIds[i],
                    EndId = cmd.EdgeEndNodeIds[i],
                    OrderStart = cmd.EdgeBuildOrderStart[i],
                    OrderEnd = cmd.EdgeBuildOrderEnd[i],
                    Age = 0,
                });
            }

            // Upgraded (sidewalks/trees/sound barriers/quays/lighting): applied BY IDENTITY via the existing
            // net-edit pipeline instead of the CreationDefinition. NetEditApplySystem drains
            // RemoteNetUpgradeQueue in FOLLOWING frames (once GenerateEdges has built this edge and both
            // endpoint node ids resolve) — zero new apply code, and it is the path already proven for
            // remote upgrades. The definition deliberately does NOT carry Upgraded (that interaction with the
            // batch was never validated).
            if (cmd.EdgeHasUpgraded[i])
            {
                float3 sPos = EntityManager.GetComponentData<Node>(startNode).m_Position;
                float3 ePos = EntityManager.GetComponentData<Node>(endNode).m_Position;
                RemoteNetUpgradeQueue.Enqueue(new NetUpgradeCommand
                {
                    StartNodeId = cmd.EdgeStartNodeIds[i],
                    EndNodeId = cmd.EdgeEndNodeIds[i],
                    General = cmd.EdgeUpgradedG[i],
                    Left = cmd.EdgeUpgradedL[i],
                    Right = cmd.EdgeUpgradedR[i],
                    // Node positions: the position fallback NetEditApplySystem.FindEdge uses when identity
                    // can't resolve (legacy/save content). Identity is the primary path here.
                    StartX = sPos.x, StartY = sPos.y, StartZ = sPos.z,
                    EndX = ePos.x, EndY = ePos.y, EndZ = ePos.z,
                    IsNode = false,
                });
            }

            CS2M.Log.Info(
                $"[Batch] EMIT-DEF edge {i} name={cmd.EdgePrefabNames[i]} len={course.m_Length:F1} " +
                $"startNode={startNode.Index} endNode={endNode.Index} upg={cmd.EdgeHasUpgraded[i]}");
            return true;
        }

        /// <summary>FOLD GUARD worker. When the wire position of <paramref name="endpointId"/> disagrees with the
        /// edge's endpoint (<c>bezier.a</c> if <paramref name="isStart"/>, else <c>bezier.d</c>) by more than
        /// <see cref="EndpointFoldTolerance"/>, ONE of two very different things happened on the builder — and
        /// they need OPPOSITE cures (proven live: 09/07 00:41 fold-real vs 01:13 stale-curve):
        ///
        ///   FOLD-REAL — the builder handed this id to a DIFFERENT physical node mid-draw (edge split / node
        ///   re-use) and the edge geometrically ends at that OTHER node. <paramref name="resolved"/> is the WRONG
        ///   node to attach this arm to (attaching FUSES two junctions the builder keeps apart). Signature: a REAL
        ///   node actually sits at the bezier endpoint. Cure: attach the arm to THAT node.
        ///
        ///   STALE-CURVE — the id is CORRECT but the captured curve is OLD: the wire snapshot caught the bezier
        ///   before the builder's NodeAlign settled the junction, so the endpoint still points at where the curve
        ///   used to reach (01:13: id ...371 gap=10.6m, wire=(462.6,-227.0), bezierEnd=(453.6,-232.6); the
        ///   builder had ONE degree-3 node at the wire pos, the diagonal joined it cleanly — minting at bezierEnd
        ///   created a phantom degree-1 node and DIVERGED). Signature: NO node sits at the bezier endpoint. Cure:
        ///   TRUST the id — return <paramref name="resolved"/> and pin the emitted bezier endpoint onto the
        ///   node's wire position (2/3–1/3 control-point falloff, same rule as <see cref="NetGraphSafety"/>) so
        ///   the NetCourse/EdgeGeometry is not born with an inconsistent chord before GenerateEdge pins the curve
        ///   to the node itself (decomp GenerateEdgesSystem.cs:1319/1347).
        ///
        /// Disambiguated by looking for a real node within <see cref="FoldNodeMatchRadius"/> of the bezier
        /// endpoint: (a) another node of THIS batch (its wire position, resolved via the id map), (b) a live local
        /// node already carrying a CS2M_NodeSyncId, (c) a fresh re-anchor node minted by a prior delivery. Found →
        /// FOLD-REAL, attach to it. None → STALE-CURVE, trust + pin. Returns <paramref name="resolved"/> unchanged
        /// in the normal (consistent-wire) case. Horizontal (xz) distance only — the y gap is terrain height.</summary>
        private Entity ReanchorFoldedEndpoint(ulong endpointId, Entity resolved, ref Bezier4x3 bezier, bool isStart,
            Dictionary<ulong, float3> wirePos)
        {
            if (!wirePos.TryGetValue(endpointId, out float3 wp))
            {
                return resolved; // no wire coord for this id → nothing to cross-check against
            }

            float3 bezierEnd = isStart ? bezier.a : bezier.d;
            float gap = math.distance(new float2(wp.x, wp.z), new float2(bezierEnd.x, bezierEnd.z));
            if (gap <= EndpointFoldTolerance)
            {
                // consistent wire — the arm truly ends at this node, UNLESS the LIVE cross-check below
                // says resolved is actually the wrong physical node (a bad FindNodeWide match can still be
                // wire-self-consistent, since bezierEnd and wp both come from the SAME command).
                return LiveFoldMismatch(endpointId, resolved, wp) ? Entity.Null : resolved;
            }

            // (a) another node of THIS batch sitting at the endpoint: scan the wire positions (NodeIds +
            // BoundaryNodeIds) and resolve the match through the id map. The wire coord is the builder's
            // authoritative settled position, so it names the split node even if the entity hasn't been touched.
            Entity attach = Entity.Null;
            float bestSq = FoldNodeMatchRadius * FoldNodeMatchRadius;
            foreach (KeyValuePair<ulong, float3> kv in wirePos)
            {
                if (kv.Key == endpointId)
                {
                    continue;
                }

                float d = math.distancesq(new float2(kv.Value.x, kv.Value.z), new float2(bezierEnd.x, bezierEnd.z));
                if (d < bestSq
                    && CS2M_NodeSyncIds.TryResolve(EntityManager, kv.Key, out Entity cand)
                    && cand != Entity.Null && cand != resolved)
                {
                    bestSq = d;
                    attach = cand;
                }
            }

            // (b) a live local node already carrying a CS2M_NodeSyncId (an existing junction the split lands on).
            if (attach == Entity.Null
                && CS2M_NodeSyncIds.TryFindNearbyRegistered(EntityManager, bezierEnd, FoldNodeMatchRadius, resolved, out Entity reg))
            {
                attach = reg;
            }

            // (c) a bare fresh node a PRIOR delivery of this same fold edge already minted (re-delivery-safe;
            // combined with the EdgeAlreadyBuilt skip in EmitEdgeCourse).
            if (attach == Entity.Null && FindReanchorNode(bezierEnd, resolved, out Entity reuse))
            {
                attach = reuse;
            }

            if (attach != Entity.Null)
            {
                // FOLD-REAL: the builder really has a distinct node here. Attach the arm to it instead of the
                // stale neighbour; GenerateEdge pins the curve to that node this frame.
                CS2M.Log.Info($"[Batch] FOLD-SPLIT id={endpointId} gap={gap:F1}m " +
                              $"wire=({wp.x:F1},{wp.z:F1}) bezierEnd=({bezierEnd.x:F1},{bezierEnd.z:F1}) " +
                              $"stale={resolved.Index}->node={attach.Index} " +
                              "(id folded on builder — arm re-anchored to the distinct node, not fused onto the neighbour)");
                return attach;
            }

            // STALE-CURVE: no real node at the bezier endpoint → the id is right, the captured curve is old.
            // Do NOT mint (that is exactly what created the 01:13 phantom). Trust the identity and pin the
            // emitted endpoint onto the node's wire position so the course/geometry meets the node from frame 0;
            // the engine re-pins the curve to the node regardless — UNLESS the LIVE cross-check below says
            // resolved isn't actually anywhere near where the wire says this id should be, which means
            // FindNodeWide/Strict handed us the wrong node in the first place.
            if (LiveFoldMismatch(endpointId, resolved, wp))
            {
                return Entity.Null;
            }

            float3 delta = wp - bezierEnd;
            if (isStart)
            {
                bezier.a = wp;
                bezier.b += delta * (2f / 3f);
                bezier.c += delta * (1f / 3f);
            }
            else
            {
                bezier.d = wp;
                bezier.c += delta * (2f / 3f);
                bezier.b += delta * (1f / 3f);
            }

            CS2M.Log.Info($"[Batch] FOLD-TRUST id={endpointId} gap={gap:F1}m " +
                          $"wire=({wp.x:F1},{wp.z:F1}) bezierEnd=({bezierEnd.x:F1},{bezierEnd.z:F1}) " +
                          "(stale wire curve — trusting node identity; engine will pin curve)");
            return resolved;
        }

        /// <summary>Cross-check <paramref name="resolved"/>'s LIVE position against the WIRE position of
        /// <paramref name="endpointId"/> — the gap <see cref="ReanchorFoldedEndpoint"/>'s tolerance/ceiling
        /// checks never look at: those only ever compare <c>wirePos</c> to the edge's OWN bezier endpoint,
        /// and both of those values come from the SAME wire command, so a WRONG <see cref="FindNodeWide"/>
        /// match (candidate physically sitting somewhere else entirely) is wire-self-consistent and sails
        /// straight through. <c>SnapBoundaryNode</c>/<c>CreateNode</c> already pin a CORRECTLY resolved
        /// node's live position onto this exact wire coord earlier in the SAME atomic commit, so in every
        /// legitimate delivery (fast-path AND stale-curve) this gap is ~0 m; it only opens up when
        /// <paramref name="resolved"/> is the wrong physical node. Reuses <see cref="FoldParkCeiling"/> — a
        /// TETO on top of the existing fold logic, not a replacement for it. Not applied to the FOLD-SPLIT
        /// (<c>attach</c>) branch: that candidate is already independently verified against the bezier
        /// endpoint itself, which can legitimately sit up to <see cref="FoldParkCeiling"/> from the stale
        /// wire coord — this same check there would misfire on genuine splits.</summary>
        private bool LiveFoldMismatch(ulong endpointId, Entity resolved, float3 wp)
        {
            float3 livePos = EntityManager.GetComponentData<Node>(resolved).m_Position;
            float liveGap = math.distance(new float2(wp.x, wp.z), new float2(livePos.x, livePos.z));
            if (liveGap <= FoldParkCeiling)
            {
                return false;
            }

            CS2M.Log.Info($"[Batch] FOLD-LIVE-MISMATCH id={endpointId} wire=({wp.x:F1},{wp.z:F1}) " +
                          $"live=({livePos.x:F1},{livePos.z:F1}) d={liveGap:F1}m " +
                          "(resolved node's live position disagrees with the wire id — treating as a miss, not welding)");
            return true;
        }

        /// <summary>Find a bare re-anchor node (CS2M_RemotePlaced, no CS2M_NodeSyncId, not <paramref name="exclude"/>)
        /// sitting within 0.5 m of <paramref name="pos"/> — a fold-split node a prior delivery of the same edge
        /// already minted. Exact-scale (0.5 m) so it never adopts an unrelated junction, only its own twin.</summary>
        private bool FindReanchorNode(float3 pos, Entity exclude, out Entity node)
        {
            node = Entity.Null;
            float best = 0.25f; // 0.5 m squared
            Unity.Collections.NativeArray<Entity> arr = _liveNodes.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity n in arr)
                {
                    if (n == exclude
                        || !EntityManager.HasComponent<CS2M_RemotePlaced>(n)
                        || EntityManager.HasComponent<CS2M_NodeSyncId>(n))
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

        /// <summary>True when a LIVE edge already directly connects <paramref name="a"/> and <paramref name="b"/>
        /// (either orientation). Entity-addressed twin of <see cref="FindEdgeById"/> — the fold path re-anchors
        /// an endpoint onto a node with NO wire id, so FindEdgeById (id-keyed) can't see the resulting edge on a
        /// re-delivery; this catches the duplicate before a second edge is soldered onto the same node pair.</summary>
        private bool EdgeAlreadyBuilt(Entity a, Entity b)
        {
            if (a == Entity.Null || b == Entity.Null || a == b || !EntityManager.HasBuffer<ConnectedEdge>(a))
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
                    return true;
                }
            }

            return false;
        }

        /// <summary>Delete the edge the split removed, addressed by node-pair identity FIRST, falling back to
        /// a strict position match when identity can't resolve (save-loaded edges: <see cref="CS2M_NodeSyncId"/>
        /// is never persisted to the save, so a node loaded from a previous session has id=0 on BOTH ends —
        /// FindEdgeById always fails for it, and the batch used to just SKIP, leaving the edge stuck on the
        /// side that never received the bulldoze). Tags <c>CS2M_RemotePlaced</c> BEFORE <c>Deleted</c> (delete
        /// echo guard) and cascades the junction rebuild.</summary>
        private bool ApplyDelete(NetBatchCommand cmd, int i)
        {
            ulong aId = cmd.DelStartNodeIds[i];
            ulong bId = cmd.DelEndNodeIds[i];
            if (!FindEdgeById(aId, bId, out Entity edge)
                && !FindEdgeByPosition(cmd, i, out edge))
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

        /// <summary>Junction-scale fallback for when <see cref="FindNodeStrict"/>'s exact ≤0.5 m match
        /// misses: an existing junction RE-CENTRES as a new arm attaches to it, so the builder's settled
        /// <c>BoundaryPos</c> can land several metres from the client's still-untouched twin BEFORE the
        /// client has applied anything at all — the same physics the proven legacy path already accounts
        /// for (<see cref="NetPlaceApplySystem"/>.FindJunctionNode: "a busy junction re-centres MORE than
        /// 3.5 m as roads join over time"). Reuses that path's exact two radii: 3.5 m for ANY live node,
        /// 8 m for the wide tier, which PREFERS a node that is ALREADY a junction (2+ live connected
        /// edges) so a distant dead-end doesn't fuse onto an unrelated intersection, but falls back to a
        /// lone non-junction node when no junction candidate exists (see v73 note below). Unlike
        /// <see cref="FindNodeStrict"/>, a candidate here MAY already carry a <see cref="CS2M_NodeSyncId"/>
        /// — the client's twin can have adopted a different id first (e.g. an earlier batch that touched
        /// this same junction) — the caller REMAPS this id onto it instead of re-deriving identity from
        /// scratch.
        ///
        /// Ambiguity-safe: each radius tier is accepted ONLY when it has EXACTLY ONE candidate within it
        /// (mirrors <see cref="FindEdgeByPosition"/>'s matches==1 rule) — two candidates in range means
        /// "can't tell which junction this is" and the whole lookup refuses rather than guess. The tight
        /// tier is tried first; the wide tier only fires when the tight tier found NOTHING (an ambiguous
        /// tight tier does not fall through — it is already an unresolvable read). Within the wide tier,
        /// a single junction candidate always wins over any non-junction candidates also in range; the
        /// lone-non-junction tier only fires when the wide radius holds NO junction at all.
        ///
        /// v73: a save node never touched by either side still has degree&lt;2 when the first remote batch
        /// that would give it a second arm arrives — the arm that WOULD make it a junction is IN that same
        /// batch, so requiring an existing junction here was order-dependent and dropped the whole batch
        /// (boundary-miss DROP proven in a real test on 09/07/2026; see CS2M_NodeSyncId.cs:10-17). The wide
        /// tier now also accepts a live non-junction node, but only as the single candidate in its radius
        /// and only once no junction claims that radius, preserving the old "prefer the junction" rule.</summary>
        private bool FindNodeWide(float3 pos, HashSet<Entity> claimed, out Entity node)
        {
            node = Entity.Null;
            const float tightRadiusSq = 12.25f; // 3.5 m — any live node
            const float wideRadiusSq = 64f;     // 8 m — junction preferred, lone non-junction as fallback (v73)

            Entity tightBest = Entity.Null;
            float tightBestSq = tightRadiusSq;
            int tightMatches = 0;
            Entity wideJunctionBest = Entity.Null;
            float wideJunctionBestSq = wideRadiusSq;
            int wideJunctionMatches = 0;
            Entity wideAnyBest = Entity.Null; // v73: lone non-junction candidate in the wide radius
            float wideAnyBestSq = wideRadiusSq;
            int wideAnyMatches = 0;

            Unity.Collections.NativeArray<Entity> arr = _liveNodes.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity n in arr)
                {
                    if (claimed.Contains(n))
                    {
                        continue;
                    }

                    float d = math.distancesq(EntityManager.GetComponentData<Node>(n).m_Position, pos);
                    if (d < tightRadiusSq)
                    {
                        tightMatches++;
                        if (d < tightBestSq)
                        {
                            tightBestSq = d;
                            tightBest = n;
                        }
                    }

                    if (d < wideRadiusSq)
                    {
                        // Map-edge boundary node — never a valid junction/dead-end to adopt as a player
                        // construction endpoint (Owner sub-nets already die via the _liveNodes query; this
                        // is the other non-player case: the world's own outside connections). Reject before
                        // it can win either wide-tier bucket.
                        if (EntityManager.HasComponent<Game.Net.OutsideConnection>(n))
                        {
                            continue;
                        }

                        if (IsJunctionNodeWide(n))
                        {
                            wideJunctionMatches++;
                            if (d < wideJunctionBestSq)
                            {
                                wideJunctionBestSq = d;
                                wideJunctionBest = n;
                            }
                        }
                        else
                        {
                            wideAnyMatches++;
                            if (d < wideAnyBestSq)
                            {
                                wideAnyBestSq = d;
                                wideAnyBest = n;
                            }
                        }
                    }
                }
            }
            finally
            {
                arr.Dispose();
            }

            if (tightMatches == 1)
            {
                node = tightBest;
                return true;
            }

            if (tightMatches == 0 && wideJunctionMatches == 1)
            {
                node = wideJunctionBest;
                return true;
            }

            // v73: no junction in range — fall back to a lone non-junction node, still ambiguity-safe
            // (matches==1 rule) and still behind the tight tier and the junction tier above.
            if (tightMatches == 0 && wideJunctionMatches == 0 && wideAnyMatches == 1)
            {
                node = wideAnyBest;
                return true;
            }

            return false;
        }

        /// <summary>True when a live node has 2+ live connected edges (a real junction, not a dead-end).
        /// Local copy of the same check <see cref="NetPlaceApplySystem"/> uses for its own 8 m fallback
        /// tier, so <see cref="FindNodeWide"/>'s wide radius never fuses a road that merely ENDS near an
        /// intersection.</summary>
        private bool IsJunctionNodeWide(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                return false;
            }

            DynamicBuffer<ConnectedEdge> ce = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            int live = 0;
            for (int i = 0; i < ce.Length; i++)
            {
                Entity e = ce[i].m_Edge;
                if (EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e))
                {
                    live++;
                    if (live >= 2)
                    {
                        return true;
                    }
                }
            }

            return false;
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

        // How close BOTH endpoints of a live edge must sit to the wire's (DelStart, DelEnd) — in either
        // orientation — to be treated as the edge a save-loaded delete named. Save geometry is byte-identical
        // on both machines at join (same file), so this is exact resolution for that case, not proximity
        // guessing; loose enough to absorb the sub-metre float round-trip NodePinSystem/BOUND-SNAP already
        // tolerate elsewhere in this file, tight enough that two distinct junctions a street apart never
        // collide.
        private const float DeletePosTolerance = 3f;

        /// <summary>Fallback for a save-loaded delete: <see cref="CS2M_NodeSyncId"/> is never persisted to the
        /// save, so BOTH ends of an edge that came from a previous session carry id=0 on every machine and
        /// <see cref="FindEdgeById"/> can never match it. Scans every LIVE edge (no Temp/Deleted) and matches
        /// on the two endpoint NODE positions against the wire's (DelStart, DelEnd), tried in both
        /// orientations, within <see cref="DeletePosTolerance"/>. Exactly one match → delete it. Zero or more
        /// than one match within tolerance → refuse (logs <c>noMatch-pos</c>) rather than risk deleting the
        /// wrong edge.</summary>
        private bool FindEdgeByPosition(NetBatchCommand cmd, int i, out Entity edge)
        {
            edge = Entity.Null;

            if (cmd.DelStartX == null || cmd.DelStartZ == null || cmd.DelEndX == null || cmd.DelEndZ == null
                || i >= cmd.DelStartX.Length || i >= cmd.DelStartZ.Length
                || i >= cmd.DelEndX.Length || i >= cmd.DelEndZ.Length)
            {
                return false;
            }

            float delStartY = cmd.DelStartY != null && i < cmd.DelStartY.Length ? cmd.DelStartY[i] : 0f;
            float delEndY = cmd.DelEndY != null && i < cmd.DelEndY.Length ? cmd.DelEndY[i] : 0f;
            var delStart = new float3(cmd.DelStartX[i], delStartY, cmd.DelStartZ[i]);
            var delEnd = new float3(cmd.DelEndX[i], delEndY, cmd.DelEndZ[i]);

            float tolSq = DeletePosTolerance * DeletePosTolerance;
            int matches = 0;
            Entity found = Entity.Null;

            Unity.Collections.NativeArray<Entity> arr = _liveEdges.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity e in arr)
                {
                    Edge ed = EntityManager.GetComponentData<Edge>(e);
                    if (!EntityManager.Exists(ed.m_Start) || !EntityManager.Exists(ed.m_End)
                        || !EntityManager.HasComponent<Node>(ed.m_Start) || !EntityManager.HasComponent<Node>(ed.m_End))
                    {
                        continue;
                    }

                    float3 sPos = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
                    float3 ePos = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;

                    bool straight = math.distancesq(sPos, delStart) <= tolSq && math.distancesq(ePos, delEnd) <= tolSq;
                    bool flipped = math.distancesq(sPos, delEnd) <= tolSq && math.distancesq(ePos, delStart) <= tolSq;
                    if (straight || flipped)
                    {
                        matches++;
                        found = e;
                    }
                }
            }
            finally
            {
                arr.Dispose();
            }

            if (matches == 1)
            {
                edge = found;
                CS2M.Log.Info($"[Batch] delete FOUND-BY-POS edge={found.Index} " +
                              $"start=({delStart.x:F1},{delStart.z:F1}) end=({delEnd.x:F1},{delEnd.z:F1})");
                return true;
            }

            CS2M.Log.Info($"[Batch] delete noMatch-pos matches={matches} " +
                          $"start=({delStart.x:F1},{delStart.z:F1}) end=({delEnd.x:F1},{delEnd.z:F1}) " +
                          "(0 or >1 candidates within tolerance — refusing to guess)");
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
                // DEFER the orphan delete one frame (crash #4): AddComponent<Deleted> on a node in the
                // SAME frame its junction is being restructured recycles the node while BlockSystem@Mod4
                // may still walk a sibling edge's ConnectedEdge that references it. Drained at the top of
                // the next OnUpdate, which re-checks orphan status before committing the delete.
                if (!_pendingOrphanNodes.Contains(node))
                {
                    _pendingOrphanNodes.Add(node);
                }
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
