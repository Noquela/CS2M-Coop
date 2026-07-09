using System.Collections.Generic;
using Colossal.Mathematics;
using CS2M.API.Commands;
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
    /// <summary>Global toggle for the AtomicBatch net path. ON by default since v66.5 (env
    /// <c>CS2M_ATOMIC=0</c> to opt out — see <see cref="Enabled"/> for the v66.5 root-cause fix). It was
    /// flipped ON in v66 for zero road drift, then BACK TO OFF in v66.3 after 2-sim field testing showed
    /// it CRASHES THE RECEIVER (host or client — whoever did NOT draw the road) when a batch materializes
    /// a complex junction (a degree-4 node + multiple deletes in one frame): the game's own Burst net
    /// pipeline (GenerateEdges/NodeAlign) faults on the topology the batch apply leaves mid-frame. It is a
    /// native crash — our per-batch try/catch can't catch it. v66.5 root-caused the crash to the zone
    /// reconcile's block create/delete (NativeQuadTree), not the batch apply itself, fixed that at the
    /// root, and re-enabled the path. The legacy NetDetector→NetPlaceApply reconstruct path remains
    /// availability-proven (weeks of play, no crash) and is what runs when this is disabled; the v66 zone
    /// SET-reconcile (host owns the grid) converges zones on top of it regardless of small junction drift.
    /// When ON, <see cref="NetBatchCaptureSystem"/> is the only net sender and the legacy
    /// NetDetectorSystem + NetEditDetectorSystem early-return.</summary>
    public static class AtomicBatch
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    // v66.5: back ON by default. The receiver crash blamed on the batch was actually the
                    // zone reconcile's block create/delete (NativeQuadTree), now fixed at the root; the
                    // batch path gives zero road drift, which is what makes zone blocks derive identically.
                    _state = System.Environment.GetEnvironmentVariable("CS2M_ATOMIC") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     AtomicBatch CAPTURE (builder side, gated <c>CS2M_ATOMIC=1</c>). Runs in the same slot as
    ///     <see cref="NetDetectorSystem"/> (<c>UpdateBefore(ModificationEnd)</c>) where the just-committed
    ///     geometry has already SETTLED this frame (ToolOutput → GenerateNodes/Edges → NodeAlign → Geometry
    ///     all ran before ModificationEnd — investigation I2). It bundles ONE tool apply into a single
    ///     <see cref="NetBatchCommand"/>: every new node, every new edge, the edges the split removed, and the
    ///     ids of pre-existing boundary nodes that gained an arm — so the receiver rebuilds the whole result
    ///     atomically and the derived pipeline converges from an identical, complete input set.
    ///
    ///     Partition (I2): NEW = <c>Applied &amp; Created &amp; !Deleted &amp; !Temp &amp; !Owner</c>; REMOVED
    ///     edges = <c>Deleted &amp; !Applied &amp; !Temp &amp; !Owner</c>; boundary = a new edge's endpoint
    ///     node that is NOT itself a new node. Every query excludes <c>CS2M_RemotePlaced</c>, so the batch
    ///     NEVER re-captures what another player's batch just created (the frame echo guard, by component).
    ///     Node positions are read from the <c>Node</c> COMPONENT (the settled/aligned coord), never the
    ///     bezier ends (I2 gotcha: the align is not written back into the curve).
    /// </summary>
    public partial class NetBatchCaptureSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _newNodes;
        private EntityQuery _newEdges;
        private EntityQuery _removedEdges;

        // ---- A. Continuous settled-position stream (NodePosUpdate) --------------------------------------
        // id -> last position THIS side already SENT for that node (populated after each batch send + after
        // each stream send). Only ids this side AUTHORED live here (new nodes always; boundary nodes only
        // when they are NOT another player's remote-placed node) — the echo guard: the receiver never
        // re-broadcasts a node it merely received. A "star" junction re-seats its nodes AFTER the batch left
        // (NodeAlign re-centres per new arm; successive splits nudge 1-2 m each), so every NodePosScanInterval
        // frames we compare each node's current Node.m_Position to the last-sent value and ship the delta.
        private readonly Dictionary<ulong, float3> _sentNodePos = new Dictionary<ulong, float3>();
        private int _streamFrame;

        // Scan cadence (frames) and the min displacement (m) worth streaming, and the per-command id cap.
        private const int NodePosScanInterval = 30;
        private const float NodePosSendThreshold = 0.5f;
        private const int NodePosBatchCap = 32;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _newNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Applied>(),
                    ComponentType.ReadOnly<Created>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),           // building sub-nets regenerate deterministically
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(), // never re-capture a remote batch's output
                },
            });
            _newEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Applied>(),
                    ComponentType.ReadOnly<Created>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });
            _removedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Applied>(), // an Applied+Deleted piece is a transient, not a removal
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    // CS2M_RemoteDeleted (NOT RemotePlaced): skip only deletes a remote batch just applied.
                    // Excluding RemotePlaced here would swallow a LOCAL bulldoze of a remote-BUILT edge —
                    // the delete would never ship and the original builder would keep a ghost road.
                    ComponentType.ReadOnly<CS2M_RemoteDeleted>(),
                },
            });

            CS2M.Log.Info("[Batch] NetBatchCaptureSystem created");
        }

        protected override void OnUpdate()
        {
            if (!AtomicBatch.Enabled)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                // Teardown-equivalent cleanup (LocalPlayer.cs is intentionally not touched by this fix): a
                // reconnect must not carry over stale ids/positions from the previous session.
                if (_sentNodePos.Count > 0) { _sentNodePos.Clear(); }
                _streamFrame = 0;
                return;
            }

            // A. Every NodePosScanInterval frames, stream any node whose settled position moved since it was
            // last sent. Runs BEFORE the empty-query early-return below, so it fires even on frames with no
            // new tool apply (that is exactly when the LATE re-seating of an already-shipped junction shows up).
            if (++_streamFrame >= NodePosScanInterval)
            {
                _streamFrame = 0;
                StreamNodePosUpdates();
            }

            if (_newNodes.IsEmptyIgnoreFilter && _newEdges.IsEmptyIgnoreFilter && _removedEdges.IsEmptyIgnoreFilter)
            {
                return;
            }

            // Materialize up front — Ensure() below adds CS2M_NodeSyncId (a structural change) which would
            // otherwise invalidate a live query array mid-iteration.
            List<Entity> nodeEnts = ToList(_newNodes);
            List<Entity> edgeEnts = ToList(_newEdges);
            List<Entity> removedEnts = ToList(_removedEdges);

            // entity -> shipped node id (new nodes + boundary nodes all live here).
            var nodeIdOf = new Dictionary<Entity, ulong>();

            // ---- PASS 1: id every new node (arrays are built in pass 3, AFTER the edge echo filter) ----
            foreach (Entity n in nodeEnts)
            {
                ulong id = CS2M_NodeSyncIds.Ensure(EntityManager, n);
                if (id != 0)
                {
                    nodeIdOf[n] = id;
                }
            }

            // ---- PASS 2: NEW EDGES + BOUNDARY -----------------------------------------------------
            var boundaryIds = new List<ulong>();
            var boundaryPosX = new List<float>(); var boundaryPosY = new List<float>(); var boundaryPosZ = new List<float>();
            var boundarySeen = new HashSet<ulong>();
            // Node ids actually referenced by a SHIPPED edge (drives the pass-3 node filter).
            var referencedNodeIds = new HashSet<ulong>();
            int echoSkipped = 0;

            var eStart = new List<ulong>(); var eEnd = new List<ulong>();
            var eAX = new List<float>(); var eAY = new List<float>(); var eAZ = new List<float>();
            var eBX = new List<float>(); var eBY = new List<float>(); var eBZ = new List<float>();
            var eCX = new List<float>(); var eCY = new List<float>(); var eCZ = new List<float>();
            var eDX = new List<float>(); var eDY = new List<float>(); var eDZ = new List<float>();
            var eTypes = new List<string>(); var eNames = new List<string>();
            var eHasUpg = new List<bool>(); var eUpgG = new List<uint>(); var eUpgL = new List<uint>(); var eUpgR = new List<uint>();
            var eHasElev = new List<bool>(); var eElevX = new List<float>(); var eElevY = new List<float>();
            var eSeeds = new List<int>();
            var eBoStart = new List<uint>(); var eBoEnd = new List<uint>();

            foreach (Entity e in edgeEnts)
            {
                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                        out PrefabBase prefab) || prefab == null)
                {
                    continue;
                }

                Bezier4x3 b = EntityManager.GetComponentData<Curve>(e).m_Bezier;

                // A LEGACY net apply (e.g. /resync replaying NetPlaceCommands while ATOMIC is on) creates
                // edges that carry Applied+Created just like a local tool apply — but it Marks its segment
                // in RemoteNetEcho. Respect that mark here (same check as NetDetectorSystem) or the batch
                // would re-broadcast a remote road as if locally built. Checked BEFORE endpoint resolution
                // so a skipped edge cannot pollute the boundary list.
                int segHash = RemoteNetEcho.SegHash(b.a, b.d, prefab.name);
                if (RemoteNetEcho.IsRecent(segHash))
                {
                    echoSkipped++;
                    CS2M.Log.Info($"[Batch] SKIP reason=remoteEcho segHash={segHash} name={prefab.name}");
                    continue;
                }

                Edge ed = EntityManager.GetComponentData<Edge>(e);
                ulong startId = ResolveEndpointId(ed.m_Start, nodeIdOf, boundaryIds, boundarySeen,
                    boundaryPosX, boundaryPosY, boundaryPosZ);
                ulong endId = ResolveEndpointId(ed.m_End, nodeIdOf, boundaryIds, boundarySeen,
                    boundaryPosX, boundaryPosY, boundaryPosZ);
                if (startId == 0 || endId == 0)
                {
                    CS2M.Log.Info($"[Batch] SKIP edge={e.Index} unresolved endpoint (start={startId} end={endId})");
                    continue;
                }

                referencedNodeIds.Add(startId);
                referencedNodeIds.Add(endId);
                eStart.Add(startId); eEnd.Add(endId);
                eAX.Add(b.a.x); eAY.Add(b.a.y); eAZ.Add(b.a.z);
                eBX.Add(b.b.x); eBY.Add(b.b.y); eBZ.Add(b.b.z);
                eCX.Add(b.c.x); eCY.Add(b.c.y); eCZ.Add(b.c.z);
                eDX.Add(b.d.x); eDY.Add(b.d.y); eDZ.Add(b.d.z);
                eTypes.Add(prefab.GetType().Name);
                eNames.Add(prefab.name);

                if (EntityManager.HasComponent<Upgraded>(e))
                {
                    CompositionFlags f = EntityManager.GetComponentData<Upgraded>(e).m_Flags;
                    eHasUpg.Add(true);
                    eUpgG.Add((uint) f.m_General); eUpgL.Add((uint) f.m_Left); eUpgR.Add((uint) f.m_Right);
                }
                else
                {
                    eHasUpg.Add(false); eUpgG.Add(0u); eUpgL.Add(0u); eUpgR.Add(0u);
                }

                if (EntityManager.HasComponent<Game.Net.Elevation>(e))
                {
                    float2 el = EntityManager.GetComponentData<Game.Net.Elevation>(e).m_Elevation;
                    eHasElev.Add(true); eElevX.Add(el.x); eElevY.Add(el.y);
                }
                else
                {
                    eHasElev.Add(false); eElevX.Add(0f); eElevY.Add(0f);
                }

                eSeeds.Add(EntityManager.HasComponent<PseudoRandomSeed>(e)
                    ? EntityManager.GetComponentData<PseudoRandomSeed>(e).m_Seed : 0);

                if (EntityManager.HasComponent<BuildOrder>(e))
                {
                    BuildOrder bo = EntityManager.GetComponentData<BuildOrder>(e);
                    eBoStart.Add(bo.m_Start); eBoEnd.Add(bo.m_End);
                }
                else
                {
                    eBoStart.Add(0u); eBoEnd.Add(0u);
                }
            }

            // ---- PASS 3: NEW NODE arrays. When any edge was echo-skipped this frame, ship ONLY nodes a
            // shipped edge references — the skipped (remote-applied) road's nodes would otherwise go out
            // as orphans. With no echo in the frame, unreferenced nodes (Point-mode placements) still ship,
            // UNLESS one sits on top of a boundary node THIS SAME batch resolved (see IsStrayNearBoundary
            // below) — that is a materialization artifact, not a legitimate standalone placement.
            var nNodeIds = new List<ulong>();
            var nPosX = new List<float>(); var nPosY = new List<float>(); var nPosZ = new List<float>();
            var nRotX = new List<float>(); var nRotY = new List<float>(); var nRotZ = new List<float>(); var nRotW = new List<float>();
            var nTypes = new List<string>(); var nNames = new List<string>();
            var nStandalone = new List<bool>();
            var nHasElev = new List<bool>(); var nElevX = new List<float>(); var nElevY = new List<float>();
            var nSeeds = new List<int>();
            int droppedStray = 0;

            foreach (Entity n in nodeEnts)
            {
                if (!nodeIdOf.TryGetValue(n, out ulong id))
                {
                    continue;
                }

                if (echoSkipped > 0 && !referencedNodeIds.Contains(id))
                {
                    continue;
                }

                Node node = EntityManager.GetComponentData<Node>(n);

                // STRAY-NEAR-BOUNDARY (proven live 06/07, XSESSION-OVERDRAW): a control point given no
                // explicit snap owner (ControlPoint.m_OriginalEntity == Entity.Null, e.g. this batch's
                // builder replayed the tool with snap=0 near pre-existing infra after a session
                // rejoin) makes CreateDefinitionsJob instantiate a genuine free NODE for it — but
                // GenerateNodes/Edges' OWN later, position-based reuse still re-points the new EDGE onto
                // the pre-existing node at that same spot (that pre-existing node is exactly what Pass 2
                // above resolved into CAP-BOUND). Nothing ever deletes the now-redundant free node it
                // leaves behind: it carries Applied+Created (so it looks like a normal new node to the
                // queries above) but no shipped edge ever references it (excluded from
                // referencedNodeIds). Shipping it anyway hands the receiver a disconnected phantom entity
                // NetBatchApplySystem.CreateNode can never prune (raw archetype instantiation — it never
                // runs through vanilla's own dedup, unlike a real tool apply). Measured: HOST kept a
                // degree-0(ish) orphan at the exact coordinate of a CAP-BOUND entry while the CLIENT
                // dutifully manufactured a matching permanent phantom node from the wire payload — a
                // node-count divergence (581 vs 580) with zero edge/zone/area drift otherwise. Fixed at
                // the SOURCE (never shipped) instead of asking the receiver to merge it by proximity —
                // this architecture's identity-over-proximity law applies to the CAPTURE side too: the
                // receiver must never guess, so the builder must never hand it something to guess about.
                if (!referencedNodeIds.Contains(id)
                    && IsStrayNearBoundary(node.m_Position, boundaryPosX, boundaryPosY, boundaryPosZ))
                {
                    droppedStray++;
                    CS2M.Log.Info($"[Batch] DROP-STRAY id={id} pos=({node.m_Position.x:F1},{node.m_Position.z:F1}) " +
                                  "(unreferenced new node coincident with a boundary node this same batch " +
                                  "— materialization artifact, not shipped)");
                    // Also delete the artifact LOCALLY: no edge references it, and keeping it host-only
                    // would leave a permanent nodes-count divergence (the R14 581vs580) that every
                    // statediff run flags forever. Deleted → the game's CleanupSystem destroys it
                    // end-of-frame — both worlds converge instead of just both-not-knowing.
                    if (!EntityManager.HasComponent<Deleted>(n))
                    {
                        EntityManager.AddComponent<Deleted>(n);
                    }

                    continue;
                }

                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(n).m_Prefab,
                        out PrefabBase prefab) || prefab == null)
                {
                    continue;
                }

                nNodeIds.Add(id);
                nPosX.Add(node.m_Position.x); nPosY.Add(node.m_Position.y); nPosZ.Add(node.m_Position.z);
                nRotX.Add(node.m_Rotation.value.x); nRotY.Add(node.m_Rotation.value.y);
                nRotZ.Add(node.m_Rotation.value.z); nRotW.Add(node.m_Rotation.value.w);
                nTypes.Add(prefab.GetType().Name);
                nNames.Add(prefab.name);

                nStandalone.Add(EntityManager.HasComponent<Standalone>(n));

                if (EntityManager.HasComponent<Game.Net.Elevation>(n))
                {
                    float2 el = EntityManager.GetComponentData<Game.Net.Elevation>(n).m_Elevation;
                    nHasElev.Add(true); nElevX.Add(el.x); nElevY.Add(el.y);
                }
                else
                {
                    nHasElev.Add(false); nElevX.Add(0f); nElevY.Add(0f);
                }

                nSeeds.Add(EntityManager.HasComponent<PseudoRandomSeed>(n)
                    ? EntityManager.GetComponentData<PseudoRandomSeed>(n).m_Seed : 0);
            }

            // A shipped edge may reference a NEW node filtered out above only if echo skipping removed its
            // edges — impossible by construction (referenced ids are kept). Boundary ids are unaffected.

            // ---- REMOVED EDGES (split originals / bulldozes) — delete by node-pair identity -------
            var dStart = new List<ulong>(); var dEnd = new List<ulong>();
            var dsX = new List<float>(); var dsY = new List<float>(); var dsZ = new List<float>();
            var deX = new List<float>(); var deY = new List<float>(); var deZ = new List<float>();
            foreach (Entity e in removedEnts)
            {
                Edge ed = EntityManager.GetComponentData<Edge>(e);
                ulong sId = EntityManager.HasComponent<CS2M_NodeSyncId>(ed.m_Start)
                    ? EntityManager.GetComponentData<CS2M_NodeSyncId>(ed.m_Start).m_Id : 0UL;
                ulong eId = EntityManager.HasComponent<CS2M_NodeSyncId>(ed.m_End)
                    ? EntityManager.GetComponentData<CS2M_NodeSyncId>(ed.m_End).m_Id : 0UL;
                float3 sp = EntityManager.HasComponent<Node>(ed.m_Start)
                    ? EntityManager.GetComponentData<Node>(ed.m_Start).m_Position : float3.zero;
                float3 ep = EntityManager.HasComponent<Node>(ed.m_End)
                    ? EntityManager.GetComponentData<Node>(ed.m_End).m_Position : float3.zero;

                // Legacy delete applies (NetEditApplySystem) mark SegHash(s,en,"del") instead of tagging
                // CS2M_RemoteDeleted — honour that mark too, or a legacy-applied delete would be re-broadcast.
                if (RemoteNetEcho.IsRecent(RemoteNetEcho.SegHash(sp, ep, "del")))
                {
                    CS2M.Log.Info("[Batch] SKIP del reason=remoteEcho");
                    continue;
                }

                dStart.Add(sId); dEnd.Add(eId);
                dsX.Add(sp.x); dsY.Add(sp.y); dsZ.Add(sp.z);
                deX.Add(ep.x); deY.Add(ep.y); deZ.Add(ep.z);
            }

            if (nNodeIds.Count == 0 && eStart.Count == 0 && dStart.Count == 0)
            {
                return;
            }

            var cmd = new NetBatchCommand
            {
                NodeIds = nNodeIds.ToArray(),
                NodePosX = nPosX.ToArray(), NodePosY = nPosY.ToArray(), NodePosZ = nPosZ.ToArray(),
                NodeRotX = nRotX.ToArray(), NodeRotY = nRotY.ToArray(), NodeRotZ = nRotZ.ToArray(), NodeRotW = nRotW.ToArray(),
                NodePrefabTypes = nTypes.ToArray(), NodePrefabNames = nNames.ToArray(),
                NodeHasStandalone = nStandalone.ToArray(),
                NodeHasElevation = nHasElev.ToArray(), NodeElevX = nElevX.ToArray(), NodeElevY = nElevY.ToArray(),
                NodeSeeds = nSeeds.ToArray(),

                EdgeStartNodeIds = eStart.ToArray(), EdgeEndNodeIds = eEnd.ToArray(),
                EdgeAX = eAX.ToArray(), EdgeAY = eAY.ToArray(), EdgeAZ = eAZ.ToArray(),
                EdgeBX = eBX.ToArray(), EdgeBY = eBY.ToArray(), EdgeBZ = eBZ.ToArray(),
                EdgeCX = eCX.ToArray(), EdgeCY = eCY.ToArray(), EdgeCZ = eCZ.ToArray(),
                EdgeDX = eDX.ToArray(), EdgeDY = eDY.ToArray(), EdgeDZ = eDZ.ToArray(),
                EdgePrefabTypes = eTypes.ToArray(), EdgePrefabNames = eNames.ToArray(),
                EdgeHasUpgraded = eHasUpg.ToArray(),
                EdgeUpgradedG = eUpgG.ToArray(), EdgeUpgradedL = eUpgL.ToArray(), EdgeUpgradedR = eUpgR.ToArray(),
                EdgeHasElevation = eHasElev.ToArray(), EdgeElevX = eElevX.ToArray(), EdgeElevY = eElevY.ToArray(),
                EdgeSeeds = eSeeds.ToArray(),
                EdgeBuildOrderStart = eBoStart.ToArray(), EdgeBuildOrderEnd = eBoEnd.ToArray(),

                DelStartNodeIds = dStart.ToArray(), DelEndNodeIds = dEnd.ToArray(),
                DelStartX = dsX.ToArray(), DelStartY = dsY.ToArray(), DelStartZ = dsZ.ToArray(),
                DelEndX = deX.ToArray(), DelEndY = deY.ToArray(), DelEndZ = deZ.ToArray(),

                BoundaryNodeIds = boundaryIds.ToArray(),
                BoundaryPosX = boundaryPosX.ToArray(),
                BoundaryPosY = boundaryPosY.ToArray(),
                BoundaryPosZ = boundaryPosZ.ToArray(),
            };

            Command.SendToAll?.Invoke(cmd);

            // A. Remember what we JUST shipped for each node so the continuous stream (StreamNodePosUpdates)
            // only sends a NodePosUpdate once the builder's later NodeAlign re-seats it past the threshold.
            // New nodes: always this side's authorship. Boundary nodes: record ONLY when they are not another
            // player's remote-placed node (that node's author streams it — never double-author across the echo
            // guard); a save-loaded shared node has no CS2M_RemotePlaced and is co-authored, which is fine —
            // whoever re-seats it past the threshold ships, and a converged pair sends nothing.
            for (int i = 0; i < nNodeIds.Count; i++)
            {
                _sentNodePos[nNodeIds[i]] = new float3(nPosX[i], nPosY[i], nPosZ[i]);
            }

            for (int i = 0; i < boundaryIds.Count; i++)
            {
                ulong bId = boundaryIds[i];
                if (CS2M_NodeSyncIds.TryResolve(EntityManager, bId, out Entity bEnt)
                    && EntityManager.HasComponent<CS2M_RemotePlaced>(bEnt))
                {
                    continue; // authored by the other side — let them stream it
                }

                _sentNodePos[bId] = new float3(boundaryPosX[i], boundaryPosY[i], boundaryPosZ[i]);
            }

            // Debug-only (logging, no behavior change): id+position dumps so a divergence hunt can tell
            // "receiver never got this node" from "receiver got it, then moved it".
            if (nNodeIds.Count > 0)
            {
                CS2M.Log.Info($"[Batch] CAP-NODES {FormatIdPosList(nNodeIds, nPosX, nPosZ)}");
            }

            if (boundaryIds.Count > 0)
            {
                CS2M.Log.Info($"[Batch] CAP-BOUND {FormatIdPosList(boundaryIds, boundaryPosX, boundaryPosZ)}");
            }

            CS2M.Log.Info(
                $"[Batch] CAPTURED nodes={nNodeIds.Count} edges={eStart.Count} dels={dStart.Count} " +
                $"boundary={boundaryIds.Count} strays={droppedStray}");
        }

        /// <summary>A. Scan every node THIS side has shipped and stream (by identity) each one whose settled
        /// Node.m_Position moved &gt; <see cref="NodePosSendThreshold"/> since it was last sent — the builder
        /// re-seating a junction AFTER the batch left. Ids no longer resolvable (node deleted/split away) are
        /// dropped from the tracking dict. Emits <see cref="NodePosUpdateCommand"/>s of ≤ <see
        /// cref="NodePosBatchCap"/> ids each, and advances the last-sent record for every id shipped.</summary>
        private void StreamNodePosUpdates()
        {
            if (_sentNodePos.Count == 0)
            {
                return;
            }

            List<ulong> gone = null;
            var upIds = new List<ulong>();
            var upX = new List<float>(); var upY = new List<float>(); var upZ = new List<float>();

            foreach (KeyValuePair<ulong, float3> kv in _sentNodePos)
            {
                if (!CS2M_NodeSyncIds.TryResolve(EntityManager, kv.Key, out Entity node))
                {
                    (gone ??= new List<ulong>()).Add(kv.Key);
                    continue;
                }

                float3 pos = EntityManager.GetComponentData<Node>(node).m_Position;
                if (math.distance(pos, kv.Value) > NodePosSendThreshold)
                {
                    upIds.Add(kv.Key); upX.Add(pos.x); upY.Add(pos.y); upZ.Add(pos.z);
                }
            }

            if (gone != null)
            {
                foreach (ulong id in gone)
                {
                    _sentNodePos.Remove(id);
                }
            }

            if (upIds.Count == 0)
            {
                return;
            }

            for (int start = 0; start < upIds.Count; start += NodePosBatchCap)
            {
                int cnt = System.Math.Min(NodePosBatchCap, upIds.Count - start);
                var cmd = new NodePosUpdateCommand
                {
                    Ids = new ulong[cnt],
                    X = new float[cnt], Y = new float[cnt], Z = new float[cnt],
                };
                for (int j = 0; j < cnt; j++)
                {
                    int k = start + j;
                    cmd.Ids[j] = upIds[k];
                    cmd.X[j] = upX[k]; cmd.Y[j] = upY[k]; cmd.Z[j] = upZ[k];
                    _sentNodePos[upIds[k]] = new float3(upX[k], upY[k], upZ[k]); // advance last-sent
                }

                Command.SendToAll?.Invoke(cmd);
                CS2M.Log.Info($"[Batch] NPU-SEND count={cnt}");
            }
        }

        // How close an unreferenced new node must sit to one of THIS batch's boundary nodes to be
        // treated as the CreateDefinitionsJob-vs-GenerateEdges materialization artifact described above,
        // rather than a genuinely distinct standalone placement. 2.5 m is comfortably inside normal net
        // node spacing (road junctions are metres apart at minimum) yet wide enough to catch the
        // sub-metre float drift the proven case showed (308/309/310 all rounded to the same F1 digits).
        // Full 3D (not just XZ) so a vertically-stacked distinct junction (overpass/underpass ramps,
        // legitimately close in plan but metres apart in elevation) is never misclassified as a stray.
        private const float StrayNearBoundaryDistance = 2.5f;

        private static bool IsStrayNearBoundary(float3 pos, List<float> bx, List<float> by, List<float> bz)
        {
            float maxSq = StrayNearBoundaryDistance * StrayNearBoundaryDistance;
            for (int i = 0; i < bx.Count; i++)
            {
                float dx = pos.x - bx[i];
                float dy = pos.y - by[i];
                float dz = pos.z - bz[i];
                if (dx * dx + dy * dy + dz * dz <= maxSq)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Debug-only formatter: "<c>id@x,z</c>" tokens, space-separated, capped at 32 items
        /// (a "+N" suffix marks how many more were omitted). Used only by CAP-NODES/CAP-BOUND logging.</summary>
        private static string FormatIdPosList(List<ulong> ids, List<float> x, List<float> z, int cap = 32)
        {
            var sb = new System.Text.StringBuilder();
            int shown = ids.Count < cap ? ids.Count : cap;
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(ids[i]).Append('@').Append(x[i].ToString("F1")).Append(',').Append(z[i].ToString("F1"));
            }

            if (ids.Count > cap)
            {
                sb.Append(" +").Append(ids.Count - cap);
            }

            return sb.ToString();
        }

        /// <summary>Resolve a new edge's endpoint node to its shipped id. If it is one of this batch's new
        /// nodes it is already in <paramref name="nodeIdOf"/>; otherwise it is a PRE-EXISTING boundary node
        /// that gained an arm — Ensure it a stable id (already present for session-placed nodes) and record it
        /// in the boundary set (id ONLY; the receiver resolves it by identity, never re-creates it).</summary>
        private ulong ResolveEndpointId(Entity node, Dictionary<Entity, ulong> nodeIdOf,
            List<ulong> boundaryIds, HashSet<ulong> boundarySeen,
            List<float> boundaryPosX, List<float> boundaryPosY, List<float> boundaryPosZ)
        {
            if (nodeIdOf.TryGetValue(node, out ulong known))
            {
                return known;
            }

            if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
            {
                return 0;
            }

            ulong id = CS2M_NodeSyncIds.Ensure(EntityManager, node);
            if (id == 0)
            {
                return 0;
            }

            nodeIdOf[node] = id;
            if (boundarySeen.Add(id))
            {
                boundaryIds.Add(id);
                // Settled position: the receiver's id-miss fallback for SAVE nodes (identical geometry).
                float3 p = EntityManager.GetComponentData<Node>(node).m_Position;
                boundaryPosX.Add(p.x); boundaryPosY.Add(p.y); boundaryPosZ.Add(p.z);
            }

            return id;
        }

        private List<Entity> ToList(EntityQuery q)
        {
            var list = new List<Entity>();
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    list.Add(arr[i]);
                }
            }
            finally
            {
                arr.Dispose();
            }

            return list;
        }
    }
}
