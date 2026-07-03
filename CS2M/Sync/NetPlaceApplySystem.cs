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
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Materializes nets placed by remote players.
    ///
    ///     v3 approach (vanilla definition injection): the direct-archetype edge (v2) turned out to be a
    ///     hollow shell — this system ran at Modification5, AFTER every net consumer (References@Mod2B,
    ///     CompositionSelect@Mod3, Geometry/Lane/Block@Mod4), and the game strips Created/Updated at the
    ///     end of the same frame, so no downstream system ever saw the edge: no composition, no mesh, no
    ///     zoning blocks (the "[Zone] SKIP noBlock" from the first 2-PC test).
    ///
    ///     The fix copies what the game itself does for programmatic, non-tool construction
    ///     (Game.Simulation.BuildingConstructionSystem.CreateNets / ZoneSpawnSystem): create a
    ///     CreationDefinition with <c>CreationFlags.Permanent</c> + NetCourse + Updated, registered
    ///     BEFORE Modification1. GenerateNodesSystem(@Mod1)/GenerateEdgesSystem(@Mod2) then build the
    ///     REAL net — terrain-adjusted curve, node merge/reuse, BuildOrder, ConnectedNode — and the whole
    ///     pipeline (composition, geometry, lanes, zone blocks, search tree, mesh) completes in the same
    ///     frame. With Permanent, no Temp is added (GenerateEdge skips it), so nothing needs the tool flow.
    /// </summary>
    public partial class NetPlaceApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _liveNodes;
        private readonly List<Entity> _pendingDefinitions = new List<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _liveNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Node>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(), // standalone road nodes only, not building sub-nets
                },
            });
            CS2M.Log.Info("[Net] NetPlaceApplySystem created (definition-injection mode)");
        }

        protected override void OnUpdate()
        {
            // Definitions injected last frame were consumed by GenerateNodes/Edges already — clean up.
            // (ToolClearSystem may have destroyed them first; the Exists guard makes this idempotent.)
            for (int i = 0; i < _pendingDefinitions.Count; i++)
            {
                if (EntityManager.Exists(_pendingDefinitions[i]))
                {
                    EntityManager.DestroyEntity(_pendingDefinitions[i]);
                }
            }

            _pendingDefinitions.Clear();

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // v51: ONE net per frame. Same-batch segments must see each other as REAL edges —
            // the snap/split machinery can't cut a road that is still a pending definition, which
            // stacked same-frame arrivals on top of each other (selftest net-tee caught it).
            // 60/s throughput is far above any human build rate; resync bursts just take moments.
            if (RemoteNetQueue.TryDequeue(out NetPlaceCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] apply failed in NetPlaceApplySystem: {ex.Message}"); }
            }
        }

        private void ApplyOne(NetPlaceCommand cmd)
        {
            var hash = new Colossal.Hash128(new uint4(cmd.Hash0, cmd.Hash1, cmd.Hash2, cmd.Hash3));
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, hash);

            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info($"[Net] RESOLVE-FAIL type={cmd.PrefabType} name={cmd.PrefabName}");
                return;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out Entity netPrefab))
            {
                CS2M.Log.Info($"[Net] RESOLVE-FAIL no prefab entity name={cmd.PrefabName}");
                return;
            }

            if (!EntityManager.HasComponent<NetData>(netPrefab))
            {
                CS2M.Log.Info($"[Net] APPLY-FAIL prefab {cmd.PrefabName} has no NetData (not a net?)");
                return;
            }

            var bezier = new Bezier4x3(
                new float3(cmd.Ax, cmd.Ay, cmd.Az),
                new float3(cmd.Bx, cmd.By, cmd.Bz),
                new float3(cmd.Cx, cmd.Cy, cmd.Cz),
                new float3(cmd.Dx, cmd.Dy, cmd.Dz));

            // Connect to the existing net where the endpoints already have nodes (cross-PC snapping).
            Entity startNode = FindExistingNode(bezier.a);
            Entity endNode = FindExistingNode(bezier.d);

            // Idempotency: if an edge with this prefab already links these two nodes (duplicate or
            // re-sent packet, echo miss), don't build the same segment twice.
            if (startNode != Entity.Null && endNode != Entity.Null && EdgeExists(startNode, endNode, netPrefab))
            {
                CS2M.Log.Info($"[Net] SKIP duplicate name={cmd.PrefabName} " +
                              $"start=({bezier.a.x:F1},{bezier.a.z:F1}) end=({bezier.d.x:F1},{bezier.d.z:F1})");
                return;
            }

            // v50.2 FIELD FIX ("roads on top of roads"): intersection rebuilds re-send derived
            // pieces the v39 split filter can't always recognize, and this PC has usually already
            // produced the identical piece itself when the CAUSAL road arrived. If an existing
            // same-prefab edge's curve already covers this segment (start, mid and end all within
            // ~1.5 m of it), building it again would stack a phantom road on top.
            if (CoveredByExistingEdge(bezier, netPrefab))
            {
                CS2M.Log.Info($"[Net] SKIP covered name={cmd.PrefabName} " +
                              $"start=({bezier.a.x:F1},{bezier.a.z:F1}) end=({bezier.d.x:F1},{bezier.d.z:F1})");
                return;
            }

            // v51 FIELD FIX (T-junctions never wired): the game's Permanent-definition pipeline
            // deliberately does NOT connect or split existing edges (GenerateNodesSystem returns
            // early for m_IsPermanent) — only the real tool's Temp flow does. So when this new
            // segment's endpoint lands MID-SPAN on an existing edge, we do the split ourselves:
            // delete the target and rebuild it as two pieces meeting at the exact junction point.
            // All three courses share that point bit-for-bit, so they fuse into one node (same
            // mechanism vanilla sub-nets use).
            if (startNode == Entity.Null)
            {
                bezier.a = SnapOrSplitAt(bezier.a, ref startNode);
            }

            if (endNode == Entity.Null)
            {
                bezier.d = SnapOrSplitAt(bezier.d, ref endNode);
            }

            // v51 FIELD FIX (X-crossings never wired — "roads on top of roads where nothing works"):
            // when the new curve CROSSES existing edges mid-span, cut those targets AND slice the
            // new road at each junction; every piece is emitted as its own Permanent course and all
            // shared points match bit-for-bit so they fuse into real intersection nodes.
            var cutTs = new List<float>();
            var cutPoints = new List<float3>();
            FindMidSpanCrossings(bezier, cutTs, cutPoints);

            if (cutTs.Count == 0)
            {
                EmitCourse(netPrefab, cmd.PrefabName, bezier, startNode, endNode,
                    new float2(cmd.StartElevX, cmd.StartElevY), new float2(cmd.EndElevX, cmd.EndElevY),
                    cmd.RandomSeed);
                return;
            }

            Bezier4x3 rest = bezier;
            float consumed = 0f;
            Entity chainStartNode = startNode;
            float2 chainStartElev = new float2(cmd.StartElevX, cmd.StartElevY);
            for (int i = 0; i < cutTs.Count; i++)
            {
                float tLocal = (cutTs[i] - consumed) / (1f - consumed);
                tLocal = math.clamp(tLocal, 0.02f, 0.98f);
                MathUtils.Divide(rest, out Bezier4x3 piece, out Bezier4x3 remainder, tLocal);
                piece.d = cutPoints[i];      // fuse with the split target's junction node
                remainder.a = cutPoints[i];
                EmitCourse(netPrefab, cmd.PrefabName, piece, chainStartNode, Entity.Null,
                    chainStartElev, float2.zero, cmd.RandomSeed * 31 + i);
                chainStartNode = Entity.Null;
                chainStartElev = float2.zero;
                rest = remainder;
                consumed = cutTs[i];
            }

            EmitCourse(netPrefab, cmd.PrefabName, rest, Entity.Null, endNode,
                float2.zero, new float2(cmd.EndElevX, cmd.EndElevY), cmd.RandomSeed * 31 + cutTs.Count);
            CS2M.Log.Info($"[Net] X-CROSS name={cmd.PrefabName} cuts={cutTs.Count} (new road sliced at each junction)");
        }

        /// <summary>Emits one Permanent course for a (piece of the) synced road, with echo marking.</summary>
        private void EmitCourse(Entity netPrefab, string prefabName, Bezier4x3 bezier,
            Entity startNode, Entity endNode, float2 startElev, float2 endElev, int seed)
        {
            int segHash = RemoteNetEcho.SegHash(bezier.a, bezier.d, prefabName);
            RemoteNetEcho.Mark(segHash);

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;

            course.m_StartPosition.m_Position = bezier.a;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(bezier));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_Elevation = startElev;
            course.m_StartPosition.m_ParentMesh = -1;
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst;
            course.m_StartPosition.m_Entity = startNode;

            course.m_EndPosition.m_Position = bezier.d;
            course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(bezier));
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_Elevation = endElev;
            course.m_EndPosition.m_ParentMesh = -1;
            course.m_EndPosition.m_Flags = CoursePosFlags.IsLast;
            course.m_EndPosition.m_Entity = endNode;

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = netPrefab,
                m_RandomSeed = seed,
                m_Flags = CreationFlags.Permanent, // no Temp: GenerateEdge builds the REAL segment
            });
            EntityManager.AddComponentData(def, course);
            EntityManager.AddComponent<Updated>(def); // required by GenerateEdgesSystem's definition query
            _pendingDefinitions.Add(def);

            CS2M.Log.Info(
                $"[Net] APPLIED-DEF name={prefabName} len={course.m_Length:F1} segHash={segHash} " +
                $"startNode={(startNode != Entity.Null ? startNode.Index : 0)} " +
                $"endNode={(endNode != Entity.Null ? endNode.Index : 0)} " +
                $"start=({bezier.a.x:F1},{bezier.a.y:F1},{bezier.a.z:F1}) " +
                $"end=({bezier.d.x:F1},{bezier.d.y:F1},{bezier.d.z:F1})");
        }

        /// <summary>Finds mid-span crossings between the new curve and existing live edges: for each
        /// crossing, splits the TARGET edge there and records (t on the new curve, exact junction
        /// point). Results are ordered by t. Endpoint touches are excluded (SnapOrSplitAt owns those).</summary>
        private void FindMidSpanCrossings(Bezier4x3 bezier, List<float> cutTs, List<float3> cutPoints)
        {
            EntityQuery edges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Game.Net.Elevation>(),
                },
            });

            var found = new List<(float t, Entity target, float targetT)>();
            NativeArray<Entity> ents = edges.ToEntityArray(Allocator.Temp);
            try
            {
                float3 newMid = MathUtils.Position(bezier, 0.5f);
                float newReach = MathUtils.Length(bezier) * 0.5f + 8f;
                foreach (Entity cand in ents)
                {
                    Curve cc = EntityManager.GetComponentData<Curve>(cand);
                    float3 cmid = MathUtils.Position(cc.m_Bezier, 0.5f);
                    float reach = newReach + MathUtils.Length(cc.m_Bezier) * 0.5f;
                    float dxm = cmid.x - newMid.x, dzm = cmid.z - newMid.z;
                    if (dxm * dxm + dzm * dzm > reach * reach)
                    {
                        continue;
                    }

                    // Closest approach of the new curve to this candidate (sampled).
                    float bestD = float.MaxValue, bestT = 0f, bestTc = 0f;
                    for (int s = 1; s < 24; s++)
                    {
                        float t = s / 24f;
                        float3 p = MathUtils.Position(bezier, t);
                        float tc;
                        float d = MathUtils.Distance(cc.m_Bezier.xz, p.xz, out tc);
                        if (d < bestD)
                        {
                            bestD = d;
                            bestT = t;
                            bestTc = tc;
                        }
                    }

                    // Mid-span on BOTH curves and genuinely touching → a crossing to wire.
                    if (bestD < 0.9f && bestT > 0.04f && bestT < 0.96f && bestTc > 0.06f && bestTc < 0.94f)
                    {
                        found.Add((bestT, cand, bestTc));
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            if (found.Count == 0)
            {
                return;
            }

            found.Sort((x, y) => x.t.CompareTo(y.t));
            float lastT = -1f;
            foreach ((float t, Entity target, float targetT) hit in found)
            {
                if (cutTs.Count >= 6 || hit.t - lastT < 0.03f)
                {
                    continue; // cap + de-dup near-identical cuts
                }

                if (!EntityManager.Exists(hit.target) || EntityManager.HasComponent<Deleted>(hit.target))
                {
                    continue; // already split by an earlier hit this same apply
                }

                float3 junction = SplitTargetEdge(hit.target, hit.targetT);
                cutTs.Add(hit.t);
                cutPoints.Add(junction);
                lastT = hit.t;
            }
        }

        /// <summary>
        ///     v51: resolves an endpoint that has no node yet. If it lands mid-span on a live edge
        ///     (within ~4 m of the curve), SPLIT that edge: mark echo hashes, delete it, and rebuild
        ///     it as two Permanent pieces (same prefab/upgrades) meeting exactly at the junction
        ///     point. Returns the (possibly snapped) endpoint position; near an edge's END instead,
        ///     returns that end's node via <paramref name="nodeInOut"/> (plain merge).
        /// </summary>
        private float3 SnapOrSplitAt(float3 endpoint, ref Entity nodeInOut)
        {
            EntityQuery edges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),                 // never split building sub-nets
                    ComponentType.ReadOnly<Game.Net.Elevation>(),    // bridges/tunnels: too risky, skip
                },
            });

            Entity target = Entity.Null;
            float bestD = 4f; // meters to the curve
            float bestT = 0f;
            NativeArray<Entity> ents = edges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    Curve curve = EntityManager.GetComponentData<Curve>(cand);
                    float t;
                    float d = MathUtils.Distance(curve.m_Bezier.xz, endpoint.xz, out t);
                    if (d < bestD)
                    {
                        bestD = d;
                        bestT = t;
                        target = cand;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            if (target == Entity.Null)
            {
                return endpoint; // truly open ground — a fresh node is correct
            }

            Edge targetEdge = EntityManager.GetComponentData<Edge>(target);
            Curve targetCurve = EntityManager.GetComponentData<Curve>(target);

            // Near one of the target's ends → plain node merge, no split needed.
            if (bestT < 0.06f || bestT > 0.94f)
            {
                Entity node = bestT < 0.06f ? targetEdge.m_Start : targetEdge.m_End;
                if (EntityManager.HasComponent<Node>(node))
                {
                    nodeInOut = node;
                    return EntityManager.GetComponentData<Node>(node).m_Position;
                }

                return endpoint;
            }

            return SplitTargetEdge(target, bestT);
        }

        /// <summary>Splits a live edge at curve parameter <paramref name="t"/>: marks echo hashes,
        /// deletes it and emits two Permanent pieces (same prefab/upgrades) whose outer ends reuse
        /// the original nodes. Returns the exact junction point (shared bit-for-bit).</summary>
        private float3 SplitTargetEdge(Entity target, float t)
        {
            Edge targetEdge = EntityManager.GetComponentData<Edge>(target);
            Curve targetCurve = EntityManager.GetComponentData<Curve>(target);

            string targetPrefabName = "";
            Entity targetPrefab = EntityManager.GetComponentData<PrefabRef>(target).m_Prefab;
            if (_prefabSystem.TryGetPrefab(targetPrefab, out PrefabBase tp) && tp != null)
            {
                targetPrefabName = tp.name;
            }

            MathUtils.Divide(targetCurve.m_Bezier, out Bezier4x3 left, out Bezier4x3 right, t);
            float3 junction = left.d; // == right.a

            // Echo bookkeeping so our own detectors don't re-broadcast this derived surgery:
            // the delete of the target and the two Applied pieces.
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(targetCurve.m_Bezier.a, targetCurve.m_Bezier.d, "del"));
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(left.a, left.d, targetPrefabName));
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(right.a, right.d, targetPrefabName));

            Game.Net.Upgraded upgraded = EntityManager.HasComponent<Game.Net.Upgraded>(target)
                ? EntityManager.GetComponentData<Game.Net.Upgraded>(target)
                : default;
            int seed = EntityManager.HasComponent<PseudoRandomSeed>(target)
                ? EntityManager.GetComponentData<PseudoRandomSeed>(target).m_Seed
                : 0;

            EntityManager.AddComponent<Deleted>(target);

            CreatePieceDefinition(targetPrefab, left, targetEdge.m_Start, Entity.Null, upgraded, seed);
            CreatePieceDefinition(targetPrefab, right, Entity.Null, targetEdge.m_End, upgraded, seed * 31 + 7);

            CS2M.Log.Info($"[Net] SPLIT edge={target.Index} prefab={targetPrefabName} t={t:F3} " +
                          $"junction=({junction.x:F1},{junction.z:F1}) (junction wired)");
            return junction;
        }

        /// <summary>One Permanent course for a split piece — same pattern as the main segment;
        /// the outer end reuses the original node (m_Entity), the cut end fuses by position.</summary>
        private void CreatePieceDefinition(Entity netPrefab, Bezier4x3 curve, Entity startEntity,
            Entity endEntity, Game.Net.Upgraded upgraded, int seed)
        {
            NetCourse course = default;
            course.m_Curve = curve;
            course.m_Length = MathUtils.Length(curve);
            course.m_FixedIndex = -1;

            course.m_StartPosition.m_Position = curve.a;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(curve));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_ParentMesh = -1;
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst;
            course.m_StartPosition.m_Entity = startEntity;

            course.m_EndPosition.m_Position = curve.d;
            course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(curve));
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_ParentMesh = -1;
            course.m_EndPosition.m_Flags = CoursePosFlags.IsLast;
            course.m_EndPosition.m_Entity = endEntity;

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = netPrefab,
                m_RandomSeed = seed,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponentData(def, course);
            if (upgraded.m_Flags != default(CompositionFlags))
            {
                EntityManager.AddComponentData(def, upgraded);
            }

            EntityManager.AddComponent<Updated>(def);
            _pendingDefinitions.Add(def);
        }

        /// <summary>Nearest live standalone node within 0.5 m (XZ) of <paramref name="pos"/>, or Null.</summary>
        private Entity FindExistingNode(float3 pos)
        {
            NativeArray<Entity> nodes = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestSq = 0.25f;
                foreach (Entity n in nodes)
                {
                    float3 p = EntityManager.GetComponentData<Node>(n).m_Position;
                    float dx = p.x - pos.x;
                    float dz = p.z - pos.z;
                    float d = dx * dx + dz * dz;
                    if (d < bestSq)
                    {
                        bestSq = d;
                        best = n;
                    }
                }

                return best;
            }
            finally
            {
                nodes.Dispose();
            }
        }

        /// <summary>True when a live same-prefab edge's curve passes within ~1.5 m (XZ) of the new
        /// segment's start, midpoint AND end — i.e. the segment already exists here (a derived
        /// intersection-rebuild piece this PC produced on its own). A legitimate parallel
        /// bypass shares endpoints but NOT the midpoint, so it still builds.</summary>
        private bool CoveredByExistingEdge(Bezier4x3 bezier, Entity netPrefab)
        {
            const float tolSq = 2.25f; // 1.5 m
            float3 mid = MathUtils.Position(bezier, 0.5f);

            EntityQuery edges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            Unity.Collections.NativeArray<Entity> ents = edges.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(cand).m_Prefab != netPrefab)
                    {
                        continue;
                    }

                    Bezier4x3 c = EntityManager.GetComponentData<Curve>(cand).m_Bezier;
                    // Cheap reject by distance to the candidate's own midpoint.
                    float3 cmid = MathUtils.Position(c, 0.5f);
                    float dxm = cmid.x - mid.x, dzm = cmid.z - mid.z;
                    float reach = MathUtils.Length(c) + 3f;
                    if (dxm * dxm + dzm * dzm > reach * reach)
                    {
                        continue;
                    }

                    if (DistSqXZ(c, bezier.a) < tolSq && DistSqXZ(c, mid) < tolSq && DistSqXZ(c, bezier.d) < tolSq)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return false;
        }

        private static float DistSqXZ(Bezier4x3 curve, float3 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i <= 16; i++)
            {
                float3 c = MathUtils.Position(curve, i / 16f);
                float dx = c.x - p.x;
                float dz = c.z - p.z;
                float d = dx * dx + dz * dz;
                if (d < best)
                {
                    best = d;
                }
            }

            return best;
        }

        private bool EdgeExists(Entity startNode, Entity endNode, Entity netPrefab)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(startNode))
            {
                return false;
            }

            DynamicBuffer<ConnectedEdge> connected = EntityManager.GetBuffer<ConnectedEdge>(startNode);
            for (int i = 0; i < connected.Length; i++)
            {
                Entity e = connected[i].m_Edge;
                if (!EntityManager.Exists(e) || !EntityManager.HasComponent<Edge>(e))
                {
                    continue;
                }

                Edge edge = EntityManager.GetComponentData<Edge>(e);
                bool linksSameNodes = (edge.m_Start == startNode && edge.m_End == endNode)
                                      || (edge.m_Start == endNode && edge.m_End == startNode);
                if (!linksSameNodes)
                {
                    continue;
                }

                if (EntityManager.HasComponent<PrefabRef>(e)
                    && EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == netPrefab)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
