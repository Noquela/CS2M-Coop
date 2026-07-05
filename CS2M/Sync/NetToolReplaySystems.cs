using System.Collections.Generic;
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
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue for remote net-tool replays (input-replay, v56).</summary>
    public static class RemoteReplayQueue
    {
        private static readonly Queue<NetToolReplayCommand> Queue = new Queue<NetToolReplayCommand>();
        private static readonly object Lock = new object();

        public static void Enqueue(NetToolReplayCommand cmd) { lock (Lock) { Queue.Enqueue(cmd); } }

        public static bool TryDequeue(out NetToolReplayCommand cmd)
        {
            lock (Lock)
            {
                if (Queue.Count > 0) { cmd = Queue.Dequeue(); return true; }
                cmd = null;
                return false;
            }
        }

        public static void Clear() { lock (Lock) { Queue.Clear(); } }
    }

    /// <summary>
    ///     INPUT-REPLAY receiver (v56): replays a remote player's net-tool action by feeding the shipped
    ///     ControlPoints into the game's OWN <see cref="NetToolSystem.CreateDefinitionsJob"/>. The game does
    ///     the snapping / splitting / junction merging itself, so the resulting topology is identical on
    ///     every PC by construction — no reconstruction, no phantom nodes, no mismatched blocks/areas.
    ///
    ///     This mirrors NetToolSystem.Apply()'s job setup (decomp lines ~6974-7028) but with THIS system's
    ///     read-only lookups and our own command buffer, run synchronously. Runs before Modification1 so
    ///     GenerateNodes/Edges consume the definitions the same frame (same slot as NetPlaceApplySystem).
    /// </summary>
    public partial class NetToolReplaySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private Game.Simulation.WaterSystem _waterSystem;
        private EntityQuery _liveNodes;
        private EntityQuery _liveEdges;
        private EntityQuery _defsQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _waterSystem = World.GetOrCreateSystemManaged<Game.Simulation.WaterSystem>();
            _liveNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _defsQuery = GetEntityQuery(ComponentType.ReadWrite<CreationDefinition>());
            CS2M.Log.Info("[Replay] NetToolReplaySystem created (input-replay)");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteReplayQueue.TryDequeue(out NetToolReplayCommand cmd))
            {
                try { ReplayOne(cmd); }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] replay failed: {ex.Message}"); }
            }
        }

        private void ReplayOne(NetToolReplayCommand cmd)
        {
            var hash = new Colossal.Hash128(new uint4(cmd.Hash0, cmd.Hash1, cmd.Hash2, cmd.Hash3));
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, hash);
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out Entity netPrefab))
            {
                CS2M.Log.Info($"[Replay] RESOLVE-FAIL name={cmd.PrefabName}");
                return;
            }

            int n = cmd.PosX?.Length ?? 0;
            if (n < 2)
            {
                CS2M.Log.Info($"[Replay] SKIP too few control points ({n}) name={cmd.PrefabName}");
                return;
            }

            var controlPoints = new NativeList<ControlPoint>(n, Allocator.TempJob);
            var upgradeStates = new NativeList<NetToolSystem.UpgradeState>(Allocator.TempJob);
            var ecb = new EntityCommandBuffer(Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    controlPoints.Add(RebuildControlPoint(cmd, i));
                }

                var job = default(NetToolSystem.CreateDefinitionsJob);
                job.m_EditorMode = cmd.EditorMode;
                job.m_RemoveUpgrade = cmd.RemoveUpgrade;
                job.m_LefthandTraffic = cmd.LeftHandTraffic;
                job.m_Mode = (NetToolSystem.Mode) cmd.Mode;
                job.m_ParallelCount = cmd.LeftHandTraffic
                    ? new int2(0, cmd.ParallelCount)
                    : new int2(cmd.ParallelCount, 0);
                job.m_ParallelOffset = cmd.ParallelOffset;
                job.m_RandomSeed = MakeSeed(cmd.RandomSeed);
                job.m_ControlPoints = controlPoints;
                job.m_UpgradeStates = upgradeStates;
                job.m_EdgeData = GetComponentLookup<Edge>(true);
                job.m_NodeData = GetComponentLookup<Node>(true);
                job.m_CurveData = GetComponentLookup<Curve>(true);
                job.m_UpgradedData = GetComponentLookup<Upgraded>(true);
                job.m_FixedData = GetComponentLookup<Fixed>(true);
                job.m_EditorContainerData = GetComponentLookup<Game.Tools.EditorContainer>(true);
                job.m_OwnerData = GetComponentLookup<Owner>(true);
                job.m_TempData = GetComponentLookup<Temp>(true);
                job.m_LocalTransformCacheData = GetComponentLookup<LocalTransformCache>(true);
                job.m_TransformData = GetComponentLookup<Game.Objects.Transform>(true);
                job.m_AttachmentData = GetComponentLookup<Game.Objects.Attachment>(true);
                job.m_BuildingData = GetComponentLookup<Game.Buildings.Building>(true);
                job.m_ExtensionData = GetComponentLookup<Game.Buildings.Extension>(true);
                job.m_PrefabRefData = GetComponentLookup<PrefabRef>(true);
                job.m_NetGeometryData = GetComponentLookup<NetGeometryData>(true);
                job.m_PlaceableData = GetComponentLookup<PlaceableNetData>(true);
                job.m_PrefabSpawnableObjectData = GetComponentLookup<SpawnableObjectData>(true);
                job.m_PrefabAreaGeometryData = GetComponentLookup<AreaGeometryData>(true);
                job.m_ConnectedEdges = GetBufferLookup<ConnectedEdge>(true);
                job.m_SubReplacements = GetBufferLookup<SubReplacement>(true);
                job.m_SubNets = GetBufferLookup<Game.Net.SubNet>(true);
                job.m_CachedNodes = GetBufferLookup<LocalNodeCache>(true);
                job.m_SubAreas = GetBufferLookup<Game.Areas.SubArea>(true);
                job.m_AreaNodes = GetBufferLookup<Game.Areas.Node>(true);
                job.m_InstalledUpgrades = GetBufferLookup<Game.Buildings.InstalledUpgrade>(true);
                job.m_PrefabSubObjects = GetBufferLookup<Game.Prefabs.SubObject>(true);
                job.m_PrefabSubNets = GetBufferLookup<Game.Prefabs.SubNet>(true);
                job.m_PrefabSubAreas = GetBufferLookup<Game.Prefabs.SubArea>(true);
                job.m_PrefabSubAreaNodes = GetBufferLookup<Game.Prefabs.SubAreaNode>(true);
                job.m_PrefabPlaceholderElements = GetBufferLookup<PlaceholderObjectElement>(true);
                job.m_NetPrefab = netPrefab;
                job.m_WaterSurfaceData = _waterSystem.GetVelocitiesSurfaceData(out JobHandle waterDeps);
                job.m_CommandBuffer = ecb;

                // Snapshot existing definitions so we can find the ones the job creates.
                var beforeDefs = new HashSet<Entity>();
                NativeArray<Entity> pre = _defsQuery.ToEntityArray(Allocator.Temp);
                foreach (Entity pe in pre) { beforeDefs.Add(pe); }
                pre.Dispose();

                JobHandle handle = IJobExtensions.Schedule(job, waterDeps);
                _waterSystem.AddVelocitySurfaceReader(handle);
                handle.Complete();
                ecb.Playback(EntityManager);

                // The tool's job creates PREVIEW definitions (no Permanent flag → GenerateEdge builds a Temp
                // net that ToolClearSystem strips at end of frame → "roads don't appear on the receiver").
                // Stamp Permanent on the ones we just created so GenerateNodes/Edges build the REAL net.
                int stamped = 0;
                NativeArray<Entity> post = _defsQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity de in post)
                    {
                        if (beforeDefs.Contains(de)) { continue; }
                        CreationDefinition cd = EntityManager.GetComponentData<CreationDefinition>(de);
                        if ((cd.m_Flags & CreationFlags.Permanent) == 0)
                        {
                            cd.m_Flags |= CreationFlags.Permanent;
                            EntityManager.SetComponentData(de, cd);
                            stamped++;
                        }
                    }
                }
                finally { post.Dispose(); }

                CS2M.Log.Info($"[Replay] APPLIED name={cmd.PrefabName} mode={cmd.Mode} points={n} perm={stamped}");
            }
            finally
            {
                controlPoints.Dispose();
                upgradeStates.Dispose();
                ecb.Dispose();
            }
        }

        /// <summary>Rebuild a ControlPoint from the shipped arrays, re-resolving the snapped entity locally
        /// (by stable node id first, else by position) so the game's snap/split logic reconnects correctly.</summary>
        private ControlPoint RebuildControlPoint(NetToolReplayCommand cmd, int i)
        {
            var cp = default(ControlPoint);
            cp.m_Position = new float3(cmd.PosX[i], cmd.PosY[i], cmd.PosZ[i]);
            cp.m_HitPosition = new float3(cmd.HitX[i], cmd.HitY[i], cmd.HitZ[i]);
            cp.m_Direction = new float2(cmd.DirX[i], cmd.DirZ[i]);
            cp.m_HitDirection = new float3(cmd.HitDirX[i], cmd.HitDirY[i], cmd.HitDirZ[i]);
            cp.m_Rotation = new quaternion(cmd.RotX[i], cmd.RotY[i], cmd.RotZ[i], cmd.RotW[i]);
            cp.m_SnapPriority = new float2(cmd.SnapPriX[i], cmd.SnapPriY[i]);
            cp.m_ElementIndex = new int2(cmd.ElemIdxX[i], cmd.ElemIdxY[i]);
            cp.m_CurvePosition = cmd.CurvePos[i];
            cp.m_Elevation = cmd.Elev[i];
            cp.m_OriginalEntity = ResolveSnap(cmd.SnapKind[i], cmd.SnapNodeId[i],
                new float3(cmd.SnapPosX[i], 0f, cmd.SnapPosZ[i]));
            return cp;
        }

        // RandomSeed has a private m_Seed and no int ctor; box+set so both PCs derive identical
        // per-entity randomness (pylon/detail placement) from the shipped seed.
        private static System.Reflection.FieldInfo _seedField;

        private static Game.Common.RandomSeed MakeSeed(int seed)
        {
            if (_seedField == null)
            {
                _seedField = typeof(Game.Common.RandomSeed).GetField("m_Seed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }

            object boxed = default(Game.Common.RandomSeed);
            _seedField.SetValue(boxed, (uint) seed);
            return (Game.Common.RandomSeed) boxed;
        }

        private Entity ResolveSnap(int kind, ulong nodeId, float3 pos)
        {
            if (kind == 0)
            {
                return Entity.Null;
            }

            if (kind == 1) // node — stable id first, else nearest node
            {
                if (CS2M_NodeSyncIds.TryResolve(EntityManager, nodeId, out Entity byId))
                {
                    return byId;
                }

                return NearestOfQuery(_liveNodes, pos, 3f);
            }

            // kind == 2 : edge — nearest edge whose curve passes near the snap position
            return NearestEdge(pos, 3f);
        }

        private Entity NearestOfQuery(EntityQuery q, float3 pos, float maxDist)
        {
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestSq = maxDist * maxDist;
                foreach (Entity e in ents)
                {
                    if (!EntityManager.HasComponent<Node>(e)) { continue; }
                    float3 p = EntityManager.GetComponentData<Node>(e).m_Position;
                    float dx = p.x - pos.x, dz = p.z - pos.z;
                    float d = dx * dx + dz * dz;
                    if (d < bestSq) { bestSq = d; best = e; }
                }

                return best;
            }
            finally { ents.Dispose(); }
        }

        private Entity NearestEdge(float3 pos, float maxDist)
        {
            NativeArray<Entity> ents = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestD = maxDist;
                foreach (Entity e in ents)
                {
                    Colossal.Mathematics.Bezier4x3 c = EntityManager.GetComponentData<Curve>(e).m_Bezier;
                    float d = Colossal.Mathematics.MathUtils.Distance(c.xz, pos.xz, out float _);
                    if (d < bestD) { bestD = d; best = e; }
                }

                return best;
            }
            finally { ents.Dispose(); }
        }
    }
}
