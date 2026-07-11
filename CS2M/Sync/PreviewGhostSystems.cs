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

                // Never ship a NaN/Inf control point onto the wire: the receiver feeds these straight into
                // NetToolSystem.CreateDefinitionsJob (curve/geometry math) with no validation of its own, so
                // a bad float here would propagate into a live Burst job on every other machine.
                if (!math.all(math.isfinite(cp.m_Position)) || !math.all(math.isfinite(cp.m_HitPosition))
                    || !math.all(math.isfinite(cp.m_Direction)) || !math.all(math.isfinite(cp.m_HitDirection))
                    || !math.all(math.isfinite(cp.m_Rotation.value)))
                {
                    CS2M.Log.Info($"[Preview] CAPTURE-DROP road control point {i}/{n} has NaN/Inf — not sending");
                    return null;
                }

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

                // Never ship a NaN/Inf transform — same rationale as the road control-point guard above:
                // the receiver's InjectBuildingGhost feeds this straight into ObjectDefinition/GenerateObjectsSystem.
                if (!math.all(math.isfinite(tf.m_Position)) || !math.all(math.isfinite(tf.m_Rotation.value)))
                {
                    CS2M.Log.Info("[Preview] CAPTURE-DROP building ghost has NaN/Inf transform — not sending");
                    return null;
                }

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
    ///     Lifecycle (redesigned 11/07/2026 after 3 stress runs measured the old "DeletePlayerGhosts +
    ///     recreate definition on every command (~12-60 Hz)" pattern fighting the engine's own Temp-reuse
    ///     optimization: pre-fix it leaked orphans, post-fix it turned into a 40k-ops/5s delete/revive
    ///     perpetual-motion machine (decomp GenerateObjectsSystem.cs:119-138/745-783/1100-1104, same reuse
    ///     documented on <see cref="PreviewTagSystem"/>). The REAL object/net tool never deletes its own
    ///     in-progress definition per frame either: it keeps the SAME definition entity alive and mutates it,
    ///     and the Generate* pipeline updates the existing Temp in place. This system now does the same,
    ///     split by kind):
    ///     - BUILDING (Kind=2): update-in-place. The player's <c>DefSlot</c> keeps the live
    ///       CreationDefinition/ObjectDefinition entity across frames; a same-prefab command just overwrites
    ///       ObjectDefinition (position/rotation) and re-stamps <see cref="Updated"/> — no delete, no new
    ///       entity, no tagger re-arm (the ghost already carries <see cref="CS2M_RemotePreview"/>). A
    ///       prefab/kind change or first sight falls back to delete+recreate and registers a fresh slot.
    ///     - ROAD (Kind=1): still delete+recreate (CreateDefinitionsJob has no reusable single-entity target —
    ///       it can emit any number of node/edge definitions), but throttled: a 0.5 m-quantized signature of
    ///       the control points + prefab + mode is compared against the slot's last-applied signature, and an
    ///       unchanged command (e.g. the sender's 0.5 s keepalive while the tool is held still) is dropped
    ///       before touching any entity — only the TTL clock is refreshed.
    ///     - HIDE (Kind=0) and TTL expiry: delete the rendered ghost AND destroy the slot's definition entity
    ///       (it no longer lives in <c>_pendingDefs</c>, so nothing else will ever destroy it) and drop the
    ///       slot; OnStopRunning does the same for every remaining slot so nothing leaks on unload.
    ///     Runs before Modification1 so the Generate* consumers see the definitions this same frame (same
    ///     slot as NetPlaceApplySystem).
    /// </summary>
    public partial class PreviewApplySystem : GameSystemBase
    {
        private const double TtlSeconds = 1.5; // > the sender keepalive (0.5 s) so an active preview survives

        // v73.1 validation counters (throttled log 1×/5s): prove in the logs that the native-ghost path
        // actually ran — apply/delete/drop are otherwise fully silent, which made a stress run's "no
        // crash" unfalsifiable (couldn't tell exercised-and-survived from silently-dropped).
        // _statUpdatedInPlace (11/07/2026) separately proves the update-in-place path is the one actually
        // running under normal building-preview traffic, not the delete+recreate fallback.
        private int _statApplied, _statDeleted, _statDropped, _statUpdatedInPlace;
        private DateTime _lastStatLog = DateTime.MinValue;

        private PrefabSystem _prefabSystem;
        private Game.Simulation.WaterSystem _waterSystem;
        private EntityQuery _liveNodes;
        private EntityQuery _liveEdges;
        private EntityQuery _tempAll;
        private EntityQuery _defsQuery;
        private readonly List<Entity> _pendingDefs = new List<Entity>();

        /// <summary>Per-player live definition slot (11/07/2026 redesign). For a BUILDING (Kind=2) this is
        /// the SAME CreationDefinition/ObjectDefinition entity across frames — update-in-place mutates it
        /// instead of deleting/recreating. For a ROAD (Kind=1) <see cref="Def"/> is unused
        /// (<see cref="Entity.Null"/>): CreateDefinitionsJob's outputs are transient and still flow through
        /// <see cref="_pendingDefs"/>; only <see cref="RoadSig"/> (the last-applied quantized signature) is
        /// tracked here, to throttle repeat/keepalive commands.</summary>
        private sealed class DefSlot
        {
            public Entity Def;
            public int Kind;         // 1 road, 2 building — mirrors PreviewCommand.Kind
            public string PrefabName;
            public long RoadSig;     // road only: last-applied quantized control-point signature
        }

        private readonly Dictionary<int, DefSlot> _defSlots = new Dictionary<int, DefSlot>();

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

            // The update-in-place redesign (11/07/2026) means a building's definition entity can be alive
            // and NOT in _pendingDefs — it never leaks in normal play (Kind=0/TTL destroy it), but a leave
            // mid-preview must still sweep every remaining slot so nothing survives the world teardown.
            foreach (KeyValuePair<int, DefSlot> kv in _defSlots)
            {
                if (kv.Value.Def != Entity.Null && EntityManager.Exists(kv.Value.Def))
                {
                    EntityManager.DestroyEntity(kv.Value.Def);
                }
            }
            _defSlots.Clear();

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
                    DestroySlotDefIfAny(pid); // the slot's def (building) or bookkeeping (road) dies with it
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

            // Only a CREATION this frame (InjectBuildingGhost/InjectRoadGhost) ever adds to `pending` — an
            // update-in-place (UpdateBuildingInPlace) or a throttled road skip (ApplyRoad) touches nothing
            // new, so there is nothing for PreviewTagSystem to tag and it correctly stays un-armed.
            if (pending.Count > 0)
            {
                PreviewTagSystem.Arm(preTemp, pending);
            }

            if ((now - _lastStatLog).TotalSeconds > 5
                && (_statApplied | _statDeleted | _statDropped | _statUpdatedInPlace) != 0)
            {
                _lastStatLog = now;
                CS2M.Log.Info($"[Preview] GHOST stats applied={_statApplied} updated={_statUpdatedInPlace} " +
                              $"deleted={_statDeleted} droppedPrefab={_statDropped}");
                _statApplied = 0; _statDeleted = 0; _statDropped = 0; _statUpdatedInPlace = 0;
            }
        }

        private void ApplyOne(PreviewCommand cmd, List<PreviewTagSystem.Pending> pending)
        {
            int playerId = cmd.SenderId;

            if (cmd.Kind == 2) { ApplyBuilding(cmd, playerId, pending); return; }
            if (cmd.Kind == 1) { ApplyRoad(cmd, playerId, pending); return; }
            ApplyHide(playerId); // Kind == 0
        }

        /// <summary>BUILDING dispatch (11/07/2026 redesign): reuse the player's live definition entity when
        /// possible (same prefab, slot still alive) — pure update-in-place, no delete, no new entity, tagger
        /// not re-armed. Otherwise falls back to the original delete+recreate (first sight / prefab or kind
        /// switch / slot lost).</summary>
        private void ApplyBuilding(PreviewCommand cmd, int playerId, List<PreviewTagSystem.Pending> pending)
        {
            if (_defSlots.TryGetValue(playerId, out DefSlot slot)
                && slot.Kind == 2
                && slot.Def != Entity.Null
                && EntityManager.Exists(slot.Def)
                && slot.PrefabName == cmd.PrefabName)
            {
                UpdateBuildingInPlace(cmd, slot);
                return;
            }

            // No reusable slot: destroy whatever the slot pointed at (a stale/foreign-kind def would
            // otherwise never get destroyed — it lives outside _pendingDefs now), then delete+recreate.
            DestroySlotDefIfAny(playerId);
            DeletePlayerGhosts(playerId);
            RemotePreviewGhosts.Rec rec = RemotePreviewGhosts.GetOrAdd(playerId);
            rec.Ghosts.Clear();
            rec.LastUpdate = DateTime.UtcNow;
            InjectBuildingGhost(cmd, playerId, pending);
        }

        /// <summary>Mutate the SAME CreationDefinition's ObjectDefinition in place (position/rotation only —
        /// every other field is the constant the definition was created with) and re-stamp
        /// <see cref="Updated"/> so the Generate* pipeline refreshes the existing Temp ghost instead of
        /// building a new one. No delete, no new entity, no <see cref="PreviewTagSystem"/> re-arm (the ghost
        /// already carries <see cref="CS2M_RemotePreview"/> from when the slot was created).</summary>
        private void UpdateBuildingInPlace(PreviewCommand cmd, DefSlot slot)
        {
            var pos = new float3(cmd.BPosX, cmd.BPosY, cmd.BPosZ);
            var rot = new quaternion(cmd.BRotX, cmd.BRotY, cmd.BRotZ, cmd.BRotW);

            // Same NaN/Inf guard as InjectBuildingGhost's initial creation — a corrupt update must not reach
            // the live definition (it would corrupt the ghost already on screen, not just fail to appear).
            if (!math.all(math.isfinite(pos)) || !math.all(math.isfinite(rot.value)))
            {
                CS2M.Log.Info($"[Preview] GHOST-FAIL building '{slot.PrefabName}' has NaN/Inf transform — update dropped");
                _statDropped++;
                return;
            }

            EntityManager.SetComponentData(slot.Def, new ObjectDefinition
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

            if (!EntityManager.HasComponent<Updated>(slot.Def)) { EntityManager.AddComponent<Updated>(slot.Def); }

            RemotePreviewGhosts.GetOrAdd(cmd.SenderId).LastUpdate = DateTime.UtcNow;
            _statUpdatedInPlace++;
        }

        /// <summary>ROAD dispatch: still delete+recreate (CreateDefinitionsJob has no single reusable target
        /// entity — it can emit any number of node/edge definitions per call), but throttled by a quantized
        /// signature so an unchanged command (typically the sender's 0.5 s keepalive) costs one hash compare
        /// and nothing else.</summary>
        private void ApplyRoad(PreviewCommand cmd, int playerId, List<PreviewTagSystem.Pending> pending)
        {
            long sig = RoadSignature(cmd);
            if (_defSlots.TryGetValue(playerId, out DefSlot slot)
                && slot.Kind == 1
                && slot.PrefabName == cmd.PrefabName
                && slot.RoadSig == sig)
            {
                // Identical (quantized) control points as last time — do NOT delete, do NOT recreate; only
                // keep the TTL clock alive so a held-still road tool doesn't get swept.
                RemotePreviewGhosts.GetOrAdd(playerId).LastUpdate = DateTime.UtcNow;
                return;
            }

            // Shape/prefab changed, first sight, or a kind switch away from a building slot: drop any
            // leftover def from a different kind, then delete+recreate as before.
            DestroySlotDefIfAny(playerId);
            DeletePlayerGhosts(playerId);
            RemotePreviewGhosts.Rec rec = RemotePreviewGhosts.GetOrAdd(playerId);
            rec.Ghosts.Clear();
            rec.LastUpdate = DateTime.UtcNow;
            InjectRoadGhost(cmd, playerId, pending, sig);
        }

        private void ApplyHide(int playerId)
        {
            DeletePlayerGhosts(playerId);
            DestroySlotDefIfAny(playerId); // no more definition to update — kill it, it's outside _pendingDefs
            RemotePreviewGhosts.Rec rec = RemotePreviewGhosts.GetOrAdd(playerId);
            rec.Ghosts.Clear();
            rec.LastUpdate = DateTime.UtcNow;
        }

        /// <summary>Destroy the player's live definition entity (building only — road slots carry
        /// <see cref="Entity.Null"/>) and drop the slot itself. Called whenever the slot's identity is about
        /// to change (kind/prefab switch, hide, TTL) so a definition living OUTSIDE <c>_pendingDefs</c>
        /// is never orphaned.</summary>
        private void DestroySlotDefIfAny(int playerId)
        {
            if (_defSlots.TryGetValue(playerId, out DefSlot slot))
            {
                if (slot.Def != Entity.Null && EntityManager.Exists(slot.Def)) { EntityManager.DestroyEntity(slot.Def); }
                _defSlots.Remove(playerId);
            }
        }

        /// <summary>Quantized (0.5 m) signature of a road command's control points + prefab + mode, used to
        /// throttle ApplyRoad: coarser than PreviewCaptureSystem's own ~5 cm send-side signature on purpose —
        /// this one only needs to catch "truly unchanged" (keepalive) traffic, not every sub-5cm jitter.</summary>
        private static long RoadSignature(PreviewCommand cmd)
        {
            unchecked
            {
                long h = 17;
                h = h * 31 + (cmd.PrefabName?.GetHashCode() ?? 0);
                h = h * 31 + cmd.Mode;
                int n = cmd.PosX?.Length ?? 0;
                h = h * 31 + n;
                for (int i = 0; i < n; i++)
                {
                    h = h * 31 + QRoad(cmd.PosX[i]);
                    h = h * 31 + QRoad(cmd.PosY[i]);
                    h = h * 31 + QRoad(cmd.PosZ[i]);
                }

                return h;
            }
        }

        private static long QRoad(float v) => (long) math.round(v * 2f); // 0.5 m buckets

        private void InjectBuildingGhost(PreviewCommand cmd, int playerId, List<PreviewTagSystem.Pending> pending)
        {
            var hash = new Colossal.Hash128(0, 0, 0, 0);
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, hash);
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out Entity prefabEntity))
            {
                _statDropped++;
                return;
            }

            // Radar for a silent drop: FillCreationListJob discards any definition whose prefab entity has
            // no ObjectData/archetype WITHOUT logging anything (decomp GenerateObjectsSystem.cs:377) — the
            // ghost would just never appear, indistinguishable from "still in flight" or "TTL'd out". Same
            // check RemotePlacementApplySystem already does for the real (Permanent) placement path.
            if (!EntityManager.HasComponent<Game.Prefabs.ObjectData>(prefabEntity))
            {
                CS2M.Log.Info($"[Preview] GHOST-FAIL prefab '{cmd.PrefabName}' has no ObjectData/archetype — definition would be silently dropped");
                _statDropped++;
                return;
            }

            var pos = new float3(cmd.BPosX, cmd.BPosY, cmd.BPosZ);
            var rot = new quaternion(cmd.BRotX, cmd.BRotY, cmd.BRotZ, cmd.BRotW);

            // Reject NaN/Inf transforms before they reach CreationDefinition/ObjectDefinition — a corrupt
            // float here (bad network payload, upstream game NaN) would otherwise ride straight into
            // GenerateObjectsSystem and its downstream math (bounds, colliders, culling). isfinite() is
            // false for both NaN and +/-Infinity (the isnan-only check at PlayerCursorSystem.cs:131 is not
            // enough here since these floats cross the network unclamped).
            if (!math.all(math.isfinite(pos)) || !math.all(math.isfinite(rot.value)))
            {
                CS2M.Log.Info($"[Preview] GHOST-FAIL building '{cmd.PrefabName}' has NaN/Inf transform — dropped");
                _statDropped++;
                return;
            }

            _statApplied++;

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

            // 11/07/2026: this definition entity now LIVES across frames (update-in-place, see ApplyBuilding/
            // UpdateBuildingInPlace) — it must NOT go into _pendingDefs (that list is destroyed every frame),
            // only DestroySlotDefIfAny (Kind=0/TTL/kind-switch/OnStopRunning) is allowed to kill it.
            _defSlots[playerId] = new DefSlot { Def = def, Kind = 2, PrefabName = cmd.PrefabName };

            pending.Add(new PreviewTagSystem.Pending
            {
                PlayerId = playerId,
                Kind = 2,
                Prefab = prefabEntity,
                Anchors = new List<float3> { pos },
            });
        }

        private void InjectRoadGhost(PreviewCommand cmd, int playerId, List<PreviewTagSystem.Pending> pending, long sig)
        {
            var hash = new Colossal.Hash128(0, 0, 0, 0);
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, hash);
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out Entity netPrefab))
            {
                _statDropped++;
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

                    // Defense in depth: PreviewCaptureSystem already refuses to send a NaN/Inf point, but
                    // this receiver also serves whatever arrives on the wire, so validate again here before
                    // it reaches CreateDefinitionsJob's curve/geometry Burst math. A single bad point makes
                    // the whole control-point list (and the curve it defines) meaningless, so the entire
                    // command is dropped rather than just the one point.
                    if (!IsFiniteControlPoint(cp)
                        || !math.isfinite(cmd.SnapPosX[i]) || !math.isfinite(cmd.SnapPosZ[i]))
                    {
                        CS2M.Log.Info($"[Preview] GHOST-FAIL road control point {i}/{n} has NaN/Inf — command dropped");
                        _statDropped++;
                        return;
                    }

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

                // Road slots never hold a live entity (Def stays Null — CreateDefinitionsJob's outputs are
                // transient and already tracked via _pendingDefs); only RoadSig survives, to throttle the
                // next command's delete+recreate against this one.
                _defSlots[playerId] = new DefSlot { Def = Entity.Null, Kind = 1, PrefabName = cmd.PrefabName, RoadSig = sig };
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

        /// <summary>NaN/Inf guard for a rebuilt control point (position/direction/rotation only — snap kind
        /// and element indices are integers, immune). Mirrors the sender-side check in
        /// PreviewCaptureSystem.CaptureRoad so a corrupt point is rejected on both ends of the wire.</summary>
        private static bool IsFiniteControlPoint(ControlPoint cp)
        {
            return math.all(math.isfinite(cp.m_Position))
                   && math.all(math.isfinite(cp.m_HitPosition))
                   && math.all(math.isfinite(cp.m_Direction))
                   && math.all(math.isfinite(cp.m_HitDirection))
                   && math.all(math.isfinite(cp.m_Rotation.value));
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
            if (roots.Count > 0) { _statDeleted += roots.Count; DeleteTempChildren(roots); }
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

            int statNew = 0, statTagged = 0, statRevived = 0, statInherited = 0;
            NativeArray<Entity> nowTemp = _tempAll.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in nowTemp)
                {
                    // Self-heal / re-association ramo: GenerateObjectsSystem's REUSE optimization can
                    // un-delete our OWN previous ghost instead of creating a fresh one — when we mark it
                    // Deleted and inject a same-prefab definition the same frame, FillOldObjectsJob /
                    // CollectCreationDataJob match it and revive it (RemoveComponent<Deleted> + SetComponent
                    // Transform) rather than spawning new (decomp GenerateObjectsSystem.cs:119-138 FillOldObjectsJob,
                    // :745-783 CollectCreationDataJob, :1100-1104 un-delete). The revived entity keeps its
                    // CS2M_RemotePreview tag from before, but ApplyOne already Clear()ed rec.Ghosts this
                    // frame, so it's an orphan bookkeeping-wise. preTemp-membership alone can't distinguish
                    // "existed before, not ours" from "revived before our snapshot, still ours": at the time
                    // PreviewApplySystem snapshots preTemp, the entity still carries last frame's Deleted (it
                    // is only un-deleted later, at Modification1 by GenerateObjectsSystem), so preTemp
                    // (Temp AND NOT Deleted) excludes it — it looks brand-new here even though it's a revival.
                    // Re-link it into the registry so the next DeletePlayerGhosts/TTL sweep can reach it;
                    // this must run on EVERY tagged Temp this frame (not just ones outside preTemp), so the
                    // preTemp skip below is intentionally ordered AFTER this branch. Known trade-off: this
                    // loop only runs when SOME player's command armed it (PreviewApplySystem.Arm), but it
                    // walks every still-tagged Temp in the world, not just that player's — so a disconnected
                    // player's not-yet-TTL'd ghost also gets its LastUpdate nudged forward whenever any OTHER
                    // player is actively previewing. Bounded (TTL still fires once nobody previews for
                    // TtlSeconds) and far narrower than the orphan leak this fix closes.
                    if (EntityManager.HasComponent<CS2M_RemotePreview>(e))
                    {
                        int revivedPlayerId = EntityManager.GetComponentData<CS2M_RemotePreview>(e).PlayerId;
                        RemotePreviewGhosts.Rec revivedRec = RemotePreviewGhosts.GetOrAdd(revivedPlayerId);
                        if (!revivedRec.Ghosts.Contains(e)) { revivedRec.Ghosts.Add(e); }
                        revivedRec.LastUpdate = DateTime.UtcNow;
                        continue;
                    }

                    // MECANISM 1 — revive match: measured over two 10-min stress runs, tagged stayed 0
                    // because the building ghost's ROOT is not "new" Temp past the first cycle at all — the
                    // engine's REUSE optimization (decomp GenerateObjectsSystem.cs:119-138 FillOldObjectsJob,
                    // :745-783 CollectCreationDataJob, :1100-1104 un-delete) finds the SAME entity we marked
                    // Deleted last tick and revives it IN PLACE (RemoveComponent<Deleted> + SetComponent
                    // Transform to the new anchor) — but only when it still carries CS2M_RemotePreview from
                    // before, which the self-heal branch above requires. When it does NOT (e.g. the very
                    // first tag never landed, or a prior sweep dropped it), the revived root is
                    // indistinguishable here from any other live Temp: it is NOT in preTemp (it still
                    // carried last frame's Deleted at snapshot time, same reasoning as the self-heal comment
                    // above), so it WOULD flow into the "new" path below — except its position is now the
                    // NEW anchor, not wherever it sat before, so it is exactly as identifiable as a genuinely
                    // new Temp: prefab-exact + within BuildingMatchRadius of a Kind=2 pending's anchor.
                    // Checked BEFORE the preTemp skip (independent of preTemp membership) because a revived
                    // root that DOES still look "old" some frame is just as valid a match as one that looks
                    // "new" — the anchor proximity is what actually proves identity, not preTemp status.
                    if (TryReviveMatch(e, pending, out int revivedById))
                    {
                        EntityManager.AddComponentData(e, new CS2M_RemotePreview { PlayerId = revivedById });
                        RemotePreviewGhosts.Rec reviveRec = RemotePreviewGhosts.GetOrAdd(revivedById);
                        if (!reviveRec.Ghosts.Contains(e)) { reviveRec.Ghosts.Add(e); }
                        reviveRec.LastUpdate = DateTime.UtcNow;
                        statRevived++;
                        continue;
                    }

                    if (preTemp.Contains(e)) { continue; } // existed before our injection — not ours
                    statNew++;

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
                            // Road: any new Temp node/edge/pillar sitting near a control point. Prefab-gate
                            // only EDGES (Curve) — a node/pillar's own PrefabRef is not the net prefab (it's
                            // the node/pillar prefab), so gating there would false-negative real ghosts.
                            // pd.Prefab is the netPrefab (populated by InjectRoadGhost). WITHOUT this guard,
                            // an 8 m-radius proximity match alone can catch the LOCAL player's own same-frame
                            // net-tool drag of the SAME prefab and mistag it CS2M_RemotePreview — then next
                            // tick PreviewApplySystem/DeletePlayerGhosts marks it Deleted while the local
                            // NetToolSystem still owns/renders it, a race that can crash mid-drag. A local
                            // drag of a DIFFERENT prefab, or the same prefab beyond 8 m, is unaffected by the
                            // bug and unaffected by this fix; a same-prefab local drag under 8 m remains a
                            // known residual (not fully closed here — needs an origin-player check).
                            bool prefabOk = !EntityManager.HasComponent<Curve>(e) || PrefabIs(e, pd.Prefab);
                            if (prefabOk && NearRoad(e, pos, pd.Anchors, RoadMatchRadius)) { matched = p; break; }
                        }
                    }

                    if (matched < 0)
                    {
                        // Miss diagnostics (throttled 1×/5s): WHY didn't this new Temp match any pending?
                        // CHAOS run measured newTemp>0 with tagged==0 for 10 straight minutes — the leak's
                        // first registration never happens, so the self-heal branch above has nothing to heal.
                        if (pending.Count > 0 && (DateTime.UtcNow - _lastMissLog).TotalSeconds > 5)
                        {
                            _lastMissLog = DateTime.UtcNow;
                            Pending pd0 = pending[0];
                            float best = float.MaxValue;
                            for (int a = 0; a < pd0.Anchors.Count; a++)
                            {
                                float dx = pos.x - pd0.Anchors[a].x, dz = pos.z - pd0.Anchors[a].z;
                                best = math.min(best, math.sqrt(dx * dx + dz * dz));
                            }
                            string shape = EntityManager.HasComponent<Curve>(e) ? "edge"
                                : EntityManager.HasComponent<Node>(e) ? "node" : "obj";
                            CS2M.Log.Info($"[Preview] TAG-MISS shape={shape} kind0={pd0.Kind} minDist={best:F1} " +
                                          $"prefabOk={PrefabIs(e, pd0.Prefab)} anchors={pd0.Anchors.Count}");
                        }

                        continue;
                    }

                    int playerId = pending[matched].PlayerId;
                    EntityManager.AddComponentData(e, new CS2M_RemotePreview { PlayerId = playerId });
                    RemotePreviewGhosts.Rec rec = RemotePreviewGhosts.GetOrAdd(playerId);
                    rec.Ghosts.Add(e);
                    rec.LastUpdate = DateTime.UtcNow;
                    statTagged++;
                }

                // MECANISM 2 — owner-chain inheritance: the building ghost's sub-network (farm internal
                // roads/lanes) materializes as its OWN Temp nodes/edges every cycle — never the root, always
                // far from the building anchor (measured: TAG-MISS shape=node minDist=16-40m prefabOk=False,
                // since their PrefabRef is the sub-net's own prefab, not the building's). They never match
                // the anchor-proximity checks above by design (they are not the building itself), but they
                // DO carry Owner (Game.Common.Owner.m_Owner) pointing at the root or at another child in the
                // same sub-tree the Generate* pipeline built this frame. Walk each still-untagged Temp's
                // Owner chain (bounded 4 hops) and inherit the same PlayerId from whichever ancestor already
                // carries CS2M_RemotePreview — including one tagged by self-heal/revive-match/fine-match
                // earlier in THIS pass. Repeat the whole scan until nothing changes or 4 passes, so a chain
                // deeper than 4 raw Owner hops still resolves once an intermediate ancestor gets tagged on an
                // earlier iteration (effective reach up to 4x4 hops across iterations).
                for (int iter = 0; iter < 4; iter++)
                {
                    bool anyInherited = false;
                    foreach (Entity e in nowTemp)
                    {
                        if (EntityManager.HasComponent<CS2M_RemotePreview>(e)) { continue; }
                        if (!EntityManager.HasComponent<Owner>(e)) { continue; }

                        Entity cur = EntityManager.GetComponentData<Owner>(e).m_Owner;
                        int hops = 0;
                        int inheritedId = -1;
                        while (cur != Entity.Null && EntityManager.Exists(cur) && hops < 4)
                        {
                            if (EntityManager.HasComponent<CS2M_RemotePreview>(cur))
                            {
                                inheritedId = EntityManager.GetComponentData<CS2M_RemotePreview>(cur).PlayerId;
                                break;
                            }

                            if (!EntityManager.HasComponent<Owner>(cur)) { break; }
                            cur = EntityManager.GetComponentData<Owner>(cur).m_Owner;
                            hops++;
                        }

                        if (inheritedId < 0) { continue; }

                        EntityManager.AddComponentData(e, new CS2M_RemotePreview { PlayerId = inheritedId });
                        RemotePreviewGhosts.Rec inhRec = RemotePreviewGhosts.GetOrAdd(inheritedId);
                        if (!inhRec.Ghosts.Contains(e)) { inhRec.Ghosts.Add(e); }
                        inhRec.LastUpdate = DateTime.UtcNow;
                        statInherited++;
                        anyInherited = true;
                    }

                    if (!anyInherited) { break; }
                }
            }
            finally { nowTemp.Dispose(); }

            // v73.1 validation radar (throttled 1×/5s): newTemp==0 with pendings armed means our injected
            // definition produced NO Temp at all (ghost never materialized); newTemp>0 with tagged==0 and
            // revived==0 and inherited==0 means every match path missed and the ghost leaked as an eternal
            // orphan. revived>0 proves MECANISM 1 (root reused/reposition-revived) is doing work; inherited>0
            // proves MECANISM 2 (owner-chain sub-network) is doing work.
            if ((DateTime.UtcNow - _lastTagLog).TotalSeconds > 5)
            {
                _lastTagLog = DateTime.UtcNow;
                CS2M.Log.Info($"[Preview] TAG newTemp={statNew} tagged={statTagged} revived={statRevived} " +
                              $"inherited={statInherited} pendings={pending.Count}");
            }
        }

        private static DateTime _lastTagLog = DateTime.MinValue;
        private static DateTime _lastMissLog = DateTime.MinValue;

        /// <summary>MECANISM 1 helper: does this Temp's OWN transform sit within <see cref="BuildingMatchRadius"/>
        /// of a Kind=2 pending's anchor, with the exact same prefab? Same precision as the fine match below,
        /// deliberately usable on an entity that is NOT "new" this frame (a revived root) — see the call site
        /// comment for why that is safe.</summary>
        private bool TryReviveMatch(Entity e, List<Pending> pending, out int playerId)
        {
            playerId = -1;
            if (!EntityManager.HasComponent<Game.Objects.Transform>(e)) { return false; }

            float3 pos = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
            for (int p = 0; p < pending.Count; p++)
            {
                Pending pd = pending[p];
                if (pd.Kind != 2) { continue; }
                if (!PrefabIs(e, pd.Prefab)) { continue; }
                if (!Near(pos, pd.Anchors, BuildingMatchRadius)) { continue; }
                playerId = pd.PlayerId;
                return true;
            }

            return false;
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
