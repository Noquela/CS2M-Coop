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
    /// <summary>Global toggle for node-position healing on the LEGACY (default) net path. OFF by default
    /// (env <c>CS2M_NODEHEAL=1</c>) until validated on a 2-sim — see <see cref="NetPlaceApplySystem"/>.
    /// HealNodePosition for the mechanism this unblocks.</summary>
    public static class NodeHeal
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    // ON por padrão desde 2026-07-07 — validado em 2-sim (drift sintético de 15m:
                    // client logou HEAL-SNAP e o nó convergiu). CS2M_NODEHEAL=0 desliga.
                    _state = System.Environment.GetEnvironmentVariable("CS2M_NODEHEAL") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>Global toggle for the cross-session-overdraw silent-drop guard (see <c>ApplyOne</c>'s
    /// HasNodes branch, <c>AnyLiveConnectionExists</c>). ON by default since 2026-07-07 — validated live:
    /// TRIREPRO PHASE8 XSESSION-OVERDRAW (2-sim, ~50% of TRIREPRO runs) applies an edge (logged
    /// <c>[Net] APPLIED-DEF</c>) that then vanishes from the client's world with its two dead-end nodes
    /// and ZERO <c>[Net] DELETE/SKIP</c> ever logged, because
    /// <c>Game.Tools.GenerateEdgesSystem.GenerateEdge</c> (decomp line ~1280) silently drops ANY Permanent
    /// course whose two resolved node endpoints already share a live edge of ANY prefab
    /// (<c>ConnectionExists</c>, decomp line ~1744, prefab-agnostic) — one frame AFTER our own
    /// <c>EmitCourse</c> already logged success. Our own pre-check (<c>EdgeExists</c>) only guards the
    /// SAME prefab, so it does not see this coming. Also re-confirmed clean in the 88 PASS/0 FAIL
    /// selftest run with every gated fix enabled together (no regression/echo/crash). Set env
    /// <c>CS2M_OVERDRAWFIX=0</c> to disable.</summary>
    public static class OverdrawFix
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_OVERDRAWFIX") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

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

        // Nodes created from last frame's courses whose stable id must be stamped once GenerateNodes has
        // built them, so sibling edges (this batch or a later one) fuse onto the SAME node by identity.
        private struct PendingStamp { public ulong Id; public float3 Pos; public int Age; }
        private readonly List<PendingStamp> _pendingNodeStamps = new List<PendingStamp>();

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

            // Stamp the node ids of last frame's freshly-built nodes BEFORE applying the next net, so a
            // sibling edge arriving now resolves its shared endpoint by identity instead of proximity.
            ProcessPendingStamps();

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

            // JUNCTION FIX (topology-authoritative): the host already split every road at its junctions
            // and sends each piece carrying its authoritative node coords (StartNode/EndNode). So when
            // HasNodes we snap the endpoints to those coords and DON'T re-split (no SnapOrSplitAt /
            // FindMidSpanCrossings): the receiver used to GUESS a second topology on top of the host's
            // pieces — that is the +roads X-crossing drift. Adjacent pieces resolve the SAME node
            // position, so junctions emerge from shared nodes. Paired with the emitter now propagating
            // the delete of the split original (NetEditDetectorSystem), the client matches the host
            // edge-for-edge. Legacy/relayed commands (HasNodes=false) fall through to the guess path.
            if (cmd.HasNodes)
            {
                // Snap the endpoints to the host's authoritative node coords. EMPIRICAL (selftest): on the
                // client's Permanent-course path the created node sits AT the curve endpoint — it is NOT
                // decoupled from the curve the way the host's post-geometry pullback separates them. So to
                // land the shared junction node at the host's coord we put the curve endpoint there.
                // (A prior attempt to keep the pulled-back curve and pass the node coord separately made
                // the node land on the curve endpoint 6 m off-centre and the arms failed to fuse — reverted.)
                bezier.a = new float3(cmd.StartNodeX, cmd.StartNodeY, cmd.StartNodeZ);
                bezier.d = new float3(cmd.EndNodeX, cmd.EndNodeY, cmd.EndNodeZ);

                // Share junction nodes by STABLE IDENTITY (id), falling back to the authoritative-coord
                // proximity search only for pre-existing/save nodes never id-stamped. Identity is immune
                // to the order-dependence that made proximity forge phantom nodes: a junction re-centres
                // as roads connect, so a later piece's coord lands 5-7 m from where this sim first placed
                // the node and the wide fallback (degree>=2 only) misses it — because the pieces that make
                // it a junction have not arrived yet. Same sender node → same id → one shared node here.
                Entity aStart = ResolveNode(cmd.StartNodeId, bezier.a);
                Entity aEnd = ResolveNode(cmd.EndNodeId, bezier.d);

                // T3 "never apply half" (crash #3 fix): if the resolved node sits farther than
                // NodeHealMergeDist (10 m) from the authoritative endpoint coord, DO NOT reuse it. Reusing a
                // far node makes EmitCourse pin this edge's curve endpoint (at the authoritative pos) onto a
                // node metres away — an I4 violation the game's Burst net jobs null-deref on (the "node with
                // 24 m drift stayed put but the state marched on" crash). Freeing the endpoint to Null makes
                // GenerateNodes mint a FRESH node AT the authoritative pos: the local graph then diverges
                // from the host's topology but stays internally CONSISTENT (curve endpoint == node), and the
                // divergence shows up on the radar to be healed later — better than a hard crash.
                aStart = RejectFarNode(aStart, cmd.StartNodeId, bezier.a, "start");
                aEnd = RejectFarNode(aEnd, cmd.EndNodeId, bezier.d, "end");

                // Degenerate guard: a short piece must never collapse BOTH ends onto one node (that would
                // build a looping/zero-span edge and lose a node).
                if (aStart != Entity.Null && aStart == aEnd)
                {
                    aEnd = Entity.Null;
                }

                if (aStart != Entity.Null && aEnd != Entity.Null && EdgeExists(aStart, aEnd, netPrefab))
                {
                    CS2M.Log.Info($"[Net] SKIP duplicate (auth) name={cmd.PrefabName} " +
                                  $"start=({bezier.a.x:F1},{bezier.a.z:F1}) end=({bezier.d.x:F1},{bezier.d.z:F1})");
                    return;
                }

                // OVERDRAW-GUARD (gated, CS2M_OVERDRAWFIX, default ON since 2026-07-07 — CS2M_OVERDRAWFIX=0
                // disables): EdgeExists above only checks
                // the SAME prefab. The vanilla generator's own dedup (GenerateEdgesSystem.GenerateEdge,
                // decomp ~line 1280: ConnectionExists) is prefab-AGNOSTIC — if aStart/aEnd (resolved above
                // by identity or, on a miss, by FindJunctionNode's pure position+degree proximity search)
                // already share a live edge of ANY OTHER prefab, the game will silently discard this
                // Permanent course next frame: no edge, no exception, no CS2M log, because it happens
                // entirely inside undecorated vanilla/Burst code. EmitCourse below would still log
                // "APPLIED-DEF" — a false success. Free the end node's identity so GenerateNodesSystem
                // mints a fresh one instead: the course still materializes (as a duplicate/overlap
                // matching whatever the SENDER's own world already has — the same trade-off the host
                // itself lives with after a cross-session overdraw) instead of vanishing outright.
                if (OverdrawFix.Enabled && aStart != Entity.Null && aEnd != Entity.Null
                    && AnyLiveConnectionExists(aStart, aEnd))
                {
                    CS2M.Log.Info($"[Net] OVERDRAW-GUARD name={cmd.PrefabName} start={aStart.Index} " +
                                  $"end={aEnd.Index} already connected by a different-prefab edge — vanilla " +
                                  "GenerateEdge would silently drop this course; freeing endNode identity");
                    aEnd = Entity.Null;
                }

                // Delete the un-split ORIGINAL this piece replaces (an edge that spans it). The sender
                // split it into pieces and its position-addressed NetDelete can MISS on the receiver
                // (FindEdge needs both node coords within ~3 m), leaving the original stacked under the
                // pieces — the +roads X-crossing drift. Deleting the covering edge here is deterministic
                // and fires exactly once (the first piece deletes it; later pieces find it already gone).
                // NOTE: we delete the ORIGINAL and keep the piece — the opposite of CoveredByExistingEdge,
                // which would drop the piece and keep the original (a hole).
                Entity covering = FindCoveringEdge(bezier, netPrefab);
                if (covering != Entity.Null && !EntityManager.HasComponent<Deleted>(covering))
                {
                    // Echo guard (issue #1): without this Mark, NetEditDetectorSystem sees the covering
                    // edge Deleted and re-broadcasts a NetDeleteCommand for it — every received T/X
                    // split leaked a phantom delete. Same bookkeeping SplitTargetEdge already does;
                    // the detector hashes NODE positions, which sit on the curve endpoints (0.5 m grid).
                    Curve coveringCurve = EntityManager.GetComponentData<Curve>(covering);
                    RemoteNetEcho.Mark(RemoteNetEcho.SegHash(coveringCurve.m_Bezier.a, coveringCurve.m_Bezier.d, "del"));
                    EntityManager.AddComponent<Deleted>(covering);
                    CS2M.Log.Info($"[Net] DELETE split-original edge={covering.Index} (piece {cmd.PrefabName} replaces it)");
                }

                EmitCourse(netPrefab, cmd.PrefabName, bezier, aStart, aEnd,
                    new float2(cmd.StartElevX, cmd.StartElevY), new float2(cmd.EndElevX, cmd.EndElevY),
                    cmd.RandomSeed);
                return;
            }

            // ---- legacy path (HasNodes=false): receiver guesses splits/junctions by proximity ----
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

        /// <summary>Emits one Permanent course for a (piece of the) synced road, with echo marking.
        /// The node COORDS (<paramref name="startNodePos"/>/<paramref name="endNodePos"/>) are where the
        /// junction node is placed/fused; they are DECOUPLED from the curve endpoints because at a
        /// junction the node sits at the intersection centre while the edge curve is pulled back. When
        /// null (legacy path) they default to the curve endpoints (node == curve end, no pullback).</summary>
        private void EmitCourse(Entity netPrefab, string prefabName, Bezier4x3 bezier,
            Entity startNode, Entity endNode, float2 startElev, float2 endElev, int seed,
            float3? startNodePos = null, float3? endNodePos = null)
        {
            int segHash = RemoteNetEcho.SegHash(bezier.a, bezier.d, prefabName);
            RemoteNetEcho.Mark(segHash);

            float3 sPos = startNodePos ?? bezier.a;
            float3 ePos = endNodePos ?? bezier.d;

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;

            course.m_StartPosition.m_Position = sPos;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(bezier));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_Elevation = startElev;
            course.m_StartPosition.m_ParentMesh = -1;
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst;
            course.m_StartPosition.m_Entity = startNode;

            course.m_EndPosition.m_Position = ePos;
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

        /// <summary>Resolve an edge endpoint to a node, IDENTITY-first. (1) A live node already stamped
        /// with this id → reuse it exactly, regardless of arrival order or how far it re-centred. (2) Else
        /// a pre-existing/save node at the coord → adopt it under this id so siblings fuse exactly. (3)
        /// Else Null (GenerateNodes creates a fresh node) and we schedule its id-stamp for next frame.</summary>
        private Entity ResolveNode(ulong id, float3 pos)
        {
            // Record the host's authoritative coord for this node id so NodePinSystem can snap the local
            // node back to it (fixes the <1 m junction drift that breaks zone-block matching).
            CS2M_NodeSyncIds.SetAuthPos(id, pos);

            if (CS2M_NodeSyncIds.TryResolve(EntityManager, id, out Entity byId))
            {
                // NODEHEAL (gated): a split/derived node's identity can be handed off to a DIFFERENT
                // physical node on the SENDER between two pieces of the same multi-edge placement (the
                // real net editor re-uses one endpoint's identity for the new split point and mints a
                // fresh one for the vacated end — the exact mechanism NetBatchApplySystem's BOUND-MERGE/
                // BOUND-SNAP were built for, proven live with a node id 100 m off). This legacy path had no
                // equivalent reconciliation: once TryResolve hit, the freshly-declared pos was silently
                // discarded, so the client kept using the STALE node forever. See HealNodePosition.
                if (NodeHeal.Enabled)
                {
                    HealNodePosition(byId, id, pos);

                    // HealNodePosition may have Remapped this id onto a DIFFERENT node (HEAL-MERGE) — re-
                    // resolve so we return the node the id now names, not the pre-merge (far) one. Without
                    // this the merge survivor was computed and then thrown away, and the caller kept using
                    // the stale far node (an I4 hazard the T3 far-guard would otherwise have to clean up).
                    if (CS2M_NodeSyncIds.TryResolve(EntityManager, id, out Entity afterHeal))
                    {
                        byId = afterHeal;
                    }
                }

                return byId;
            }

            Entity byPos = FindJunctionNode(pos);
            if (byPos != Entity.Null)
            {
                if (id != 0)
                {
                    CS2M_NodeSyncIds.Register(EntityManager, byPos, id);
                }

                return byPos;
            }

            if (id != 0)
            {
                _pendingNodeStamps.Add(new PendingStamp { Id = id, Pos = pos, Age = 0 });
            }

            return Entity.Null;
        }

        /// <summary>T3 "never apply half": returns <paramref name="node"/> only if it sits within
        /// <see cref="NodeHealMergeDist"/> of the authoritative endpoint coord <paramref name="authPos"/>;
        /// otherwise returns <see cref="Entity.Null"/> (so GenerateNodes mints a fresh node AT authPos) and
        /// logs <c>NODE-FAR-NEWNODE</c>. Reusing a far node would pin this edge's curve endpoint (authPos)
        /// onto a node metres away — an I4 violation the game's Burst net jobs crash on. A brand-new node has
        /// no edges, so it can never trigger I6 (a duplicate live connection) — only the reuse-both-ends case
        /// can, and that is already caught upstream by <see cref="EdgeExists"/> / OVERDRAW-GUARD.</summary>
        private Entity RejectFarNode(Entity node, ulong id, float3 authPos, string which)
        {
            if (node == Entity.Null || !EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
            {
                return Entity.Null;
            }

            float3 p = EntityManager.GetComponentData<Node>(node).m_Position;
            float distSq = math.distancesq(p, authPos);
            if (distSq <= NodeHealMergeDist * NodeHealMergeDist)
            {
                return node;
            }

            CS2M.Log.Info($"[Net] NODE-FAR-NEWNODE {which} id={id} drift={math.sqrt(distSq):F1}m " +
                          $"node=({p.x:F1},{p.z:F1}) auth=({authPos.x:F1},{authPos.z:F1}) " +
                          "— refusing to reuse a far node (would violate I4 → Burst crash); minting a fresh one");
            return Entity.Null;
        }

        // ---- NODEHEAL (CS2M_NODEHEAL=1): position reconciliation for an id-resolved node on the legacy
        // path. Exact port of NetBatchApplySystem's SnapBoundaryNode/BOUND-MERGE (same constants, same
        // two-branch logic), duplicated here rather than shared so the proven AtomicBatch path stays
        // byte-for-byte untouched. See that type's doc comment for the full "why" (live-observed node
        // slid ~70 m from a mid-draw split/merge on the sender). ----------------------------------------

        // Below this: noise (sub-metre float round-trip / normal junction settle NodePinSystem already
        // tolerates). At/above 200 m: the declared pos is implausible for a "same node" correction — most
        // likely stale/bad data — skip rather than teleport across the map.
        private const float NodeHealMinDistSq = 0.0625f; // 0.25 m
        private const float NodeHealMaxDistSq = 40000f;  // 200 m

        // Above this, a "snap" is no longer plausibly the same junction settling as arms attach — treat it
        // as the sender having folded this id's node into a DIFFERENT one (edge split/node reuse) and look
        // for the survivor instead of moving this node.
        private const float NodeHealMergeDist = 10f;
        private const float NodeHealMergeSearchRadius = 2f;

        /// <summary>Align an id-resolved node to the sender's freshly-declared position when the two have
        /// drifted apart. A large drift is treated as a MERGE (the id now names a different physical node
        /// on the sender) and re-points the id via <see cref="CS2M_NodeSyncIds.Remap"/> instead of moving
        /// this node — moving would drag this node's OTHER (unrelated) edges across the map.</summary>
        private void HealNodePosition(Entity node, ulong id, float3 wantPos)
        {
            if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
            {
                return;
            }

            Node cur = EntityManager.GetComponentData<Node>(node);
            float distSq = math.distancesq(cur.m_Position, wantPos);
            if (distSq <= NodeHealMinDistSq || distSq >= NodeHealMaxDistSq)
            {
                return;
            }

            if (distSq > NodeHealMergeDist * NodeHealMergeDist
                && CS2M_NodeSyncIds.TryFindNearbyRegistered(EntityManager, wantPos, NodeHealMergeSearchRadius, node, out Entity survivor))
            {
                CS2M_NodeSyncIds.Remap(id, survivor);
                CS2M.Log.Info($"[Net] HEAL-MERGE id={id} node={node.Index}->{survivor.Index} " +
                              $"dist={math.sqrt(distSq):F1}m wantPos=({wantPos.x:F1},{wantPos.z:F1}) " +
                              "(re-pointed id to the survivor instead of moving the stale node)");
                return;
            }

            float3 oldPos = cur.m_Position;

            // v66.6 CRASH FIX (proven from a native full dump: c0000005 null-deref inside a game Burst
            // net job, stack rooted at this method after a "HEAL-LARGE moved=40m"). Teleporting a node
            // that ALREADY has connected edges by a large distance leaves the road graph geometrically
            // inconsistent; the game's GenerateEdges/NodeAlign Burst pass then dereferences null and
            // ABORTS the whole process (the "receiver of the road crashes" bug, in every config —
            // this is the legacy path, independent of AtomicBatch/zone-reconcile). A real junction
            // settle is sub-metre; a 40 m "heal" is not a settle, it is corruption. If we get here with
            // a large drift and NO survivor to merge onto, DO NOT move — skip and accept the local
            // position (the radar will still flag it; a later real edit reconciles it). Only sub-merge-
            // distance drifts (plausible settle) are safe to snap.
            if (distSq > NodeHealMergeDist * NodeHealMergeDist)
            {
                CS2M.Log.Info($"[Net] HEAL-SKIP-LARGE id={id} drift={math.sqrt(distSq):F1}m " +
                              $"({oldPos.x:F1},{oldPos.z:F1})->({wantPos.x:F1},{wantPos.z:F1}) " +
                              "— refusing to teleport a connected node (would corrupt the graph → Burst crash)");
                return;
            }

            // I4 FIX: move the node AND drag its connected edge curves with it in one consistent step.
            // The old code here set the Node position and marked the edges Updated but left every edge's
            // m_Bezier.a/.d on the OLD coordinate — marking Updated does NOT re-fit the curve
            // (GenerateEdgesSystem.UpdateNodeConnections), so the graph was left violating I4 (curve
            // endpoint != node position) and the game's Burst net jobs null-deref on it. Only sub-merge-
            // distance drifts reach here (HEAL-SKIP-LARGE handled the rest above), so this is a small,
            // plausible settle — exactly what MoveNodeWithCurves is safe to apply.
            NetGraphSafety.MoveNodeWithCurves(EntityManager, node, wantPos);

            CS2M.Log.Info($"[Net] HEAL-SNAP id={id} moved={math.sqrt(distSq):F1}m " +
                          $"({oldPos.x:F1},{oldPos.z:F1})->({wantPos.x:F1},{wantPos.z:F1})");
        }

        /// <summary>Stamp the id onto each node GenerateNodes built from last frame's Null-ended courses.
        /// The node sits at the scheduled coord (Permanent path: node == curve end, and we apply one net
        /// per frame so nothing re-centred it yet). Drop an entry once stamped, resolved elsewhere, or aged
        /// out (the node never materialised — a skipped duplicate).</summary>
        private void ProcessPendingStamps()
        {
            for (int i = _pendingNodeStamps.Count - 1; i >= 0; i--)
            {
                PendingStamp ps = _pendingNodeStamps[i];
                if (CS2M_NodeSyncIds.TryResolve(EntityManager, ps.Id, out _))
                {
                    _pendingNodeStamps.RemoveAt(i);
                    continue;
                }

                Entity node = FindUnstampedNodeAt(ps.Pos);
                if (node != Entity.Null)
                {
                    CS2M_NodeSyncIds.Register(EntityManager, node, ps.Id);
                    _pendingNodeStamps.RemoveAt(i);
                }
                else if (++ps.Age > 6)
                {
                    _pendingNodeStamps.RemoveAt(i);
                }
                else
                {
                    _pendingNodeStamps[i] = ps;
                }
            }
        }

        /// <summary>Nearest live standalone node within 2.5 m of <paramref name="pos"/> that has no id yet,
        /// or Null — the node just built for a scheduled stamp (closest-wins guards a rare near neighbour).</summary>
        private Entity FindUnstampedNodeAt(float3 pos)
        {
            NativeArray<Entity> nodes = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestSq = 6.25f; // 2.5 m
                foreach (Entity n in nodes)
                {
                    if (EntityManager.HasComponent<CS2M_NodeSyncId>(n))
                    {
                        continue; // already owned by another id
                    }

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

        /// <summary>Nearest live standalone node within a JUNCTION-scale radius (3.5 m XZ) of the
        /// host's authoritative node coord, or Null. Used ONLY on the topology-authoritative path:
        /// the host says "this endpoint is a node at P", so the closest local node to P is that same
        /// junction even if it settled a couple of metres off (the intersection re-centres as roads
        /// join). Wide enough to catch the moved sibling, tight enough not to swallow a neighbouring
        /// junction (Small Road nodes sit ~8 m apart). Closest-wins guards against grabbing the wrong
        /// one when two are in range.</summary>
        private Entity FindJunctionNode(float3 pos)
        {
            NativeArray<Entity> nodes = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestSq = 12.25f; // 3.5 m — any node (the normal case)
                Entity bestJunc = Entity.Null;
                float bestJuncSq = 64f; // 8 m — but ONLY an existing junction (degree >= 2)
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

                    if (d < bestJuncSq && IsJunctionNode(n))
                    {
                        bestJuncSq = d;
                        bestJunc = n;
                    }
                }

                // Prefer the tight 3.5 m match. If it misses, fall back to a nearby EXISTING junction
                // (degree >= 2) within 8 m: a busy junction re-centres MORE than 3.5 m as roads join over
                // time, so the host's authoritative coord for a LATER road lands >3.5 m from where this sim
                // first placed the node — the "+nodes on incremental junctions" drift Bruno saw. Requiring
                // degree >= 2 means we never fuse a road that merely ENDS near a junction (a dead-end is
                // degree 1 and won't match wide), so distinct dead-ends stay distinct.
                return best != Entity.Null ? best : bestJunc;
            }
            finally
            {
                nodes.Dispose();
            }
        }

        /// <summary>True when a node has 2+ live connected edges (a real junction, not a dead-end).</summary>
        private bool IsJunctionNode(Entity node)
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
                    if (++live >= 2) { return true; }
                }
            }

            return false;
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
            // Sample the new curve at several points; it is redundant only if EVERY sample sits on
            // SOME existing same-prefab edge. The old check required a SINGLE edge to cover all three
            // of start/mid/end — which failed once a road had been split by junctions (no one piece
            // spans it), letting exact-duplicate roads through as overlapping dup-edges. The bot
            // hunter caught precisely that: re-sending a road already sliced by a T/X made a second
            // stacked copy. Collective coverage (by many pieces) closes it.
            const int N = 5;
            var samples = new float3[N];
            for (int i = 0; i < N; i++)
            {
                samples[i] = MathUtils.Position(bezier, i / (float)(N - 1));
            }

            var covered = new bool[N];
            int coveredCount = 0;

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
                    ComponentType.ReadOnly<Owner>(), // never suppress against a building sub-net
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
                    float3 cmid = MathUtils.Position(c, 0.5f);
                    float reach = MathUtils.Length(c) + 3f;
                    float reachSq = reach * reach;

                    for (int i = 0; i < N; i++)
                    {
                        if (covered[i])
                        {
                            continue;
                        }

                        float dxm = cmid.x - samples[i].x, dzm = cmid.z - samples[i].z;
                        if (dxm * dxm + dzm * dzm > reachSq)
                        {
                            continue; // cheap reject: sample too far from this edge to lie on it
                        }

                        if (DistSqXZ(c, samples[i]) < tolSq)
                        {
                            covered[i] = true;
                            if (++coveredCount == N)
                            {
                                return true; // every sample sits on some existing edge → redundant
                            }
                        }
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return false;
        }

        /// <summary>Returns a LIVE same-prefab edge that CONTAINS this piece: every sample of the piece
        /// lies on it AND it is meaningfully longer than the piece (so it is the un-split ORIGINAL the
        /// sender broke into pieces, not a same-length duplicate). The receiver deletes it while
        /// building the piece — deterministic, unlike the position-addressed NetDelete that missed on
        /// the X-crossings (its FindEdge needs the two node coords within ~3 m). Entity.Null if none.</summary>
        private Entity FindCoveringEdge(Bezier4x3 bezier, Entity netPrefab)
        {
            const float tolSq = 2.25f; // 1.5 m
            const int N = 5;
            float pieceLen = MathUtils.Length(bezier);
            float pieceMidY = MathUtils.Position(bezier, 0.5f).y;
            var samples = new float3[N];
            for (int i = 0; i < N; i++) { samples[i] = MathUtils.Position(bezier, i / (float)(N - 1)); }

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
                    ComponentType.ReadOnly<Owner>(), // never delete a building sub-net
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
                    // Only a longer edge can be the un-split original that spans this piece; a same-
                    // length edge is a duplicate (EdgeExists owns those) — never delete it.
                    if (MathUtils.Length(c) < pieceLen * 1.15f)
                    {
                        continue;
                    }

                    // Different elevation layer (e.g. a bridge over a ground road that only overlaps in
                    // XZ): the un-split original shares this piece's elevation, so reject far-Y edges —
                    // DistSqXZ ignores Y and would otherwise delete the road underneath.
                    if (math.abs(MathUtils.Position(c, 0.5f).y - pieceMidY) > 3f)
                    {
                        continue;
                    }

                    bool all = true;
                    for (int i = 0; i < N; i++)
                    {
                        if (DistSqXZ(c, samples[i]) >= tolSq) { all = false; break; }
                    }

                    if (all) { return cand; }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return Entity.Null;
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

        /// <summary>Mirrors <c>Game.Tools.GenerateEdgesSystem.ConnectionExists</c> (decomp
        /// GenerateEdgesSystem.cs ~line 1744): true when a LIVE (non-Deleted) edge of ANY prefab already
        /// links these two nodes. Prefab-AGNOSTIC — unlike <see cref="EdgeExists"/> — because that is
        /// exactly the check the vanilla generator applies, with no prefab filter, right before
        /// materializing a Permanent course; see <see cref="OverdrawFix"/> for why we need to see this
        /// coming instead of finding out via a silently-vanished edge.</summary>
        private bool AnyLiveConnectionExists(Entity node1, Entity node2)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node1))
            {
                return false;
            }

            DynamicBuffer<ConnectedEdge> connected = EntityManager.GetBuffer<ConnectedEdge>(node1);
            for (int i = 0; i < connected.Length; i++)
            {
                Entity e = connected[i].m_Edge;
                if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e)
                    || !EntityManager.HasComponent<Edge>(e))
                {
                    continue;
                }

                Edge edge = EntityManager.GetComponentData<Edge>(e);
                if ((edge.m_Start == node2 && edge.m_End == node1) || (edge.m_End == node2 && edge.m_Start == node1))
                {
                    return true;
                }
            }

            return false;
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
