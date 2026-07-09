using System;
using System.Collections.Generic;
using System.Reflection;
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
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Gate for the v67 NATIVE build-preview stream. ON by default (env <c>CS2M_PREVIEW=0</c>
    /// disables). Controls only the SENDER (capture); the receiver systems stay registered but are inert
    /// with nothing arriving.</summary>
    public static class RemotePreview
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_PREVIEW") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>Thread-safe latest-wins inbox: the network thread drops the newest preview per player;
    /// <see cref="PreviewApplySystem"/> drains it on the main thread each frame. Latest-wins because a
    /// preview is a live snapshot — an older frame is worthless once a newer one arrived.</summary>
    public static class RemotePreviewInbox
    {
        private static readonly Dictionary<int, PreviewCommand> Latest = new Dictionary<int, PreviewCommand>();
        private static readonly object Lock = new object();

        public static void Put(PreviewCommand cmd)
        {
            lock (Lock) { Latest[cmd.SenderId] = cmd; }
        }

        public static List<PreviewCommand> Drain()
        {
            lock (Lock)
            {
                if (Latest.Count == 0) { return null; }
                var list = new List<PreviewCommand>(Latest.Values);
                Latest.Clear();
                return list;
            }
        }

        public static void Clear() { lock (Lock) { Latest.Clear(); } }
    }

    /// <summary>Main-thread registry of the live native ghost entities we injected per remote player, plus
    /// the last time we heard from them (for TTL expiry). Written by <see cref="PreviewApplySystem"/> (delete
    /// / TTL) and <see cref="PreviewTagSystem"/> (register the just-created ghosts).</summary>
    internal static class RemotePreviewGhosts
    {
        internal sealed class Rec
        {
            public readonly List<Entity> Ghosts = new List<Entity>();
            public DateTime LastUpdate;
        }

        public static readonly Dictionary<int, Rec> ByPlayer = new Dictionary<int, Rec>();

        public static Rec GetOrAdd(int playerId)
        {
            if (!ByPlayer.TryGetValue(playerId, out Rec r))
            {
                r = new Rec();
                ByPlayer[playerId] = r;
            }

            return r;
        }

        public static void Clear() { ByPlayer.Clear(); }
    }

    /// <summary>
    ///     v67 SENDER: while the LOCAL player has the net or object tool active and the game is showing its
    ///     own ghost, ship the tool's raw input ~12 Hz so every other PC re-materializes the SAME native
    ///     ghost. Sends on-change plus a low-rate keepalive (so a motionless-but-active preview doesn't hit
    ///     the receiver's TTL), and exactly one hide (Kind=0) when the tool goes inactive. Reads only the
    ///     LOCAL tool, and excludes remote ghosts from the object query, so it never echoes a preview back.
    /// </summary>
    public partial class PreviewCaptureSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 5;      // ~12 Hz at 60 fps
        private const double KeepaliveSeconds = 0.5; // resend even if unchanged so the receiver TTL holds

        private ToolSystem _toolSystem;
        private PrefabSystem _prefabSystem;
        private Game.City.CityConfigurationSystem _cityConfig;
        private EntityQuery _objPreview;
        private int _frame;
        private int _lastKind = -1;
        private long _lastSig;
        private DateTime _lastSend = DateTime.MinValue;

        private static FieldInfo _modeField, _prefabField, _seedField, _rsSeedField;

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _cityConfig = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
            // The object tool's in-progress ghost: Temp object with a footprint. Excludes Curve (that's a
            // road ghost, handled via the net tool) and CS2M_RemotePreview (never re-capture a REMOTE ghost).
            _objPreview = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<Game.Objects.ObjectGeometry>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<CS2M_RemotePreview>(),
                },
            });
            CS2M.Log.Info($"[Preview] PreviewCaptureSystem created (enabled={RemotePreview.Enabled})");
        }

        protected override void OnUpdate()
        {
            if (!RemotePreview.Enabled
                || NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_frame < SendEveryNFrames) { return; }
            _frame = 0;

            PreviewCommand cmd = null;
            try { cmd = Capture(); }
            catch (System.Exception ex) { CS2M.Log.Info($"[Guard] preview capture failed: {ex.Message}"); }

            int kind = cmd?.Kind ?? 0;

            // Nothing to preview and we already sent the hide — stay quiet.
            if (kind == 0 && _lastKind == 0) { return; }

            if (kind == 0)
            {
                cmd = new PreviewCommand { Kind = 0 };
            }

            long sig = Signature(cmd);
            bool changed = kind != _lastKind || sig != _lastSig;
            bool keepalive = kind != 0 && (DateTime.UtcNow - _lastSend).TotalSeconds >= KeepaliveSeconds;
            if (!changed && !keepalive) { return; }

            string username = NetworkInterface.Instance.LocalPlayer.Username;
            cmd.Username = string.IsNullOrEmpty(username) ? "Player" : username;

            Command.SendToAll?.Invoke(cmd);
            _lastKind = kind;
            _lastSig = sig;
            _lastSend = DateTime.UtcNow;
        }

        private PreviewCommand Capture()
        {
            // ROAD — the local net tool's live control points.
            if (_toolSystem.activeTool is NetToolSystem netTool)
            {
                return CaptureRoad(netTool);
            }

            // BUILDING — the local object tool's in-progress ghost.
            if (_toolSystem.activeTool is ObjectToolSystem && !_objPreview.IsEmptyIgnoreFilter)
            {
                return CaptureBuilding();
            }

            return null;
        }

        private PreviewCommand CaptureRoad(NetToolSystem netTool)
        {
            NativeList<ControlPoint> pts = netTool.GetControlPoints(out JobHandle deps);
            deps.Complete();
            int n = pts.Length;
            if (n < 2) { return null; }

            var prefab = GetField(ref _prefabField, netTool, "m_Prefab") as PrefabBase;
            if (prefab == null) { return null; }

            int mode = (int) (NetToolSystem.Mode) GetField(ref _modeField, netTool, "m_Mode");

            var cmd = new PreviewCommand
            {
                Kind = 1,
                PrefabType = prefab.GetType().Name,
                PrefabName = prefab.name,
                Mode = mode,
                RandomSeed = ReadSeed(netTool),
                EditorMode = _toolSystem.actionMode.IsEditor(),
                LeftHandTraffic = _cityConfig.leftHandTraffic,
                RemoveUpgrade = false,
                ParallelOffset = 0f,
                ParallelCount = 0,
                PosX = new float[n], PosY = new float[n], PosZ = new float[n],
                HitX = new float[n], HitY = new float[n], HitZ = new float[n],
                DirX = new float[n], DirZ = new float[n],
                HitDirX = new float[n], HitDirY = new float[n], HitDirZ = new float[n],
                RotX = new float[n], RotY = new float[n], RotZ = new float[n], RotW = new float[n],
                SnapPriX = new float[n], SnapPriY = new float[n],
                ElemIdxX = new int[n], ElemIdxY = new int[n],
                CurvePos = new float[n], Elev = new float[n],
                SnapPosX = new float[n], SnapPosZ = new float[n],
                SnapKind = new int[n],
            };

            for (int i = 0; i < n; i++)
            {
                ControlPoint cp = pts[i];
                cmd.PosX[i] = cp.m_Position.x; cmd.PosY[i] = cp.m_Position.y; cmd.PosZ[i] = cp.m_Position.z;
                cmd.HitX[i] = cp.m_HitPosition.x; cmd.HitY[i] = cp.m_HitPosition.y; cmd.HitZ[i] = cp.m_HitPosition.z;
                cmd.DirX[i] = cp.m_Direction.x; cmd.DirZ[i] = cp.m_Direction.y;
                cmd.HitDirX[i] = cp.m_HitDirection.x; cmd.HitDirY[i] = cp.m_HitDirection.y; cmd.HitDirZ[i] = cp.m_HitDirection.z;
                cmd.RotX[i] = cp.m_Rotation.value.x; cmd.RotY[i] = cp.m_Rotation.value.y;
                cmd.RotZ[i] = cp.m_Rotation.value.z; cmd.RotW[i] = cp.m_Rotation.value.w;
                cmd.SnapPriX[i] = cp.m_SnapPriority.x; cmd.SnapPriY[i] = cp.m_SnapPriority.y;
                cmd.ElemIdxX[i] = cp.m_ElementIndex.x; cmd.ElemIdxY[i] = cp.m_ElementIndex.y;
                cmd.CurvePos[i] = cp.m_CurvePosition; cmd.Elev[i] = cp.m_Elevation;
                WriteSnap(cmd, i, cp.m_OriginalEntity);
            }

            return cmd;
        }

        /// <summary>Translate the machine-local m_OriginalEntity to a position-only snap descriptor (a
        /// throwaway ghost mints no stable node id): node/edge kind + world position, re-resolved by
        /// proximity on the receiver.</summary>
        private void WriteSnap(PreviewCommand cmd, int i, Entity e)
        {
            cmd.SnapKind[i] = 0;
            cmd.SnapPosX[i] = 0f;
            cmd.SnapPosZ[i] = 0f;
            if (e == Entity.Null || !EntityManager.Exists(e)) { return; }

            if (EntityManager.HasComponent<Node>(e))
            {
                float3 p = EntityManager.GetComponentData<Node>(e).m_Position;
                cmd.SnapKind[i] = 1;
                cmd.SnapPosX[i] = p.x; cmd.SnapPosZ[i] = p.z;
            }
            else if (EntityManager.HasComponent<Curve>(e))
            {
                float3 p = EntityManager.GetComponentData<Curve>(e).m_Bezier.a;
                cmd.SnapKind[i] = 2;
                cmd.SnapPosX[i] = p.x; cmd.SnapPosZ[i] = p.z;
            }
        }

        private PreviewCommand CaptureBuilding()
        {
            NativeArray<Entity> arr = _objPreview.ToEntityArray(Allocator.Temp);
            try
            {
                // The tool's own placement ghost is the FIRST (and usually only) match; a brush/line drag's
                // extra footprints are ignored — one preview object is enough to convey intent.
                Entity e = arr[0];
                PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(e);
                if (!_prefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) || prefab == null)
                {
                    return null;
                }

                Game.Objects.Transform tf = EntityManager.GetComponentData<Game.Objects.Transform>(e);
                int seed = 0;
                if (EntityManager.HasComponent<PseudoRandomSeed>(e))
                {
                    seed = EntityManager.GetComponentData<PseudoRandomSeed>(e).m_Seed;
                }

                return new PreviewCommand
                {
                    Kind = 2,
                    PrefabType = prefab.GetType().Name,
                    PrefabName = prefab.name,
                    RandomSeed = seed,
                    BPosX = tf.m_Position.x, BPosY = tf.m_Position.y, BPosZ = tf.m_Position.z,
                    BRotX = tf.m_Rotation.value.x, BRotY = tf.m_Rotation.value.y,
                    BRotZ = tf.m_Rotation.value.z, BRotW = tf.m_Rotation.value.w,
                };
            }
            finally { arr.Dispose(); }
        }

        /// <summary>Cheap change signature (quantized) so a still tool doesn't spam the wire.</summary>
        private static long Signature(PreviewCommand c)
        {
            unchecked
            {
                long h = 17 + c.Kind;
                if (c.Kind == 1 && c.PosX != null)
                {
                    h = h * 31 + c.PosX.Length;
                    for (int i = 0; i < c.PosX.Length; i++)
                    {
                        h = h * 31 + Q(c.PosX[i]);
                        h = h * 31 + Q(c.PosZ[i]);
                        h = h * 31 + Q(c.DirX[i]);
                        h = h * 31 + Q(c.DirZ[i]);
                    }
                }
                else if (c.Kind == 2)
                {
                    h = h * 31 + Q(c.BPosX);
                    h = h * 31 + Q(c.BPosZ);
                    h = h * 31 + Q(c.BRotY);
                    h = h * 31 + (c.PrefabName?.GetHashCode() ?? 0);
                }

                return h;
            }
        }

        private static long Q(float v) => (long) math.round(v * 20f); // ~5 cm buckets

        private int ReadSeed(NetToolSystem netTool)
        {
            object rs = GetField(ref _seedField, netTool, "m_RandomSeed");
            if (rs == null) { return 0; }

            if (_rsSeedField == null)
            {
                _rsSeedField = typeof(Game.Common.RandomSeed).GetField("m_Seed",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            return (int) (uint) _rsSeedField.GetValue(rs);
        }

        private static object GetField(ref FieldInfo cache, object target, string name)
        {
            if (cache == null)
            {
                cache = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            }

            return cache?.GetValue(target);
        }
    }

    /// <summary>
    ///     v67 RECEIVER (core). Re-materializes each remote player's live preview as the game's OWN native
    ///     ghost by injecting definitions WITHOUT <see cref="CreationFlags.Permanent"/> and WITHOUT running
    ///     any apply — so <c>GenerateObjects/Nodes/EdgesSystem</c> build a <c>Game.Tools.Temp</c> ghost that
    ///     the game renders exactly like a local drag, but which can NEVER become a real build (Applied/
    ///     Created come only from the game's Apply*System, whose queries require Temp and which we never run;
    ///     see NetToolReplaySystems.cs for the same invariant proven for the REAL build — this does the
    ///     OPPOSITE: it deliberately omits Permanent + apply).
    ///
    ///     Lifecycle (the Temp we inject is NOT auto-cleared — ToolClearSystem only runs when the LOCAL tool
    ///     sets ApplyMode.Clear, decomp ToolOutputSystem.cs:22-30): every new preview for a player deletes
    ///     that player's previous ghost first, then regenerates; a player unheard-from for
    ///     <see cref="TtlSeconds"/> (release/disconnect/crash) is swept; a Kind=0 hide deletes immediately;
    ///     and OnStopRunning nukes every remaining ghost so nothing leaks. Runs before Modification1 so the
    ///     Generate* consumers see the definitions this same frame (same slot as NetPlaceApplySystem).
    /// </summary>
    public partial class PreviewApplySystem : GameSystemBase
    {
        private const double TtlSeconds = 1.5; // > the sender keepalive (0.5 s) so an active preview survives

        private PrefabSystem _prefabSystem;
        private Game.Simulation.WaterSystem _waterSystem;
        private EntityQuery _liveNodes;
        private EntityQuery _liveEdges;
        private EntityQuery _tempAll;
        private EntityQuery _defsQuery;
        private readonly List<Entity> _pendingDefs = new List<Entity>();

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
            _tempAll = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            _defsQuery = GetEntityQuery(ComponentType.ReadWrite<CreationDefinition>());
            CS2M.Log.Info("[Preview] PreviewApplySystem created (native ghost, no Permanent)");
        }

        protected override void OnStopRunning()
        {
            // Defensive teardown: nuke every remote-preview ghost so none survives a leave/reload.
            try
            {
                EntityQuery q = GetEntityQuery(ComponentType.ReadOnly<CS2M_RemotePreview>());
                NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
                foreach (Entity e in ents)
                {
                    if (EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e))
                    {
                        EntityManager.AddComponent<Deleted>(e);
                    }
                }
                ents.Dispose();
            }
            catch (System.Exception ex) { CS2M.Log.Info($"[Guard] preview teardown failed: {ex.Message}"); }

            foreach (Entity d in _pendingDefs)
            {
                if (EntityManager.Exists(d)) { EntityManager.DestroyEntity(d); }
            }
            _pendingDefs.Clear();
            RemotePreviewGhosts.Clear();
            RemotePreviewInbox.Clear();
            PreviewTagSystem.Disarm();
            base.OnStopRunning();
        }

        protected override void OnUpdate()
        {
            // Destroy last frame's consumed definition entities (Generate* already read them).
            for (int i = 0; i < _pendingDefs.Count; i++)
            {
                if (EntityManager.Exists(_pendingDefs[i])) { EntityManager.DestroyEntity(_pendingDefs[i]); }
            }
            _pendingDefs.Clear();

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            List<PreviewCommand> cmds = RemotePreviewInbox.Drain();

            // TTL: expire players we haven't heard from (release / disconnect / crash).
            DateTime now = DateTime.UtcNow;
            List<int> expired = null;
            foreach (KeyValuePair<int, RemotePreviewGhosts.Rec> kv in RemotePreviewGhosts.ByPlayer)
            {
                if ((now - kv.Value.LastUpdate).TotalSeconds > TtlSeconds)
                {
                    (expired ?? (expired = new List<int>())).Add(kv.Key);
                }
            }

            if (expired != null)
            {
                foreach (int pid in expired)
                {
                    DeletePlayerGhosts(pid);
                    RemotePreviewGhosts.ByPlayer.Remove(pid);
                }
            }

            if (cmds == null || cmds.Count == 0) { return; }

            // Snapshot live Temp BEFORE injecting so PreviewTagSystem (Mod5) can identify exactly the ghosts
            // OUR definitions produce at Modification1 this frame.
            var preTemp = new HashSet<Entity>();
            NativeArray<Entity> pre = _tempAll.ToEntityArray(Allocator.Temp);
            foreach (Entity e in pre) { preTemp.Add(e); }
            pre.Dispose();

            var pending = new List<PreviewTagSystem.Pending>();
            foreach (PreviewCommand cmd in cmds)
            {
                try { ApplyOne(cmd, pending); }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] preview apply failed: {ex.Message}"); }
            }

            if (pending.Count > 0)
            {
                PreviewTagSystem.Arm(preTemp, pending);
            }
        }

        private void ApplyOne(PreviewCommand cmd, List<PreviewTagSystem.Pending> pending)
        {
            int playerId = cmd.SenderId;

            // Every update replaces the player's previous ghost (imitates the tool's own clear+regenerate).
            DeletePlayerGhosts(playerId);
            RemotePreviewGhosts.Rec rec = RemotePreviewGhosts.GetOrAdd(playerId);
            rec.Ghosts.Clear();
            rec.LastUpdate = DateTime.UtcNow;

            if (cmd.Kind == 1) { InjectRoadGhost(cmd, playerId, pending); }
            else if (cmd.Kind == 2) { InjectBuildingGhost(cmd, playerId, pending); }
            // Kind == 0 (hide): nothing to inject — the delete above already cleared it.
        }

        private void InjectBuildingGhost(PreviewCommand cmd, int playerId, List<PreviewTagSystem.Pending> pending)
        {
            var hash = new Colossal.Hash128(0, 0, 0, 0);
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, hash);
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out Entity prefabEntity))
            {
                return;
            }

            var pos = new float3(cmd.BPosX, cmd.BPosY, cmd.BPosZ);
            var rot = new quaternion(cmd.BRotX, cmd.BRotY, cmd.BRotZ, cmd.BRotW);

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = prefabEntity,
                m_Flags = 0, // NO Permanent → GenerateObjectsSystem tags the created object Temp (a ghost)
                m_RandomSeed = cmd.RandomSeed,
            });
            EntityManager.AddComponentData(def, new ObjectDefinition
            {
                m_Position = pos,
                m_Rotation = rot,
                m_LocalPosition = pos,
                m_LocalRotation = rot,
                m_Scale = new float3(1f, 1f, 1f),
                m_Elevation = 0f,
                m_Intensity = 0f,
                m_Age = 0f,
                m_ParentMesh = -1,
                m_GroupIndex = 0,
                m_Probability = 100,
                m_PrefabSubIndex = -1,
            });
            EntityManager.AddComponent<Updated>(def);
            _pendingDefs.Add(def);

            pending.Add(new PreviewTagSystem.Pending
            {
                PlayerId = playerId,
                Kind = 2,
                Prefab = prefabEntity,
                Anchors = new List<float3> { pos },
            });
        }

        private void InjectRoadGhost(PreviewCommand cmd, int playerId, List<PreviewTagSystem.Pending> pending)
        {
            var hash = new Colossal.Hash128(0, 0, 0, 0);
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, hash);
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out Entity netPrefab))
            {
                return;
            }

            int n = cmd.PosX?.Length ?? 0;
            if (n < 2) { return; }

            var controlPoints = new NativeList<ControlPoint>(n, Allocator.TempJob);
            var upgradeStates = new NativeList<NetToolSystem.UpgradeState>(Allocator.TempJob);
            var ecb = new EntityCommandBuffer(Allocator.TempJob);
            var anchors = new List<float3>(n);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    ControlPoint cp = RebuildControlPoint(cmd, i);
                    controlPoints.Add(cp);
                    anchors.Add(cp.m_Position);
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

                // Snapshot existing definitions so we can find (and later destroy) the ones OUR job creates.
                // Taken inside this OnUpdate around OUR playback, so no other system's definitions interleave.
                var beforeDefs = new HashSet<Entity>();
                NativeArray<Entity> pre = _defsQuery.ToEntityArray(Allocator.Temp);
                foreach (Entity pe in pre) { beforeDefs.Add(pe); }
                pre.Dispose();

                JobHandle handle = IJobExtensions.Schedule(job, waterDeps);
                _waterSystem.AddVelocitySurfaceReader(handle);
                handle.Complete();
                ecb.Playback(EntityManager);

                // Collect our new definitions to destroy next frame — and CRUCIALLY do NOT stamp Permanent
                // (the opposite of NetToolReplaySystem.ReplayOne): they stay preview definitions, so
                // GenerateNodes/EdgesSystem build a Temp net that renders as a ghost and never gets Applied.
                NativeArray<Entity> post = _defsQuery.ToEntityArray(Allocator.Temp);
                foreach (Entity de in post)
                {
                    if (!beforeDefs.Contains(de)) { _pendingDefs.Add(de); }
                }
                post.Dispose();

                pending.Add(new PreviewTagSystem.Pending
                {
                    PlayerId = playerId,
                    Kind = 1,
                    Prefab = netPrefab,
                    Anchors = anchors,
                });
            }
            finally
            {
                controlPoints.Dispose();
                upgradeStates.Dispose();
                ecb.Dispose();
            }
        }

        private ControlPoint RebuildControlPoint(PreviewCommand cmd, int i)
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
            cp.m_OriginalEntity = ResolveSnap(cmd.SnapKind[i], new float3(cmd.SnapPosX[i], 0f, cmd.SnapPosZ[i]));
            return cp;
        }

        private Entity ResolveSnap(int kind, float3 pos)
        {
            if (kind == 1) { return NearestNode(pos, 3f); }
            if (kind == 2) { return NearestEdge(pos, 3f); }
            return Entity.Null;
        }

        private Entity NearestNode(float3 pos, float maxDist)
        {
            NativeArray<Entity> ents = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestSq = maxDist * maxDist;
                foreach (Entity e in ents)
                {
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

        private void DeletePlayerGhosts(int playerId)
        {
            if (!RemotePreviewGhosts.ByPlayer.TryGetValue(playerId, out RemotePreviewGhosts.Rec rec))
            {
                return;
            }

            var roots = new List<Entity>();
            foreach (Entity e in rec.Ghosts)
            {
                if (EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e))
                {
                    EntityManager.AddComponent<Deleted>(e);
                    roots.Add(e);
                }
            }

            rec.Ghosts.Clear();
            if (roots.Count > 0) { DeleteTempChildren(roots); }
        }

        /// <summary>Cascade Deleted onto any live Temp entity owned (transitively) by a just-deleted ghost
        /// root — sub-lanes / sub-objects the Generate* pipeline hung off the ghost. Includes Temp (unlike
        /// CascadeDeleteUtil, which excludes it) because ghost children ARE Temp; never touches an entity
        /// not under one of our roots, so the local player's own tool preview is safe.</summary>
        private void DeleteTempChildren(List<Entity> roots)
        {
            var deleted = new HashSet<Entity>(roots);
            for (int depth = 0; depth < 3; depth++)
            {
                bool any = false;
                EntityQuery owned = GetEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>() },
                });
                NativeArray<Entity> ents = owned.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity child in ents)
                    {
                        Entity owner = EntityManager.GetComponentData<Owner>(child).m_Owner;
                        if (owner != Entity.Null && deleted.Contains(owner))
                        {
                            EntityManager.AddComponent<Deleted>(child);
                            deleted.Add(child);
                            any = true;
                        }
                    }
                }
                finally { ents.Dispose(); }

                if (!any) { break; }
            }
        }

        private static FieldInfo _seedField;

        private static Game.Common.RandomSeed MakeSeed(int seed)
        {
            if (_seedField == null)
            {
                _seedField = typeof(Game.Common.RandomSeed).GetField("m_Seed",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            object boxed = default(Game.Common.RandomSeed);
            _seedField.SetValue(boxed, (uint) seed);
            return (Game.Common.RandomSeed) boxed;
        }
    }

    /// <summary>
    ///     v67 RECEIVER (tag). Runs at Modification5 — after GenerateObjects@Mod1 / GenerateNodes@Mod1 /
    ///     GenerateEdges@Mod2 have materialized the Temp ghosts from the definitions
    ///     <see cref="PreviewApplySystem"/> injected before Modification1 this same frame (identical slot
    ///     logic to NetToolReplayApplySystem). Diffs the current Temp set against the pre-injection snapshot,
    ///     keeps only the entities that match an injection by prefab + position (so the LOCAL player's own
    ///     simultaneous tool ghost is never mis-tagged), stamps <see cref="CS2M_RemotePreview"/> on them and
    ///     registers them so <see cref="PreviewApplySystem"/> can delete exactly these next tick.
    /// </summary>
    public partial class PreviewTagSystem : GameSystemBase
    {
        internal struct Pending
        {
            public int PlayerId;
            public int Kind;        // 1 road, 2 building
            public Entity Prefab;
            public List<float3> Anchors;
        }

        private const float BuildingMatchRadius = 1.5f;
        private const float RoadMatchRadius = 8f;

        private EntityQuery _tempAll;
        private static HashSet<Entity> _preTemp;
        private static List<Pending> _pending;
        private static bool _armed;

        protected override void OnCreate()
        {
            base.OnCreate();
            _tempAll = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            CS2M.Log.Info("[Preview] PreviewTagSystem created (Mod5 ghost tagger)");
        }

        internal static void Arm(HashSet<Entity> preTemp, List<Pending> pending)
        {
            _preTemp = preTemp;
            _pending = pending;
            _armed = true;
        }

        internal static void Disarm()
        {
            _preTemp = null;
            _pending = null;
            _armed = false;
        }

        protected override void OnUpdate()
        {
            if (!_armed) { return; }
            _armed = false;
            HashSet<Entity> preTemp = _preTemp;
            List<Pending> pending = _pending;
            _preTemp = null;
            _pending = null;
            if (preTemp == null || pending == null) { return; }

            NativeArray<Entity> nowTemp = _tempAll.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in nowTemp)
                {
                    if (preTemp.Contains(e)) { continue; } // existed before our injection — not ours
                    if (EntityManager.HasComponent<CS2M_RemotePreview>(e)) { continue; }

                    if (!TryGetPos(e, out float3 pos)) { continue; }

                    int matched = -1;
                    for (int p = 0; p < pending.Count; p++)
                    {
                        Pending pd = pending[p];
                        if (pd.Kind == 2)
                        {
                            // Building: prefab + position must match (so a local same-frame ghost of a
                            // DIFFERENT prefab/position is never captured).
                            if (!PrefabIs(e, pd.Prefab)) { continue; }
                            if (Near(pos, pd.Anchors, BuildingMatchRadius)) { matched = p; break; }
                        }
                        else
                        {
                            // Road: any new Temp node/edge/pillar sitting near a control point.
                            if (NearRoad(e, pos, pd.Anchors, RoadMatchRadius)) { matched = p; break; }
                        }
                    }

                    if (matched < 0) { continue; }

                    int playerId = pending[matched].PlayerId;
                    EntityManager.AddComponentData(e, new CS2M_RemotePreview { PlayerId = playerId });
                    RemotePreviewGhosts.Rec rec = RemotePreviewGhosts.GetOrAdd(playerId);
                    rec.Ghosts.Add(e);
                    rec.LastUpdate = DateTime.UtcNow;
                }
            }
            finally { nowTemp.Dispose(); }
        }

        private bool TryGetPos(Entity e, out float3 pos)
        {
            if (EntityManager.HasComponent<Node>(e))
            {
                pos = EntityManager.GetComponentData<Node>(e).m_Position;
                return true;
            }

            if (EntityManager.HasComponent<Game.Objects.Transform>(e))
            {
                pos = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                return true;
            }

            if (EntityManager.HasComponent<Curve>(e))
            {
                pos = EntityManager.GetComponentData<Curve>(e).m_Bezier.a; // endpoints tested in NearRoad
                return true;
            }

            pos = default;
            return false;
        }

        private bool PrefabIs(Entity e, Entity prefab)
        {
            return EntityManager.HasComponent<PrefabRef>(e)
                   && EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == prefab;
        }

        private static bool Near(float3 pos, List<float3> anchors, float radius)
        {
            float r2 = radius * radius;
            foreach (float3 a in anchors)
            {
                float dx = a.x - pos.x, dz = a.z - pos.z;
                if (dx * dx + dz * dz <= r2) { return true; }
            }

            return false;
        }

        private bool NearRoad(Entity e, float3 fallbackPos, List<float3> anchors, float radius)
        {
            // For an edge, test BOTH bezier endpoints (each sits at/near a control point regardless of the
            // road's length); for nodes/objects, the single representative position.
            if (EntityManager.HasComponent<Curve>(e))
            {
                Colossal.Mathematics.Bezier4x3 c = EntityManager.GetComponentData<Curve>(e).m_Bezier;
                return Near(c.a, anchors, radius) || Near(c.d, anchors, radius);
            }

            return Near(fallbackPos, anchors, radius);
        }
    }
}
