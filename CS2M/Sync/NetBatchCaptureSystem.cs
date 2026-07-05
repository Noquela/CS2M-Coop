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
    /// <summary>Global toggle for the AtomicBatch net path. OFF by default (env <c>CS2M_ATOMIC=1</c>) so the
    /// proven NetDetector→NetPlaceApply reconstruct path stays the default until the batch path is validated
    /// on the 2-sim. When ON, <see cref="NetBatchCaptureSystem"/> is the only net sender and the legacy
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
                    _state = System.Environment.GetEnvironmentVariable("CS2M_ATOMIC") == "1" ? 1 : 0;
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
                return;
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
            // as orphans. With no echo in the frame, unreferenced nodes (Point-mode placements) still ship.
            var nNodeIds = new List<ulong>();
            var nPosX = new List<float>(); var nPosY = new List<float>(); var nPosZ = new List<float>();
            var nRotX = new List<float>(); var nRotY = new List<float>(); var nRotZ = new List<float>(); var nRotW = new List<float>();
            var nTypes = new List<string>(); var nNames = new List<string>();
            var nStandalone = new List<bool>();
            var nHasElev = new List<bool>(); var nElevX = new List<float>(); var nElevY = new List<float>();
            var nSeeds = new List<int>();

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

                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(n).m_Prefab,
                        out PrefabBase prefab) || prefab == null)
                {
                    continue;
                }

                Node node = EntityManager.GetComponentData<Node>(n);
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
            var dsX = new List<float>(); var dsZ = new List<float>(); var deX = new List<float>(); var deZ = new List<float>();
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
                dsX.Add(sp.x); dsZ.Add(sp.z); deX.Add(ep.x); deZ.Add(ep.z);
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
                DelStartX = dsX.ToArray(), DelStartZ = dsZ.ToArray(), DelEndX = deX.ToArray(), DelEndZ = deZ.ToArray(),

                BoundaryNodeIds = boundaryIds.ToArray(),
                BoundaryPosX = boundaryPosX.ToArray(),
                BoundaryPosY = boundaryPosY.ToArray(),
                BoundaryPosZ = boundaryPosZ.ToArray(),
            };

            Command.SendToAll?.Invoke(cmd);
            CS2M.Log.Info(
                $"[Batch] CAPTURED nodes={nNodeIds.Count} edges={eStart.Count} dels={dStart.Count} boundary={boundaryIds.Count}");
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
