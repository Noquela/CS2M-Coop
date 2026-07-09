using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Global toggle for area-surface sync (the tilled/plowed soil patch inside a farm lot). ON by
    /// default; set env <c>CS2M_AREASURF=0</c> to disable both the host detector and the client apply.</summary>
    public static class AreaSurfaceGate
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_AREASURF") == "0" ? 0 : 1;
                }

                // Stand down while the client grows its own field (CS2M_AREAGROW, option A): the local
                // spawner regrows the tilled soil itself, so mirroring it too would double the surface. The
                // legacy path (CS2M_AREAGROW=0) keeps this mirror active.
                return _state == 1 && !ExtractorGrowGate.Enabled;
            }
        }
    }

    /// <summary>Thread-safe queue for remote area-surface batches (client side).</summary>
    public static class RemoteAreaSurfaceQueue
    {
        private static readonly Queue<AreaSurfaceCommand> Queue = new Queue<AreaSurfaceCommand>();
        private static readonly object Lock = new object();

        public static void Enqueue(AreaSurfaceCommand cmd)
        {
            lock (Lock) { Queue.Enqueue(cmd); }
        }

        public static bool TryDequeue(out AreaSurfaceCommand cmd)
        {
            lock (Lock)
            {
                if (Queue.Count > 0)
                {
                    cmd = Queue.Dequeue();
                    return true;
                }

                cmd = null;
                return false;
            }
        }

        public static void Clear()
        {
            lock (Lock) { Queue.Clear(); }
        }
    }

    /// <summary>
    ///     HOST-ONLY detector: at ~1 Hz, diffs the <c>Game.Areas.Surface</c> sub-areas (tilled soil) that the
    ///     game's <c>AreaSpawnSystem</c> has grown inside every Extractor / Storage work area against the set
    ///     it last shipped, and broadcasts appearing / reshaped ones as <c>create</c> ops and vanished ones as
    ///     <c>delete</c> ops. A Surface lives in the <c>Game.Areas.SubArea</c> (area) graph — NOT the
    ///     <c>Game.Objects.SubObject</c> graph <see cref="AreaSubObjectDetectorSystem"/> walks — and carries
    ///     neither Extractor nor Storage, so <see cref="AreaEditDetectorSystem"/> never sees it either. The
    ///     client's own spawner is suppressed (<see cref="AreaSpawnSuppressSystem"/>), so this is the ONLY
    ///     path that fills its blank soil. Never runs on a client (echo-free by construction), and does
    ///     nothing while no remote client is connected; the first scan AFTER a client joins re-ships the full
    ///     state (first-sight) which the client adopts idempotently.
    /// </summary>
    public partial class AreaSurfaceDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _workAreas;
        private int _scanCounter;
        private int _lastConnectedCount = 1;
        private int _diagScans;
        private const int OpsPerCommand = 32;

        private struct SentSurface
        {
            public ulong Id;
            public string PrefabType;
            public string PrefabName;
            public int Seed;
            public int NodeHash;
            public ulong OwnerAnchorId;
            public string OwnerAnchorPrefabName;
            public float3 OwnerPos;
            public ulong BuildingSyncId;
        }

        // Surface entity -> what we last shipped for it. Persists across scans; cleared on client-join to
        // force a full first-sight resend.
        private readonly Dictionary<Entity, SentSurface> _sent = new Dictionary<Entity, SentSurface>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // Extractor OR Storage work areas that own a SubArea buffer — the areas AreaSpawnSystem grows
            // Surfaces into (same base scope as AreaSubObjectDetectorSystem._workAreas, but keyed on the AREA
            // sub-area buffer instead of the object sub-object buffer).
            _workAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.SubArea>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Extractor>(),
                    ComponentType.ReadOnly<Game.Areas.Storage>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                },
            });
            CS2M.Log.Info("[AreaSurf] AreaSurfaceDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (!AreaSurfaceGate.Enabled)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // HOST only — a client never derives these surfaces, so it must never detect/send them.
            if (NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            if (++_scanCounter < 60)
            {
                return;
            }

            _scanCounter = 0;

            int connected = NetworkInterface.Instance.PlayerListConnected.Count;
            bool clientJoined = connected > _lastConnectedCount;
            _lastConnectedCount = connected;
            if (clientJoined)
            {
                // First-sight: force a full resend so a just-connected client gets the whole state (adopted
                // idempotently on its side — no duplicates even for surfaces it already loaded).
                _sent.Clear();
                CS2M.Log.Info("[AreaSurf] client joined -> first-sight full resend");
            }

            if (connected <= 1)
            {
                return; // no remote client: nothing to send
            }

            Scan();
        }

        private void Scan()
        {
            var current = new HashSet<Entity>();
            var deletes = new Dictionary<ulong, OpBatch>();
            int diagAreas = 0, diagSurfaces = 0, diagSent = 0;

            NativeArray<Entity> areas = _workAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!ResolveAreaAnchor(area, out ulong anchorId, out string anchorPrefab,
                            out float3 anchorPos, out ulong buildingSyncId))
                    {
                        continue;
                    }

                    diagAreas++;
                    OpBatch creates = null;
                    DynamicBuffer<Game.Areas.SubArea> subs =
                        EntityManager.GetBuffer<Game.Areas.SubArea>(area, true);
                    for (int i = 0; i < subs.Length; i++)
                    {
                        Entity surface = subs[i].m_Area;
                        if (!IsSyncableSurface(surface))
                        {
                            continue;
                        }

                        diagSurfaces++;
                        DynamicBuffer<Game.Areas.Node> nodes =
                            EntityManager.GetBuffer<Game.Areas.Node>(surface, true);
                        if (nodes.Length == 0)
                        {
                            continue;
                        }

                        current.Add(surface);
                        int hash = WorkAreaHash.Compute(nodes);
                        if (_sent.TryGetValue(surface, out SentSurface prev) && prev.NodeHash == hash)
                        {
                            continue; // already shipped, polygon unchanged
                        }

                        if (!_prefabSystem.TryGetPrefab(
                                EntityManager.GetComponentData<PrefabRef>(surface).m_Prefab,
                                out PrefabBase prefab) || prefab == null)
                        {
                            continue;
                        }

                        // Stable cross-PC id: reuse the one already minted for this surface if it has one
                        // (a reshape resend keeps the same id), else mint fresh.
                        ulong id = prev.Id != 0
                            ? prev.Id
                            : EntityManager.HasComponent<CS2M_SyncId>(surface)
                                ? EntityManager.GetComponentData<CS2M_SyncId>(surface).m_Id
                                : CS2M_SyncIdSystem.Allocate();
                        if (!EntityManager.HasComponent<CS2M_SyncId>(surface))
                        {
                            CS2M_SyncIdSystem.Register(EntityManager, surface, id);
                        }

                        int seed = EntityManager.HasComponent<PseudoRandomSeed>(surface)
                            ? EntityManager.GetComponentData<PseudoRandomSeed>(surface).m_Seed
                            : 0;

                        var sent = new SentSurface
                        {
                            Id = id,
                            PrefabType = prefab.GetType().Name,
                            PrefabName = prefab.name,
                            Seed = seed,
                            NodeHash = hash,
                            OwnerAnchorId = anchorId,
                            OwnerAnchorPrefabName = anchorPrefab,
                            OwnerPos = anchorPos,
                            BuildingSyncId = buildingSyncId,
                        };
                        bool firstCaptureOfThis = !_sent.ContainsKey(surface);
                        _sent[surface] = sent;
                        diagSent++;

                        if (firstCaptureOfThis)
                        {
                            // Diagnostic: prove ownership live, once per surface first-sighted.
                            CS2M.Log.Info($"[AreaSurf] surface owner={anchorPrefab} nodes={nodes.Length} id={id}");
                        }

                        creates ??= new OpBatch(anchorId, anchorPrefab, anchorPos, buildingSyncId);
                        creates.AddCreate(sent, nodes);
                        if (creates.Count >= OpsPerCommand)
                        {
                            FlushCreate(creates);
                            creates = new OpBatch(anchorId, anchorPrefab, anchorPos, buildingSyncId);
                        }
                    }

                    if (creates != null && creates.Count > 0)
                    {
                        FlushCreate(creates);
                    }
                }
            }
            finally
            {
                areas.Dispose();
            }

            // Deletes: anything we shipped that is no longer present.
            var vanished = new List<Entity>();
            foreach (KeyValuePair<Entity, SentSurface> kv in _sent)
            {
                if (!current.Contains(kv.Key))
                {
                    vanished.Add(kv.Key);
                }
            }

            foreach (Entity gone in vanished)
            {
                SentSurface s = _sent[gone];
                _sent.Remove(gone);
                if (!deletes.TryGetValue(s.OwnerAnchorId, out OpBatch batch))
                {
                    batch = new OpBatch(s.OwnerAnchorId, s.OwnerAnchorPrefabName, s.OwnerPos, s.BuildingSyncId);
                    deletes[s.OwnerAnchorId] = batch;
                }

                batch.AddDelete(s);
                if (batch.Count >= OpsPerCommand)
                {
                    FlushDelete(batch);
                    deletes[s.OwnerAnchorId] = new OpBatch(s.OwnerAnchorId, s.OwnerAnchorPrefabName,
                        s.OwnerPos, s.BuildingSyncId);
                }
            }

            foreach (KeyValuePair<ulong, OpBatch> kv in deletes)
            {
                if (kv.Value.Count > 0)
                {
                    FlushDelete(kv.Value);
                }
            }

            // ~1 line/minute discrete diagnostic (proves the pipeline sees content).
            if (++_diagScans >= 60)
            {
                _diagScans = 0;
                CS2M.Log.Info($"[AreaSurf] scan areas={diagAreas} surfaces={diagSurfaces} sent={diagSent} tracked={_sent.Count}");
            }
        }

        private void FlushCreate(OpBatch b)
        {
            Command.SendToAll?.Invoke(b.Build());
            CS2M.Log.Info($"[AreaSurf] SEND create ops={b.Count} anchor={b.AnchorId} prefab={b.AnchorPrefab}");
        }

        private void FlushDelete(OpBatch b)
        {
            Command.SendToAll?.Invoke(b.Build());
            CS2M.Log.Info($"[AreaSurf] SEND delete ops={b.Count} anchor={b.AnchorId} prefab={b.AnchorPrefab}");
        }

        /// <summary>A real, live Surface sub-area of a work area: tagged <see cref="Game.Areas.Surface"/>,
        /// has an area polygon, is not mid-placement / Temp / Deleted.</summary>
        private bool IsSyncableSurface(Entity surface)
        {
            return surface != Entity.Null
                   && EntityManager.Exists(surface)
                   && EntityManager.HasComponent<Game.Areas.Surface>(surface)
                   && EntityManager.HasComponent<Game.Areas.Area>(surface)
                   && EntityManager.HasBuffer<Game.Areas.Node>(surface)
                   && EntityManager.HasComponent<PrefabRef>(surface)
                   && !EntityManager.HasComponent<Temp>(surface)
                   && !EntityManager.HasComponent<Deleted>(surface);
        }

        /// <summary>Resolve the owning WORK AREA's stable anchor (mint its id if new — host is authoritative),
        /// its prefab name, polygon centroid and a best-effort building id hint. Mirrors
        /// <see cref="AreaSubObjectDetectorSystem"/>'s ResolveAreaAnchor.</summary>
        private bool ResolveAreaAnchor(Entity area, out ulong anchorId, out string anchorPrefab,
            out float3 anchorPos, out ulong buildingSyncId)
        {
            anchorId = 0;
            anchorPrefab = null;
            anchorPos = default;
            buildingSyncId = 0;

            if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                    out PrefabBase prefab) || prefab == null)
            {
                return false;
            }

            anchorPrefab = prefab.name;

            if (EntityManager.HasComponent<CS2M_SyncId>(area))
            {
                anchorId = EntityManager.GetComponentData<CS2M_SyncId>(area).m_Id;
            }
            else
            {
                anchorId = CS2M_SyncIdSystem.Allocate();
                CS2M_SyncIdSystem.Register(EntityManager, area, anchorId);
            }

            if (EntityManager.HasBuffer<Game.Areas.Node>(area))
            {
                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                if (nodes.Length > 0)
                {
                    float3 sum = default;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        sum += nodes[i].m_Position;
                    }

                    anchorPos = sum / nodes.Length;
                }
            }

            Entity e = EntityManager.HasComponent<Owner>(area)
                ? EntityManager.GetComponentData<Owner>(area).m_Owner
                : Entity.Null;
            for (int guard = 0; e != Entity.Null && guard < 5 && EntityManager.Exists(e); guard++)
            {
                if (EntityManager.HasComponent<Game.Buildings.Building>(e)
                    && EntityManager.HasComponent<CS2M_SyncId>(e))
                {
                    buildingSyncId = EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id;
                    break;
                }

                if (!EntityManager.HasComponent<Owner>(e))
                {
                    break;
                }

                e = EntityManager.GetComponentData<Owner>(e).m_Owner;
            }

            return true;
        }

        /// <summary>Accumulates create OR delete ops for one owner-area anchor into flat parallel primitive
        /// arrays (polygons flattened via NodeCounts) and builds the command.</summary>
        private sealed class OpBatch
        {
            public readonly ulong AnchorId;
            public readonly string AnchorPrefab;
            private readonly float3 _anchorPos;
            private readonly ulong _buildingSyncId;

            private readonly List<byte> _ops = new List<byte>();
            private readonly List<ulong> _ids = new List<ulong>();
            private readonly List<string> _prefabTypes = new List<string>();
            private readonly List<string> _prefabNames = new List<string>();
            private readonly List<int> _seeds = new List<int>();
            private readonly List<int> _nodeCounts = new List<int>();
            private readonly List<float> _nx = new List<float>();
            private readonly List<float> _ny = new List<float>();
            private readonly List<float> _nz = new List<float>();
            private readonly List<float> _nel = new List<float>();

            public OpBatch(ulong anchorId, string anchorPrefab, float3 anchorPos, ulong buildingSyncId)
            {
                AnchorId = anchorId;
                AnchorPrefab = anchorPrefab;
                _anchorPos = anchorPos;
                _buildingSyncId = buildingSyncId;
            }

            public int Count => _ops.Count;

            public void AddCreate(SentSurface s, DynamicBuffer<Game.Areas.Node> nodes)
            {
                _ops.Add(0);
                _ids.Add(s.Id);
                _prefabTypes.Add(s.PrefabType);
                _prefabNames.Add(s.PrefabName);
                _seeds.Add(s.Seed);
                _nodeCounts.Add(nodes.Length);
                for (int i = 0; i < nodes.Length; i++)
                {
                    _nx.Add(nodes[i].m_Position.x);
                    _ny.Add(nodes[i].m_Position.y);
                    _nz.Add(nodes[i].m_Position.z);
                    _nel.Add(nodes[i].m_Elevation);
                }
            }

            public void AddDelete(SentSurface s)
            {
                _ops.Add(1);
                _ids.Add(s.Id);
                _prefabTypes.Add(s.PrefabType);
                _prefabNames.Add(s.PrefabName);
                _seeds.Add(s.Seed);
                _nodeCounts.Add(0); // delete carries no polygon
            }

            public AreaSurfaceCommand Build()
            {
                return new AreaSurfaceCommand
                {
                    OwnerAnchorId = AnchorId,
                    OwnerAnchorPrefabName = AnchorPrefab,
                    OwnerX = _anchorPos.x,
                    OwnerY = _anchorPos.y,
                    OwnerZ = _anchorPos.z,
                    BuildingSyncId = _buildingSyncId,
                    Ops = _ops.ToArray(),
                    Ids = _ids.ToArray(),
                    PrefabTypes = _prefabTypes.ToArray(),
                    PrefabNames = _prefabNames.ToArray(),
                    Seeds = _seeds.ToArray(),
                    NodeCounts = _nodeCounts.ToArray(),
                    NodeX = _nx.ToArray(),
                    NodeY = _ny.ToArray(),
                    NodeZ = _nz.ToArray(),
                    NodeEl = _nel.ToArray(),
                };
            }
        }
    }

    /// <summary>
    ///     CLIENT-side apply for <see cref="AreaSurfaceCommand"/>. Resolves the owner work area (by stable id,
    ///     falling back once to prefab-name + centroid), then materialises each Surface via the SAME
    ///     definition path the game uses in <c>AreaSpawnSystem.Spawn</c> (decomp AreaSpawnSystem.cs:448-472)
    ///     — a CreationDefinition owned by the area plus a <c>Game.Areas.Node</c> polygon, consumed by the
    ///     vanilla <c>GenerateAreasSystem</c> at Modification1 (why this system runs just before it). A newly
    ///     materialised (or save-loaded) Surface is UNSLAVED and rewritten to the host's exact polygon so
    ///     <c>GeometrySystem</c> can't regenerate it from the owner's template (decomp GeometrySystem.cs:95/160
    ///     — the v65.1 farm-field lesson). Creates are idempotent: a matching Surface already under the area is
    ///     adopted (id registered) instead of duplicated. Deletes resolve by id (or prefab + centroid) and
    ///     stamp <c>Deleted</c>. Never runs on the host.
    /// </summary>
    public partial class AreaSurfaceApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _workAreas;
        private readonly List<Entity> _pendingDefinitions = new List<Entity>();

        private struct PendingCmd { public AreaSurfaceCommand Cmd; public int FramesLeft; }
        private readonly List<PendingCmd> _pending = new List<PendingCmd>();

        // A definition-created Surface is not present the same frame — we must UNSLAVE it AFTER it
        // materialises. Park a finalize request that retries each frame until the Surface exists, then
        // unslaves + rewrites it to the host polygon.
        private struct PendingFinalize
        {
            public Entity Area;
            public ulong Id;
            public string PrefabName;
            public float3 Centroid;
            public float3[] Nodes;   // xyz per node
            public float[] Elev;
            public int FramesLeft;
        }
        private readonly List<PendingFinalize> _finalizes = new List<PendingFinalize>();

        private const int RetryTtlFrames = 300;   // ~5 s at 60 fps
        private const float AdoptRadius = 25f;     // metres — a work area holds ~1 tilled patch, so generous
        private const float AnchorSearchRadiusSq = 30f * 30f;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _workAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Extractor>(),
                    ComponentType.ReadOnly<Game.Areas.Storage>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                },
            });
            CS2M.Log.Info("[AreaSurf] AreaSurfaceApplySystem created");
        }

        protected override void OnUpdate()
        {
            // Definitions injected last frame were consumed by GenerateAreasSystem — clean up.
            for (int i = 0; i < _pendingDefinitions.Count; i++)
            {
                if (EntityManager.Exists(_pendingDefinitions[i]))
                {
                    EntityManager.DestroyEntity(_pendingDefinitions[i]);
                }
            }

            _pendingDefinitions.Clear();

            if (!AreaSurfaceGate.Enabled)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // Apply on the client only — the host authored these; applying on the host would duplicate.
            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                RemoteAreaSurfaceQueue.Clear();
                _pending.Clear();
                _finalizes.Clear();
                return;
            }

            RetryFinalizes();
            RetryPending();

            while (RemoteAreaSurfaceQueue.TryDequeue(out AreaSurfaceCommand cmd))
            {
                try
                {
                    if (!ApplyOne(cmd, lastTry: false))
                    {
                        _pending.Add(new PendingCmd { Cmd = cmd, FramesLeft = RetryTtlFrames });
                    }
                }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area surface apply failed: {ex.Message}"); }
            }
        }

        private void RetryPending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingCmd p = _pending[i];
                p.FramesLeft--;
                bool last = p.FramesLeft <= 0;
                bool handled;
                try { handled = ApplyOne(p.Cmd, last); }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area surface retry failed: {ex.Message}"); handled = true; }

                if (handled || last)
                {
                    _pending.RemoveAt(i);
                }
                else
                {
                    _pending[i] = p;
                }
            }
        }

        /// <summary>Retry parked finalize requests: once the definition-created Surface materialises under its
        /// area, UNSLAVE it and rewrite it to the host polygon.</summary>
        private void RetryFinalizes()
        {
            for (int i = _finalizes.Count - 1; i >= 0; i--)
            {
                PendingFinalize f = _finalizes[i];
                f.FramesLeft--;
                bool last = f.FramesLeft <= 0;
                bool done = false;
                try
                {
                    Entity target = ResolveSurfaceById(f.Id);
                    if (target == Entity.Null)
                    {
                        target = FindMatchingSurface(f.Area, f.PrefabName, f.Centroid);
                    }

                    if (target != Entity.Null)
                    {
                        FinalizeSurface(target, f.Id, f.Nodes, f.Elev);
                        done = true;
                    }
                }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area surface finalize failed: {ex.Message}"); done = true; }

                if (done || last)
                {
                    _finalizes.RemoveAt(i);
                }
                else
                {
                    _finalizes[i] = f;
                }
            }
        }

        /// <summary>Returns true when handled; false only when retryable (owner area not present yet).</summary>
        private bool ApplyOne(AreaSurfaceCommand cmd, bool lastTry)
        {
            if (cmd.Ops == null || cmd.Ops.Length == 0)
            {
                return true;
            }

            Entity area = ResolveArea(cmd);
            if (area == Entity.Null)
            {
                if (!lastTry)
                {
                    return false; // area may still be materialising — retry
                }

                CS2M.Log.Info($"[AreaSurf] DROP noArea anchor={cmd.OwnerAnchorId} prefab={cmd.OwnerAnchorPrefabName} after retries");
                return true;
            }

            int nodeOffset = 0;
            for (int i = 0; i < cmd.Ops.Length; i++)
            {
                int nc = cmd.NodeCounts != null && i < cmd.NodeCounts.Length ? cmd.NodeCounts[i] : 0;
                try
                {
                    if (cmd.Ops[i] == 1)
                    {
                        ApplyDelete(cmd, i, area, nodeOffset, nc);
                    }
                    else
                    {
                        ApplyCreate(cmd, i, area, nodeOffset, nc);
                    }
                }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area surface op failed: {ex.Message}"); }

                nodeOffset += nc;
            }

            return true;
        }

        private void ApplyCreate(AreaSurfaceCommand cmd, int i, Entity area, int nodeOffset, int nodeCount)
        {
            ulong id = cmd.Ids != null && i < cmd.Ids.Length ? cmd.Ids[i] : 0;
            if (nodeCount < 3)
            {
                return; // a Surface polygon needs at least a triangle
            }

            var nodes = new float3[nodeCount];
            var elev = new float[nodeCount];
            float3 sum = default;
            for (int n = 0; n < nodeCount; n++)
            {
                int k = nodeOffset + n;
                nodes[n] = new float3(cmd.NodeX[k], cmd.NodeY[k], cmd.NodeZ[k]);
                elev[n] = cmd.NodeEl != null && k < cmd.NodeEl.Length ? cmd.NodeEl[k] : float.MinValue;
                sum += nodes[n];
            }

            float3 centroid = sum / nodeCount;
            string prefabName = cmd.PrefabNames[i];

            // Idempotency by id: already placed → just re-finalise (host may have reshaped it).
            Entity known = ResolveSurfaceById(id);
            if (known != Entity.Null)
            {
                FinalizeSurface(known, id, nodes, elev);
                return;
            }

            // Idempotent adopt: a matching Surface already under this area (loaded from the save / world
            // transfer, or a prior create-def that materialised) is finalised under the shipped id.
            Entity existing = FindMatchingSurface(area, prefabName, centroid);
            if (existing != Entity.Null)
            {
                FinalizeSurface(existing, id, nodes, elev);
                CS2M.Log.Info($"[AreaSurf] ADOPT existing name={prefabName} id={id} entity={existing.Index}");
                return;
            }

            var prefabId = new PrefabID(cmd.PrefabTypes[i], prefabName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out Entity prefabEntity))
            {
                CS2M.Log.Info($"[AreaSurf] RESOLVE-FAIL name={prefabName}");
                return;
            }

            int seed = cmd.Seeds != null && i < cmd.Seeds.Length ? cmd.Seeds[i] : 0;

            // Mirror Game.Simulation.AreaSpawnSystem.Spawn (decomp AreaSpawnSystem.cs:448-472): a
            // CreationDefinition owned by the area + a Game.Areas.Node polygon, consumed by GenerateAreasSystem
            // (its query is {CreationDefinition, Node, Updated}, decomp GenerateAreasSystem.cs:529).
            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Owner = area,
                m_Prefab = prefabEntity,
                m_RandomSeed = seed,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponent<Updated>(def);
            DynamicBuffer<Game.Areas.Node> defNodes = EntityManager.AddBuffer<Game.Areas.Node>(def);
            defNodes.ResizeUninitialized(nodeCount);
            for (int n = 0; n < nodeCount; n++)
            {
                defNodes[n] = new Game.Areas.Node(nodes[n], elev[n]);
            }

            _pendingDefinitions.Add(def);

            // Park a finalize so the materialised Surface gets UNSLAVED + rewritten to the host polygon.
            _finalizes.Add(new PendingFinalize
            {
                Area = area,
                Id = id,
                PrefabName = prefabName,
                Centroid = centroid,
                Nodes = nodes,
                Elev = elev,
                FramesLeft = RetryTtlFrames,
            });

            CS2M.Log.Info($"[AreaSurf] CREATE name={prefabName} id={id} nodes={nodeCount} center=({centroid.x:F0},{centroid.z:F0})");
        }

        /// <summary>Register the id, UNSLAVE (so GeometrySystem never regenerates the polygon from the owner
        /// template — decomp GeometrySystem.cs:95/160, the v65.1 lesson), rewrite the Node buffer to the host
        /// polygon, mark Updated (Areas SearchSystem/QuadTree needs it), and stamp the echo guard.</summary>
        private void FinalizeSurface(Entity surface, ulong id, float3[] nodes, float[] elev)
        {
            if (!EntityManager.Exists(surface) || EntityManager.HasComponent<Deleted>(surface))
            {
                return;
            }

            if (id != 0 && !EntityManager.HasComponent<CS2M_SyncId>(surface))
            {
                CS2M_SyncIdSystem.Register(EntityManager, surface, id);
            }

            if (!EntityManager.HasComponent<CS2M_RemotePlaced>(surface))
            {
                EntityManager.AddComponent<CS2M_RemotePlaced>(surface); // echo guard
            }

            // UNSLAVE: a placement/template-born sub-area is a Slave, whose polygon GeometrySystem rebuilds
            // from the owner's template on every Updated — wiping our host polygon. Promote it to free-standing.
            if (EntityManager.HasComponent<Game.Areas.Area>(surface))
            {
                Game.Areas.Area areaData = EntityManager.GetComponentData<Game.Areas.Area>(surface);
                if ((areaData.m_Flags & Game.Areas.AreaFlags.Slave) != 0)
                {
                    areaData.m_Flags &= ~Game.Areas.AreaFlags.Slave;
                    EntityManager.SetComponentData(surface, areaData);
                    CS2M.Log.Info($"[AreaSurf] UNSLAVE entity={surface.Index}");
                }
            }

            if (EntityManager.HasBuffer<Game.Areas.Node>(surface))
            {
                DynamicBuffer<Game.Areas.Node> buf = EntityManager.GetBuffer<Game.Areas.Node>(surface);
                buf.ResizeUninitialized(nodes.Length);
                for (int n = 0; n < nodes.Length; n++)
                {
                    buf[n] = new Game.Areas.Node(nodes[n], elev != null && n < elev.Length ? elev[n] : float.MinValue);
                }

                // Update the shared polygon hash so any diff scanner treats this as already-known.
                WorkAreaHash.Set(surface, WorkAreaHash.Compute(buf));
            }

            // The Areas QuadTree (Game/Areas/SearchSystem.cs) goes stale unless the node write is published.
            if (!EntityManager.HasComponent<Updated>(surface))
            {
                EntityManager.AddComponent<Updated>(surface);
            }

            CS2M.Log.Info($"[AreaSurf] FINALIZE id={id} entity={surface.Index} nodes={nodes.Length}");
        }

        private void ApplyDelete(AreaSurfaceCommand cmd, int i, Entity area, int nodeOffset, int nodeCount)
        {
            ulong id = cmd.Ids != null && i < cmd.Ids.Length ? cmd.Ids[i] : 0;
            Entity target = ResolveSurfaceById(id);
            if (target == Entity.Null)
            {
                // No polygon on a delete op — fall back to nearest matching Surface under the area.
                target = FindMatchingSurface(area, cmd.PrefabNames[i], default);
            }

            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[AreaSurf] SKIP delete noMatch name={cmd.PrefabNames[i]} id={id}");
                return;
            }

            if (!EntityManager.HasComponent<CS2M_RemoteDeleted>(target))
            {
                EntityManager.AddComponent<CS2M_RemoteDeleted>(target); // echo guard
            }

            EntityManager.AddComponent<Deleted>(target);
            CS2M.Log.Info($"[AreaSurf] DELETE name={cmd.PrefabNames[i]} entity={target.Index}");
        }

        private Entity ResolveSurfaceById(ulong id)
        {
            if (id != 0 && CS2M_SyncIdSystem.Map.TryGetValue(id, out Entity known)
                && EntityManager.Exists(known) && !EntityManager.HasComponent<Deleted>(known)
                && EntityManager.HasComponent<Game.Areas.Surface>(known))
            {
                return known;
            }

            return Entity.Null;
        }

        /// <summary>Nearest live Surface sub-area of <paramref name="area"/> whose prefab name matches and,
        /// when a centroid is given, whose centroid is within the adopt radius. Entity.Null if none. Passing
        /// <c>default</c> centroid matches the nearest by prefab name only (used by delete fallback).</summary>
        private Entity FindMatchingSurface(Entity area, string prefabName, float3 centroid)
        {
            if (!EntityManager.HasBuffer<Game.Areas.SubArea>(area))
            {
                return Entity.Null;
            }

            bool haveCentroid = !centroid.Equals(default(float3));
            DynamicBuffer<Game.Areas.SubArea> subs = EntityManager.GetBuffer<Game.Areas.SubArea>(area, true);
            Entity best = Entity.Null;
            float bestSq = haveCentroid ? AdoptRadius * AdoptRadius : float.MaxValue;
            for (int i = 0; i < subs.Length; i++)
            {
                Entity s = subs[i].m_Area;
                if (s == Entity.Null || !EntityManager.Exists(s)
                    || EntityManager.HasComponent<Deleted>(s)
                    || !EntityManager.HasComponent<Game.Areas.Surface>(s)
                    || !EntityManager.HasBuffer<Game.Areas.Node>(s)
                    || !EntityManager.HasComponent<PrefabRef>(s))
                {
                    continue;
                }

                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(s).m_Prefab,
                        out PrefabBase p) || p == null || p.name != prefabName)
                {
                    continue;
                }

                if (!haveCentroid)
                {
                    return s;
                }

                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(s, true);
                if (nodes.Length == 0)
                {
                    continue;
                }

                float3 sum = default;
                for (int n = 0; n < nodes.Length; n++)
                {
                    sum += nodes[n].m_Position;
                }

                float3 c = sum / nodes.Length;
                float d = math.distancesq(c, centroid);
                if (d < bestSq)
                {
                    bestSq = d;
                    best = s;
                }
            }

            return best;
        }

        /// <summary>Resolve the owning work area: by stable id first, then a one-time prefab-name + centroid
        /// search (registering the id so later ops resolve directly). Mirrors
        /// <see cref="AreaSubObjectApplySystem"/>.ResolveArea.</summary>
        private Entity ResolveArea(AreaSurfaceCommand cmd)
        {
            if (cmd.OwnerAnchorId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(cmd.OwnerAnchorId, out Entity byId)
                && EntityManager.Exists(byId) && !EntityManager.HasComponent<Deleted>(byId)
                && EntityManager.HasComponent<Game.Areas.Area>(byId))
            {
                return byId;
            }

            if (string.IsNullOrEmpty(cmd.OwnerAnchorPrefabName))
            {
                return Entity.Null;
            }

            var hint = new float3(cmd.OwnerX, cmd.OwnerY, cmd.OwnerZ);
            Entity best = Entity.Null;
            float bestSq = AnchorSearchRadiusSq;
            NativeArray<Entity> areas = _workAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase p) || p == null || p.name != cmd.OwnerAnchorPrefabName)
                    {
                        continue;
                    }

                    if (!EntityManager.HasBuffer<Game.Areas.Node>(area))
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    if (nodes.Length == 0)
                    {
                        continue;
                    }

                    float3 sum = default;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        sum += nodes[i].m_Position;
                    }

                    float3 centroid = sum / nodes.Length;
                    float d = math.distancesq(centroid, hint);
                    if (d < bestSq)
                    {
                        bestSq = d;
                        best = area;
                    }
                }
            }
            finally
            {
                areas.Dispose();
            }

            if (best != Entity.Null && cmd.OwnerAnchorId != 0)
            {
                CS2M_SyncIdSystem.Register(EntityManager, best, cmd.OwnerAnchorId);
                CS2M.Log.Info($"[AreaSurf] ANCHOR-RESOLVE id={cmd.OwnerAnchorId} name={cmd.OwnerAnchorPrefabName} entity={best.Index} (one-time)");
            }

            return best;
        }
    }
}
