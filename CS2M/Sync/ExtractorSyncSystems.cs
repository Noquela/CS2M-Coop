using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Global toggle for the host-authoritative <c>Game.Areas.Extractor</c> mirror. ON by default;
    /// set env <c>CS2M_EXTRACTOR=0</c> to disable both the host detector and the client apply. The Extractor
    /// is the DRIVER of a farm/forestry/ore field's tilled-soil size (decomp AreaSpawnSystem.cs:182-188), so
    /// mirroring it host→client makes the client's field target the host's size.</summary>
    public static class ExtractorGate
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_EXTRACTOR") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     Option-A field-growth toggle (env <c>CS2M_AREAGROW</c>, OFF by default since v73). When ON, the
    ///     client does NOT suppress its own <c>Game.Simulation.AreaSpawnSystem</c>
    ///     (<see cref="AreaSpawnSuppressSystem"/>) — instead the field grows LOCALLY, targeting the size
    ///     implied by the host-mirrored <see cref="ExtractorGate">Extractor</see>. Because both machines then
    ///     grow their own fields, the per-field crop/surface MIRRORS (<see cref="AreaObjGate"/> /
    ///     <see cref="AreaSurfaceGate"/>) would double-create, so they stand down while this is ON.
    ///     <para>v73 (2026-07-09): flipped to OFF by default — the ON path was never validated by the 2-sim
    ///     harness (the launch script always forced =0, the inverse of the CS2M_NETSET release trap) and the
    ///     first defaults-only run showed it diverging ON SCREEN: each side's spawner picks its own lot and
    ///     grows its own barns by per-machine RNG, so the tilled field lands on a DIFFERENT polygon per
    ///     machine and the grown buildings don't match (their sub-net zone blocks then spam ZoneAuth
    ///     "DROP edge unresolved" on the peer). Default is the validated v66 path: suppress the client
    ///     spawner and mirror the exact crops/surfaces the host grew (CLIENT-FARM 529=529).
    ///     Set <c>CS2M_AREAGROW=1</c> to opt back in for experiments.</para>
    /// </summary>
    public static class ExtractorGrowGate
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_AREAGROW") == "1" ? 1 : 0;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>Thread-safe queue for remote Extractor batches (client side).</summary>
    public static class RemoteExtractorQueue
    {
        private static readonly Queue<ExtractorSyncCommand> Queue = new Queue<ExtractorSyncCommand>();
        private static readonly object Lock = new object();

        public static void Enqueue(ExtractorSyncCommand cmd)
        {
            lock (Lock) { Queue.Enqueue(cmd); }
        }

        public static bool TryDequeue(out ExtractorSyncCommand cmd)
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
    ///     HOST-ONLY detector: at ~1 Hz, reads the <c>Game.Areas.Extractor</c> of every work area and ships
    ///     the ones whose extraction state moved beyond an epsilon since the last send (continuous state, so
    ///     change-detect + 1 Hz cap — never spam). Each area is anchored by a stable host-minted
    ///     <c>CS2M_SyncId</c>. Never runs on a client (echo-free by construction — a client neither detects
    ///     nor sends), and does nothing while no remote client is connected; the first scan AFTER a client
    ///     joins re-ships EVERY area (first-sight full resend) which the client adopts idempotently.
    /// </summary>
    public partial class ExtractorDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _extractorAreas;
        private int _scanCounter;
        private int _lastConnectedCount = 1;
        private const int AreasPerCommand = 64;

        // area sync-id -> last Extractor we shipped. Cleared on client-join to force a first-sight resend.
        private readonly Dictionary<ulong, Extractor> _sent = new Dictionary<ulong, Extractor>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _extractorAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<Extractor>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<MapTile>(),
                },
            });
            CS2M.Log.Info("[Extractor] ExtractorDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (!ExtractorGate.Enabled)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // HOST only — a client never simulates the authoritative Extractor, so it must never send.
            if (NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            if (++_scanCounter < 60) // ~1 Hz cap
            {
                return;
            }

            _scanCounter = 0;

            int connected = NetworkInterface.Instance.PlayerListConnected.Count;
            bool clientJoined = connected > _lastConnectedCount;
            _lastConnectedCount = connected;
            if (clientJoined)
            {
                _sent.Clear(); // first-sight: full resend for the just-connected client
                CS2M.Log.Info("[Extractor] client joined -> first-sight full resend");
            }

            if (connected <= 1)
            {
                return; // no remote client
            }

            Scan();
        }

        private void Scan()
        {
            var ids = new List<ulong>();
            var names = new List<string>();
            var cx = new List<float>();
            var cz = new List<float>();
            var resource = new List<float>();
            var conc = new List<float>();
            var extracted = new List<float>();
            var work = new List<float>();
            var harvested = new List<float>();
            var total = new List<float>();
            var wtype = new List<int>();

            NativeArray<Entity> areas = _extractorAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!ResolveAreaAnchor(area, out ulong id, out string prefabName, out float3 center))
                    {
                        continue;
                    }

                    Extractor ex = EntityManager.GetComponentData<Extractor>(area);
                    if (_sent.TryGetValue(id, out Extractor prev) && !Differs(prev, ex))
                    {
                        continue; // unchanged beyond epsilon since last send
                    }

                    _sent[id] = ex;
                    ids.Add(id);
                    names.Add(prefabName);
                    cx.Add(center.x);
                    cz.Add(center.z);
                    resource.Add(ex.m_ResourceAmount);
                    conc.Add(ex.m_MaxConcentration);
                    extracted.Add(ex.m_ExtractedAmount);
                    work.Add(ex.m_WorkAmount);
                    harvested.Add(ex.m_HarvestedAmount);
                    total.Add(ex.m_TotalExtracted);
                    wtype.Add((int) ex.m_WorkType);

                    CS2M.Log.Info($"[Extractor] SEND id={id} extracted={ex.m_ExtractedAmount:F0} total={ex.m_TotalExtracted:F0}");

                    if (ids.Count >= AreasPerCommand)
                    {
                        Flush(ids, names, cx, cz, resource, conc, extracted, work, harvested, total, wtype);
                    }
                }
            }
            finally
            {
                areas.Dispose();
            }

            if (ids.Count > 0)
            {
                Flush(ids, names, cx, cz, resource, conc, extracted, work, harvested, total, wtype);
            }
        }

        private static void Flush(List<ulong> ids, List<string> names, List<float> cx, List<float> cz,
            List<float> resource, List<float> conc, List<float> extracted, List<float> work,
            List<float> harvested, List<float> total, List<int> wtype)
        {
            Command.SendToAll?.Invoke(new ExtractorSyncCommand
            {
                AreaIds = ids.ToArray(),
                AreaPrefabNames = names.ToArray(),
                CenterX = cx.ToArray(),
                CenterZ = cz.ToArray(),
                ResourceAmount = resource.ToArray(),
                MaxConcentration = conc.ToArray(),
                ExtractedAmount = extracted.ToArray(),
                WorkAmount = work.ToArray(),
                HarvestedAmount = harvested.ToArray(),
                TotalExtracted = total.ToArray(),
                WorkType = wtype.ToArray(),
            });

            ids.Clear(); names.Clear(); cx.Clear(); cz.Clear();
            resource.Clear(); conc.Clear(); extracted.Clear(); work.Clear();
            harvested.Clear(); total.Clear(); wtype.Clear();
        }

        /// <summary>Resolve the area's stable anchor (mint its id if new — host is authoritative), prefab name
        /// and polygon centroid. Mirrors AreaSurfaceDetectorSystem.ResolveAreaAnchor (no building hint — the
        /// Extractor apply resolves by id / prefab+centroid only).</summary>
        private bool ResolveAreaAnchor(Entity area, out ulong anchorId, out string anchorPrefab, out float3 center)
        {
            anchorId = 0;
            anchorPrefab = null;
            center = default;

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

                    center = sum / nodes.Length;
                }
            }

            return true;
        }

        /// <summary>True when any Extractor field moved beyond an epsilon (throttle: relative 0.5% or 1.0
        /// absolute on the amounts — continuous state must not resend every scan on float jitter).</summary>
        private static bool Differs(in Extractor a, in Extractor b)
        {
            return Field(a.m_ResourceAmount, b.m_ResourceAmount)
                   || Field(a.m_MaxConcentration, b.m_MaxConcentration)
                   || Field(a.m_ExtractedAmount, b.m_ExtractedAmount)
                   || Field(a.m_WorkAmount, b.m_WorkAmount)
                   || Field(a.m_HarvestedAmount, b.m_HarvestedAmount)
                   || Field(a.m_TotalExtracted, b.m_TotalExtracted)
                   || a.m_WorkType != b.m_WorkType;
        }

        private static bool Field(float a, float b)
        {
            return math.abs(a - b) > math.max(1f, 0.005f * math.max(math.abs(a), math.abs(b)));
        }
    }

    /// <summary>
    ///     CLIENT-side apply for <see cref="ExtractorSyncCommand"/>. Resolves each area (by stable id, falling
    ///     back once to prefab-name + centroid), then writes the host's <c>Game.Areas.Extractor</c> onto it
    ///     (<c>SetComponentData</c> — the struct is public, no reflection) and stamps <c>Updated</c> so the
    ///     (un-suppressed, CS2M_AREAGROW) local <c>AreaSpawnSystem</c> re-derives the field coverage from the
    ///     mirrored amounts. Never runs on the host. Unresolved areas are parked and retried for a few seconds
    ///     (the area may still be materialising from the world transfer / area sync).
    /// </summary>
    public partial class ExtractorApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _extractorAreas;

        private struct PendingCmd { public ExtractorSyncCommand Cmd; public int FramesLeft; }
        private readonly List<PendingCmd> _pending = new List<PendingCmd>();
        private const int RetryTtlFrames = 300; // ~5 s at 60 fps
        private const float AnchorSearchRadiusSq = 30f * 30f;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _extractorAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<Extractor>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<MapTile>(),
                },
            });
            CS2M.Log.Info("[Extractor] ExtractorApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (!ExtractorGate.Enabled)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // Apply on the client only — the host authored these; applying on the host is a no-op it authored.
            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                RemoteExtractorQueue.Clear();
                _pending.Clear();
                return;
            }

            RetryPending();

            while (RemoteExtractorQueue.TryDequeue(out ExtractorSyncCommand cmd))
            {
                try
                {
                    if (!ApplyOne(cmd, lastTry: false))
                    {
                        _pending.Add(new PendingCmd { Cmd = cmd, FramesLeft = RetryTtlFrames });
                    }
                }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] extractor apply failed: {ex.Message}"); }
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
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] extractor retry failed: {ex.Message}"); handled = true; }

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

        /// <summary>Returns true when fully handled; false only when at least one area was unresolved and the
        /// command is worth retrying (still within its TTL).</summary>
        private bool ApplyOne(ExtractorSyncCommand cmd, bool lastTry)
        {
            if (cmd.AreaIds == null || cmd.AreaIds.Length == 0)
            {
                return true;
            }

            bool anyUnresolved = false;
            for (int i = 0; i < cmd.AreaIds.Length; i++)
            {
                try
                {
                    Entity area = ResolveArea(cmd, i);
                    if (area == Entity.Null)
                    {
                        if (!lastTry)
                        {
                            anyUnresolved = true;
                        }
                        else
                        {
                            CS2M.Log.Info($"[Extractor] DROP noArea id={cmd.AreaIds[i]} name={Name(cmd, i)} after retries");
                        }

                        continue;
                    }

                    var ex = new Extractor
                    {
                        m_ResourceAmount = cmd.ResourceAmount[i],
                        m_MaxConcentration = cmd.MaxConcentration[i],
                        m_ExtractedAmount = cmd.ExtractedAmount[i],
                        m_WorkAmount = cmd.WorkAmount[i],
                        m_HarvestedAmount = cmd.HarvestedAmount[i],
                        m_TotalExtracted = cmd.TotalExtracted[i],
                        m_WorkType = (Game.Vehicles.VehicleWorkType) cmd.WorkType[i],
                    };

                    EntityManager.SetComponentData(area, ex);
                    if (!EntityManager.HasComponent<Updated>(area))
                    {
                        EntityManager.AddComponent<Updated>(area); // let AreaSpawnSystem re-derive the coverage
                    }

                    CS2M.Log.Info($"[Extractor] APPLY id={cmd.AreaIds[i]} extracted={ex.m_ExtractedAmount:F0} total={ex.m_TotalExtracted:F0}");
                }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] extractor op failed: {ex.Message}"); }
            }

            return !anyUnresolved;
        }

        private static string Name(ExtractorSyncCommand cmd, int i)
        {
            return cmd.AreaPrefabNames != null && i < cmd.AreaPrefabNames.Length ? cmd.AreaPrefabNames[i] : "?";
        }

        /// <summary>Resolve area i: by stable id first, then a one-time prefab-name + centroid search
        /// (registering the id so later ops resolve directly). Mirrors AreaSurfaceApplySystem.ResolveArea.</summary>
        private Entity ResolveArea(ExtractorSyncCommand cmd, int i)
        {
            ulong id = cmd.AreaIds[i];
            if (id != 0 && CS2M_SyncIdSystem.Map.TryGetValue(id, out Entity byId)
                && EntityManager.Exists(byId) && !EntityManager.HasComponent<Deleted>(byId)
                && EntityManager.HasComponent<Extractor>(byId))
            {
                return byId;
            }

            string prefabName = Name(cmd, i);
            if (string.IsNullOrEmpty(prefabName) || prefabName == "?")
            {
                return Entity.Null;
            }

            var hint = new float3(cmd.CenterX[i], 0f, cmd.CenterZ[i]);
            Entity best = Entity.Null;
            float bestSq = AnchorSearchRadiusSq;
            NativeArray<Entity> areas = _extractorAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase p) || p == null || p.name != prefabName)
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
                    for (int n = 0; n < nodes.Length; n++)
                    {
                        sum += nodes[n].m_Position;
                    }

                    float3 centroid = sum / nodes.Length;
                    float d = math.distancesq(new float3(centroid.x, 0f, centroid.z), hint);
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

            if (best != Entity.Null && id != 0)
            {
                CS2M_SyncIdSystem.Register(EntityManager, best, id);
                CS2M.Log.Info($"[Extractor] ANCHOR-RESOLVE id={id} name={prefabName} entity={best.Index} (one-time)");
            }

            return best;
        }
    }
}
