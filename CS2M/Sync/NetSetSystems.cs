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
    /// <summary>Global toggle for the NetSet host-authoritative net-graph reconcile. ON by default since v73
    /// (2026-07-09), after validating live in 2-sim — it is the network analogue of the ZoneBlockAuthority
    /// "host owns the grid" reconcile and rides ON TOP of the incremental NetBatch sync as a correction layer.
    /// Kill-switch: env <c>CS2M_NETSET=0</c> opts OUT. Lesson that forced the flip: a real host+client test run
    /// with no env vars set at all was actually running WITHOUT this layer (default was OFF), and roads that
    /// connected onto a save-loaded street were silently DROPped on the client with no cure (a boundary-miss
    /// the incremental sync alone never reconciles) — this system is the fix, so it has to actually run.
    /// Every consumer also checks <c>PlayerStatus.PLAYING</c> before acting, so single-player is unaffected
    /// regardless of this gate.</summary>
    public static class NetSetGate
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_NETSET") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     NetSet DETECTOR (host-only, gated CS2M_NETSET). Accumulates the bounding box of every net edge the
    ///     builder recently touched (Created/Updated) and, every <see cref="SweepEveryNFrames"/> frames, sweeps
    ///     that region (expanded by <see cref="RegionMargin"/>) and ships the SETTLED authoritative node+edge
    ///     SET so the client can force its own graph to match by identity. This is the arête analogue of
    ///     <see cref="ZoneBlockAuthoritySystem"/>: the incremental NetBatch sync still runs; NetSet is the
    ///     host-authoritative correction that converges complex junctions the incremental rebuild folds wrong.
    ///     First-sight: when a client connects, the whole world is marked dirty once (full resend).
    /// </summary>
    public partial class NetSetAuthoritySystem : GameSystemBase
    {
        // ~250 ms @60fps — same cadence as the ZoneAuth sweep. The dirty bbox accumulates between sweeps.
        private const int SweepEveryNFrames = 30;
        private const float RegionMargin = 50f;

        // Slice caps: a command carries every endpoint node its edges reference, so keeping edges <= 48 and
        // nodes <= 48 bounds one packet (spec). The builder flushes on whichever cap is hit first.
        private const int MaxNodesPerCommand = 48;
        private const int MaxEdgesPerCommand = 48;

        // Periodic FULL SWEEP tiling. 512 m tiles (a handful of city blocks — big enough that most edges land
        // whole inside one, small enough that one tile's command set stays modest); 6 tiles per step, and a
        // step fires on the same ~0.5 s cadence as the region sweep (SweepEveryNFrames), so a ~5 km city
        // (~100 tiles) is fully re-asserted in ~17 steps ≈ 8-9 s, then a ~10 s cooldown → one whole-world
        // cycle every ~15-20 s. Cost per step is O(edges) x 6 (a few thousand cheap InBox checks) — negligible.
        private const float FullSweepTileSize = 512f;
        private const int FullSweepTilesPerStep = 6;
        private const int FullSweepCooldownSteps = 20; // steps are ~0.5 s → ~10 s idle between cycles

        private PrefabSystem _prefabSystem;
        private EntityQuery _dirtyEdges;
        private EntityQuery _allEdges;
        private int _frameCounter;
        private int _sweepCount;
        private int _lastConnectedCount = 1;
        private double _lastSendLogAt;

        // Running dirty region (world XZ), accumulated from Created/Updated edges each frame.
        private bool _hasDirty;
        private float _minX, _minZ, _maxX, _maxZ;
        // First-sight: sweep the WHOLE world once on the next scan (a client just joined).
        private bool _worldDirty;

        // Periodic full-sweep state: the world-AABB tile list for the cycle in progress, the next tile to
        // ship, and the idle countdown between cycles.
        private struct Tile { public float MinX, MinZ, MaxX, MaxZ; }
        private System.Collections.Generic.List<Tile> _fullTiles;
        private int _fullTileIndex;
        private int _fullCooldownSteps;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _dirtyEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(), // building/extractor sub-nets grow locally by per-machine
                                                      // RNG — never NetSet's to sweep (Extractor/AreaSubObject own them)
                },
            });
            _allEdges = GetEntityQuery(new EntityQueryDesc
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
                    ComponentType.ReadOnly<Owner>(), // same — sub-nets never enter the authoritative sweep
                },
            });
            CS2M.Log.Info("[NetSet] NetSetAuthoritySystem created");
        }

        protected override void OnUpdate()
        {
            if (!NetSetGate.Enabled)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            // First-sight: a client just joined → mark the whole world dirty once (full resend, adopted
            // idempotently on the client). Mirrors AreaSubObjectDetectorSystem's first-sight resend.
            int connected = NetworkInterface.Instance.PlayerListConnected.Count;
            if (connected > _lastConnectedCount)
            {
                _worldDirty = true;
                CS2M.Log.Info("[NetSet] client joined -> first-sight full resend");
            }

            _lastConnectedCount = connected;

            // Accumulate the dirty region from edges the builder touched this frame (before CleanUpSystem
            // strips Created/Updated at end of frame).
            AccumulateDirty();

            if (++_frameCounter < SweepEveryNFrames)
            {
                return;
            }

            _frameCounter = 0;

            if (connected <= 1)
            {
                // No remote client — nothing to send; drop the accumulated region so a burst drawn while
                // solo doesn't flush the instant someone joins (first-sight handles the join case).
                _hasDirty = false;
                return;
            }

            // (1) DIRTY-REGION sweep — low latency for recent edits.
            if (_hasDirty || _worldDirty)
            {
                float minX, minZ, maxX, maxZ;
                if (_worldDirty)
                {
                    minX = minZ = -1e9f; // whole world (a bbox that contains every edge)
                    maxX = maxZ = 1e9f;
                }
                else
                {
                    minX = _minX - RegionMargin;
                    minZ = _minZ - RegionMargin;
                    maxX = _maxX + RegionMargin;
                    maxZ = _maxZ + RegionMargin;
                }

                _sweepCount++;
                (int rn, int re) = SweepRegion(minX, minZ, maxX, maxZ);
                _hasDirty = false;
                _worldDirty = false;

                if (re > 0)
                {
                    double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
                    if (now - _lastSendLogAt >= 1.0)
                    {
                        _lastSendLogAt = now;
                        CS2M.Log.Info($"[NetSet] SEND bbox=({minX:F0},{minZ:F0})-({maxX:F0},{maxZ:F0}) " +
                                      $"nodes={rn} edges={re} (sweep={_sweepCount})");
                    }
                }
            }

            // (2) PERIODIC FULL SWEEP — eventual convergence EVERYWHERE, not just where edits landed. Covers
            // the stuck-drift class the dirty-region sweep misses: an orphan road (e.g. a save-loaded street
            // the incremental sync failed to replicate) whose region is never re-marked dirty, so it would
            // otherwise never be reconciled. Tiled + throttled so a big city never freezes on one frame.
            FullSweepStep();
        }

        private void AccumulateDirty()
        {
            NativeArray<Entity> edges = _dirtyEdges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in edges)
                {
                    Edge ed = EntityManager.GetComponentData<Edge>(e);
                    if (!EntityManager.HasComponent<Node>(ed.m_Start) || !EntityManager.HasComponent<Node>(ed.m_End))
                    {
                        continue;
                    }

                    Extend(EntityManager.GetComponentData<Node>(ed.m_Start).m_Position);
                    Extend(EntityManager.GetComponentData<Node>(ed.m_End).m_Position);
                }
            }
            finally
            {
                edges.Dispose();
            }
        }

        private void Extend(float3 p)
        {
            if (!_hasDirty)
            {
                _hasDirty = true;
                _minX = _maxX = p.x;
                _minZ = _maxZ = p.z;
                return;
            }

            _minX = math.min(_minX, p.x);
            _maxX = math.max(_maxX, p.x);
            _minZ = math.min(_minZ, p.z);
            _maxZ = math.max(_maxZ, p.z);
        }

        private static bool InBox(float3 p, float minX, float minZ, float maxX, float maxZ)
        {
            return p.x >= minX && p.x <= maxX && p.z >= minZ && p.z <= maxZ;
        }

        /// <summary>One step of the periodic FULL SWEEP: re-assert a few tiles of the world's authoritative
        /// node+edge set so ANY divergence — not just where an edit landed — eventually reconciles. This is the
        /// cure for stuck drift the dirty-region sweep can't reach: an orphan road whose region is never
        /// re-marked dirty (e.g. a new street the player joined to a SAVE-loaded road the incremental sync
        /// failed to replicate). Empty tiles are skipped (ship nothing); the client CREATEs the edges/nodes a
        /// tile carries that it lacks and DELETEs the id-bearing phantoms the tile's set omits (Passo C).</summary>
        private void FullSweepStep()
        {
            if (_fullCooldownSteps > 0)
            {
                _fullCooldownSteps--;
                return;
            }

            // Start a new cycle if none is in progress: snapshot the world AABB and tile it.
            if (_fullTiles == null)
            {
                if (!TryBuildTiles())
                {
                    _fullCooldownSteps = FullSweepCooldownSteps; // no roads to sweep yet — wait and retry
                    return;
                }

                _fullTileIndex = 0;
            }

            // Ship up to FullSweepTilesPerStep tiles this step (empty tiles count toward the budget so cost is
            // bounded regardless of how sparse the map is).
            int processed = 0;
            while (_fullTileIndex < _fullTiles.Count && processed < FullSweepTilesPerStep)
            {
                Tile t = _fullTiles[_fullTileIndex++];
                (int n, int e) = SweepRegion(t.MinX, t.MinZ, t.MaxX, t.MaxZ);
                if (e > 0)
                {
                    CS2M.Log.Info($"[NetSet] FULLSWEEP tile=({t.MinX:F0},{t.MinZ:F0}) nodes={n} edges={e}");
                }

                processed++;
            }

            if (_fullTileIndex >= _fullTiles.Count)
            {
                CS2M.Log.Info($"[NetSet] FULLSWEEP cycle complete (tiles={_fullTiles.Count})");
                _fullTiles = null; // next cycle re-snapshots the AABB (the city may have grown)
                _fullCooldownSteps = FullSweepCooldownSteps;
            }
        }

        /// <summary>Snapshot the world AABB from every live net edge's endpoints and slice it into
        /// <see cref="FullSweepTileSize"/> tiles. Returns false when there are no edges yet (nothing to sweep).
        /// Every edge's MIDPOINT lies within the AABB, and SweepRegion includes an edge whose midpoint is in the
        /// tile, so every edge is covered by at least one tile (a boundary-spanning edge may ship in two — the
        /// client applies idempotently).</summary>
        private bool TryBuildTiles()
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            bool any = false;

            NativeArray<Entity> edges = _allEdges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in edges)
                {
                    Edge ed = EntityManager.GetComponentData<Edge>(e);
                    if (!EntityManager.HasComponent<Node>(ed.m_Start) || !EntityManager.HasComponent<Node>(ed.m_End))
                    {
                        continue;
                    }

                    float3 s = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
                    float3 p = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;
                    minX = math.min(minX, math.min(s.x, p.x));
                    maxX = math.max(maxX, math.max(s.x, p.x));
                    minZ = math.min(minZ, math.min(s.z, p.z));
                    maxZ = math.max(maxZ, math.max(s.z, p.z));
                    any = true;
                }
            }
            finally
            {
                edges.Dispose();
            }

            if (!any)
            {
                return false;
            }

            _fullTiles = new System.Collections.Generic.List<Tile>();
            for (float x = minX; x < maxX; x += FullSweepTileSize)
            {
                for (float z = minZ; z < maxZ; z += FullSweepTileSize)
                {
                    _fullTiles.Add(new Tile { MinX = x, MinZ = z, MaxX = x + FullSweepTileSize, MaxZ = z + FullSweepTileSize });
                }
            }

            return true;
        }

        /// <summary>Sweep one bbox: ship the authoritative node+edge SET of every net edge whose endpoints/
        /// midpoint fall inside it, sliced into cap-bounded commands. Ensures a stable id (node AND edge) on
        /// EVERYTHING it touches — including pre-existing save roads that never had one — so the client can
        /// reconcile by identity (create the orphans it lacks, delete the phantoms it has). Returns
        /// (totalNodes, totalEdges) shipped; does NOT log (the caller logs SEND vs FULLSWEEP).</summary>
        private (int nodes, int edges) SweepRegion(float minX, float minZ, float maxX, float maxZ)
        {
            var builder = new NetSetBuilder(minX, minZ, maxX, maxZ, MaxNodesPerCommand, MaxEdgesPerCommand);
            int totalNodes = 0;
            int totalEdges = 0;

            NativeArray<Entity> edges = _allEdges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in edges)
                {
                    Edge ed = EntityManager.GetComponentData<Edge>(e);
                    if (!EntityManager.HasComponent<Node>(ed.m_Start) || !EntityManager.HasComponent<Node>(ed.m_End))
                    {
                        continue;
                    }

                    float3 sPos = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
                    float3 ePos = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;
                    float3 mid = (sPos + ePos) * 0.5f;

                    // In-region if either endpoint OR the midpoint falls in the bbox — a long edge crossing
                    // the region with both nodes outside still counts (its geometry passes through).
                    if (!InBox(sPos, minX, minZ, maxX, maxZ)
                        && !InBox(ePos, minX, minZ, maxX, maxZ)
                        && !InBox(mid, minX, minZ, maxX, maxZ))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    // Stable identities: the edge and BOTH endpoints (endpoints are included in the shipped
                    // node set even when they fall outside the bbox, or the client couldn't resolve the end).
                    ulong edgeId = CS2M_EdgeSyncIds.Ensure(EntityManager, e);
                    ulong startId = CS2M_NodeSyncIds.Ensure(EntityManager, ed.m_Start);
                    ulong endId = CS2M_NodeSyncIds.Ensure(EntityManager, ed.m_End);
                    if (edgeId == 0 || startId == 0 || endId == 0)
                    {
                        continue; // allocation never returns 0; defensive
                    }

                    Bezier4x3 bez = EntityManager.GetComponentData<Curve>(e).m_Bezier;

                    // Road half-aligned latch (0 for a non-road net without a Road component — client guards).
                    byte roadFlags = EntityManager.HasComponent<Road>(e)
                        ? (byte) EntityManager.GetComponentData<Road>(e).m_Flags
                        : (byte) 0;

                    // Flush before adding if this edge (and its up-to-2 new endpoints) would overflow a cap.
                    int newNodes = (builder.ContainsNode(startId) ? 0 : 1) + (builder.ContainsNode(endId) ? 0 : 1);
                    if (builder.EdgeCount > 0
                        && (builder.EdgeCount + 1 > MaxEdgesPerCommand
                            || builder.NodeCount + newNodes > MaxNodesPerCommand))
                    {
                        totalNodes += builder.NodeCount;
                        totalEdges += builder.EdgeCount;
                        Command.SendToAll?.Invoke(builder.Build());
                        builder = new NetSetBuilder(minX, minZ, maxX, maxZ, MaxNodesPerCommand, MaxEdgesPerCommand);
                    }

                    builder.AddNode(startId, sPos);
                    builder.AddNode(endId, ePos);
                    builder.AddEdge(edgeId, startId, endId, prefab.GetType().Name, prefab.name, bez, roadFlags);
                }
            }
            finally
            {
                edges.Dispose();
            }

            if (builder.EdgeCount > 0)
            {
                totalNodes += builder.NodeCount;
                totalEdges += builder.EdgeCount;
                Command.SendToAll?.Invoke(builder.Build());
            }

            return (totalNodes, totalEdges);
        }

        /// <summary>Accumulates ONE command's worth of nodes+edges into flat parallel arrays, deduping nodes
        /// by id, and builds the <see cref="NetSetCommand"/>. Mirrors AreaSubObjectDetectorSystem.OpBatch.</summary>
        private sealed class NetSetBuilder
        {
            private readonly float _minX, _minZ, _maxX, _maxZ;
            private readonly int _maxNodes, _maxEdges;

            private readonly Dictionary<ulong, int> _nodeIndex = new Dictionary<ulong, int>();
            private readonly List<ulong> _nodeIds = new List<ulong>();
            private readonly List<float> _nx = new List<float>();
            private readonly List<float> _ny = new List<float>();
            private readonly List<float> _nz = new List<float>();

            private readonly List<ulong> _edgeIds = new List<ulong>();
            private readonly List<ulong> _edgeStart = new List<ulong>();
            private readonly List<ulong> _edgeEnd = new List<ulong>();
            private readonly List<string> _edgeType = new List<string>();
            private readonly List<string> _edgeName = new List<string>();
            private readonly List<byte> _roadFlags = new List<byte>();
            private readonly List<float> _ax = new List<float>(), _ay = new List<float>(), _az = new List<float>();
            private readonly List<float> _bx = new List<float>(), _by = new List<float>(), _bz = new List<float>();
            private readonly List<float> _cx = new List<float>(), _cy = new List<float>(), _cz = new List<float>();
            private readonly List<float> _dx = new List<float>(), _dy = new List<float>(), _dz = new List<float>();

            public NetSetBuilder(float minX, float minZ, float maxX, float maxZ, int maxNodes, int maxEdges)
            {
                _minX = minX; _minZ = minZ; _maxX = maxX; _maxZ = maxZ;
                _maxNodes = maxNodes; _maxEdges = maxEdges;
            }

            public int NodeCount => _nodeIds.Count;
            public int EdgeCount => _edgeIds.Count;
            public bool ContainsNode(ulong id) => _nodeIndex.ContainsKey(id);

            public void AddNode(ulong id, float3 pos)
            {
                if (_nodeIndex.ContainsKey(id))
                {
                    return;
                }

                _nodeIndex[id] = _nodeIds.Count;
                _nodeIds.Add(id);
                _nx.Add(pos.x); _ny.Add(pos.y); _nz.Add(pos.z);
            }

            public void AddEdge(ulong edgeId, ulong startId, ulong endId, string type, string name, Bezier4x3 bez, byte roadFlags)
            {
                _edgeIds.Add(edgeId);
                _edgeStart.Add(startId);
                _edgeEnd.Add(endId);
                _edgeType.Add(type);
                _edgeName.Add(name);
                _roadFlags.Add(roadFlags);
                _ax.Add(bez.a.x); _ay.Add(bez.a.y); _az.Add(bez.a.z);
                _bx.Add(bez.b.x); _by.Add(bez.b.y); _bz.Add(bez.b.z);
                _cx.Add(bez.c.x); _cy.Add(bez.c.y); _cz.Add(bez.c.z);
                _dx.Add(bez.d.x); _dy.Add(bez.d.y); _dz.Add(bez.d.z);
            }

            public NetSetCommand Build()
            {
                return new NetSetCommand
                {
                    MinX = _minX, MinZ = _minZ, MaxX = _maxX, MaxZ = _maxZ,
                    NodeIds = _nodeIds.ToArray(),
                    NX = _nx.ToArray(), NY = _ny.ToArray(), NZ = _nz.ToArray(),
                    EdgeIds = _edgeIds.ToArray(),
                    EdgeStartId = _edgeStart.ToArray(),
                    EdgeEndId = _edgeEnd.ToArray(),
                    EdgePrefabType = _edgeType.ToArray(),
                    EdgePrefab = _edgeName.ToArray(),
                    EdgeRoadFlags = _roadFlags.ToArray(),
                    Ax = _ax.ToArray(), Ay = _ay.ToArray(), Az = _az.ToArray(),
                    Bx = _bx.ToArray(), By = _by.ToArray(), Bz = _bz.ToArray(),
                    Cx = _cx.ToArray(), Cy = _cy.ToArray(), Cz = _cz.ToArray(),
                    Dx = _dx.ToArray(), Dy = _dy.ToArray(), Dz = _dz.ToArray(),
                };
            }
        }
    }

    /// <summary>
    ///     NetSet APPLIER (client-only, gated CS2M_NETSET). Drains one region command per frame and forces the
    ///     local net graph to match the host's authoritative SET by identity, in strict order: (A) nodes —
    ///     resolve by <see cref="CS2M_NodeSyncId"/> and nudge to the host coord (<see cref="NetGraphSafety"/>,
    ///     I4) or create; (B) edges — resolve by <see cref="CS2M_EdgeSyncId"/>/node-pair identity and heal /
    ///     replace / create (vanilla CreationDefinition path, I9); (C) conservative phantom deletion inside the
    ///     region (only id-bearing entities, I8). Runs at <c>UpdateBefore(Modification1)</c> AFTER
    ///     <see cref="NetBatchApplySystem"/> so it is the correction layer over the incremental sync.
    /// </summary>
    public partial class NetSetApplySystem : GameSystemBase
    {
        private const int ParkTtl = 120;
        private const int IdStampTtl = 60;
        // 1e-3 m — near-exact node equality. NodeAlign is deterministic given identical connected curves (it
        // errs ~1e-6 m), so any looser leftover (0.05 m) is enough to flip a sub-metre BlockSystem decision
        // (floor((len+0.1)/8) → 6x6 vs 7x6; FindContinuousEdge 0.01 m threshold) and shift zones ~1 m. Above
        // this the node is dragged to the host coord via MoveNodeWithCurves (I4). Idempotent below it.
        private const float NodeMoveThreshold = 1e-3f;

        // 1e-4 m — effectively "rewrite unless the bezier is already bit-identical". The target is fixed (the
        // host curve) and NodeAlign is deterministic, so this converges and stops (no churn). Any looser leaves
        // the sub-métrico curve difference that lets NodeAlign settle the shared node ~0.1-0.2 m apart.
        private const float CurveMatchTolerance = 1e-4f;

        // How close an unclaimed local net node must sit to a host node's authoritative position to be ADOPTED
        // onto the host id (instead of creating a duplicate). A node the incremental sync built in this region
        // is at most a junction-recentre / sub-metre-derivation apart from the host's coord; 3 m absorbs that
        // without reaching across to an unrelated intersection (same order as NetBatch's 3.5 m tight tier).
        private const float NodeAdoptRadius = 3f;

        private PrefabSystem _prefabSystem;
        private EntityQuery _liveNodes;
        private EntityQuery _liveEdges;

        // Edge definitions injected this frame (vanilla path, EmitCourse) — consumed by GenerateNodes/Edges
        // this same frame, destroyed at the TOP of next OnUpdate. Exact mirror of NetBatchApplySystem.
        private readonly List<Entity> _pendingDefinitions = new List<Entity>();

        /// <summary>An edge whose materialized entity must be stamped with the host's <see cref="CS2M_EdgeSyncId"/>
        /// once GenerateEdges has built it (the edge doesn't exist the frame its course is emitted). Resolved by
        /// node-pair identity at the top of a following OnUpdate — mirror of NetBatch's _pendingOrderFixes.</summary>
        private struct PendingIdStamp
        {
            public ulong EdgeId;
            public ulong StartId;
            public ulong EndId;
            public bool HasRoadFlags;
            public byte RoadFlags;
            public int Age;
        }

        private readonly List<PendingIdStamp> _pendingIdStamps = new List<PendingIdStamp>();

        /// <summary>An edge whose endpoint nodes couldn't be resolved yet (a sibling command creating them may
        /// still be in flight) — retried self-contained for up to <see cref="ParkTtl"/> frames.</summary>
        private struct ParkedEdge
        {
            public ulong EdgeId;
            public ulong StartId;
            public ulong EndId;
            public string PrefabType;
            public string PrefabName;
            public Bezier4x3 Bezier;
            public bool HasRoadFlags;
            public byte RoadFlags;
            public int Age;
        }

        private readonly List<ParkedEdge> _parkedEdges = new List<ParkedEdge>();

        // Phantom nodes whose Deleted is DEFERRED one frame (crash #4, mirror of NetBatchApplySystem.
        // _pendingOrphanNodes): deleting a node the SAME frame its junction lost its arms opens a recycle
        // window BlockSystem.UpdateBlocksJob@Mod4 can walk. Drained at the TOP of OnUpdate — once
        // ReferencesSystem has revalidated the buffers — and only if the node is STILL orphaned.
        private readonly List<Entity> _pendingOrphanDeletes = new List<Entity>();

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
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(), // building/extractor sub-nets — never NetSet's to touch
                },
            });
            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(), // same — sub-nets are excluded from phantom deletion too
                },
            });
            CS2M.Log.Info("[NetSet] NetSetApplySystem created");
        }

        protected override void OnUpdate()
        {
            // Definitions injected last frame were consumed by GenerateNodes/Edges already — clean up.
            // Runs before the PLAYING gate (like NetBatchApplySystem) so nothing ever leaks past a disconnect.
            for (int i = 0; i < _pendingDefinitions.Count; i++)
            {
                if (EntityManager.Exists(_pendingDefinitions[i]))
                {
                    EntityManager.DestroyEntity(_pendingDefinitions[i]);
                }
            }

            _pendingDefinitions.Clear();

            // Orphan-node deletes deferred from a previous frame (crash #4). ReferencesSystem has now
            // revalidated the junction buffers; delete only if the node is STILL orphaned (0 live arms), else
            // keep it. Runs before the PLAYING gate like the cleanups above so nothing leaks past a disconnect.
            for (int i = _pendingOrphanDeletes.Count - 1; i >= 0; i--)
            {
                Entity node = _pendingOrphanDeletes[i];
                _pendingOrphanDeletes.RemoveAt(i);

                if (!EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node) || HasLiveEdge(node))
                {
                    continue;
                }

                EntityManager.AddComponent<Deleted>(node);
            }

            // Stamp CS2M_EdgeSyncId onto edges materialized from a course emitted on a previous frame. Resolve
            // by node-pair identity (the endpoints were registered when the command applied), then Register.
            for (int i = _pendingIdStamps.Count - 1; i >= 0; i--)
            {
                PendingIdStamp s = _pendingIdStamps[i];
                if (FindEdgeByNodeIds(s.StartId, s.EndId, out Entity edge))
                {
                    CS2M_EdgeSyncIds.Register(EntityManager, edge, s.EdgeId);

                    // The edge only just materialized (its Road component didn't exist when the course was
                    // emitted), so apply the host's half-aligned bits NOW — a freshly-created course starts
                    // with half-aligned=0 and would otherwise shift the zone half-cell vs the host.
                    if (s.HasRoadFlags
                        && CS2M_NodeSyncIds.TryResolve(EntityManager, s.StartId, out Entity sN)
                        && CS2M_NodeSyncIds.TryResolve(EntityManager, s.EndId, out Entity eN))
                    {
                        int dummy = 0;
                        ApplyRoadFlags(edge, sN, eN, s.RoadFlags, ref dummy);
                    }

                    _pendingIdStamps.RemoveAt(i);
                }
                else
                {
                    s.Age++;
                    if (s.Age >= IdStampTtl)
                    {
                        _pendingIdStamps.RemoveAt(i);
                    }
                    else
                    {
                        _pendingIdStamps[i] = s;
                    }
                }
            }

            if (!NetSetGate.Enabled)
            {
                RemoteNetSetQueue.Clear();
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                RemoteNetSetQueue.Clear();
                return;
            }

            // Apply on the client only — the host authored these; applying on the host would double-mutate.
            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                RemoteNetSetQueue.Clear();
                return;
            }

            // Retry parked edges (endpoints that hadn't resolved on an earlier frame).
            RetryParked();

            if (RemoteNetSetQueue.TryDequeue(out NetSetCommand cmd))
            {
                try
                {
                    ApplyCommand(cmd);
                }
                catch (System.Exception ex)
                {
                    CS2M.Log.Info($"[Guard] NetSet apply failed (survived): {ex}");
                }
            }
        }

        private void ApplyCommand(NetSetCommand cmd)
        {
            if (cmd.NodeIds == null || cmd.EdgeIds == null)
            {
                return;
            }

            // Host's authoritative id sets for this region (for the conservative phantom-deletion pass).
            var hostNodeIds = new HashSet<ulong>(cmd.NodeIds);
            var hostEdgeIds = new HashSet<ulong>(cmd.EdgeIds);

            // Each new node is created from the archetype of an INCIDENT edge's prefab (a net node has no
            // prefab of its own on the wire — it inherits the road's node archetype, exactly how the game
            // mints endpoint nodes from an edge definition). Map endpoint id -> (type,name) from the edges.
            var nodePrefab = new Dictionary<ulong, KeyValuePair<string, string>>();
            for (int i = 0; i < cmd.EdgeIds.Length; i++)
            {
                var pt = new KeyValuePair<string, string>(cmd.EdgePrefabType[i], cmd.EdgePrefab[i]);
                if (!nodePrefab.ContainsKey(cmd.EdgeStartId[i])) { nodePrefab[cmd.EdgeStartId[i]] = pt; }
                if (!nodePrefab.ContainsKey(cmd.EdgeEndId[i])) { nodePrefab[cmd.EdgeEndId[i]] = pt; }
            }

            // ---- Passo A: NODES — resolve / adopt-by-pos / create; nudge drifted ----
            // The incremental NetBatch path may have ALREADY built this region's nodes under a DIFFERENT
            // identity (a node born of a client-side split, or an id that diverged), so the host's NetSet id
            // won't TryResolve to it. Creating a new node then DUPLICATES it — the +14 ghost-node divergence
            // the 2-sim statediff caught. So: (pass 1) claim every node the host id resolves to exactly, THEN
            // (pass 2) for each still-unresolved host id, ADOPT the nearest unclaimed local node within
            // NodeAdoptRadius before ever creating one. Two passes so an exact-id node is claimed before any
            // position adoption could steal it (a local node is adopted at most once per command via `adopted`).
            var adopted = new HashSet<Entity>();
            var handled = new bool[cmd.NodeIds.Length];
            int createdNodes = 0, movedNodes = 0, adoptedNodes = 0;

            // Pass 1 — exact identity. Claim + nudge every node whose host id resolves locally.
            for (int i = 0; i < cmd.NodeIds.Length; i++)
            {
                ulong id = cmd.NodeIds[i];
                var pos = new float3(cmd.NX[i], cmd.NY[i], cmd.NZ[i]);

                if (!CS2M_NodeSyncIds.TryResolve(EntityManager, id, out Entity node))
                {
                    continue;
                }

                adopted.Add(node); // protect from a position-adoption by another host id in pass 2
                MoveIfDrifted(node, pos, ref movedNodes);
                CS2M_NodeSyncIds.SetAuthPos(id, pos);
                handled[i] = true;
            }

            // Pass 2 — adopt-by-pos, else create. Runs only after every exact-id node is already claimed.
            for (int i = 0; i < cmd.NodeIds.Length; i++)
            {
                if (handled[i])
                {
                    continue;
                }

                ulong id = cmd.NodeIds[i];
                var pos = new float3(cmd.NX[i], cmd.NY[i], cmd.NZ[i]);

                // Adopt the nearest unclaimed local node (incremental-built, with or WITHOUT its own id) onto
                // the host's id instead of duplicating it.
                if (FindNearbyNode(pos, adopted, out Entity local))
                {
                    adopted.Add(local);
                    CS2M_NodeSyncIds.Register(EntityManager, local, id);
                    MoveIfDrifted(local, pos, ref movedNodes);
                    CS2M_NodeSyncIds.SetAuthPos(id, pos);
                    adoptedNodes++;
                    CS2M.Log.Verbose($"[NetSet] node adopted-by-pos id={id} entity={local.Index}");
                    continue;
                }

                // Genuinely missing — create from the incident edge's node archetype.
                if (!nodePrefab.TryGetValue(id, out KeyValuePair<string, string> pn))
                {
                    // A node with no incident edge in this command — can't derive an archetype and a net node
                    // never exists without edges anyway. Skip (conservative); an edge referencing it will park.
                    continue;
                }

                Entity created = CreateNode(pn.Key, pn.Value, pos, id);
                if (created != Entity.Null)
                {
                    adopted.Add(created);
                    CS2M_NodeSyncIds.SetAuthPos(id, pos);
                    createdNodes++;
                }
            }

            // ---- Passo B: EDGES (heal/replace/create by identity) ----
            int createdEdges = 0, replacedEdges = 0, adoptedEdges = 0, parkedEdges = 0, curveFixed = 0, roadFixed = 0;
            for (int i = 0; i < cmd.EdgeIds.Length; i++)
            {
                ulong edgeId = cmd.EdgeIds[i];
                ulong startId = cmd.EdgeStartId[i];
                ulong endId = cmd.EdgeEndId[i];
                bool hasRoadFlags = cmd.EdgeRoadFlags != null && i < cmd.EdgeRoadFlags.Length;
                byte hostRoadFlags = hasRoadFlags ? cmd.EdgeRoadFlags[i] : (byte) 0;

                if (!CS2M_NodeSyncIds.TryResolve(EntityManager, startId, out Entity startNode)
                    || !CS2M_NodeSyncIds.TryResolve(EntityManager, endId, out Entity endNode)
                    || startNode == endNode)
                {
                    // Endpoint(s) not resolvable yet — park this edge self-contained and retry next frame.
                    ParkEdge(cmd, i);
                    parkedEdges++;
                    continue;
                }

                var bez = new Bezier4x3(
                    new float3(cmd.Ax[i], cmd.Ay[i], cmd.Az[i]),
                    new float3(cmd.Bx[i], cmd.By[i], cmd.Bz[i]),
                    new float3(cmd.Cx[i], cmd.Cy[i], cmd.Cz[i]),
                    new float3(cmd.Dx[i], cmd.Dy[i], cmd.Dz[i]));

                if (CS2M_EdgeSyncIds.TryResolve(EntityManager, edgeId, out Entity localEdge))
                {
                    Edge ed = EntityManager.GetComponentData<Edge>(localEdge);
                    bool sameNodes = (ed.m_Start == startNode && ed.m_End == endNode)
                                     || (ed.m_Start == endNode && ed.m_End == startNode);
                    if (sameNodes)
                    {
                        // Known edge connecting the right two nodes — keep it, but FORCE the host's full curve
                        // so both machines feed NodeAlign identical connected-edge geometry and re-derive the
                        // SAME node position (topology already matched, but sub-metre curve differences let
                        // NodeAlign settle the shared node 0.1-0.2 m apart — the residual roads/zones drift).
                        ForceCurveIfDiff(localEdge, startNode, endNode, bez, ref curveFixed);
                        if (hasRoadFlags) { ApplyRoadFlags(localEdge, startNode, endNode, hostRoadFlags, ref roadFixed); }
                        continue;
                    }

                    // Known edge id but it connects DIFFERENT nodes — the host restructured this edge. Delete
                    // the stale local one (I8) and recreate onto the right endpoints below.
                    DeleteEdge(localEdge);
                    replacedEdges++;
                }
                else if (FindEdgeByNodeIds(startId, endId, out Entity byPair))
                {
                    // No edge id locally, but an edge already connects these two nodes (built by the
                    // incremental sync, which doesn't stamp edge ids) — ADOPT it under the host's id so future
                    // reconciles address it by identity and phantom-deletion can ever reach it (I8 policy).
                    // Then force the host's curve AND half-aligned road flags (same reasons as above).
                    CS2M_EdgeSyncIds.Register(EntityManager, byPair, edgeId);
                    ForceCurveIfDiff(byPair, startNode, endNode, bez, ref curveFixed);
                    if (hasRoadFlags) { ApplyRoadFlags(byPair, startNode, endNode, hostRoadFlags, ref roadFixed); }
                    adoptedEdges++;
                    continue;
                }

                // Create the edge via the vanilla definition path (I9). Its Road component doesn't exist yet, so
                // the host's half-aligned flags are applied by the deferred id-stamp drain once it materializes.
                if (EmitCourse(cmd.EdgePrefabType[i], cmd.EdgePrefab[i], bez, startNode, endNode))
                {
                    _pendingIdStamps.Add(new PendingIdStamp
                    {
                        EdgeId = edgeId, StartId = startId, EndId = endId,
                        HasRoadFlags = hasRoadFlags, RoadFlags = hostRoadFlags, Age = 0,
                    });
                    createdEdges++;
                }
            }

            // ---- Passo C: conservative phantom deletion within the region ----
            int deletedEdges = 0, deletedNodes = 0;
            DeletePhantomEdges(cmd, hostEdgeIds, ref deletedEdges);
            DeletePhantomNodes(cmd, hostNodeIds, ref deletedNodes);

            CS2M.Log.Info($"[NetSet] RECONCILE nodes={{created:{createdNodes},adopted:{adoptedNodes},moved:{movedNodes}}} " +
                          $"edges={{created:{createdEdges},adopted:{adoptedEdges},replaced:{replacedEdges},curveFixed:{curveFixed},roadFixed:{roadFixed}}} " +
                          $"parked={parkedEdges} deleted={{edges:{deletedEdges},nodes:{deletedNodes}}}");
        }

        private void RetryParked()
        {
            for (int i = _parkedEdges.Count - 1; i >= 0; i--)
            {
                ParkedEdge p = _parkedEdges[i];

                if (!CS2M_NodeSyncIds.TryResolve(EntityManager, p.StartId, out Entity startNode)
                    || !CS2M_NodeSyncIds.TryResolve(EntityManager, p.EndId, out Entity endNode)
                    || startNode == endNode)
                {
                    p.Age++;
                    if (p.Age >= ParkTtl)
                    {
                        CS2M.Log.Info($"[NetSet] DROP parked edge id={p.EdgeId} (endpoints unresolved {p.Age}f)");
                        _parkedEdges.RemoveAt(i);
                    }
                    else
                    {
                        _parkedEdges[i] = p;
                    }

                    continue;
                }

                _parkedEdges.RemoveAt(i);

                // Endpoints resolved now — same heal/adopt/create decision as the inline path.
                if (CS2M_EdgeSyncIds.TryResolve(EntityManager, p.EdgeId, out Entity localEdge))
                {
                    Edge ed = EntityManager.GetComponentData<Edge>(localEdge);
                    bool sameNodes = (ed.m_Start == startNode && ed.m_End == endNode)
                                     || (ed.m_Start == endNode && ed.m_End == startNode);
                    if (sameNodes)
                    {
                        int dummy = 0;
                        ForceCurveIfDiff(localEdge, startNode, endNode, p.Bezier, ref dummy);
                        if (p.HasRoadFlags) { ApplyRoadFlags(localEdge, startNode, endNode, p.RoadFlags, ref dummy); }
                        continue;
                    }

                    DeleteEdge(localEdge);
                }
                else if (FindEdgeByNodeIds(p.StartId, p.EndId, out Entity byPair))
                {
                    CS2M_EdgeSyncIds.Register(EntityManager, byPair, p.EdgeId);
                    int dummy = 0;
                    ForceCurveIfDiff(byPair, startNode, endNode, p.Bezier, ref dummy);
                    if (p.HasRoadFlags) { ApplyRoadFlags(byPair, startNode, endNode, p.RoadFlags, ref dummy); }
                    continue;
                }

                if (EmitCourse(p.PrefabType, p.PrefabName, p.Bezier, startNode, endNode))
                {
                    _pendingIdStamps.Add(new PendingIdStamp
                    {
                        EdgeId = p.EdgeId, StartId = p.StartId, EndId = p.EndId,
                        HasRoadFlags = p.HasRoadFlags, RoadFlags = p.RoadFlags, Age = 0,
                    });
                }
            }
        }

        private void ParkEdge(NetSetCommand cmd, int i)
        {
            _parkedEdges.Add(new ParkedEdge
            {
                EdgeId = cmd.EdgeIds[i],
                StartId = cmd.EdgeStartId[i],
                EndId = cmd.EdgeEndId[i],
                PrefabType = cmd.EdgePrefabType[i],
                PrefabName = cmd.EdgePrefab[i],
                Bezier = new Bezier4x3(
                    new float3(cmd.Ax[i], cmd.Ay[i], cmd.Az[i]),
                    new float3(cmd.Bx[i], cmd.By[i], cmd.Bz[i]),
                    new float3(cmd.Cx[i], cmd.Cy[i], cmd.Cz[i]),
                    new float3(cmd.Dx[i], cmd.Dy[i], cmd.Dz[i])),
                HasRoadFlags = cmd.EdgeRoadFlags != null && i < cmd.EdgeRoadFlags.Length,
                RoadFlags = cmd.EdgeRoadFlags != null && i < cmd.EdgeRoadFlags.Length ? cmd.EdgeRoadFlags[i] : (byte) 0,
                Age = 0,
            });
        }

        /// <summary>Phantom EDGE deletion (I8): a local edge that carries a <see cref="CS2M_EdgeSyncId"/> whose
        /// id the host's set does NOT contain, and whose midpoint lies inside the region bbox, is deleted by
        /// <c>AddComponent&lt;Deleted&gt;</c> (components kept for ReferencesSystem@Mod2B). An edge WITHOUT an
        /// id is never touched — it may be pre-existing save content the region doesn't own (conservative).</summary>
        private void DeletePhantomEdges(NetSetCommand cmd, HashSet<ulong> hostEdgeIds, ref int deleted)
        {
            NativeArray<Entity> arr = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in arr)
                {
                    if (!EntityManager.HasComponent<CS2M_EdgeSyncId>(e))
                    {
                        continue; // never delete an un-identified edge (may be uncovered save content)
                    }

                    if (EntityManager.HasComponent<Owner>(e))
                    {
                        continue; // defense in depth: _liveEdges already excludes Owner, but a future refactor
                                  // of the query must not resurrect the sub-net create/delete loop — a building
                                  // or extractor sub-net is grown locally by per-machine RNG and is managed by
                                  // the Extractor/AreaSubObject sync, never by NetSet.
                    }

                    ulong id = EntityManager.GetComponentData<CS2M_EdgeSyncId>(e).m_Id;
                    if (id == 0 || hostEdgeIds.Contains(id))
                    {
                        continue;
                    }

                    Bezier4x3 bez = EntityManager.GetComponentData<Curve>(e).m_Bezier;
                    float3 mid = (bez.a + bez.d) * 0.5f;
                    if (!InBox(mid, cmd))
                    {
                        continue;
                    }

                    DeleteEdge(e);
                    deleted++;
                }
            }
            finally
            {
                arr.Dispose();
            }
        }

        /// <summary>Phantom NODE deletion (I8): a local node with a <see cref="CS2M_NodeSyncId"/> the host's set
        /// lacks, inside the bbox, and with NO live connected edges remaining, is deleted. Edges first (above),
        /// then nodes — a node only orphans once its phantom arms are gone. Un-identified nodes are never
        /// touched (conservative).</summary>
        private void DeletePhantomNodes(NetSetCommand cmd, HashSet<ulong> hostNodeIds, ref int deleted)
        {
            NativeArray<Entity> arr = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in arr)
                {
                    if (!EntityManager.HasComponent<CS2M_NodeSyncId>(n))
                    {
                        continue;
                    }

                    if (EntityManager.HasComponent<Owner>(n))
                    {
                        continue; // defense in depth: _liveNodes already excludes Owner — see DeletePhantomEdges
                    }

                    ulong id = EntityManager.GetComponentData<CS2M_NodeSyncId>(n).m_Id;
                    if (id == 0 || hostNodeIds.Contains(id))
                    {
                        continue;
                    }

                    float3 pos = EntityManager.GetComponentData<Node>(n).m_Position;
                    if (!InBox(pos, cmd))
                    {
                        continue;
                    }

                    if (HasLiveEdge(n))
                    {
                        continue; // still carries arms — not an orphan; leave it for a later sweep
                    }

                    // DEFER the delete one frame (crash #4) — a node deleted the same frame its phantom arms
                    // were Deleted can be walked by BlockSystem@Mod4 in the recycle window. Re-checked next frame.
                    if (!_pendingOrphanDeletes.Contains(n))
                    {
                        _pendingOrphanDeletes.Add(n);
                        deleted++;
                    }
                }
            }
            finally
            {
                arr.Dispose();
            }
        }

        /// <summary>Move a node (dragging its curves — I4, via <see cref="NetGraphSafety"/>) to the host's
        /// authoritative coord when it has drifted past <see cref="NodeMoveThreshold"/>. Shared by pass 1
        /// (exact-id) and pass 2 (adopt-by-pos) so both reconcile geometry the same way.</summary>
        private void MoveIfDrifted(Entity node, float3 pos, ref int movedNodes)
        {
            if (math.distance(EntityManager.GetComponentData<Node>(node).m_Position, pos) > NodeMoveThreshold)
            {
                NetGraphSafety.MoveNodeWithCurves(EntityManager, node, pos);
                movedNodes++;
            }
        }

        /// <summary>Force a reconciled edge's Curve to the host's full bezier when it differs by more than
        /// <see cref="CurveMatchTolerance"/> at any control point, then mark the edge AND its two endpoint nodes
        /// Updated+BatchesUpdated. This is the fix for the residual sub-metre NODE drift: a node's position is
        /// DERIVED by NodeAlignSystem from the SET of connected-edge curves (decomp NodeAlignSystem.cs:132/168),
        /// so two machines with matching topology but sub-different curves settle the shared node 0.1-0.2 m
        /// apart. Making every connected curve bit-identical to the host makes NodeAlign re-derive the SAME
        /// position on both sides. Read-modify-write of Curve preserves any other fields.</summary>
        private void ForceCurveIfDiff(Entity edge, Entity startNode, Entity endNode, Bezier4x3 host, ref int curveFixed)
        {
            if (!EntityManager.HasComponent<Curve>(edge))
            {
                return;
            }

            Curve curve = EntityManager.GetComponentData<Curve>(edge);
            if (BezierClose(curve.m_Bezier, host, CurveMatchTolerance))
            {
                return; // already converged — no write, no re-derive
            }

            // I4: the host bezier's a/d ARE the host's start/end node coords, which Passo A already stamped onto
            // startNode/endNode BEFORE this runs — so curve.a == node(start).m_Position and curve.d ==
            // node(end).m_Position still hold after this write (host geometry is self-consistent). We never set
            // a curve whose ends disagree with the (host-reconciled) node positions.
            curve.m_Bezier = host;
            curve.m_Length = MathUtils.Length(host);
            EntityManager.SetComponentData(edge, curve); // non-structural

            // I9: an edge marked Updated is only revalidated (ConnectedEdge/NodeAlign re-derive) when BOTH its
            // endpoint nodes are also Updated — the game only re-walks a node's connected edges for a node it
            // sees Updated. Mark all three so NodeAlign re-runs on the shared nodes with the corrected curves.
            MarkUpdated(edge);
            MarkUpdated(startNode);
            MarkUpdated(endNode);
            curveFixed++;
        }

        private static bool BezierClose(Bezier4x3 a, Bezier4x3 b, float tol)
        {
            return math.distance(a.a, b.a) <= tol && math.distance(a.b, b.b) <= tol
                && math.distance(a.c, b.c) <= tol && math.distance(a.d, b.d) <= tol;
        }

        /// <summary>Adopt the host's StartHalfAligned/EndHalfAligned bits onto a reconciled road edge, preserving
        /// the machine-local lighting bits (IsLit/AlwaysLit/LightsOff), then mark the edge (and its endpoint
        /// nodes, I9) Updated so BlockSystem re-derives the zone half-cell with the correct alignment (decomp
        /// BlockSystem.cs:191-192/780-785). The half-aligned bits are a per-machine build-time LATCH (decomp
        /// GenerateEdgesSystem.cs:1061/1070 — length parity XOR the prior value) that forcing the curve does NOT
        /// cure, so they must be copied explicitly. No-op on a non-road net (no Road component). Idempotent: if
        /// the masked bits already match, no write and no Updated (anti-churn).</summary>
        private void ApplyRoadFlags(Entity edge, Entity startNode, Entity endNode, byte hostFlags, ref int roadFixed)
        {
            if (!EntityManager.HasComponent<Road>(edge))
            {
                return; // fence / pipe / power line — no half-aligned concept
            }

            const Game.Net.RoadFlags halfMask = Game.Net.RoadFlags.StartHalfAligned | Game.Net.RoadFlags.EndHalfAligned;
            Road road = EntityManager.GetComponentData<Road>(edge);
            Game.Net.RoadFlags want = (road.m_Flags & ~halfMask) | ((Game.Net.RoadFlags) hostFlags & halfMask);
            if (want == road.m_Flags)
            {
                return; // already converged — no write, no re-derive (anti-oscillation)
            }

            road.m_Flags = want;
            EntityManager.SetComponentData(edge, road); // non-structural

            MarkUpdated(edge);
            MarkUpdated(startNode);
            MarkUpdated(endNode);
            roadFixed++;
        }

        private void MarkUpdated(Entity e)
        {
            if (!EntityManager.HasComponent<Updated>(e)) { EntityManager.AddComponent<Updated>(e); }
            if (!EntityManager.HasComponent<BatchesUpdated>(e)) { EntityManager.AddComponent<BatchesUpdated>(e); }
        }

        /// <summary>Nearest LIVE net node within <see cref="NodeAdoptRadius"/> of <paramref name="pos"/> that is
        /// not already <paramref name="claimed"/> by another host id this command. Unlike
        /// <see cref="NetBatchApplySystem"/>.FindNodeStrict this DELIBERATELY accepts an id-bearing node — the
        /// whole point is to adopt an incremental-built node that carries a DIFFERENT identity onto the host's
        /// NetSet id, instead of minting a duplicate (the ghost-node divergence). Ambiguity is bounded by the
        /// tight radius + nearest pick + the per-command <paramref name="claimed"/> set.</summary>
        private bool FindNearbyNode(float3 pos, HashSet<Entity> claimed, out Entity node)
        {
            node = Entity.Null;
            float bestSq = NodeAdoptRadius * NodeAdoptRadius;
            NativeArray<Entity> arr = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in arr)
                {
                    if (claimed.Contains(n))
                    {
                        continue;
                    }

                    float d = math.distancesq(EntityManager.GetComponentData<Node>(n).m_Position, pos);
                    if (d < bestSq)
                    {
                        bestSq = d;
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

        private bool HasLiveEdge(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                return false;
            }

            DynamicBuffer<ConnectedEdge> ce = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < ce.Length; i++)
            {
                Entity e = ce[i].m_Edge;
                if (EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool InBox(float3 p, NetSetCommand cmd)
        {
            return p.x >= cmd.MinX && p.x <= cmd.MaxX && p.z >= cmd.MinZ && p.z <= cmd.MaxZ;
        }

        /// <summary>Delete an edge KEEPING its components (I8): AddComponent&lt;Deleted&gt; only, never
        /// DestroyEntity — ReferencesSystem@Mod2B walks Edge/m_Start/m_End to unwire the junction. Tags
        /// <see cref="CS2M_RemoteDeleted"/> so no local detector echoes the delete back to the host.</summary>
        private void DeleteEdge(Entity edge)
        {
            if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge))
            {
                return;
            }

            if (!EntityManager.HasComponent<CS2M_RemoteDeleted>(edge))
            {
                EntityManager.AddComponent<CS2M_RemoteDeleted>(edge);
            }

            EntityManager.AddComponent<Deleted>(edge);
        }

        /// <summary>Create a net node from the incident edge prefab's <c>NetData.m_NodeArchetype</c> — the same
        /// direct-archetype path <see cref="NetBatchApplySystem"/>.CreateNode uses (proven: direct nodes render
        /// correctly). Position is authoritative; rotation is left identity (NodeAlign re-centres it once the
        /// edges attach and mark it Updated). Registered under the host's id and tagged
        /// <see cref="CS2M_RemotePlaced"/> (the by-component echo guard).</summary>
        private Entity CreateNode(string prefabType, string prefabName, float3 pos, ulong id)
        {
            if (!ResolveNetPrefab(prefabType, prefabName, out Entity netPrefab, out NetData netData))
            {
                return Entity.Null;
            }

            if (!netData.m_NodeArchetype.Valid)
            {
                CS2M.Log.Info($"[NetSet] node RESOLVE-FAIL {prefabName} invalid node archetype");
                return Entity.Null;
            }

            Entity node = EntityManager.CreateEntity();
            EntityManager.SetArchetype(node, netData.m_NodeArchetype);

            SetOrAdd(node, new PrefabRef(netPrefab));
            SetOrAdd(node, new Node { m_Position = pos, m_Rotation = quaternion.identity });
            SetOrAdd(node, new PseudoRandomSeed(0));

            CS2M_NodeSyncIds.Register(EntityManager, node, id);
            if (!EntityManager.HasComponent<CS2M_RemotePlaced>(node))
            {
                EntityManager.AddComponent<CS2M_RemotePlaced>(node);
            }

            return node;
        }

        /// <summary>Emit ONE vanilla net DEFINITION for a new edge — a faithful copy of the CreationDefinition
        /// (Permanent) + NetCourse pattern of <see cref="NetBatchApplySystem"/>.EmitEdgeCourse (I9: never a raw
        /// edge archetype). GenerateNodes@Mod1/GenerateEdges@Mod2 build the REAL edge this frame with the
        /// endpoints pinned by <c>m_Entity</c> onto the two resolved nodes (so the curve is fitted to the node
        /// coords — I4). Echo-guards the produced edge's seg hash so NetBatchCaptureSystem doesn't re-broadcast
        /// it. Returns true when a definition was emitted.</summary>
        private bool EmitCourse(string prefabType, string prefabName, Bezier4x3 bezier, Entity startNode, Entity endNode)
        {
            if (!ResolveNetPrefab(prefabType, prefabName, out Entity netPrefab, out NetData _))
            {
                return false;
            }

            // Echo guard: the edge GenerateEdges produces is born Applied+Created WITHOUT CS2M_RemotePlaced
            // (we never hold that entity), so mark its seg hash first or NetBatchCaptureSystem re-broadcasts it.
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(bezier.a, bezier.d, prefabName));

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;

            course.m_StartPosition.m_Position = bezier.a;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(bezier));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_ParentMesh = -1;
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst;
            course.m_StartPosition.m_Entity = startNode;

            course.m_EndPosition.m_Position = bezier.d;
            course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(bezier));
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_ParentMesh = -1;
            course.m_EndPosition.m_Flags = CoursePosFlags.IsLast;
            course.m_EndPosition.m_Entity = endNode;

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = netPrefab,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponentData(def, course);
            EntityManager.AddComponent<Updated>(def); // required by GenerateEdgesSystem's definition query
            _pendingDefinitions.Add(def);
            return true;
        }

        /// <summary>Identity-first edge resolution by node-pair (copy of NetBatchApplySystem.FindEdgeById): the
        /// two ids resolve to nodes, then walk one node's ConnectedEdge for the edge joining both.</summary>
        private bool FindEdgeByNodeIds(ulong aId, ulong bId, out Entity edge)
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

        private bool ResolveNetPrefab(string type, string name, out Entity netPrefab, out NetData netData)
        {
            netPrefab = Entity.Null;
            netData = default;

            var prefabId = new PrefabID(type, name, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info($"[NetSet] RESOLVE-FAIL type={type} name={name}");
                return false;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out netPrefab))
            {
                CS2M.Log.Info($"[NetSet] RESOLVE-FAIL no prefab entity name={name}");
                return false;
            }

            if (!EntityManager.HasComponent<NetData>(netPrefab))
            {
                CS2M.Log.Info($"[NetSet] RESOLVE-FAIL prefab {name} has no NetData (not a net?)");
                return false;
            }

            netData = EntityManager.GetComponentData<NetData>(netPrefab);
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
    }
}
