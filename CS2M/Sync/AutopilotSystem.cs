using System;
using System.Collections.Generic;
using System.IO;
using Colossal.Serialization.Entities;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Headless self-test driver. Inert unless the <c>CS2M_AUTOPILOT</c> environment variable is
    ///     set, so the build friends run is byte-for-byte identical to the normal mod.
    ///
    ///     Roles: host / client (real 2-PC or 2-instance test, over the wire) and <b>selftest</b>
    ///     (ONE instance: StartServer makes us PLAYING with no client, then we inject the SAME commands
    ///     straight into the local apply queues — exactly what the network handler does on receipt —
    ///     and read the real game state back to verify EVERY sync feature). This is the fix for the
    ///     RUNE crack blocking a second instance.
    ///
    ///     selftest validates, reading the world before/after each action:
    ///       money, XP/milestone, object (tree+building), move, zoning, net, delete, pause-on-join.
    ///       It also reports population/max-pop (a KNOWN gap: emergent sim state, not directly synced).
    /// </summary>
    public partial class AutopilotSystem : GameSystemBase
    {
        // Amounts big enough to dwarf per-tick sim drift during the ~3s verify window.
        private const int MoneyAdd = 1_000_000;
        private const int XpAdd = 200_000;

        private bool _disabled = true;
        private bool _isHost;
        private bool _selftest;
        private int _port = 1111;
        private string _ip = "127.0.0.1";
        private bool _testEnabled = true;
        private string _logPath;

        private GameMode _gameMode = GameMode.Other;
        private PrefabSystem _prefabSystem;
        private CitySystem _citySystem;
        private SimulationSystem _sim;
        private CS2M_SyncIdSystem _idSystem;
        private TaxSystem _taxSystem;
        private CityServiceBudgetSystem _budgetSystem;

        private EntityQuery _treeQuery;
        private EntityQuery _buildingQuery;
        private EntityQuery _edgeQuery;
        private EntityQuery _blockQuery;
        private EntityQuery _remotePlacedQuery;
        private EntityQuery _allEdgesQuery;

        // host/selftest
        private int _hostArmFrames = -1;
        private bool _hosting;
        private bool _testStarted;
        private int _testStep;
        private int _testTimer;
        private ulong _treeSyncId;
        private ulong _buildingSyncId;

        // captured state for the selftest verifications
        private int _moneyExpected;
        private bool _moneyUnlimited;
        private int _xpExpected;
        private int _taxExpectedMain = int.MinValue;
        private Entity _budgetService;
        private int _budgetExpected = int.MinValue;
        private float3 _movePosExpected;
        private Entity _zoneBlock;
        private ushort _zoneExpectedIndex;
        private int _edgesBeforeNet;
        private float3 _netStart;
        private float3 _netEnd;
        private int _edgesBeforeNetDelete;
        private Entity _upgradeEdge;
        private uint _upgradeExpectedLeft;
        private Entity _policyEntity;
        private bool _policyExpectedActive;
        private float _speedBeforePause;
        private bool _forceRun = true; // keep the sim ticking (game auto-pauses when unfocused)
        private readonly List<string> _results = new List<string>();

        // verify (client + selftest counts)
        private bool _playingLogged;
        private int _verifyFrames;
        private int _lastRemoteCount = -1;
        private int _lastEdgeCount = -1;

        // client
        private bool _connectRequested;
        private int _connectRetryFrames;
        private int _connectAttempts;

        protected override void OnCreate()
        {
            base.OnCreate();

            string role = Environment.GetEnvironmentVariable("CS2M_AUTOPILOT");
            if (string.IsNullOrEmpty(role))
            {
                return;
            }

            role = role.Trim().ToLowerInvariant();
            if (role == "host" || role == "server") { _isHost = true; }
            else if (role == "selftest" || role == "self") { _isHost = true; _selftest = true; }
            else if (role == "client" || role == "join") { _isHost = false; }
            else
            {
                CS2M.Log.Info($"[Auto] CS2M_AUTOPILOT='{role}' not understood (host|client|selftest); disabled");
                return;
            }

            _disabled = false;
            _logPath = Environment.GetEnvironmentVariable("CS2M_AP_LOG");

            string portEnv = Environment.GetEnvironmentVariable("CS2M_AP_PORT");
            if (!string.IsNullOrEmpty(portEnv) && int.TryParse(portEnv, out int p)) { _port = p; }

            string ipEnv = Environment.GetEnvironmentVariable("CS2M_AP_IP");
            if (!string.IsNullOrEmpty(ipEnv)) { _ip = ipEnv.Trim(); }

            _testEnabled = Environment.GetEnvironmentVariable("CS2M_AP_TEST") != "0";

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            _sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            _idSystem = World.GetOrCreateSystemManaged<CS2M_SyncIdSystem>();
            _taxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            _budgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();

            _treeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Tree>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            _buildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            _edgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Edge>(),
                    ComponentType.ReadOnly<Game.Net.Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            _blockQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Block>(), ComponentType.ReadOnly<Cell>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            _remotePlacedQuery = GetEntityQuery(ComponentType.ReadOnly<CS2M_RemotePlaced>());
            _allEdgesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Edge>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            GameManager.instance.onGameLoadingComplete += OnLoadingComplete;

            string r = _selftest ? "SELFTEST" : (_isHost ? "HOST" : "CLIENT");
            L($"[Auto] ENABLED role={r} port={_port} ip={_ip} scriptedTest={_testEnabled} " +
              $"log={_logPath ?? "(game log only)"}");
        }

        protected override void OnDestroy()
        {
            if (!_disabled)
            {
                try { GameManager.instance.onGameLoadingComplete -= OnLoadingComplete; }
                catch { }
            }

            base.OnDestroy();
        }

        private void OnLoadingComplete(Purpose purpose, GameMode mode)
        {
            _gameMode = mode;
            if (_disabled) { return; }

            L($"[Auto] loadingComplete purpose={purpose} mode={mode}");

            if (_isHost && mode == GameMode.Game && !_hosting && _hostArmFrames < 0)
            {
                _hostArmFrames = 120;
                L("[Auto] city loaded -> arming StartServer");
            }
            else if (!_isHost && mode == GameMode.MainMenu && !_connectRequested)
            {
                TryClientConnect();
            }
        }

        protected override void OnUpdate()
        {
            if (_disabled) { return; }
            if (_isHost) { UpdateHost(); } else { UpdateClient(); }
        }

        // ---------------------------------------------------------------- HOST / SELFTEST

        private void UpdateHost()
        {
            if (!_hosting)
            {
                if (_hostArmFrames < 0) { return; }
                if (--_hostArmFrames > 0) { return; }

                NetworkInterface.Instance.UpdateLocalPlayerUsername(_selftest ? "SelfTest" : "AutoHost");
                NetworkInterface.Instance.StartServer(new ConnectionConfig(_port));
                _hosting = true;
                L($"[Auto] StartServer on :{_port} status={Status()}");
                return;
            }

            if (_testEnabled)
            {
                if (!_testStarted)
                {
                    if (!_selftest && NetworkInterface.Instance.PlayerListJoined.Count <= 1)
                    {
                        return;
                    }

                    _testStarted = true;
                    _testTimer = _selftest ? 150 : 240;
                    L(_selftest
                        ? "[Auto] SELFTEST beginning — validating every feature against real game state"
                        : $"[Auto] HOST client joined (joined={NetworkInterface.Instance.PlayerListJoined.Count}); test begins");
                }
                else if (_selftest)
                {
                    RunSelftestStep();
                }
                else
                {
                    RunHostStep();
                }
            }

            if (_selftest && _hosting)
            {
                // The game auto-pauses when its window isn't focused (background run), which would
                // stop the simulation (net generation, growth). Re-assert a running speed every frame
                // until we reach the pause test, so sim-driven features actually run.
                if (_forceRun && _sim.selectedSpeed == 0f)
                {
                    _sim.selectedSpeed = 3f;
                }

                LogCounts(Status());
            }
        }

        // ------- Rich single-instance validation: inject into apply queues, read state back -------

        private void RunSelftestStep()
        {
            if (_testStep > 15) { return; }
            if (_testTimer > 0) { _testTimer--; return; }
            _testTimer = 200;

            switch (_testStep)
            {
                case 0: ActMoney(); break;
                case 1: VerifyMoney(); ActXp(); break;
                case 2: VerifyXp(); _treeSyncId = InjectObject(_treeQuery, "tree", new float3(20f, 0f, 20f)); break;
                case 3: VerifyCount("object:tree", 1); _buildingSyncId = InjectObject(_buildingQuery, "building", new float3(60f, 0f, 60f)); break;
                case 4: VerifyCount("object:building", 2); ActMove(); break;
                case 5: VerifyMove(); ActZone(); break;
                case 6: VerifyZone(); ActNet(); break;
                case 7: VerifyNet(); ActNetDelete(); break;
                case 8: VerifyNetDelete(); ActNetUpgrade(); break;
                case 9: VerifyNetUpgrade(); ActDelete(); break;
                case 10: VerifyDelete(); ActTax(); break;
                case 11: VerifyTax(); ActBudget(); break;
                case 12: VerifyBudget(); ActPolicy(); break;
                case 13: VerifyPolicy(); ActPause(); break;
                case 14: VerifyPause(); ActResume(); break;
                case 15: VerifyResume(); Summary(); break;
            }

            _testStep++;
        }

        private void ActMoney()
        {
            int money = ReadMoney(out _moneyUnlimited);
            if (money == int.MinValue) { Result("money", false, "no City/PlayerMoney"); _moneyExpected = int.MinValue; return; }
            _moneyExpected = money + MoneyAdd;
            L($"[Auto] TEST money INJECT before={money} unlimited={_moneyUnlimited} -> set={_moneyExpected}");
            RemoteMoneyQueue.Set(_moneyExpected);
        }

        private void VerifyMoney()
        {
            if (_moneyExpected == int.MinValue) { return; }
            int money = ReadMoney(out bool unl);
            if (unl) { Result("money", true, "guard OK: city has unlimited money -> apply correctly skipped"); return; }
            bool ok = Math.Abs(money - _moneyExpected) < 200_000;
            Result("money", ok, $"after={money} expected~{_moneyExpected}");
        }

        private void ActXp()
        {
            if (!TryCity(out Entity city) || !EntityManager.HasComponent<XP>(city)) { Result("progression", false, "no City/XP"); _xpExpected = int.MinValue; return; }
            XP xp = EntityManager.GetComponentData<XP>(city);
            _xpExpected = xp.m_XP + XpAdd;
            int milestone = EntityManager.HasComponent<MilestoneLevel>(city) ? EntityManager.GetComponentData<MilestoneLevel>(city).m_AchievedMilestone : -1;
            L($"[Auto] TEST progression INJECT beforeXp={xp.m_XP} milestone={milestone} maxPop={xp.m_MaximumPopulation} -> setXp={_xpExpected}");
            RemoteProgressionQueue.Set(new ProgressionSyncCommand
            {
                Xp = _xpExpected,
                MaxPopulation = xp.m_MaximumPopulation,
                MaxIncome = xp.m_MaximumIncome,
                XpRewardRecord = (byte) xp.m_XPRewardRecord,
                AchievedMilestone = milestone,
            });
        }

        private void VerifyXp()
        {
            if (_xpExpected == int.MinValue) { return; }
            if (!TryCity(out Entity city) || !EntityManager.HasComponent<XP>(city)) { Result("progression", false, "no City/XP"); return; }
            int xp = EntityManager.GetComponentData<XP>(city).m_XP;
            bool ok = xp >= _xpExpected - 50_000;
            Result("progression", ok, $"afterXp={xp} expected~{_xpExpected}");
        }

        private ulong InjectObject(EntityQuery query, string kind, float3 offset)
        {
            if (query.IsEmptyIgnoreFilter) { L($"[Auto] TEST object SKIP kind={kind} reason=noneInCity"); return 0; }

            NativeArray<Entity> ents = query.ToEntityArray(Allocator.Temp);
            try
            {
                Entity src = ents[0];
                PrefabRef pr = EntityManager.GetComponentData<PrefabRef>(src);
                if (!_prefabSystem.TryGetPrefab(pr.m_Prefab, out PrefabBase prefab) || prefab == null)
                {
                    L($"[Auto] TEST object SKIP kind={kind} reason=noPrefab");
                    return 0;
                }

                Game.Objects.Transform t = EntityManager.GetComponentData<Game.Objects.Transform>(src);
                float3 pos = t.m_Position + offset;
                int seed = EntityManager.HasComponent<PseudoRandomSeed>(src)
                    ? EntityManager.GetComponentData<PseudoRandomSeed>(src).m_Seed : 0;

                var cmd = new ObjectPlaceCommand
                {
                    SyncId = CS2M_SyncIdSystem.Allocate(),
                    PrefabType = prefab.GetType().Name, PrefabName = prefab.name,
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    RotX = t.m_Rotation.value.x, RotY = t.m_Rotation.value.y,
                    RotZ = t.m_Rotation.value.z, RotW = t.m_Rotation.value.w,
                    RandomSeed = seed,
                };
                L($"[Auto] TEST object INJECT kind={kind} name={cmd.PrefabName} pos=({pos.x:F0},{pos.y:F0},{pos.z:F0}) syncId={cmd.SyncId}");
                RemotePlacementQueue.EnqueueObject(cmd);
                return cmd.SyncId;
            }
            finally { ents.Dispose(); }
        }

        private void VerifyCount(string name, int atLeast)
        {
            int remote = _remotePlacedQuery.CalculateEntityCount();
            Result(name, remote >= atLeast, $"remoteObjects={remote} (>= {atLeast})");
        }

        private void ActMove()
        {
            if (_buildingSyncId == 0 || !_idSystem.TryResolve(_buildingSyncId, out Entity e) || !EntityManager.Exists(e)
                || !EntityManager.HasComponent<Game.Objects.Transform>(e))
            {
                Result("move", false, "building not resolvable");
                _movePosExpected = new float3(float.NaN);
                return;
            }

            float3 cur = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
            _movePosExpected = cur + new float3(15f, 0f, 15f);
            L($"[Auto] TEST move INJECT syncId={_buildingSyncId} from=({cur.x:F0},{cur.z:F0}) to=({_movePosExpected.x:F0},{_movePosExpected.z:F0})");
            RemoteEditQueue.EnqueueMove(new MoveCommand
            {
                SyncId = _buildingSyncId,
                PosX = _movePosExpected.x, PosY = _movePosExpected.y, PosZ = _movePosExpected.z,
                RotW = 1f,
            });
        }

        private void VerifyMove()
        {
            if (math.any(math.isnan(_movePosExpected))) { return; }
            if (!_idSystem.TryResolve(_buildingSyncId, out Entity e) || !EntityManager.Exists(e)
                || !EntityManager.HasComponent<Game.Objects.Transform>(e))
            {
                Result("move", false, "building gone after move");
                return;
            }

            float3 pos = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
            bool ok = math.distance(pos, _movePosExpected) < 1f;
            Result("move", ok, $"pos=({pos.x:F0},{pos.z:F0}) expected=({_movePosExpected.x:F0},{_movePosExpected.z:F0})");
        }

        private void ActZone()
        {
            _zoneBlock = Entity.Null;
            if (_blockQuery.IsEmptyIgnoreFilter) { Result("zoning", false, "no zoning blocks in city"); return; }

            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);
            if (!TryGetZone(out string targetName, out ushort targetIdx))
            {
                Result("zoning", false, "no ZonePrefab found to paint");
                return;
            }

            NativeArray<Entity> blocks = _blockQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity block = blocks[0];
                Block b = EntityManager.GetComponentData<Block>(block);
                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(block, true);
                if (cells.Length == 0) { Result("zoning", false, "block has no cells"); return; }

                ushort cur0 = cells[0].m_Zone.m_Index;
                _zoneBlock = block;
                _zoneExpectedIndex = targetIdx;
                L($"[Auto] TEST zoning INJECT block=({b.m_Position.x:F0},{b.m_Position.z:F0}) cell0 {cur0}->{targetIdx} name='{targetName}'");
                RemoteZoneQueue.Enqueue(new ZonePaintCommand
                {
                    BlockX = b.m_Position.x, BlockZ = b.m_Position.z,
                    DirX = b.m_Direction.x, DirZ = b.m_Direction.y,
                    SizeX = b.m_Size.x, SizeY = b.m_Size.y,
                    CellIndices = new[] { 0 },
                    ZoneNames = new[] { targetName },
                });
            }
            finally { blocks.Dispose(); }
        }

        /// <summary>Finds any real ZonePrefab (ZoneData) with a non-zero index and a resolvable name.</summary>
        private bool TryGetZone(out string name, out ushort index)
        {
            name = null;
            index = 0;
            EntityQuery q = GetEntityQuery(ComponentType.ReadOnly<ZoneData>());
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    ZoneData zd = EntityManager.GetComponentData<ZoneData>(e);
                    if (zd.m_ZoneType.m_Index == 0) { continue; }
                    if (_prefabSystem.TryGetPrefab(e, out PrefabBase pb) && pb != null)
                    {
                        name = pb.name;
                        index = zd.m_ZoneType.m_Index;
                        return true;
                    }
                }
            }
            finally { ents.Dispose(); }

            return false;
        }

        private void VerifyZone()
        {
            if (_zoneBlock == Entity.Null) { return; }
            if (!EntityManager.Exists(_zoneBlock) || !EntityManager.HasBuffer<Cell>(_zoneBlock))
            {
                Result("zoning", false, "block gone after paint");
                return;
            }

            DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(_zoneBlock, true);
            ushort got = cells.Length > 0 ? cells[0].m_Zone.m_Index : ushort.MaxValue;
            Result("zoning", got == _zoneExpectedIndex, $"cell0={got} expected={_zoneExpectedIndex}");
        }

        private void ActNet()
        {
            _edgesBeforeNet = _allEdgesQuery.CalculateEntityCount();
            if (_edgeQuery.IsEmptyIgnoreFilter) { Result("net", false, "no edges in city to clone"); return; }

            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity src = ents[0];
                PrefabRef pr = EntityManager.GetComponentData<PrefabRef>(src);
                if (!_prefabSystem.TryGetPrefab(pr.m_Prefab, out PrefabBase prefab) || prefab == null)
                {
                    Result("net", false, "no prefab for edge");
                    return;
                }

                Colossal.Mathematics.Bezier4x3 b = EntityManager.GetComponentData<Game.Net.Curve>(src).m_Bezier;
                var off = new float3(0f, 0f, 40f);
                _netStart = new float3(b.a.x + off.x, b.a.y, b.a.z + off.z);
                _netEnd = new float3(b.d.x + off.x, b.d.y, b.d.z + off.z);
                L($"[Auto] TEST net INJECT name={prefab.name} edgesBefore={_edgesBeforeNet}");
                RemoteNetQueue.Enqueue(new NetPlaceCommand
                {
                    SyncId = CS2M_SyncIdSystem.Allocate(),
                    PrefabType = prefab.GetType().Name, PrefabName = prefab.name,
                    Ax = b.a.x + off.x, Ay = b.a.y, Az = b.a.z + off.z,
                    Bx = b.b.x + off.x, By = b.b.y, Bz = b.b.z + off.z,
                    Cx = b.c.x + off.x, Cy = b.c.y, Cz = b.c.z + off.z,
                    Dx = b.d.x + off.x, Dy = b.d.y, Dz = b.d.z + off.z,
                    RandomSeed = 0,
                });
            }
            finally { ents.Dispose(); }
        }

        private void VerifyNet()
        {
            int edges = _allEdgesQuery.CalculateEntityCount();
            Result("net", edges > _edgesBeforeNet, $"edges {_edgesBeforeNet}->{edges}");
        }

        private void ActDelete()
        {
            if (_treeSyncId == 0) { Result("delete", false, "no tree to delete"); return; }
            L($"[Auto] TEST delete INJECT syncId={_treeSyncId}");
            RemoteEditQueue.EnqueueDelete(new DeleteCommand { SyncId = _treeSyncId });
        }

        private void VerifyDelete()
        {
            if (_treeSyncId == 0) { return; }
            bool gone = !_idSystem.TryResolve(_treeSyncId, out Entity e) || !EntityManager.Exists(e)
                        || EntityManager.HasComponent<Deleted>(e);
            Result("delete", gone, gone ? "tree removed" : "tree still present");
        }

        private void ActBudget()
        {
            _budgetService = Entity.Null;
            _budgetExpected = int.MinValue;
            EntityQuery q = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.ServiceData>());
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    Game.Prefabs.ServiceData sd = EntityManager.GetComponentData<Game.Prefabs.ServiceData>(e);
                    if (!sd.m_BudgetAdjustable) { continue; }
                    if (!_prefabSystem.TryGetPrefab(e, out PrefabBase pb) || pb == null) { continue; }
                    int cur = _budgetSystem.GetServiceBudget(e);
                    int target = cur >= 100 ? cur - 10 : cur + 10; // nudge the funding %
                    _budgetService = e;
                    _budgetExpected = target;
                    L($"[Auto] TEST budget INJECT name={pb.name} {cur}->{target}");
                    RemoteBudgetQueue.Enqueue(new BudgetCommand { ServiceType = pb.GetType().Name, ServiceName = pb.name, Percentage = target });
                    return;
                }

                Result("budget", false, "no adjustable service found");
            }
            finally { ents.Dispose(); }
        }

        private void VerifyBudget()
        {
            if (_budgetExpected == int.MinValue) { return; }
            int got = _budgetSystem.GetServiceBudget(_budgetService);
            Result("budget", got == _budgetExpected, $"pct={got} expected={_budgetExpected}");
        }

        private void ActNetUpgrade()
        {
            _upgradeEdge = Entity.Null;
            if (_edgeQuery.IsEmptyIgnoreFilter) { Result("net-upgrade", false, "no edges to upgrade"); return; }
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity e = ents[0];
                Game.Net.Edge ed = EntityManager.GetComponentData<Game.Net.Edge>(e);
                if (!EntityManager.HasComponent<Game.Net.Node>(ed.m_Start) || !EntityManager.HasComponent<Game.Net.Node>(ed.m_End))
                {
                    Result("net-upgrade", false, "edge has no nodes");
                    return;
                }

                float3 s = EntityManager.GetComponentData<Game.Net.Node>(ed.m_Start).m_Position;
                float3 en = EntityManager.GetComponentData<Game.Net.Node>(ed.m_End).m_Position;
                _upgradeEdge = e;
                _upgradeExpectedLeft = 0x1000u; // CompositionFlags.Side.PrimaryBeautification (trees, left)
                L($"[Auto] TEST net-upgrade INJECT edge={e.Index} left=0x{_upgradeExpectedLeft:X}");
                RemoteNetUpgradeQueue.Enqueue(new NetUpgradeCommand
                {
                    StartX = s.x, StartY = s.y, StartZ = s.z,
                    EndX = en.x, EndY = en.y, EndZ = en.z,
                    General = 0, Left = _upgradeExpectedLeft, Right = 0,
                });
            }
            finally { ents.Dispose(); }
        }

        private void VerifyNetUpgrade()
        {
            if (_upgradeEdge == Entity.Null) { return; }
            if (!EntityManager.Exists(_upgradeEdge) || !EntityManager.HasComponent<Game.Net.Upgraded>(_upgradeEdge))
            {
                Result("net-upgrade", false, "no Upgraded on edge (game may have cleared an invalid flag for this road)");
                return;
            }

            Game.Net.Upgraded u = EntityManager.GetComponentData<Game.Net.Upgraded>(_upgradeEdge);
            uint left = (uint) u.m_Flags.m_Left;
            Result("net-upgrade", (left & _upgradeExpectedLeft) == _upgradeExpectedLeft, $"left=0x{left:X} expectedBit=0x{_upgradeExpectedLeft:X}");
        }

        private void ActPolicy()
        {
            _policyEntity = Entity.Null;
            _policyExpectedActive = false;
            if (!TryCity(out Entity city) || !EntityManager.HasBuffer<Game.Policies.Policy>(city))
            {
                Result("policy", false, "no Policy buffer on City");
                return;
            }

            DynamicBuffer<Game.Policies.Policy> buf = EntityManager.GetBuffer<Game.Policies.Policy>(city, true);
            if (buf.Length == 0) { Result("policy", false, "no unlocked policies in this (empty) city"); return; }

            Entity target = Entity.Null;
            bool newActive = true;
            string name = null;
            for (int i = 0; i < buf.Length; i++)
            {
                bool active = (buf[i].m_Flags & Game.Policies.PolicyFlags.Active) != 0;
                if (!active && _prefabSystem.TryGetPrefab(buf[i].m_Policy, out PrefabBase pb) && pb != null)
                {
                    target = buf[i].m_Policy; newActive = true; name = pb.name; break;
                }
            }

            if (target == Entity.Null && _prefabSystem.TryGetPrefab(buf[0].m_Policy, out PrefabBase pb0) && pb0 != null)
            {
                target = buf[0].m_Policy;
                newActive = (buf[0].m_Flags & Game.Policies.PolicyFlags.Active) == 0;
                name = pb0.name;
            }

            if (target == Entity.Null) { Result("policy", false, "no resolvable policy"); return; }

            _policyEntity = target;
            _policyExpectedActive = newActive;
            string type = _prefabSystem.TryGetPrefab(target, out PrefabBase pbt) && pbt != null ? pbt.GetType().Name : "PolicyPrefab";
            L($"[Auto] TEST policy INJECT name={name} active={newActive}");
            RemotePolicyQueue.Enqueue(new PolicyCommand { PolicyType = type, PolicyName = name, Active = newActive, Adjustment = 0f });
        }

        private void VerifyPolicy()
        {
            if (_policyEntity == Entity.Null) { return; }
            if (!TryCity(out Entity city) || !EntityManager.HasBuffer<Game.Policies.Policy>(city)) { Result("policy", false, "no Policy buffer"); return; }
            DynamicBuffer<Game.Policies.Policy> buf = EntityManager.GetBuffer<Game.Policies.Policy>(city, true);
            bool found = false, active = false;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_Policy == _policyEntity)
                {
                    found = true;
                    active = (buf[i].m_Flags & Game.Policies.PolicyFlags.Active) != 0;
                    break;
                }
            }

            Result("policy", found && active == _policyExpectedActive, $"found={found} active={active} expected={_policyExpectedActive}");
        }

        private void ActNetDelete()
        {
            if (math.lengthsq(_netStart) < 1f) { Result("net-delete", false, "no net was created to delete"); _edgesBeforeNetDelete = -1; return; }
            _edgesBeforeNetDelete = _allEdgesQuery.CalculateEntityCount();
            L($"[Auto] TEST net-delete INJECT start=({_netStart.x:F0},{_netStart.z:F0}) end=({_netEnd.x:F0},{_netEnd.z:F0}) edgesBefore={_edgesBeforeNetDelete}");
            RemoteNetDeleteQueue.Enqueue(new NetDeleteCommand
            {
                StartX = _netStart.x, StartY = _netStart.y, StartZ = _netStart.z,
                EndX = _netEnd.x, EndY = _netEnd.y, EndZ = _netEnd.z,
            });
        }

        private void VerifyNetDelete()
        {
            if (_edgesBeforeNetDelete < 0) { return; }
            int edges = _allEdgesQuery.CalculateEntityCount();
            Result("net-delete", edges < _edgesBeforeNetDelete, $"edges {_edgesBeforeNetDelete}->{edges}");
        }

        private void ActTax()
        {
            NativeArray<int> rates = _taxSystem.GetTaxRates();
            if (!rates.IsCreated || rates.Length == 0) { Result("tax", false, "no tax rates"); _taxExpectedMain = int.MinValue; return; }
            var copy = new int[rates.Length];
            for (int i = 0; i < rates.Length; i++) { copy[i] = rates[i]; }
            copy[0] = copy[0] + 3; // bump the main tax rate
            _taxExpectedMain = copy[0];
            L($"[Auto] TEST tax INJECT main {rates[0]}->{copy[0]}");
            RemoteTaxQueue.Set(copy);
        }

        private void VerifyTax()
        {
            if (_taxExpectedMain == int.MinValue) { return; }
            NativeArray<int> rates = _taxSystem.GetTaxRates();
            int got = (rates.IsCreated && rates.Length > 0) ? rates[0] : int.MinValue;
            Result("tax", got == _taxExpectedMain, $"main={got} expected={_taxExpectedMain}");
        }

        private void ActPause()
        {
            _forceRun = false; // stop forcing the sim to run so the pause test can actually pause
            _speedBeforePause = _sim.selectedSpeed;
            L($"[Auto] TEST pause INJECT joining=true (speedBefore={_speedBeforePause})");
            RemoteJoinState.Update("TestJoiner", true);
        }

        private void VerifyPause()
        {
            float speed = _sim.selectedSpeed;
            Result("pause", speed == 0f, $"selectedSpeed={speed} (expected 0 while joining)");
        }

        private void ActResume()
        {
            L("[Auto] TEST pause INJECT joining=false (resume)");
            RemoteJoinState.Update("TestJoiner", false);
        }

        private void VerifyResume()
        {
            float speed = _sim.selectedSpeed;
            Result("resume", speed > 0f, $"selectedSpeed={speed} (expected >0 after resume)");
        }

        private void Summary()
        {
            int pop = ReadPopulation();
            int maxPop = ReadMaxPop();
            L("[Auto] ================ SELFTEST RESULTS ================");
            foreach (string line in _results) { L("[Auto] RESULT " + line); }
            L($"[Auto] INFO population={pop} maxPopulation(xp)={maxPop} " +
              "(population is emergent sim state — NOT directly synced; see gaps)");
            L("[Auto] GAPS not-validated-here: live population/citizens/vehicles/traffic/economy tick " +
              "are not synced (emergent simulation); net snapping/split, growable edits, zone flood-fill are v2.");
            L("[Auto] scripted test DONE");
        }

        private void Result(string name, bool ok, string detail)
        {
            string line = $"{name}: {(ok ? "PASS" : "FAIL")} ({detail})";
            _results.Add(line);
            L($"[Auto] RESULT {line}");
        }

        // ------- state readers -------

        private bool TryCity(out Entity city)
        {
            city = _citySystem.City;
            return city != Entity.Null;
        }

        private int ReadMoney(out bool unlimited)
        {
            unlimited = false;
            if (TryCity(out Entity c) && EntityManager.HasComponent<PlayerMoney>(c))
            {
                PlayerMoney pm = EntityManager.GetComponentData<PlayerMoney>(c);
                unlimited = pm.m_Unlimited;
                return pm.money;
            }

            return int.MinValue;
        }

        private int ReadPopulation()
        {
            if (TryCity(out Entity c) && EntityManager.HasComponent<Population>(c))
            {
                return EntityManager.GetComponentData<Population>(c).m_Population;
            }

            return -1;
        }

        private int ReadMaxPop()
        {
            if (TryCity(out Entity c) && EntityManager.HasComponent<XP>(c))
            {
                return EntityManager.GetComponentData<XP>(c).m_MaximumPopulation;
            }

            return -1;
        }

        // ------- original over-the-wire host sequence (for the 2-PC test) -------

        private void RunHostStep()
        {
            if (_testStep >= 5) { return; }
            if (_testTimer > 0) { _testTimer--; return; }

            switch (_testStep)
            {
                case 0: _treeSyncId = SendObject(_treeQuery, "tree", new float3(20f, 0f, 20f)); _testTimer = 300; break;
                case 1: _buildingSyncId = SendObject(_buildingQuery, "building", new float3(60f, 0f, 60f)); _testTimer = 300; break;
                case 2: SendNet(); _testTimer = 300; break;
                case 3: if (_treeSyncId != 0) { Command.SendToAll?.Invoke(new DeleteCommand { SyncId = _treeSyncId }); L($"[Auto] TEST delete SEND syncId={_treeSyncId}"); } _testTimer = 240; break;
                case 4: L("[Auto] scripted test DONE (over the wire). Check CLIENT log VERIFY lines."); break;
            }

            _testStep++;
        }

        private ulong SendObject(EntityQuery query, string kind, float3 offset)
        {
            if (query.IsEmptyIgnoreFilter) { L($"[Auto] TEST object SKIP kind={kind} reason=noneInCity"); return 0; }
            NativeArray<Entity> ents = query.ToEntityArray(Allocator.Temp);
            try
            {
                Entity src = ents[0];
                PrefabRef pr = EntityManager.GetComponentData<PrefabRef>(src);
                if (!_prefabSystem.TryGetPrefab(pr.m_Prefab, out PrefabBase prefab) || prefab == null) { return 0; }
                Game.Objects.Transform t = EntityManager.GetComponentData<Game.Objects.Transform>(src);
                float3 pos = t.m_Position + offset;
                int seed = EntityManager.HasComponent<PseudoRandomSeed>(src)
                    ? EntityManager.GetComponentData<PseudoRandomSeed>(src).m_Seed : 0;
                var cmd = new ObjectPlaceCommand
                {
                    SyncId = CS2M_SyncIdSystem.Allocate(),
                    PrefabType = prefab.GetType().Name, PrefabName = prefab.name,
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    RotX = t.m_Rotation.value.x, RotY = t.m_Rotation.value.y,
                    RotZ = t.m_Rotation.value.z, RotW = t.m_Rotation.value.w,
                    RandomSeed = seed,
                };
                L($"[Auto] TEST object SEND kind={kind} name={cmd.PrefabName} syncId={cmd.SyncId}");
                Command.SendToAll?.Invoke(cmd);
                return cmd.SyncId;
            }
            finally { ents.Dispose(); }
        }

        private void SendNet()
        {
            if (_edgeQuery.IsEmptyIgnoreFilter) { L("[Auto] TEST net SKIP reason=noEdgesInCity"); return; }
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity src = ents[0];
                PrefabRef pr = EntityManager.GetComponentData<PrefabRef>(src);
                if (!_prefabSystem.TryGetPrefab(pr.m_Prefab, out PrefabBase prefab) || prefab == null) { return; }
                Colossal.Mathematics.Bezier4x3 b = EntityManager.GetComponentData<Game.Net.Curve>(src).m_Bezier;
                var off = new float3(0f, 0f, 40f);
                var cmd = new NetPlaceCommand
                {
                    SyncId = CS2M_SyncIdSystem.Allocate(),
                    PrefabType = prefab.GetType().Name, PrefabName = prefab.name,
                    Ax = b.a.x + off.x, Ay = b.a.y, Az = b.a.z + off.z,
                    Bx = b.b.x + off.x, By = b.b.y, Bz = b.b.z + off.z,
                    Cx = b.c.x + off.x, Cy = b.c.y, Cz = b.c.z + off.z,
                    Dx = b.d.x + off.x, Dy = b.d.y, Dz = b.d.z + off.z,
                };
                L($"[Auto] TEST net SEND name={cmd.PrefabName} syncId={cmd.SyncId}");
                Command.SendToAll?.Invoke(cmd);
            }
            finally { ents.Dispose(); }
        }

        // ---------------------------------------------------------------- CLIENT

        private void UpdateClient()
        {
            PlayerStatus status = NetworkInterface.Instance.LocalPlayer.PlayerStatus;
            if (status != PlayerStatus.PLAYING)
            {
                if (_gameMode == GameMode.MainMenu && status == PlayerStatus.INACTIVE)
                {
                    if (_connectRetryFrames > 0) { _connectRetryFrames--; }
                    else if (_connectAttempts < 30) { TryClientConnect(); _connectRetryFrames = 240; }
                }

                return;
            }

            LogCounts(status.ToString());
        }

        private void LogCounts(string status)
        {
            if (!_playingLogged)
            {
                _playingLogged = true;
                L("[Auto] PLAYING — watching for placements in the world.");
            }

            int remote = _remotePlacedQuery.CalculateEntityCount();
            int edges = _allEdgesQuery.CalculateEntityCount();
            bool changed = remote != _lastRemoteCount || edges != _lastEdgeCount;

            if (++_verifyFrames >= 180 || changed)
            {
                _verifyFrames = 0;
                string tag = changed ? "CHANGED" : "heartbeat";
                float speed = _sim != null ? _sim.selectedSpeed : -1f;
                L($"[Auto] VERIFY {tag} remoteObjects={remote} (was {_lastRemoteCount}) " +
                  $"totalEdges={edges} (was {_lastEdgeCount}) simSpeed={speed} status={status}");
                _lastRemoteCount = remote;
                _lastEdgeCount = edges;
            }
        }

        private void TryClientConnect()
        {
            _connectRequested = true;
            _connectAttempts++;
            L($"[Auto] CLIENT connect attempt #{_connectAttempts} -> {_ip}:{_port}");
            NetworkInterface.Instance.UpdateLocalPlayerUsername("AutoClient");
            NetworkInterface.Instance.Connect(new ConnectionConfig(_ip, _port, ""));
        }

        private static string Status()
        {
            return NetworkInterface.Instance.LocalPlayer.PlayerStatus.ToString();
        }

        private void L(string msg)
        {
            CS2M.Log.Info(msg);
            if (string.IsNullOrEmpty(_logPath)) { return; }
            try { File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}"); }
            catch { }
        }
    }
}
