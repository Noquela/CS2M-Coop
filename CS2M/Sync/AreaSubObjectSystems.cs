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
    /// <summary>Global toggle for area sub-object sync (crops / animals / resource piles). ON by default;
    /// set env <c>CS2M_AREAOBJ=0</c> to disable both the host detector and the client apply.</summary>
    public static class AreaObjGate
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_AREAOBJ") == "0" ? 0 : 1;
                }

                // Stand down while the client grows its own field (CS2M_AREAGROW, option A): the host mirror
                // and the local spawner would otherwise BOTH create crops (different RNG positions -> no
                // adopt -> duplicates). The legacy path (CS2M_AREAGROW=0) keeps this mirror active.
                return _state == 1 && !ExtractorGrowGate.Enabled;
            }
        }
    }

    /// <summary>Thread-safe queue for remote area sub-object batches (client side).</summary>
    public static class RemoteAreaSubObjectQueue
    {
        private static readonly Queue<AreaSubObjectCommand> Queue = new Queue<AreaSubObjectCommand>();
        private static readonly object Lock = new object();

        public static void Enqueue(AreaSubObjectCommand cmd)
        {
            lock (Lock) { Queue.Enqueue(cmd); }
        }

        public static bool TryDequeue(out AreaSubObjectCommand cmd)
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
    ///     HOST-ONLY detector: at ~1 Hz, diffs the sub-objects the game's <c>AreaSpawnSystem</c> has grown
    ///     inside every Extractor / Storage work area (crops, animals, resource piles) against the set it
    ///     last shipped, and broadcasts the appearing ones as <c>create</c> ops and the vanished ones as
    ///     <c>delete</c> ops. The client's own <c>AreaSpawnSystem</c> is suppressed
    ///     (<see cref="AreaSpawnSuppressSystem"/>), so this is the ONLY path that keeps its fields from
    ///     looking empty. Never runs on a client (echo-free by construction — a client neither detects nor
    ///     sends), and does nothing while no remote client is connected; the first scan AFTER a client
    ///     joins re-ships the full state (first-sight) which the client adopts idempotently.
    /// </summary>
    public partial class AreaSubObjectDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _workAreas;
        private int _scanCounter;
        private int _lastConnectedCount = 1;
        private int _diagScans;
        private const int OpsPerCommand = 64;

        private struct SentSub
        {
            public ulong Id;
            public string PrefabType;
            public string PrefabName;
            public float3 Pos;
            public quaternion Rot;
            public float Elevation;
            public int Seed;
            // Owner-area anchor captured at send time so a later delete can be addressed even after the
            // sub-object entity is gone.
            public ulong OwnerAnchorId;
            public string OwnerAnchorPrefabName;
            public float3 OwnerPos;
            public ulong BuildingSyncId;
        }

        // sub-object entity -> what we shipped for it. Persists across scans; cleared on client-join to
        // force a full first-sight resend.
        private readonly Dictionary<Entity, SentSub> _sent = new Dictionary<Entity, SentSub>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // Extractor OR Storage work areas — the only areas AreaSpawnSystem grows sub-objects into
            // (decomp AreaSpawnSystem.cs:151-153: it processes chunks that have a Storage or Extractor array).
            _workAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Objects.SubObject>(),
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
            CS2M.Log.Info("[AreaObj] AreaSubObjectDetectorSystem created");

            // CS2M_AREAOBJ_FAST=1 (validação): força o AreaSpawnSystem do jogo a semear sub-objetos
            // imediatamente (propriedade debug do próprio jogo — decomp AreaSpawnSystem.cs:753/:174-186),
            // sem esperar a rampa de extração/colheita. Só afeta o processo local; no host isso permite
            // validar o pipeline detector→SEND→apply em minutos em vez de horas de sim.
            if (System.Environment.GetEnvironmentVariable("CS2M_AREAOBJ_FAST") == "1")
            {
                var spawn = World.GetExistingSystemManaged<Game.Simulation.AreaSpawnSystem>();
                if (spawn != null)
                {
                    spawn.debugFastSpawn = true;
                    CS2M.Log.Info("[AreaObj] debugFastSpawn=ON (CS2M_AREAOBJ_FAST=1 — validation aid)");
                }
            }
        }

        protected override void OnUpdate()
        {
            if (!AreaObjGate.Enabled)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // HOST only — a client never derives these objects, so it must never detect/send them.
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
                // idempotently on its side — no duplicates even for objects it already loaded).
                _sent.Clear();
                CS2M.Log.Info("[AreaObj] client joined -> first-sight full resend");
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
            // Per-area create batches keyed by area entity; deletes grouped by stored anchor id.
            var deletes = new Dictionary<ulong, OpBatch>();

            // Discrete per-minute diagnostic (proves the pipeline sees content, and why any is dropped —
            // e.g. all-buildings would read syncable=0/buildings=0 with the OLD code; now buildings flow).
            int diagAreas = 0, diagSub = 0, diagSyncable = 0, diagBuildings = 0,
                diagSkipSecondary = 0, diagSkipOther = 0;

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
                    DynamicBuffer<Game.Objects.SubObject> subs =
                        EntityManager.GetBuffer<Game.Objects.SubObject>(area, true);
                    diagSub += subs.Length;
                    for (int i = 0; i < subs.Length; i++)
                    {
                        Entity sub = subs[i].m_SubObject;
                        if (!IsSyncableSubObject(sub))
                        {
                            if (sub != Entity.Null && EntityManager.Exists(sub)
                                && EntityManager.HasComponent<Game.Objects.Secondary>(sub))
                            {
                                diagSkipSecondary++;
                            }
                            else
                            {
                                diagSkipOther++;
                            }

                            continue;
                        }

                        diagSyncable++;
                        if (EntityManager.HasComponent<Game.Buildings.Building>(sub))
                        {
                            diagBuildings++;
                        }

                        current.Add(sub);
                        if (_sent.ContainsKey(sub))
                        {
                            continue; // already shipped
                        }

                        if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(sub).m_Prefab,
                                out PrefabBase prefab) || prefab == null)
                        {
                            continue;
                        }

                        var tf = EntityManager.GetComponentData<Game.Objects.Transform>(sub);
                        float elevation = EntityManager.HasComponent<Game.Objects.Elevation>(sub)
                            ? EntityManager.GetComponentData<Game.Objects.Elevation>(sub).m_Elevation
                            : 0f;
                        int seed = EntityManager.HasComponent<PseudoRandomSeed>(sub)
                            ? EntityManager.GetComponentData<PseudoRandomSeed>(sub).m_Seed
                            : 0;

                        // Mint a stable cross-PC id for this sub-object (host is the sole authority).
                        ulong id = CS2M_SyncIdSystem.Allocate();
                        CS2M_SyncIdSystem.Register(EntityManager, sub, id);

                        var sent = new SentSub
                        {
                            Id = id,
                            PrefabType = prefab.GetType().Name,
                            PrefabName = prefab.name,
                            Pos = tf.m_Position,
                            Rot = tf.m_Rotation,
                            Elevation = elevation,
                            Seed = seed,
                            OwnerAnchorId = anchorId,
                            OwnerAnchorPrefabName = anchorPrefab,
                            OwnerPos = anchorPos,
                            BuildingSyncId = buildingSyncId,
                        };
                        _sent[sub] = sent;

                        creates ??= new OpBatch(anchorId, anchorPrefab, anchorPos, buildingSyncId);
                        creates.AddCreate(sent);
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
            foreach (KeyValuePair<Entity, SentSub> kv in _sent)
            {
                if (!current.Contains(kv.Key))
                {
                    vanished.Add(kv.Key);
                }
            }

            foreach (Entity gone in vanished)
            {
                SentSub s = _sent[gone];
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

            // ~1 line/minute (Scan is ~1 Hz). Dense enough to root-cause an empty client field next
            // session without guessing: areas matched, sub-objects enumerated, how many were syncable
            // (and of those, buildings — the farm/livestock content), and why the rest were dropped.
            if (++_diagScans >= 60)
            {
                _diagScans = 0;
                CS2M.Log.Info(
                    $"[AreaObj] scan areas={diagAreas} subObjects={diagSub} syncable={diagSyncable} " +
                    $"buildings={diagBuildings} sent={_sent.Count} " +
                    $"skip={{secondary:{diagSkipSecondary},other:{diagSkipOther}}}");
            }
        }

        private void FlushCreate(OpBatch b)
        {
            Command.SendToAll?.Invoke(b.Build());
            CS2M.Log.Info($"[AreaObj] SEND create ops={b.Count} anchor={b.AnchorId} prefab={b.AnchorPrefab}");
        }

        private void FlushDelete(OpBatch b)
        {
            Command.SendToAll?.Invoke(b.Build());
            CS2M.Log.Info($"[AreaObj] SEND delete ops={b.Count} anchor={b.AnchorId} prefab={b.AnchorPrefab}");
        }

        /// <summary>A real object grown by the sim inside this work area: has Object+Transform+PrefabRef,
        /// is not a derived prefab sub-object (<see cref="Game.Objects.Secondary"/>), is not mid-placement /
        /// player-authored (<see cref="Applied"/>), and is not Temp/Deleted.
        /// <para>Buildings ARE included. A livestock/farm pasture grows its content as BUILDING sub-objects
        /// (barns, sheds, silos): decomp AreaSpawnSystem.cs:233-263 selects a prefab from the AREA prefab's
        /// SubObject buffer — which may carry <c>BuildingData</c> (:253/:270) — and SpawnObject (:566) owns
        /// it to the AREA; GenerateObjectsSystem.cs:1225/1236 then materialises it with the building
        /// archetype + <c>Owner(area)</c>. Being sim-grown it has NO placement sync, so the previous
        /// <c>Building</c> exclusion left the client's fenced pasture empty (host logged ZERO SEND). It is
        /// safe to include: everything here is a sub-object OWNED BY the area (never the farm building
        /// itself, which OWNS the area), and PlacementDetectorSystem excludes <c>Owner</c> objects, so a
        /// player building can never be an area sub-object.</para></summary>
        private bool IsSyncableSubObject(Entity sub)
        {
            return sub != Entity.Null
                   && EntityManager.Exists(sub)
                   && EntityManager.HasComponent<Game.Objects.Object>(sub)
                   && EntityManager.HasComponent<Game.Objects.Transform>(sub)
                   && EntityManager.HasComponent<PrefabRef>(sub)
                   && !EntityManager.HasComponent<Game.Objects.Secondary>(sub)
                   && !EntityManager.HasComponent<Applied>(sub)
                   && !EntityManager.HasComponent<Temp>(sub)
                   && !EntityManager.HasComponent<Deleted>(sub);
        }

        /// <summary>Resolve the owning AREA's stable anchor (mint its id if new — host is authoritative),
        /// its prefab name, polygon centroid and a best-effort building id hint.</summary>
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

            // Centroid of the area polygon (Game.Areas.Node buffer).
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

            // Best-effort building hint: walk the owner chain to a Building that carries a sync id.
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

        /// <summary>Accumulates create OR delete ops for one owner-area anchor into parallel primitive
        /// arrays and builds the command.</summary>
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
            private readonly List<float> _px = new List<float>();
            private readonly List<float> _py = new List<float>();
            private readonly List<float> _pz = new List<float>();
            private readonly List<float> _rx = new List<float>();
            private readonly List<float> _ry = new List<float>();
            private readonly List<float> _rz = new List<float>();
            private readonly List<float> _rw = new List<float>();
            private readonly List<float> _elev = new List<float>();
            private readonly List<int> _seeds = new List<int>();

            public OpBatch(ulong anchorId, string anchorPrefab, float3 anchorPos, ulong buildingSyncId)
            {
                AnchorId = anchorId;
                AnchorPrefab = anchorPrefab;
                _anchorPos = anchorPos;
                _buildingSyncId = buildingSyncId;
            }

            public int Count => _ops.Count;

            public void AddCreate(SentSub s)
            {
                Add(0, s);
            }

            public void AddDelete(SentSub s)
            {
                Add(1, s);
            }

            private void Add(byte op, SentSub s)
            {
                _ops.Add(op);
                _ids.Add(s.Id);
                _prefabTypes.Add(s.PrefabType);
                _prefabNames.Add(s.PrefabName);
                _px.Add(s.Pos.x);
                _py.Add(s.Pos.y);
                _pz.Add(s.Pos.z);
                _rx.Add(s.Rot.value.x);
                _ry.Add(s.Rot.value.y);
                _rz.Add(s.Rot.value.z);
                _rw.Add(s.Rot.value.w);
                _elev.Add(s.Elevation);
                _seeds.Add(s.Seed);
            }

            public AreaSubObjectCommand Build()
            {
                return new AreaSubObjectCommand
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
                    PosX = _px.ToArray(),
                    PosY = _py.ToArray(),
                    PosZ = _pz.ToArray(),
                    RotX = _rx.ToArray(),
                    RotY = _ry.ToArray(),
                    RotZ = _rz.ToArray(),
                    RotW = _rw.ToArray(),
                    Elevation = _elev.ToArray(),
                    Seeds = _seeds.ToArray(),
                };
            }
        }
    }

    /// <summary>
    ///     CLIENT-side apply for <see cref="AreaSubObjectCommand"/>. Resolves the owner area (by stable id,
    ///     falling back once to prefab-name + centroid), then materialises each create via the SAME
    ///     definition path the game uses in <c>AreaSpawnSystem.SpawnObject</c> — a CreationDefinition +
    ///     ObjectDefinition entity with <c>m_Owner</c> = the area — consumed by the vanilla
    ///     <c>GenerateObjectsSystem</c> at Modification1 (why this system runs just before it). Deletes
    ///     resolve the target by id (or prefab + position fallback) and stamp <c>Deleted</c>. Creates are
    ///     idempotent: an already-present matching sub-object is adopted (id registered) instead of
    ///     duplicated, so a client that already loaded the field (save / world transfer) never doubles it.
    ///     Never runs on the host.
    /// </summary>
    public partial class AreaSubObjectApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _workAreas;
        private readonly List<Entity> _pendingDefinitions = new List<Entity>();

        private struct PendingCmd { public AreaSubObjectCommand Cmd; public int FramesLeft; }
        private readonly List<PendingCmd> _pending = new List<PendingCmd>();
        private const int RetryTtlFrames = 300; // ~5 s at 60 fps
        private const float AdoptRadiusSq = 1.0f; // 1 m — same save/world-transfer positions match tightly
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
            CS2M.Log.Info("[AreaObj] AreaSubObjectApplySystem created");
        }

        protected override void OnUpdate()
        {
            // Definitions injected last frame were consumed by GenerateObjectsSystem — clean up.
            for (int i = 0; i < _pendingDefinitions.Count; i++)
            {
                if (EntityManager.Exists(_pendingDefinitions[i]))
                {
                    EntityManager.DestroyEntity(_pendingDefinitions[i]);
                }
            }

            _pendingDefinitions.Clear();

            if (!AreaObjGate.Enabled)
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
                RemoteAreaSubObjectQueue.Clear();
                return;
            }

            RetryPending();

            while (RemoteAreaSubObjectQueue.TryDequeue(out AreaSubObjectCommand cmd))
            {
                try
                {
                    if (!ApplyOne(cmd, lastTry: false))
                    {
                        _pending.Add(new PendingCmd { Cmd = cmd, FramesLeft = RetryTtlFrames });
                    }
                }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area sub-object apply failed: {ex.Message}"); }
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
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area sub-object retry failed: {ex.Message}"); handled = true; }

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

        /// <summary>Returns true when handled; false only when retryable (owner area not present yet).</summary>
        private bool ApplyOne(AreaSubObjectCommand cmd, bool lastTry)
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

                CS2M.Log.Info($"[AreaObj] DROP noArea anchor={cmd.OwnerAnchorId} prefab={cmd.OwnerAnchorPrefabName} after retries");
                return true;
            }

            for (int i = 0; i < cmd.Ops.Length; i++)
            {
                try
                {
                    if (cmd.Ops[i] == 1)
                    {
                        ApplyDelete(cmd, i, area);
                    }
                    else
                    {
                        ApplyCreate(cmd, i, area);
                    }
                }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area sub-object op failed: {ex.Message}"); }
            }

            return true;
        }

        private void ApplyCreate(AreaSubObjectCommand cmd, int i, Entity area)
        {
            ulong id = cmd.Ids != null && i < cmd.Ids.Length ? cmd.Ids[i] : 0;

            // Idempotency: already placed under this id?
            if (id != 0 && CS2M_SyncIdSystem.Map.TryGetValue(id, out Entity known)
                && EntityManager.Exists(known) && !EntityManager.HasComponent<Deleted>(known))
            {
                return;
            }

            var pos = new float3(cmd.PosX[i], cmd.PosY[i], cmd.PosZ[i]);
            string prefabName = cmd.PrefabNames[i];

            // Idempotent adopt: a matching sub-object already under this area (loaded from the save / world
            // transfer) is registered under the shipped id instead of creating a duplicate.
            Entity existing = FindMatchingSubObject(area, prefabName, pos);
            if (existing != Entity.Null)
            {
                CS2M_SyncIdSystem.Register(EntityManager, existing, id);
                CS2M.Log.Verbose($"[AreaObj] ADOPT existing name={prefabName} id={id} entity={existing.Index}");
                return;
            }

            var prefabId = new PrefabID(cmd.PrefabTypes[i], prefabName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out Entity prefabEntity))
            {
                CS2M.Log.Info($"[AreaObj] RESOLVE-FAIL name={prefabName}");
                return;
            }

            var rot = new quaternion(cmd.RotX[i], cmd.RotY[i], cmd.RotZ[i], cmd.RotW[i]);
            float elevation = cmd.Elevation != null && i < cmd.Elevation.Length ? cmd.Elevation[i] : 0f;
            int seed = cmd.Seeds != null && i < cmd.Seeds.Length ? cmd.Seeds[i] : 0;

            // Mirror Game.Simulation.AreaSpawnSystem.SpawnObject (decomp AreaSpawnSystem.cs:566-595): a
            // CreationDefinition + ObjectDefinition definition owned by the area, consumed by
            // GenerateObjectsSystem@Modification1 (decomp GenerateObjectsSystem.cs:1696-1708 — its query is
            // {CreationDefinition, Updated} + Any{ObjectDefinition, NetCourse}, no Temp/Deleted needed).
            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Owner = area,
                m_Prefab = prefabEntity,
                m_RandomSeed = seed,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponentData(def, new ObjectDefinition
            {
                m_ParentMesh = -1,
                m_Elevation = elevation,
                m_Position = pos,
                m_Rotation = rot,
                m_LocalPosition = pos,
                m_LocalRotation = rot,
            });
            EntityManager.AddComponent<Updated>(def);
            _pendingDefinitions.Add(def);

            CS2M.Log.Info($"[AreaObj] CREATE-DEF name={prefabName} id={id} pos=({pos.x:F0},{pos.z:F0})");
        }

        private void ApplyDelete(AreaSubObjectCommand cmd, int i, Entity area)
        {
            ulong id = cmd.Ids != null && i < cmd.Ids.Length ? cmd.Ids[i] : 0;
            Entity target = Entity.Null;

            if (id != 0 && CS2M_SyncIdSystem.Map.TryGetValue(id, out Entity known)
                && EntityManager.Exists(known) && !EntityManager.HasComponent<Deleted>(known))
            {
                target = known;
            }
            else
            {
                var pos = new float3(cmd.PosX[i], cmd.PosY[i], cmd.PosZ[i]);
                target = FindMatchingSubObject(area, cmd.PrefabNames[i], pos);
            }

            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[AreaObj] SKIP delete noMatch name={cmd.PrefabNames[i]} id={id}");
                return;
            }

            if (!EntityManager.HasComponent<CS2M_RemoteDeleted>(target))
            {
                EntityManager.AddComponent<CS2M_RemoteDeleted>(target); // echo guard (no client detector, but consistent)
            }

            EntityManager.AddComponent<Deleted>(target);
            CS2M.Log.Info($"[AreaObj] APPLIED delete name={cmd.PrefabNames[i]} entity={target.Index}");
        }

        /// <summary>Nearest live sub-object of <paramref name="area"/> whose prefab name matches and whose
        /// position is within the adopt radius of <paramref name="pos"/>. Entity.Null if none.</summary>
        private Entity FindMatchingSubObject(Entity area, string prefabName, float3 pos)
        {
            if (!EntityManager.HasBuffer<Game.Objects.SubObject>(area))
            {
                return Entity.Null;
            }

            DynamicBuffer<Game.Objects.SubObject> subs = EntityManager.GetBuffer<Game.Objects.SubObject>(area, true);
            Entity best = Entity.Null;
            float bestSq = AdoptRadiusSq;
            for (int i = 0; i < subs.Length; i++)
            {
                Entity sub = subs[i].m_SubObject;
                if (sub == Entity.Null || !EntityManager.Exists(sub)
                    || EntityManager.HasComponent<Deleted>(sub)
                    || !EntityManager.HasComponent<Game.Objects.Transform>(sub)
                    || !EntityManager.HasComponent<PrefabRef>(sub))
                {
                    continue;
                }

                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(sub).m_Prefab,
                        out PrefabBase p) || p == null || p.name != prefabName)
                {
                    continue;
                }

                float3 sp = EntityManager.GetComponentData<Game.Objects.Transform>(sub).m_Position;
                float d = math.distancesq(sp, pos);
                if (d < bestSq)
                {
                    bestSq = d;
                    best = sub;
                }
            }

            return best;
        }

        /// <summary>Resolve the owning area: by stable id first, then a one-time prefab-name + centroid
        /// search (registering the id so later ops resolve directly).</summary>
        private Entity ResolveArea(AreaSubObjectCommand cmd)
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
                CS2M.Log.Info($"[AreaObj] ANCHOR-RESOLVE id={cmd.OwnerAnchorId} name={cmd.OwnerAnchorPrefabName} entity={best.Index} (one-time)");
            }

            return best;
        }
    }
}
