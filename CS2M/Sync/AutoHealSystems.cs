using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     v60 AUTO-HEAL — the radar stops just WARNING about drift and starts FIXING it, in-session,
    ///     with domain slices instead of the full-world /resync:
    ///
    ///       client radar confirms settled drift (StateHashApplySystem, 2 strikes ≈ 20 s)
    ///         → client sends HealRequestCommand for each HEALABLE domain (rate-limited)
    ///         → host answers with the domain's authoritative slice:
    ///             water    → WaterHealCommand (complete source list, ~1 KB)
    ///             terrain  → TerrainPatchCommand per diverged 32×32-grid cell (~32 KB each, exact texels)
    ///             fees/tax/policies/budget/loan → re-broadcast of current values through the EXISTING
    ///                                             commands and apply paths (idempotent by construction)
    ///         → client applies; next radar samples converge and the drift clears silently.
    ///
    ///     The chat "/resync" warning remains ONLY for non-healable domains (roads/buildings/areas —
    ///     entity-identity surgery, out of scope here; zones heal continuously via ZoneBlockAuthority)
    ///     and as a fallback when a healable domain still drifts after <see cref="AutoHealClient.MaxAttempts"/>.
    ///
    ///     Gate: <c>CS2M_AUTOHEAL=0</c> disables (ON by default). Recovery-path only — with no drift,
    ///     none of this runs.
    /// </summary>
    public static class AutoHeal
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_AUTOHEAL") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>Receive-side queues (same pattern as every Remote*Queue in the repo).</summary>
    public static class AutoHealQueues
    {
        private static readonly Queue<HealRequestCommand> Requests = new Queue<HealRequestCommand>();
        private static readonly Queue<WaterHealCommand> Water = new Queue<WaterHealCommand>();
        private static readonly Queue<TerrainPatchCommand> TerrainPatches = new Queue<TerrainPatchCommand>();
        private static readonly object Lock = new object();

        public static void EnqueueRequest(HealRequestCommand c) { lock (Lock) { Requests.Enqueue(c); } }
        public static void EnqueueWater(WaterHealCommand c) { lock (Lock) { Water.Enqueue(c); } }
        public static void EnqueueTerrainPatch(TerrainPatchCommand c) { lock (Lock) { TerrainPatches.Enqueue(c); } }

        public static bool TryDequeueRequest(out HealRequestCommand c)
        {
            lock (Lock)
            {
                if (Requests.Count > 0) { c = Requests.Dequeue(); return true; }
                c = null; return false;
            }
        }

        public static bool TryDequeueWater(out WaterHealCommand c)
        {
            lock (Lock)
            {
                if (Water.Count > 0) { c = Water.Dequeue(); return true; }
                c = null; return false;
            }
        }

        public static bool TryDequeueTerrainPatch(out TerrainPatchCommand c)
        {
            lock (Lock)
            {
                if (TerrainPatches.Count > 0) { c = TerrainPatches.Dequeue(); return true; }
                c = null; return false;
            }
        }

        public static void Clear()
        {
            lock (Lock) { Requests.Clear(); Water.Clear(); TerrainPatches.Clear(); }
        }
    }

    /// <summary>
    ///     Client-side heal driver, invoked by StateHashApplySystem on every confirmed-drift sample.
    ///     Decides which domains to request, rate-limits, counts attempts, and tells the caller whether
    ///     the chat warning should stay suppressed (true = every drifting domain is being healed).
    /// </summary>
    public static class AutoHealClient
    {
        public const int MaxAttempts = 3;
        private const double MinSecondsBetweenRequests = 90.0; // per domain; radar samples every ~10 s

        private static readonly HashSet<string> Healable = new HashSet<string>
        {
            "water", "terrain", "fees", "tax", "policies", "budget", "loan",
        };

        private static readonly Dictionary<string, double> _lastRequestAt = new Dictionary<string, double>();
        private static readonly Dictionary<string, int> _attempts = new Dictionary<string, int>();

        /// <summary>Drift names arrive as "water 3vs4(hash)" / "fees(hash)" — strip to the bare domain.</summary>
        private static string DomainOf(string drift)
        {
            int cut = drift.IndexOfAny(new[] { ' ', '(' });
            return cut < 0 ? drift : drift.Substring(0, cut);
        }

        /// <summary>Request slices for every healable drifting domain (rate-limited). Returns true when
        /// ALL drifting domains are healable and under the attempt cap — the caller then skips the chat
        /// warning and lets the heal work.</summary>
        public static bool TryHeal(List<string> drifts, Game.Simulation.TerrainSystem terrain)
        {
            if (!AutoHeal.Enabled)
            {
                return false;
            }

            bool allHealable = true;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            foreach (string drift in drifts)
            {
                string domain = DomainOf(drift);
                if (!Healable.Contains(domain))
                {
                    allHealable = false;
                    continue;
                }

                _attempts.TryGetValue(domain, out int tries);
                if (tries >= MaxAttempts)
                {
                    allHealable = false; // heal isn't converging — let the chat warning through
                    continue;
                }

                if (_lastRequestAt.TryGetValue(domain, out double last)
                    && now - last < MinSecondsBetweenRequests)
                {
                    continue; // request in flight / recently answered — give it time to converge
                }

                var req = new HealRequestCommand { Domain = domain };
                if (domain == "terrain")
                {
                    req.TerrainHeights = StateHash.SampleTerrainGrid(terrain);
                    if (req.TerrainHeights == null)
                    {
                        continue; // heightmap not ready — retry next sample
                    }
                }

                _lastRequestAt[domain] = now;
                _attempts[domain] = tries + 1;
                Command.SendToAll?.Invoke(req); // client → server (RelayOnServer=false: no fan-out)
                CS2M.Log.Info($"[Heal] REQUEST domain={domain} attempt={tries + 1}/{MaxAttempts}");
            }

            return allHealable;
        }

        /// <summary>Drift cleared — domains converged; re-arm the attempt counters.</summary>
        public static void Converged()
        {
            if (_attempts.Count > 0)
            {
                CS2M.Log.Info("[Heal] converged — attempt counters reset");
            }

            _attempts.Clear();
        }

        public static void Reset()
        {
            _attempts.Clear();
            _lastRequestAt.Clear();
        }
    }

    /// <summary>
    ///     HOST: answers HealRequests with authoritative domain slices. Per-domain rate limit so several
    ///     clients (or a retry burst) can't turn the answer into spam — the answers are broadcast, so one
    ///     answer serves every drifted client at once.
    /// </summary>
    public partial class AutoHealHostSystem : GameSystemBase
    {
        private const double MinSecondsBetweenAnswers = 60.0;
        private const float TerrainCellDivergenceMeters = 1.0f; // heal cells the radar quantum would flag
        private const int MaxTerrainPatchesPerRequest = 6;

        private TerrainSystem _terrain;
        private CitySystem _city;
        private TaxSystem _tax;
        private CityServiceBudgetSystem _budget;
        private Game.Prefabs.PrefabSystem _prefabs;
        private EntityQuery _waterSources;
        private EntityQuery _services;
        private readonly Dictionary<string, double> _lastAnswerAt = new Dictionary<string, double>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            _city = World.GetOrCreateSystemManaged<CitySystem>();
            _tax = World.GetOrCreateSystemManaged<TaxSystem>();
            _budget = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            _prefabs = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>();
            _waterSources = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<WaterSourceData>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _services = GetEntityQuery(StateHash.ServiceDesc());
            CS2M.Log.Info($"[Heal] AutoHealHostSystem created (enabled={AutoHeal.Enabled})");
        }

        protected override void OnUpdate()
        {
            if (!AutoHeal.Enabled
                || NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING
                || NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            int handled = 0;
            while (handled < 4 && AutoHealQueues.TryDequeueRequest(out HealRequestCommand req))
            {
                handled++;
                try { Answer(req); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] heal answer failed: {ex.Message}"); }
            }
        }

        private void Answer(HealRequestCommand req)
        {
            string domain = req.Domain ?? "";
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_lastAnswerAt.TryGetValue(domain, out double last) && now - last < MinSecondsBetweenAnswers)
            {
                return; // just answered this domain — the broadcast already served this client too
            }

            _lastAnswerAt[domain] = now;
            switch (domain)
            {
                case "water": AnswerWater(); break;
                case "terrain": AnswerTerrain(req); break;
                case "fees": AnswerFees(); break;
                case "tax": AnswerTax(); break;
                case "policies": AnswerPolicies(); break;
                case "budget": AnswerBudgets(); break;
                case "loan": AnswerLoan(); break;
                default:
                    CS2M.Log.Info($"[Heal] SKIP unknown domain={domain}");
                    break;
            }
        }

        private void AnswerWater()
        {
            NativeArray<Entity> ents = _waterSources.ToEntityArray(Allocator.Temp);
            try
            {
                int n = ents.Length;
                var cmd = new WaterHealCommand
                {
                    PosX = new float[n], PosY = new float[n], PosZ = new float[n],
                    Radius = new float[n], Height = new float[n], Multiplier = new float[n],
                    Polluted = new float[n], ConstantDepth = new int[n],
                };
                for (int i = 0; i < n; i++)
                {
                    float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(ents[i]).m_Position;
                    WaterSourceData w = EntityManager.GetComponentData<WaterSourceData>(ents[i]);
                    cmd.PosX[i] = p.x; cmd.PosY[i] = p.y; cmd.PosZ[i] = p.z;
                    cmd.Radius[i] = w.m_Radius; cmd.Height[i] = w.m_Height;
                    cmd.Multiplier[i] = w.m_Multiplier; cmd.Polluted[i] = w.m_Polluted;
                    cmd.ConstantDepth[i] = w.m_ConstantDepth;
                }

                Command.SendToAll?.Invoke(cmd);
                CS2M.Log.Info($"[Heal] ANSWER water sources={n}");
            }
            finally { ents.Dispose(); }
        }

        /// <summary>Compares the requester's sample grid against ours and ships an exact texel patch for
        /// each diverged cell (worst first, capped). Pixel rect derives from the SAME world→heightmap
        /// mapping the game samples with (TerrainUtils.ToHeightmapSpace: pixel = (world+offset)*scale).</summary>
        private void AnswerTerrain(HealRequestCommand req)
        {
            const int N = StateHash.TerrainGridN;
            float[] mine = StateHash.SampleTerrainGrid(_terrain);
            float[] theirs = req.TerrainHeights;
            if (mine == null || theirs == null || theirs.Length != mine.Length)
            {
                CS2M.Log.Info("[Heal] SKIP terrain (grid unavailable/mismatched)");
                return;
            }

            // Worst-diverged cells first.
            var diverged = new List<int>();
            for (int k = 0; k < mine.Length; k++)
            {
                if (math.abs(mine[k] - theirs[k]) > TerrainCellDivergenceMeters)
                {
                    diverged.Add(k);
                }
            }

            if (diverged.Count == 0)
            {
                CS2M.Log.Info("[Heal] terrain already converged (no cell above threshold)");
                return;
            }

            diverged.Sort((a, b) => math.abs(mine[b] - theirs[b]).CompareTo(math.abs(mine[a] - theirs[a])));

            TerrainHeightData hd = _terrain.GetHeightData(true); // exact texels — wait for pending readback
            if (!hd.isCreated)
            {
                return;
            }

            UnityEngine.Bounds bounds = _terrain.GetTerrainBounds();
            float3 min = bounds.min, max = bounds.max;
            float2 step = new float2(max.x - min.x, max.z - min.z) / N;

            int sent = 0;
            foreach (int k in diverged)
            {
                if (sent >= MaxTerrainPatchesPerRequest)
                {
                    break; // the follow-up request (rate-limited) heals the rest
                }

                int i = k / N, j = k % N;
                // Cell world rect → heightmap pixel rect (+1 px margin for the bilinear sampler).
                var wMin = new float3(min.x + i * step.x, 0f, min.z + j * step.y);
                var wMax = new float3(wMin.x + step.x, 0f, wMin.z + step.y);
                float3 pMin = Game.Simulation.TerrainUtils.ToHeightmapSpace(ref hd, wMin);
                float3 pMax = Game.Simulation.TerrainUtils.ToHeightmapSpace(ref hd, wMax);
                int x0 = math.clamp((int) math.floor(pMin.x) - 1, 0, hd.resolution.x - 1);
                int y0 = math.clamp((int) math.floor(pMin.z) - 1, 0, hd.resolution.z - 1);
                int x1 = math.clamp((int) math.ceil(pMax.x) + 1, 0, hd.resolution.x - 1);
                int y1 = math.clamp((int) math.ceil(pMax.z) + 1, 0, hd.resolution.z - 1);
                int w = x1 - x0 + 1, h = y1 - y0 + 1;
                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                var data = new ushort[w * h];
                for (int row = 0; row < h; row++)
                {
                    int src = (y0 + row) * hd.resolution.x + x0;
                    for (int col = 0; col < w; col++)
                    {
                        data[row * w + col] = hd.heights[src + col];
                    }
                }

                Command.SendToAll?.Invoke(new TerrainPatchCommand { X = x0, Y = y0, W = w, H = h, Data = data });
                sent++;
            }

            CS2M.Log.Info($"[Heal] ANSWER terrain divergedCells={diverged.Count} patchesSent={sent}");
        }

        private void AnswerFees()
        {
            Entity c = _city.City;
            if (c == Entity.Null || !EntityManager.HasBuffer<Game.City.ServiceFee>(c))
            {
                return;
            }

            DynamicBuffer<Game.City.ServiceFee> fees = EntityManager.GetBuffer<Game.City.ServiceFee>(c, true);
            for (int i = 0; i < fees.Length; i++)
            {
                Command.SendToAll?.Invoke(new FeeCommand
                {
                    Resource = (int) fees[i].m_Resource,
                    Fee = fees[i].m_Fee,
                });
            }

            CS2M.Log.Info($"[Heal] ANSWER fees entries={fees.Length}");
        }

        private void AnswerTax()
        {
            NativeArray<int> rates = _tax.GetTaxRates();
            if (!rates.IsCreated)
            {
                return;
            }

            // Legacy full-array shape (Indices=null) = exactly "replace with the authoritative set".
            var all = new int[rates.Length];
            for (int i = 0; i < rates.Length; i++) { all[i] = rates[i]; }
            Command.SendToAll?.Invoke(new TaxSyncCommand { Rates = all, Indices = null });
            CS2M.Log.Info($"[Heal] ANSWER tax rates={all.Length}");
        }

        private void AnswerPolicies()
        {
            Entity c = _city.City;
            if (c == Entity.Null || !EntityManager.HasBuffer<Game.Policies.Policy>(c))
            {
                return;
            }

            DynamicBuffer<Game.Policies.Policy> buf = EntityManager.GetBuffer<Game.Policies.Policy>(c, true);
            int sentCount = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                if (!_prefabs.TryGetPrefab(buf[i].m_Policy, out Game.Prefabs.PrefabBase pb) || pb == null)
                {
                    continue;
                }

                Command.SendToAll?.Invoke(new PolicyCommand
                {
                    PolicyType = pb.GetType().Name,
                    PolicyName = pb.name,
                    Active = (buf[i].m_Flags & Game.Policies.PolicyFlags.Active) != 0,
                    Adjustment = buf[i].m_Adjustment,
                    TargetKind = 0, // city
                });
                sentCount++;
            }

            CS2M.Log.Info($"[Heal] ANSWER policies entries={sentCount}");
        }

        private void AnswerBudgets()
        {
            NativeArray<Entity> ents = _services.ToEntityArray(Allocator.Temp);
            int sentCount = 0;
            try
            {
                foreach (Entity e in ents)
                {
                    if (!EntityManager.GetComponentData<Game.Prefabs.ServiceData>(e).m_BudgetAdjustable)
                    {
                        continue;
                    }

                    if (!_prefabs.TryGetPrefab(e, out Game.Prefabs.PrefabBase pb) || pb == null)
                    {
                        continue;
                    }

                    Command.SendToAll?.Invoke(new BudgetCommand
                    {
                        ServiceType = pb.GetType().Name,
                        ServiceName = pb.name,
                        Percentage = _budget.GetServiceBudget(e),
                    });
                    sentCount++;
                }
            }
            finally { ents.Dispose(); }

            CS2M.Log.Info($"[Heal] ANSWER budget services={sentCount}");
        }

        private void AnswerLoan()
        {
            Entity c = _city.City;
            if (c == Entity.Null || !EntityManager.HasComponent<Loan>(c))
            {
                return;
            }

            Command.SendToAll?.Invoke(new LoanCommand
            {
                Amount = EntityManager.GetComponentData<Loan>(c).m_Amount,
            });
            CS2M.Log.Info("[Heal] ANSWER loan");
        }
    }

    /// <summary>
    ///     CLIENT: applies the host's heal slices. Water = reconcile against the authoritative list
    ///     (create missing / delete extra / rewrite params — every path stamps the matching echo guard
    ///     so WaterDetectorSystem never bounces the correction back). Terrain = exact texel patch into
    ///     the heightmap RenderTexture, then the same bookkeeping the game's own ApplyBrush does
    ///     (water notice, min-max update, async CPU readback — decomp TerrainSystem.cs:3918-3925).
    /// </summary>
    public partial class AutoHealApplySystem : GameSystemBase
    {
        private TerrainSystem _terrain;
        private WaterSystem _water;
        private EntityQuery _waterSources;

        protected override void OnCreate()
        {
            base.OnCreate();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            _water = World.GetOrCreateSystemManaged<WaterSystem>();
            _waterSources = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<WaterSourceData>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            CS2M.Log.Info($"[Heal] AutoHealApplySystem created (enabled={AutoHeal.Enabled})");
        }

        protected override void OnUpdate()
        {
            // No SERVER-role guard: the host never receives its own broadcasts (star topology — handlers
            // run on RECEIVE only), so on a real host these queues stay empty; leaving the system live
            // everywhere lets the selftest exercise the actual apply path single-instance.
            if (!AutoHeal.Enabled
                || NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (AutoHealQueues.TryDequeueWater(out WaterHealCommand wc))
            {
                try { ReconcileWater(wc); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] water heal failed: {ex.Message}"); }
            }

            int patches = 0;
            while (patches < 4 && AutoHealQueues.TryDequeueTerrainPatch(out TerrainPatchCommand tp))
            {
                patches++;
                try { ApplyTerrainPatch(tp); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] terrain patch failed: {ex.Message}"); }
            }
        }

        private void ReconcileWater(WaterHealCommand cmd)
        {
            int n = cmd.PosX?.Length ?? 0;
            NativeArray<Entity> ents = _waterSources.ToEntityArray(Allocator.Temp);
            try
            {
                var matched = new bool[ents.Length];
                int created = 0, edited = 0, deleted = 0;

                for (int i = 0; i < n; i++)
                {
                    // Nearest unmatched local source within 2 m of the authoritative one.
                    int best = -1;
                    float bestD = 4f; // 2 m²
                    for (int e = 0; e < ents.Length; e++)
                    {
                        if (matched[e]) { continue; }

                        float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(ents[e]).m_Position;
                        float dx = p.x - cmd.PosX[i], dz = p.z - cmd.PosZ[i];
                        float d = dx * dx + dz * dz;
                        if (d < bestD) { bestD = d; best = e; }
                    }

                    if (best >= 0)
                    {
                        matched[best] = true;
                        Entity ent = ents[best];
                        WaterSourceData w = EntityManager.GetComponentData<WaterSourceData>(ent);
                        if (math.abs(w.m_Radius - cmd.Radius[i]) > 1e-3f
                            || math.abs(w.m_Height - cmd.Height[i]) > 1e-3f
                            || math.abs(w.m_Multiplier - cmd.Multiplier[i]) > 1e-3f
                            || math.abs(w.m_Polluted - cmd.Polluted[i]) > 1e-3f
                            || w.m_ConstantDepth != cmd.ConstantDepth[i])
                        {
                            w.m_Radius = cmd.Radius[i];
                            w.m_Height = cmd.Height[i];
                            w.m_Multiplier = cmd.Multiplier[i];
                            w.m_Polluted = cmd.Polluted[i];
                            w.m_ConstantDepth = cmd.ConstantDepth[i];
                            w.m_Modifier = 1f; // dead-source trap (see WaterApplySystem.ApplyOne)
                            EntityManager.SetComponentData(ent, w);
                            if (!EntityManager.HasComponent<Updated>(ent))
                            {
                                EntityManager.AddComponent<Updated>(ent);
                            }

                            WaterSync.MarkRemoteEdit(
                                EntityManager.GetComponentData<Game.Objects.Transform>(ent).m_Position);
                            edited++;
                        }

                        continue;
                    }

                    // Missing here — create (same shape as WaterApplySystem.ApplyOne, Y anchored locally).
                    float y = cmd.PosY[i];
                    try
                    {
                        TerrainHeightData hd = _terrain.GetHeightData(true);
                        y = TerrainUtils.SampleHeight(ref hd, new float3(cmd.PosX[i], cmd.PosY[i], cmd.PosZ[i]));
                    }
                    catch
                    {
                        // heightmap unavailable — sender Y is a sane fallback
                    }

                    Entity ne = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(ne, new WaterSourceData
                    {
                        m_Radius = cmd.Radius[i], m_Height = cmd.Height[i],
                        m_Multiplier = cmd.Multiplier[i], m_Polluted = cmd.Polluted[i],
                        m_ConstantDepth = cmd.ConstantDepth[i], m_Modifier = 1f,
                    });
                    EntityManager.AddComponentData(ne,
                        new Game.Objects.Transform(new float3(cmd.PosX[i], y, cmd.PosZ[i]), quaternion.identity));
                    EntityManager.AddComponent<CS2M_RemotePlaced>(ne);
                    EntityManager.AddComponent<Created>(ne);
                    EntityManager.AddComponent<Updated>(ne);
                    created++;
                }

                // Extra here — the host doesn't have them; delete (echo-guarded).
                for (int e = 0; e < ents.Length; e++)
                {
                    if (matched[e])
                    {
                        continue;
                    }

                    WaterSync.MarkRemoteDelete(ents[e]);
                    EntityManager.AddComponent<Deleted>(ents[e]);
                    deleted++;
                }

                CS2M.Log.Info($"[Heal] water reconciled auth={n} created={created} edited={edited} deleted={deleted}");
            }
            finally { ents.Dispose(); }
        }

        private void ApplyTerrainPatch(TerrainPatchCommand cmd)
        {
            if (cmd.Data == null || cmd.Data.Length != cmd.W * cmd.H || cmd.W <= 0 || cmd.H <= 0)
            {
                return;
            }

            var heightmap = _terrain.heightmap as UnityEngine.RenderTexture;
            if (heightmap == null
                || cmd.X < 0 || cmd.Y < 0
                || cmd.X + cmd.W > heightmap.width || cmd.Y + cmd.H > heightmap.height)
            {
                CS2M.Log.Info($"[Heal] SKIP terrain patch (heightmap null/rect out of range {cmd.X},{cmd.Y} {cmd.W}x{cmd.H})");
                return;
            }

            TerrainHeightData hd = _terrain.GetHeightData(false);
            if (!hd.isCreated)
            {
                CS2M.Log.Info("[Heal] SKIP terrain patch (height data not ready)");
                return;
            }

            var tex = new UnityEngine.Texture2D(cmd.W, cmd.H, UnityEngine.TextureFormat.R16, false, true);
            try
            {
                tex.SetPixelData(cmd.Data, 0);
                tex.Apply(false, false);
                UnityEngine.Graphics.CopyTexture(tex, 0, 0, 0, 0, cmd.W, cmd.H, heightmap, 0, 0, cmd.X, cmd.Y);
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }

            // The CopyTexture wrote m_Heightmap, but everything downstream reads the CASCADE, which
            // only refreshes for dirty regions — and ALL the dirty-marking (m_UpdateArea merge, ground
            // height buffer, water notice, min-max, async CPU readback) lives inside the PRIVATE
            // ApplyToTerrain (decomp TerrainSystem.cs:3722-3736). First validation run proved it: the
            // patch landed on the GPU texture and the sampled (CPU) height never moved. So run the
            // game's own bookkeeping over the patch area by applying a brush whose strength is far
            // below half an R16 step (delta·1e-9·speed/scale ≪ 1/65535 — rounds to +0 on every texel):
            // the compute pass is a no-op on the data we just wrote, and the bookkeeping is exact.
            float3 wMin = TerrainUtils.ToWorldSpace(ref hd, new float3(cmd.X, 0f, cmd.Y));
            float3 wMax = TerrainUtils.ToWorldSpace(ref hd, new float3(cmd.X + cmd.W, 0f, cmd.Y + cmd.H));
            var area = new Colossal.Mathematics.Bounds2(wMin.xz, wMax.xz);
            var center = new float3((wMin.x + wMax.x) * 0.5f, 0f, (wMin.z + wMax.z) * 0.5f);
            var bookkeeping = new Brush
            {
                m_Tool = Entity.Null,
                m_Position = center,
                m_Target = center,
                m_Start = center,
                m_Angle = 0f,
                m_Size = math.max(wMax.x - wMin.x, wMax.z - wMin.z),
                m_Strength = 1e-9f, // NOT 0 — strength==0 early-returns before the bookkeeping
                m_Opacity = 1f,
            };
            _terrain.ApplyBrush(Game.Prefabs.TerraformingType.Shift, area, bookkeeping,
                UnityEngine.Texture2D.whiteTexture);

            CS2M.Log.Info($"[Heal] terrain patch applied rect=({cmd.X},{cmd.Y}) {cmd.W}x{cmd.H}");
        }
    }
}
