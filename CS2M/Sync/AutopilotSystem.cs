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
        private bool _concurrent;   // CS2M_AP_CONCURRENT=1: BOTH sides stamp roads at the same spot
        private int _concStep;
        private int _concTimer;
        private string _logPath;

        private GameMode _gameMode = GameMode.Other;
        private PrefabSystem _prefabSystem;
        private CitySystem _citySystem;
        private SimulationSystem _sim;
        private CS2M_SyncIdSystem _idSystem;
        private TaxSystem _taxSystem;
        private CityServiceBudgetSystem _budgetSystem;
        private TerrainSystem _terrain;

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
        private ulong _stopSyncId;      // v50 step 24
        private Entity _fireTarget;     // v50 steps 25-26
        private ulong _fireSyncId;

        // captured state for the selftest verifications
        private int _moneyExpected;
        private bool _moneyUnlimited;
        private int _xpExpected;
        private int _taxExpectedMain = int.MinValue;
        private Entity _budgetService;
        private int _budgetExpected = int.MinValue;
        private int _districtsBefore = -1;
        private int _waterBefore = -1;
        private float3 _terrainPos;
        private float _terrainH0 = float.NaN;
        private float3 _movePosExpected;
        private Entity _zoneBlock;
        private ushort _zoneExpectedIndex;
        private int _edgesBeforeNet;
        private float3 _netStart;
        private float3 _netEnd;
        private int _edgesBeforeNetDelete;
        private ulong _batchNodeA;
        private ulong _batchNodeB;
        private int _edgesBeforeBatch;
        private Entity _upgradeEdge;
        private uint _upgradeExpectedLeft;
        private Entity _policyEntity;
        private float _policyExpectedAdj = float.NaN;
        private Entity _upgradeCompositionBefore;
        private float _speedBeforePause;
        private uint _frameIndexAtPause;
        private uint _frameIndexAtSabotage;
        private uint _frameIndexAtResume;
        private bool _forceRun = true; // keep the sim ticking (game auto-pauses when unfocused)
        private readonly List<string> _results = new List<string>();

        // verify (client + selftest counts)
        private bool _playingLogged;
        private int _verifyFrames;
        private bool _joinReAnnounced;
        private int _joinReannounceFrames;
        private int _lastRemoteCount = -1;
        private int _lastEdgeCount = -1;

        // client
        private bool _connectRequested;
        private int _connectRetryFrames;
        private int _connectAttempts;

        // ---- "/validate" chat command: run the full self-check inside the CURRENT live session ----
        private static bool _chatValidationRequested;
        private bool _chatValidation;

        /// <summary>Called by the chat panel when the player types "/validate".</summary>
        public static void RequestChatValidation()
        {
            _chatValidationRequested = true;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            // Systems/queries are initialized unconditionally so the "/validate" chat command works
            // in any session; the env-var only gates the AUTOMATED (headless) roles below.
            InitSystemsAndQueries();

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
            _concurrent = Environment.GetEnvironmentVariable("CS2M_AP_CONCURRENT") == "1";

            GameManager.instance.onGameLoadingComplete += OnLoadingComplete;

            string r = _selftest ? "SELFTEST" : (_isHost ? "HOST" : "CLIENT");
            L($"[Auto] ENABLED role={r} port={_port} ip={_ip} scriptedTest={_testEnabled} " +
              $"log={_logPath ?? "(game log only)"}");
        }

        private void InitSystemsAndQueries()
        {
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            _sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            _idSystem = World.GetOrCreateSystemManaged<CS2M_SyncIdSystem>();
            _taxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            _budgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();

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
            if (_chatValidationRequested)
            {
                _chatValidationRequested = false;
                StartChatValidation();
            }

            if (_chatValidation)
            {
                UpdateChatValidation();
                return;
            }

            if (_disabled) { return; }
            if (_isHost) { UpdateHost(); } else { UpdateClient(); }
        }

        // ------------- "/validate": full self-check inside the live session (no relaunch) -------------

        private void StartChatValidation()
        {
            if (_chatValidation)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                Chat("validation needs an active session (host or join first).");
                return;
            }

            _chatValidation = true;
            _testStep = 0;
            _testTimer = 60;
            _results.Clear();
            _forceRun = false; // never override the live session's speed
            L("[Auto] CHAT VALIDATION starting (triggered by /validate)");
            Chat($"validating this build in-session — ~28 checks over ~2 min. role={CS2M.API.Commands.Command.CurrentRole}. " +
                 "NOTE: the check modifies the city (money/XP/test objects) — best on a test save.");
        }

        private void UpdateChatValidation()
        {
            RunSelftestStep();

            // Fake remote cursor while validating: the player can SEE the label pipeline working.
            if (TryAnchor(out float3 cursorAnchor))
            {
                RemotePlayerCursors.Update(1, cursorAnchor.x + 30f, cursorAnchor.y, cursorAnchor.z + 30f,
                    true, "FakeFriend");
            }

            // v50: was stuck at 19 since v40 — /validate silently skipped every newer check
            // (devtree/env/tile/route). Now tracks the full suite.
            if (_testStep > 31)
            {
                _chatValidation = false;
                RemotePlayerCursors.Remove(1);
                int pass = 0, fail = 0;
                foreach (string line in _results)
                {
                    if (line.Contains(": PASS")) { pass++; } else { fail++; }
                }

                Chat($"validation DONE: {pass} PASS / {fail} FAIL (details in CS2M.log)");
            }
        }

        private void Chat(string msg)
        {
            try { CS2M.API.Chat.Instance?.PrintChatMessage("CS2M", msg); }
            catch { }
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
                if (_selftest)
                {
                    // Non-circular: reads the effect of the real StartServer transition. The first
                    // 2-PC session shipped with CurrentRole permanently None (never assigned), which
                    // silently killed every host-authoritative sender.
                    Result("role",
                        CS2M.API.Commands.Command.CurrentRole == CS2M.API.Commands.MultiplayerRole.Server,
                        $"CurrentRole={CS2M.API.Commands.Command.CurrentRole} (expect Server after StartServer)");

                    // Fase 0 guarantee: every command classified + every game tool covered. Reflects the
                    // live assembly, so a new command/tool added without a manifest row surfaces HERE as a
                    // selftest FAIL — "nada passa" enforced at run time, where all assemblies are loaded.
                    List<string> contractViolations = SyncContract.Verify();
                    Result("contract", contractViolations.Count == 0,
                        contractViolations.Count == 0
                            ? $"{SyncContract.Manifest.Count} comandos classificados, {SyncContract.ToolCoverage.Count} tools cobertos"
                            : string.Join(" | ", contractViolations));
                }

                return;
            }

            if (_testEnabled)
            {
                if (!_testStarted)
                {
                    // v55: gate on the HOST-side connected-peer count, NOT RemoteJoinState.CompletedJoins.
                    // CompletedJoins relies on the client's JoinNoticeCommand reaching the host, which was
                    // NOT arriving in the autopilot 2-sim (roteiro never fired -> smoke tests vacuous). The
                    // host's PlayerListConnected DOES include a connected client (line 154 adds it on peer
                    // connect; /resync uses Count-1 as the client count), so it is the reliable trigger.
                    // Generous initial delay so the client has finished the world transfer and is PLAYING
                    // before the first over-the-wire command (else it would arrive before the client is live).
                    // Gate on a HEALTHY join (count reaches 2). A fallback force-start when the count stays
                    // <2 was TRIED and REVERTED: forcing the roteiro under an incomplete join (host counted
                    // 0 peers) raced the early net commands and produced a MISLEADING roads/nodes drift (an
                    // arm-delete that never reached the client). Better a vacuous flaky run (just re-run)
                    // than a false DRIFT. The count is only occasionally flaky; normal runs fire fine.
                    if (!_selftest && NetworkInterface.Instance.PlayerListConnected.Count < 2)
                    {
                        return;
                    }

                    _testStarted = true;
                    _testTimer = _selftest ? 150 : 600;
                    L(_selftest
                        ? "[Auto] SELFTEST beginning — validating every feature against real game state"
                        : $"[Auto] HOST client connected (peers={NetworkInterface.Instance.PlayerListConnected.Count - 1}); test begins in {_testTimer}f");
                }
                else if (_selftest)
                {
                    RunSelftestStep();
                }
                else if (_concurrent)
                {
                    RunConcurrentStep(true);
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

                // Fake remote cursor: exercises the REAL label pipeline (PlayerCursorSystem →
                // camera projection → JSON binding → cohtml layout → render-ack log line).
                if (TryAnchor(out float3 cursorAnchor))
                {
                    RemotePlayerCursors.Update(1, cursorAnchor.x + 30f, cursorAnchor.y, cursorAnchor.z + 30f,
                        true, "FakeFriend");
                }

                LogCounts(Status());
            }
        }

        // ------- Rich single-instance validation: inject into apply queues, read state back -------

        private void RunSelftestStep()
        {
            if (_testStep > 54) { return; }
            if (_testTimer > 0) { _testTimer--; return; }
            _testTimer = 200;

            switch (_testStep)
            {
                case 0: CleanupOrphanTestWater(); InjectReplayRoad(); ActMoney(); break;
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
                case 12: VerifyBudget(); ActDistrict(); break;
                case 13: VerifyDistrict(); ActWater(); break;
                case 14: VerifyWater(); ActTerrain(); break;
                case 15: VerifyTerrain(); ActPolicy(); break;
                case 16: VerifyPolicy(); ActPause(); break;
                case 17: VerifyPauseFrozen(); SabotagePause(); break;
                case 18: VerifyPauseEnforced(); ActResume(); break;
                case 19: VerifyResume(); ActDevTree(); break;
                case 20: VerifyDevTree(); ActEnvAndNativeDelete(); break;
                case 21: VerifyEnvAndNativeDelete(); ActTile(); break;
                case 22: VerifyTile(); ActRoute(); break;
                case 23: VerifyRoute(); ActStop(); break;
                case 24: VerifyStop(); ActFireStart(); break;
                case 25: VerifyFireStart(); ActFireCollapse(); break;
                case 26: VerifyFireCollapse(); ActTee(); break;
                case 27: VerifyTee(); ActXCrossDrift(); break;
                case 28: VerifyXCrossDrift(); ActArmDelete(); break;
                case 29: VerifyArmDelete(); ActSplitFlow(); break;
                case 30: VerifySplitFlow(); ActMovedJunction(); break;
                case 31: VerifyMovedJunction(); ActSaveRerouteCreate(); break;
                case 32: ActSaveReroute(); break;
                case 33: VerifySaveReroute(); break;
                case 34: ActNodeUpgrade(); break;
                case 35: VerifyNodeUpgrade(); break;
                case 36: ActFee(); break;
                case 37: VerifyFee(); break;
                case 38: ActWaterMoveCreate(); break;
                case 39: ActWaterMove(); break;
                case 40: VerifyWaterMove(); break;
                case 41: ActExtDisable(); break;
                case 42: VerifyExtDisable(); break;
                case 43: ActCityName(); break;
                case 44: VerifyCityName(); break;
                case 45: ActDistrictReshapeCreate(); break;
                case 46: ActDistrictReshape(); break;
                case 47: VerifyDistrictReshape(); break;
                case 48: ActLineVisibility(); break;
                case 49: VerifyLineVisibility(); break;
                case 50: ActExtMove(); break;
                case 51: VerifyExtMove(); break;
                // AtomicBatch: inject a fabricated NetBatchCommand into the REAL remote queue (exactly what
                // a received builder batch does) and assert the direct-archetype apply produced a road the
                // derivation pipeline actually consumed (ConnectedEdge wired + Composition resolved).
                case 52: ActNetBatch(); break;
                case 53: VerifyNetBatch(); break;
                case 54: Summary(); break;
            }

            _testStep++;
        }

        /// <summary>Selftest boot: older test builds left their water sources alive and autosaves
        /// kept them flooding the test city forever. They all carry CS2M_RemotePlaced, so they are
        /// safe to sweep here (selftest mode only — never runs in a real session).</summary>
        private void CleanupOrphanTestWater()
        {
            EntityQuery orphans = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Simulation.WaterSourceData>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            int n = orphans.CalculateEntityCount();
            if (n == 0)
            {
                return;
            }

            EntityManager.AddComponent<Deleted>(orphans);
            L($"[Auto] BOOT cleanup removed {n} orphan test water source(s) from earlier runs");
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

        /// <summary>2-sim host roteiro: paint a zone locally AND ship it, so the client applies the same
        /// paint and the StateHash radar confirms zone cells converge cross-machine.</summary>
        private void SendZone()
        {
            if (_blockQuery.IsEmptyIgnoreFilter) { L("[Auto] TEST zone SEND SKIP noBlocks"); return; }
            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);
            if (!TryGetZone(out string targetName, out ushort _)) { L("[Auto] TEST zone SEND SKIP noZonePrefab"); return; }
            NativeArray<Entity> blocks = _blockQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity block = blocks[0];
                Block b = EntityManager.GetComponentData<Block>(block);
                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(block, true);
                if (cells.Length == 0) { return; }
                var cmd = new ZonePaintCommand
                {
                    BlockX = b.m_Position.x, BlockZ = b.m_Position.z,
                    DirX = b.m_Direction.x, DirZ = b.m_Direction.y,
                    SizeX = b.m_Size.x, SizeY = b.m_Size.y,
                    CellIndices = new[] { 0 },
                    ZoneNames = new[] { targetName },
                };
                RemoteZoneQueue.Enqueue(cmd);       // host paints
                Command.SendToAll?.Invoke(cmd);     // client paints -> StateHash checks zone convergence
                L($"[Auto] TEST zone SEND block=({b.m_Position.x:F0},{b.m_Position.z:F0}) name='{targetName}'");
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
            // Entity count alone is NOT proof — the v37 direct-archetype path grew the count with a
            // hollow edge that never got composition/geometry/zone blocks (invisible in real play).
            // v51: the road may legitimately have been SLICED by the X-crossing splitter (it now
            // wires real intersections), so find the CHAIN piece that starts at our start point;
            // its far end is where the delete test must aim.
            int edges = _allEdgesQuery.CalculateEntityCount();
            Entity found = Entity.Null;
            NativeArray<Entity> ents = _allEdgesQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!EntityManager.HasComponent<Game.Net.Curve>(e)) { continue; }
                    Colossal.Mathematics.Bezier4x3 bz = EntityManager.GetComponentData<Game.Net.Curve>(e).m_Bezier;
                    bool fwd = NearXZ(bz.a, _netStart) && NearXZ(bz.d, _netEnd);
                    bool rev = NearXZ(bz.a, _netEnd) && NearXZ(bz.d, _netStart);
                    if (fwd || rev) { found = e; break; }
                }

                if (found == Entity.Null)
                {
                    // sliced: take the piece touching the start point; retarget delete to its span
                    foreach (Entity e in ents)
                    {
                        if (!EntityManager.HasComponent<Game.Net.Curve>(e)) { continue; }
                        Colossal.Mathematics.Bezier4x3 bz = EntityManager.GetComponentData<Game.Net.Curve>(e).m_Bezier;
                        if (NearXZ(bz.a, _netStart)) { found = e; _netEnd = bz.d; break; }
                        if (NearXZ(bz.d, _netStart)) { found = e; _netEnd = bz.a; break; }
                    }
                }
            }
            finally { ents.Dispose(); }

            if (found == Entity.Null)
            {
                Result("net", false, $"edges {_edgesBeforeNet}->{edges} but injected edge NOT found by endpoints");
                return;
            }

            bool composed = EntityManager.HasComponent<Game.Net.Composition>(found)
                            && EntityManager.GetComponentData<Game.Net.Composition>(found).m_Edge != Entity.Null;

            int subBlocks = -1; // -1 = prefab is not zoneable (no SubBlock buffer) — not required
            if (EntityManager.HasBuffer<Game.Zones.SubBlock>(found))
            {
                subBlocks = EntityManager.GetBuffer<Game.Zones.SubBlock>(found).Length;
            }

            bool connected = false;
            Game.Net.Edge edgeData = EntityManager.GetComponentData<Game.Net.Edge>(found);
            if (EntityManager.HasBuffer<Game.Net.ConnectedEdge>(edgeData.m_Start))
            {
                DynamicBuffer<Game.Net.ConnectedEdge> ce =
                    EntityManager.GetBuffer<Game.Net.ConnectedEdge>(edgeData.m_Start);
                for (int i = 0; i < ce.Length; i++)
                {
                    if (ce[i].m_Edge == found) { connected = true; break; }
                }
            }

            bool ok = composed && connected && subBlocks != 0;
            Result("net", ok,
                $"edges {_edgesBeforeNet}->{edges} composition={composed} nodeConnected={connected} " +
                $"zoneBlocks={(subBlocks < 0 ? "n/a" : subBlocks.ToString())} (real build, not a hollow edge)");
        }

        private static bool NearXZ(float3 a, float3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz < 2.25f; // 1.5 m
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

        private void ActTerrain()
        {
            _terrainH0 = float.NaN;
            if (!TryAnchor(out float3 a)) { Result("terrain", false, "no anchor point in city"); return; }
            var pos = new float3(a.x + 120f, a.y, a.z + 120f); // offset a bit from buildings/roads
            TerrainHeightData hd = _terrain.GetHeightData(true);
            _terrainH0 = TerrainUtils.SampleHeight(ref hd, pos);
            _terrainPos = pos;
            L($"[Auto] TEST terrain INJECT raise pos=({pos.x:F0},{pos.z:F0}) h0={_terrainH0:F2}");
            // Gentle bump — 100000 built a 4 km sky-tower, wrecked nearby buildings and the mess
            // accumulated through autosaves (field report). A few meters proves the brush the same.
            RemoteTerrainQueue.Enqueue(new TerrainCommand
            {
                Type = 0, // Shift (raise/lower)
                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                Size = 40f, Strength = 2000f,
            });
        }

        private void VerifyTerrain()
        {
            if (float.IsNaN(_terrainH0)) { return; }
            TerrainHeightData hd = _terrain.GetHeightData(true);
            float h1 = TerrainUtils.SampleHeight(ref hd, _terrainPos);
            Result("terrain", h1 > _terrainH0 + 0.05f, $"height {_terrainH0:F2}->{h1:F2}");

            // Leave no trace: apply the exact inverse brush stroke.
            RemoteTerrainQueue.Enqueue(new TerrainCommand
            {
                Type = 0,
                PosX = _terrainPos.x, PosY = _terrainPos.y, PosZ = _terrainPos.z,
                Size = 40f, Strength = -2000f,
            });
        }

        private void ActWater()
        {
            _waterBefore = -1;
            if (!TryAnchor(out float3 center)) { Result("water", false, "no anchor point in city"); return; }
            _waterBefore = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Simulation.WaterSourceData>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }).CalculateEntityCount();
            L($"[Auto] TEST water INJECT pos=({center.x:F0},{center.z:F0}) before={_waterBefore}");
            RemoteWaterQueue.Enqueue(new WaterCommand
            {
                PosX = center.x, PosY = center.y, PosZ = center.z,
                Radius = 20f, Height = 5f, Multiplier = 1f, Polluted = 0f, ConstantDepth = 0,
            });
        }

        private void VerifyWater()
        {
            if (_waterBefore < 0) { return; }
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Simulation.WaterSourceData>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            int after = q.CalculateEntityCount();
            Result("water", after > _waterBefore, $"waterSources {_waterBefore}->{after}");

            // Leave no trace: delete the source we just made (nearest to the anchor) — the flood it
            // spawned drains on its own once the source is gone (field report: a lake stayed behind
            // and autosaves kept it forever).
            if (after > _waterBefore && TryAnchor(out float3 anchor))
            {
                Entity best = Entity.Null;
                float bestD = 50f * 50f;
                NativeArray<Entity> sources = q.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity s in sources)
                    {
                        if (!EntityManager.HasComponent<Game.Objects.Transform>(s)) { continue; }
                        float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(s).m_Position;
                        float dx = p.x - anchor.x, dz = p.z - anchor.z;
                        float d = dx * dx + dz * dz;
                        if (d < bestD) { bestD = d; best = s; }
                    }
                }
                finally { sources.Dispose(); }

                if (best != Entity.Null)
                {
                    EntityManager.AddComponent<Deleted>(best);
                    L($"[Auto] TEST water CLEANUP removed test source entity={best.Index}");
                }
            }
        }

        private void ActDistrict()
        {
            _districtsBefore = -1;
            if (!TryGetDistrictPrefab(out string type, out string name, out Entity _))
            {
                Result("district", false, "no District prefab found");
                return;
            }

            if (!TryAnchor(out float3 center))
            {
                Result("district", false, "no anchor point in city");
                return;
            }

            float y = center.y;
            var xs = new[] { center.x - 60f, center.x + 60f, center.x + 60f, center.x - 60f, center.x - 60f };
            var zs = new[] { center.z - 60f, center.z - 60f, center.z + 60f, center.z + 60f, center.z - 60f };
            var ys = new[] { y, y, y, y, y };

            _districtsBefore = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Areas.District>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }).CalculateEntityCount();

            L($"[Auto] TEST district INJECT name={name} center=({center.x:F0},{center.z:F0}) before={_districtsBefore}");
            RemoteDistrictQueue.Enqueue(new DistrictCommand
            {
                PrefabType = type, PrefabName = name, OptionMask = 0u, Xs = xs, Ys = ys, Zs = zs,
            });
        }

        private void VerifyDistrict()
        {
            if (_districtsBefore < 0) { return; }
            int after = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Areas.District>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            }).CalculateEntityCount();
            Result("district", after > _districtsBefore, $"districts {_districtsBefore}->{after}");
        }

        private bool TryGetDistrictPrefab(out string type, out string name, out Entity dp)
        {
            type = null;
            name = null;
            dp = Entity.Null;
            EntityQuery q = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.AreaData>());
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_prefabSystem.TryGetPrefab(e, out PrefabBase pb) || pb == null) { continue; }
                    if (pb.name.IndexOf("District", System.StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    type = pb.GetType().Name;
                    name = pb.name;
                    dp = e;
                    return true;
                }
            }
            finally { ents.Dispose(); }

            return false;
        }

        private bool TryAnchor(out float3 center)
        {
            center = default;
            if (!_buildingQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> b = _buildingQuery.ToEntityArray(Allocator.Temp);
                try { center = EntityManager.GetComponentData<Game.Objects.Transform>(b[0]).m_Position; return true; }
                finally { b.Dispose(); }
            }

            if (!_edgeQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> e = _edgeQuery.ToEntityArray(Allocator.Temp);
                try { center = EntityManager.GetComponentData<Game.Net.Curve>(e[0]).m_Bezier.a; return true; }
                finally { e.Dispose(); }
            }

            return false;
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
                // Pick a ROAD edge AND a side flag the road actually SUPPORTS but doesn't yet have. A
                // hardcoded flag (trees) false-FAILs on roads that lack a verge — the game silently keeps
                // the same composition. NetData.m_SideFlagMask is the road's supported-upgrade set; pick a
                // masked flag not already present in the current composition so the upgrade must change it.
                // Preference order = the visible cosmetic upgrades a player would toggle.
                uint[] cands = { 0x1000u, 0x2000u, 0x20000u, 0x10000u, 0x80000u }; // trees, sec-trees, wide/plain sidewalk, sound barrier
                Entity e = Entity.Null;
                uint pick = 0;
                foreach (Entity cand in ents)
                {
                    Entity prefabEnt = EntityManager.GetComponentData<PrefabRef>(cand).m_Prefab;
                    if (!_prefabSystem.TryGetPrefab(prefabEnt, out PrefabBase p) || p == null
                        || !p.name.Contains("Road") || p.name.Contains("Invisible")
                        || !EntityManager.HasComponent<Game.Prefabs.NetData>(prefabEnt)
                        || !EntityManager.HasComponent<Game.Net.Edge>(cand))
                    {
                        continue;
                    }

                    Game.Net.Edge edC = EntityManager.GetComponentData<Game.Net.Edge>(cand);
                    if (!EntityManager.HasComponent<Game.Net.Node>(edC.m_Start)
                        || !EntityManager.HasComponent<Game.Net.Node>(edC.m_End))
                    {
                        continue;
                    }

                    uint mask = (uint) EntityManager.GetComponentData<Game.Prefabs.NetData>(prefabEnt).m_SideFlagMask;
                    uint curLeft = 0;
                    if (EntityManager.HasComponent<Game.Net.Composition>(cand))
                    {
                        Entity comp = EntityManager.GetComponentData<Game.Net.Composition>(cand).m_Edge;
                        if (comp != Entity.Null && EntityManager.HasComponent<Game.Prefabs.NetCompositionData>(comp))
                        {
                            curLeft = (uint) EntityManager.GetComponentData<Game.Prefabs.NetCompositionData>(comp).m_Flags.m_Left;
                        }
                    }

                    foreach (uint f in cands)
                    {
                        if ((mask & f) == f && (curLeft & f) == 0) { pick = f; break; }
                    }

                    if (pick != 0) { e = cand; break; }
                }

                if (e == Entity.Null)
                {
                    Result("net-upgrade", true, "skipped: no road offers an unused side upgrade in its SideFlagMask (nothing to test, not a sync fault)");
                    return;
                }

                Game.Net.Edge ed = EntityManager.GetComponentData<Game.Net.Edge>(e);
                float3 s = EntityManager.GetComponentData<Game.Net.Node>(ed.m_Start).m_Position;
                float3 en = EntityManager.GetComponentData<Game.Net.Node>(ed.m_End).m_Position;
                _upgradeEdge = e;
                _upgradeCompositionBefore = EntityManager.HasComponent<Game.Net.Composition>(e)
                    ? EntityManager.GetComponentData<Game.Net.Composition>(e).m_Edge
                    : Entity.Null;
                _upgradeExpectedLeft = pick; // a Side flag the road supports and doesn't yet carry
                L($"[Auto] TEST net-upgrade INJECT edge={e.Index} left=0x{_upgradeExpectedLeft:X} (from SideFlagMask)");
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
            if (!EntityManager.Exists(_upgradeEdge))
            {
                Result("net-upgrade", false, "edge vanished");
                return;
            }

            // Tag still present (apply landed late in the step window): check the flag directly.
            if (EntityManager.HasComponent<Game.Net.Upgraded>(_upgradeEdge))
            {
                Game.Net.Upgraded u = EntityManager.GetComponentData<Game.Net.Upgraded>(_upgradeEdge);
                uint left = (uint) u.m_Flags.m_Left;
                Result("net-upgrade", (left & _upgradeExpectedLeft) == _upgradeExpectedLeft,
                    $"left=0x{left:X} expectedBit=0x{_upgradeExpectedLeft:X}");
                return;
            }

            // Normal since v41 (apply runs before Mod1): the pipeline CONSUMES Upgraded the same
            // frame — the real effect is CompositionSelectSystem picking a NEW composition for the
            // edge. Reading the re-selected composition is the non-circular proof the upgrade landed.
            Entity compNow = EntityManager.HasComponent<Game.Net.Composition>(_upgradeEdge)
                ? EntityManager.GetComponentData<Game.Net.Composition>(_upgradeEdge).m_Edge
                : Entity.Null;
            bool changed = compNow != Entity.Null && compNow != _upgradeCompositionBefore;

            // Robust proof: the road's FINAL (merged base+upgrade) composition carries the expected
            // beautification bit. If the picked road already had left-trees, `changed` is false yet the
            // bit is still present — both sims converge to a composition WITH the flag, so its presence
            // (not necessarily a change) is what proves the upgrade landed. Avoids a false-FAIL when the
            // first city road happens to already be beautified. Still FAILs if the flag is truly absent.
            bool flagPresent = false;
            if (compNow != Entity.Null && EntityManager.HasComponent<Game.Prefabs.NetCompositionData>(compNow))
            {
                uint left = (uint) EntityManager.GetComponentData<Game.Prefabs.NetCompositionData>(compNow).m_Flags.m_Left;
                flagPresent = (left & _upgradeExpectedLeft) == _upgradeExpectedLeft;
            }

            Result("net-upgrade", changed || flagPresent,
                $"composition {_upgradeCompositionBefore.Index}->{compNow.Index} changed={changed} " +
                $"flagPresent={flagPresent} (Upgraded consumed; final composition must carry the beautification bit)");
        }

        private void ActPolicy()
        {
            _policyEntity = Entity.Null;
            _policyExpectedAdj = float.NaN;
            if (!TryCity(out Entity city) || !EntityManager.HasBuffer<Game.Policies.Policy>(city))
            {
                Result("policy", false, "no Policy buffer on City");
                return;
            }

            DynamicBuffer<Game.Policies.Policy> buf = EntityManager.GetBuffer<Game.Policies.Policy>(city, true);
            if (buf.Length == 0) { Result("policy", false, "no unlocked policies in this (empty) city"); return; }

            // Change the FIRST policy's adjustment to a distinct value (keeping it active). This proves
            // whether the Modify event is actually consumed, regardless of on/off-vs-fee policy type.
            if (!_prefabSystem.TryGetPrefab(buf[0].m_Policy, out PrefabBase pb) || pb == null)
            {
                Result("policy", false, "first policy prefab unresolved");
                return;
            }

            _policyEntity = buf[0].m_Policy;
            bool active = (buf[0].m_Flags & Game.Policies.PolicyFlags.Active) != 0;
            float cur = buf[0].m_Adjustment;
            _policyExpectedAdj = cur + 17f;
            L($"[Auto] TEST policy INJECT name={pb.name} active={active} adj {cur}->{_policyExpectedAdj}");
            RemotePolicyQueue.Enqueue(new PolicyCommand
            {
                PolicyType = pb.GetType().Name, PolicyName = pb.name, Active = active, Adjustment = _policyExpectedAdj,
            });
        }

        private void VerifyPolicy()
        {
            if (_policyEntity == Entity.Null || float.IsNaN(_policyExpectedAdj)) { return; }
            if (!TryCity(out Entity city) || !EntityManager.HasBuffer<Game.Policies.Policy>(city)) { Result("policy", false, "no Policy buffer"); return; }
            DynamicBuffer<Game.Policies.Policy> buf = EntityManager.GetBuffer<Game.Policies.Policy>(city, true);
            bool found = false;
            float adj = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_Policy == _policyEntity)
                {
                    found = true;
                    adj = buf[i].m_Adjustment;
                    break;
                }
            }

            Result("policy", found && math.abs(adj - _policyExpectedAdj) < 0.5f, $"found={found} adj={adj} expected={_policyExpectedAdj}");
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

        /// <summary>AtomicBatch smoke: fabricate the batch a remote builder's capture would ship for one
        /// isolated straight road (2 new nodes + 1 edge, no boundary) and enqueue it into the REAL
        /// RemoteNetBatchQueue. NetBatchApplySystem (always on) must create it via the direct-archetype
        /// recipe the same frame.</summary>
        private void ActNetBatch()
        {
            if (_edgeQuery.IsEmptyIgnoreFilter) { Result("net-batch", false, "no edges in city to clone"); _edgesBeforeBatch = -1; return; }
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity src = ents[0];
                PrefabRef pr = EntityManager.GetComponentData<PrefabRef>(src);
                if (!_prefabSystem.TryGetPrefab(pr.m_Prefab, out PrefabBase prefab) || prefab == null)
                {
                    Result("net-batch", false, "no prefab for edge");
                    _edgesBeforeBatch = -1;
                    return;
                }

                Colossal.Mathematics.Bezier4x3 srcB = EntityManager.GetComponentData<Game.Net.Curve>(src).m_Bezier;
                var a0 = new float3(srcB.a.x + 140f, srcB.a.y, srcB.a.z + 140f);
                var d0 = new float3(srcB.a.x + 240f, srcB.a.y, srcB.a.z + 140f);
                float3 b0 = math.lerp(a0, d0, 1f / 3f);
                float3 c0 = math.lerp(a0, d0, 2f / 3f);

                _batchNodeA = CS2M_SyncIdSystem.Allocate();
                _batchNodeB = CS2M_SyncIdSystem.Allocate();
                _edgesBeforeBatch = _allEdgesQuery.CalculateEntityCount();
                L($"[Auto] TEST net-batch INJECT prefab={prefab.name} ids={_batchNodeA}/{_batchNodeB} edgesBefore={_edgesBeforeBatch}");

                RemoteNetBatchQueue.Enqueue(new NetBatchCommand
                {
                    NodeIds = new[] { _batchNodeA, _batchNodeB },
                    NodePosX = new[] { a0.x, d0.x }, NodePosY = new[] { a0.y, d0.y }, NodePosZ = new[] { a0.z, d0.z },
                    NodeRotX = new[] { 0f, 0f }, NodeRotY = new[] { 0f, 0f }, NodeRotZ = new[] { 0f, 0f }, NodeRotW = new[] { 1f, 1f },
                    NodePrefabTypes = new[] { prefab.GetType().Name, prefab.GetType().Name },
                    NodePrefabNames = new[] { prefab.name, prefab.name },
                    NodeHasStandalone = new[] { false, false },
                    NodeHasElevation = new[] { false, false }, NodeElevX = new[] { 0f, 0f }, NodeElevY = new[] { 0f, 0f },
                    NodeSeeds = new[] { 11, 12 },

                    EdgeStartNodeIds = new[] { _batchNodeA }, EdgeEndNodeIds = new[] { _batchNodeB },
                    EdgeAX = new[] { a0.x }, EdgeAY = new[] { a0.y }, EdgeAZ = new[] { a0.z },
                    EdgeBX = new[] { b0.x }, EdgeBY = new[] { b0.y }, EdgeBZ = new[] { b0.z },
                    EdgeCX = new[] { c0.x }, EdgeCY = new[] { c0.y }, EdgeCZ = new[] { c0.z },
                    EdgeDX = new[] { d0.x }, EdgeDY = new[] { d0.y }, EdgeDZ = new[] { d0.z },
                    EdgePrefabTypes = new[] { prefab.GetType().Name },
                    EdgePrefabNames = new[] { prefab.name },
                    EdgeHasUpgraded = new[] { false },
                    EdgeUpgradedG = new[] { 0u }, EdgeUpgradedL = new[] { 0u }, EdgeUpgradedR = new[] { 0u },
                    EdgeHasElevation = new[] { false }, EdgeElevX = new[] { 0f }, EdgeElevY = new[] { 0f },
                    EdgeSeeds = new[] { 13 },
                    EdgeBuildOrderStart = new[] { 0u }, EdgeBuildOrderEnd = new[] { 15u },

                    DelStartNodeIds = new ulong[0], DelEndNodeIds = new ulong[0],
                    DelStartX = new float[0], DelStartZ = new float[0], DelEndX = new float[0], DelEndZ = new float[0],
                    BoundaryNodeIds = new ulong[0],
                });
            }
            finally { ents.Dispose(); }
        }

        /// <summary>2-sim wire test: the host applies AND ships the same fabricated batch, so both machines
        /// build the identical road from identical input — the client's [Batch] RECV/APPLIED plus convergent
        /// roads/nodes hashes prove the full network round-trip of the AtomicBatch path.</summary>
        private void SendNetBatch2()
        {
            if (_edgeQuery.IsEmptyIgnoreFilter) { L("[Auto] TEST net-batch2 SKIP no edges to clone"); return; }
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity src = ents[0];
                PrefabRef pr = EntityManager.GetComponentData<PrefabRef>(src);
                if (!_prefabSystem.TryGetPrefab(pr.m_Prefab, out PrefabBase prefab) || prefab == null)
                {
                    L("[Auto] TEST net-batch2 SKIP no prefab");
                    return;
                }

                Colossal.Mathematics.Bezier4x3 srcB = EntityManager.GetComponentData<Game.Net.Curve>(src).m_Bezier;
                var a0 = new float3(srcB.a.x + 320f, srcB.a.y, srcB.a.z + 320f);
                var d0 = new float3(srcB.a.x + 420f, srcB.a.y, srcB.a.z + 320f);
                float3 b0 = math.lerp(a0, d0, 1f / 3f);
                float3 c0 = math.lerp(a0, d0, 2f / 3f);
                ulong idA = CS2M_SyncIdSystem.Allocate();
                ulong idB = CS2M_SyncIdSystem.Allocate();

                var cmd = new NetBatchCommand
                {
                    NodeIds = new[] { idA, idB },
                    NodePosX = new[] { a0.x, d0.x }, NodePosY = new[] { a0.y, d0.y }, NodePosZ = new[] { a0.z, d0.z },
                    NodeRotX = new[] { 0f, 0f }, NodeRotY = new[] { 0f, 0f }, NodeRotZ = new[] { 0f, 0f }, NodeRotW = new[] { 1f, 1f },
                    NodePrefabTypes = new[] { prefab.GetType().Name, prefab.GetType().Name },
                    NodePrefabNames = new[] { prefab.name, prefab.name },
                    NodeHasStandalone = new[] { false, false },
                    NodeHasElevation = new[] { false, false }, NodeElevX = new[] { 0f, 0f }, NodeElevY = new[] { 0f, 0f },
                    NodeSeeds = new[] { 21, 22 },
                    EdgeStartNodeIds = new[] { idA }, EdgeEndNodeIds = new[] { idB },
                    EdgeAX = new[] { a0.x }, EdgeAY = new[] { a0.y }, EdgeAZ = new[] { a0.z },
                    EdgeBX = new[] { b0.x }, EdgeBY = new[] { b0.y }, EdgeBZ = new[] { b0.z },
                    EdgeCX = new[] { c0.x }, EdgeCY = new[] { c0.y }, EdgeCZ = new[] { c0.z },
                    EdgeDX = new[] { d0.x }, EdgeDY = new[] { d0.y }, EdgeDZ = new[] { d0.z },
                    EdgePrefabTypes = new[] { prefab.GetType().Name },
                    EdgePrefabNames = new[] { prefab.name },
                    EdgeHasUpgraded = new[] { false },
                    EdgeUpgradedG = new[] { 0u }, EdgeUpgradedL = new[] { 0u }, EdgeUpgradedR = new[] { 0u },
                    EdgeHasElevation = new[] { false }, EdgeElevX = new[] { 0f }, EdgeElevY = new[] { 0f },
                    EdgeSeeds = new[] { 23 },
                    EdgeBuildOrderStart = new[] { 0u }, EdgeBuildOrderEnd = new[] { 15u },
                    DelStartNodeIds = new ulong[0], DelEndNodeIds = new ulong[0],
                    DelStartX = new float[0], DelStartZ = new float[0], DelEndX = new float[0], DelEndZ = new float[0],
                    BoundaryNodeIds = new ulong[0],
                    BoundaryPosX = new float[0], BoundaryPosY = new float[0], BoundaryPosZ = new float[0],
                };

                RemoteNetBatchQueue.Enqueue(cmd);      // host builds it locally
                Command.SendToAll?.Invoke(cmd);        // client builds the SAME road over the wire
                L($"[Auto] TEST net-batch2 SEND ids={idA}/{idB} prefab={prefab.name}");
            }
            finally { ents.Dispose(); }
        }

        /// <summary>The strong assertions for the direct-archetype recipe: the node ids resolve, the game's
        /// ReferencesSystem WIRED the edge into ConnectedEdge (derivation ran), and CompositionSelectSystem
        /// RESOLVED Composition to a real NetComposition entity (carries NetCompositionData) — i.e. it no
        /// longer points at the raw prefab we seeded. That was the implementation's #1 open risk.</summary>
        private void VerifyNetBatch()
        {
            if (_edgesBeforeBatch < 0) { return; }
            int edges = _allEdgesQuery.CalculateEntityCount();
            bool countOk = edges > _edgesBeforeBatch;

            bool wired = false;
            bool compResolved = false;
            int subBlocks = -1;
            if (CS2M_NodeSyncIds.TryResolve(EntityManager, _batchNodeA, out Entity na)
                && EntityManager.HasBuffer<Game.Net.ConnectedEdge>(na))
            {
                DynamicBuffer<Game.Net.ConnectedEdge> ce = EntityManager.GetBuffer<Game.Net.ConnectedEdge>(na, true);
                for (int i = 0; i < ce.Length; i++)
                {
                    Entity e = ce[i].m_Edge;
                    if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e)) { continue; }

                    wired = true;
                    if (EntityManager.HasComponent<Game.Net.Composition>(e))
                    {
                        Game.Net.Composition comp = EntityManager.GetComponentData<Game.Net.Composition>(e);
                        compResolved = comp.m_Edge != Entity.Null
                                       && EntityManager.HasComponent<Game.Prefabs.NetCompositionData>(comp.m_Edge);
                    }

                    if (EntityManager.HasBuffer<Game.Zones.SubBlock>(e))
                    {
                        subBlocks = EntityManager.GetBuffer<Game.Zones.SubBlock>(e, true).Length;
                    }

                    break;
                }
            }

            Result("net-batch", countOk && wired && compResolved,
                $"edges {_edgesBeforeBatch}->{edges} wired={wired} compResolved={compResolved} subBlocks={subBlocks}");
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
            _frameIndexAtPause = _sim.frameIndex;
            L($"[Auto] TEST pause INJECT joining=true (speedBefore={_speedBeforePause} fi={_frameIndexAtPause})");
            RemoteJoinState.Update("TestJoiner", true);
        }

        // Non-circular: frameIndex only advances when the GameSimulation phase actually runs, so a
        // frozen frameIndex is the real-world effect of the pause (reading selectedSpeed back would
        // just echo the value we wrote — the old test passed while the live game kept running).
        private void VerifyPauseFrozen()
        {
            uint fi = _sim.frameIndex;
            Result("pause", fi - _frameIndexAtPause <= 2,
                $"frameIndex {_frameIndexAtPause}->{fi} (sim must freeze while joining)");
        }

        // Emulates exactly what the vanilla TimeUISystem does on SPACE / speed keys: an adversarial
        // one-shot selectedSpeed write. The per-frame enforcement in JoinPauseSystem must win.
        private void SabotagePause()
        {
            _sim.selectedSpeed = 1f;
            _frameIndexAtSabotage = _sim.frameIndex;
            L("[Auto] TEST pause SABOTAGE selectedSpeed=1 (emulating the pause-button/SPACE rewrite)");
        }

        private void VerifyPauseEnforced()
        {
            uint fi = _sim.frameIndex;
            Result("pause-enforce", fi - _frameIndexAtSabotage <= 2,
                $"frameIndex {_frameIndexAtSabotage}->{fi} (must stay frozen despite adversarial speed write)");
        }

        private void ActResume()
        {
            L("[Auto] TEST pause INJECT joining=false (resume)");
            RemoteJoinState.Update("TestJoiner", false);
            // Re-arm the force-run: in a headless/unfocused window the game auto-pauses, which made
            // the old resume check a guaranteed false FAIL. frameIndex advancing is the real signal.
            // (Never force-run during a live "/validate" — the player owns the sim speed there.)
            _forceRun = !_chatValidation;
            _frameIndexAtResume = _sim.frameIndex;
        }

        private void VerifyResume()
        {
            uint fi = _sim.frameIndex;
            Result("resume", fi > _frameIndexAtResume,
                $"frameIndex {_frameIndexAtResume}->{fi} (sim must tick again after resume)");
        }

        // ------- v44 steps: dev tree, environment/clock, native delete, map tiles -------

        private Entity _devTreeNode;
        private Entity _nativeTree;
        private Entity _lockedTile;
        private uint _envElapsedTarget;
        private uint _envFrameIndexAtInject;

        private void ActDevTree()
        {
            _devTreeNode = Entity.Null;
            EntityQuery nodes = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.DevTreeNodeData>());
            NativeArray<Entity> ents = nodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in ents)
                {
                    if (EntityManager.HasComponent<Game.Prefabs.Locked>(n)
                        && EntityManager.IsComponentEnabled<Game.Prefabs.Locked>(n))
                    {
                        _devTreeNode = n;
                        break;
                    }
                }
            }
            finally { ents.Dispose(); }

            if (_devTreeNode == Entity.Null)
            {
                // Environment, not sync: this save simply has the whole tree unlocked already.
                Result("devtree", true, "no locked node left to test (all unlocked in this save)");
                return;
            }
            if (!_prefabSystem.TryGetPrefab(_devTreeNode, out PrefabBase p) || p == null)
            {
                Result("devtree", false, "no prefab for node");
                _devTreeNode = Entity.Null;
                return;
            }

            L($"[Auto] TEST devtree INJECT node={p.name}");
            RemoteDevTreeQueue.Enqueue(new CS2M.Commands.Data.Game.DevTreeCommand { NodeName = p.name });
        }

        private void VerifyDevTree()
        {
            if (_devTreeNode == Entity.Null) { return; }
            bool unlocked = !EntityManager.IsComponentEnabled<Game.Prefabs.Locked>(_devTreeNode);
            Result("devtree", unlocked, unlocked ? "node Locked disabled (UnlockSystem consumed our event)" : "node still locked");
        }

        private void ActEnvAndNativeDelete()
        {
            // env: force a distinct temperature + shift the clock 5000 frames ahead of local.
            EntityQuery tdq = GetEntityQuery(ComponentType.ReadOnly<Game.Common.TimeData>());
            uint elapsed = 0;
            if (!tdq.IsEmptyIgnoreFilter)
            {
                elapsed = _sim.frameIndex - tdq.GetSingleton<Game.Common.TimeData>().m_FirstFrame;
            }

            _envElapsedTarget = elapsed + 5000;
            _envFrameIndexAtInject = _sim.frameIndex;
            RemoteEnvQueue.Set(new CS2M.Commands.Data.Game.EnvSyncCommand
            {
                Temperature = 7.75f, Precipitation = 0.66f, Cloudiness = 0.55f,
                ElapsedTimeFrames = _envElapsedTarget,
            });

            // native delete: pick a tree WITHOUT a sync id and delete it by prefab+position.
            _nativeTree = Entity.Null;
            NativeArray<Entity> trees = _treeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity t in trees)
                {
                    if (!EntityManager.HasComponent<CS2M_SyncId>(t))
                    {
                        _nativeTree = t;
                        break;
                    }
                }
            }
            finally { trees.Dispose(); }

            if (_nativeTree == Entity.Null)
            {
                L("[Auto] TEST env INJECT (no native tree available for native-delete)");
                return;
            }

            _prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(_nativeTree).m_Prefab,
                out PrefabBase prefab);
            var pos = EntityManager.GetComponentData<Game.Objects.Transform>(_nativeTree).m_Position;
            L($"[Auto] TEST env+nativeDelete INJECT tree={prefab?.name} pos=({pos.x:F0},{pos.z:F0})");
            RemoteEditQueue.EnqueueDelete(new DeleteCommand
            {
                SyncId = 0,
                PrefabType = prefab != null ? prefab.GetType().Name : "StaticObjectPrefab",
                PrefabName = prefab != null ? prefab.name : "",
                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
            });
        }

        private void VerifyEnvAndNativeDelete()
        {
            var climate = World.GetOrCreateSystemManaged<Game.Simulation.ClimateSystem>();
            bool weather = climate.temperature.overrideState
                           && System.Math.Abs(climate.temperature.overrideValue - 7.75f) < 0.01f;

            EntityQuery tdq = GetEntityQuery(ComponentType.ReadOnly<Game.Common.TimeData>());
            bool clock = false;
            if (!tdq.IsEmptyIgnoreFilter)
            {
                // Anchor on frameIndex: the sim keeps ticking between apply and verify, so compare
                // (elapsed - framesSinceInject) against the fixed target instead of raw elapsed —
                // this is timing-independent (the old raw check was flaky at high sim speed).
                uint elapsed = _sim.frameIndex - tdq.GetSingleton<Game.Common.TimeData>().m_FirstFrame;
                long anchored = (long)elapsed - (long)(_sim.frameIndex - _envFrameIndexAtInject);
                long drift = anchored - _envElapsedTarget;
                clock = drift > -120 && drift < 120;
            }

            Result("env", weather && clock, $"weatherOverride={weather} clockRealigned={clock}");

            if (_nativeTree != Entity.Null)
            {
                bool gone = !EntityManager.Exists(_nativeTree) || EntityManager.HasComponent<Deleted>(_nativeTree);
                Result("native-delete", gone, gone ? "native tree removed by prefab+pos" : "native tree still present");
            }

            // release the override so the rest of the run isn't affected
            climate.temperature.overrideState = false;
            climate.precipitation.overrideState = false;
            climate.cloudiness.overrideState = false;
        }

        private void ActTile()
        {
            _lockedTile = Entity.Null;
            EntityQuery locked = GetEntityQuery(
                ComponentType.ReadOnly<Game.Areas.MapTile>(),
                ComponentType.ReadOnly<Game.Common.Native>(),
                ComponentType.ReadOnly<Game.Areas.Geometry>());
            if (locked.IsEmptyIgnoreFilter)
            {
                Result("tile", true, "guard OK: no locked tiles on this map (all owned)");
                return;
            }

            NativeArray<Entity> tiles = locked.ToEntityArray(Allocator.Temp);
            try { _lockedTile = tiles[0]; }
            finally { tiles.Dispose(); }

            var center = EntityManager.GetComponentData<Game.Areas.Geometry>(_lockedTile).m_CenterPosition;
            L($"[Auto] TEST tile INJECT center=({center.x:F0},{center.z:F0})");
            TileSync.Enqueue(new CS2M.Commands.Data.Game.TilePurchaseCommand
            {
                Xs = new[] { center.x }, Zs = new[] { center.z },
            });
        }

        private void VerifyTile()
        {
            if (_lockedTile == Entity.Null) { return; }
            bool owned = !EntityManager.HasComponent<Game.Common.Native>(_lockedTile);
            Result("tile", owned, owned ? "tile unlocked (Native removed)" : "tile still locked");
        }

        // ------- v49: transport line -------

        private ulong _routeSyncId;

        private void ActRoute()
        {
            _routeSyncId = 0;
            Entity linePrefab = Entity.Null;
            string prefabTypeName = null, prefabName = null;
            EntityQuery prefabs = GetEntityQuery(
                ComponentType.ReadOnly<Game.Prefabs.RouteData>(),
                ComponentType.ReadOnly<Game.Prefabs.TransportLineData>());
            NativeArray<Entity> ents = prefabs.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity p in ents)
                {
                    if (_prefabSystem.TryGetPrefab(p, out PrefabBase pb) && pb != null
                        && pb.name.Contains("Bus"))
                    {
                        linePrefab = p;
                        prefabTypeName = pb.GetType().Name;
                        prefabName = pb.name;
                        break;
                    }
                }

                if (linePrefab == Entity.Null && ents.Length > 0
                    && _prefabSystem.TryGetPrefab(ents[0], out PrefabBase first) && first != null)
                {
                    linePrefab = ents[0];
                    prefabTypeName = first.GetType().Name;
                    prefabName = first.name;
                }
            }
            finally { ents.Dispose(); }

            if (linePrefab == Entity.Null)
            {
                Result("route", false, "no transport line prefab found");
                return;
            }

            if (!TryAnchor(out float3 a))
            {
                Result("route", false, "no anchor");
                return;
            }

            _routeSyncId = CS2M_SyncIdSystem.Allocate();
            L($"[Auto] TEST route INJECT prefab={prefabName} id={_routeSyncId}");
            RouteSync.EnqueueCreate(new CS2M.Commands.Data.Game.RouteCreateCommand
            {
                SyncId = _routeSyncId,
                PrefabType = prefabTypeName,
                PrefabName = prefabName,
                Complete = false,
                ColorR = 255, ColorG = 64, ColorB = 32, ColorA = 255,
                Number = 77,
                WpX = new[] { a.x + 10f, a.x + 90f, a.x + 170f },
                WpY = new[] { a.y, a.y, a.y },
                WpZ = new[] { a.z + 10f, a.z + 40f, a.z + 10f },
                WpHasConn = new byte[3],
                WpConnId = new ulong[3],
                WpConnX = new float[3],
                WpConnZ = new float[3],
            });
        }

        private void VerifyRoute()
        {
            if (_routeSyncId == 0) { return; }
            if (!CS2M_SyncIdSystem.Map.TryGetValue(_routeSyncId, out Entity route)
                || !EntityManager.Exists(route) || !EntityManager.HasComponent<Game.Routes.Route>(route))
            {
                Result("route", false, "route entity not created");
                return;
            }

            int wps = EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(route)
                ? EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(route, true).Length
                : -1;
            int segs = EntityManager.HasBuffer<Game.Routes.RouteSegment>(route)
                ? EntityManager.GetBuffer<Game.Routes.RouteSegment>(route, true).Length
                : -1;
            int number = EntityManager.HasComponent<Game.Routes.RouteNumber>(route)
                ? EntityManager.GetComponentData<Game.Routes.RouteNumber>(route).m_Number
                : -1;

            bool ok = wps == 3 && segs == 2 && number == 77;
            Result("route", ok, $"wps={wps} segs={segs} number={number} (line built from archetypes, game systems wired it)");
        }

        // ------- v50 step 24: road-side stop object attaches to the nearest edge -------

        private void ActStop()
        {
            // First transport-stop prefab that is a placeable object (Bus Stop etc.).
            EntityQuery stopPrefabs = GetEntityQuery(
                ComponentType.ReadOnly<Game.Prefabs.TransportStopData>(),
                ComponentType.ReadOnly<Game.Prefabs.ObjectData>());
            EntityQuery edges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Edge>(),
                    ComponentType.ReadOnly<Game.Net.Curve>(),
                    ComponentType.ReadOnly<Game.Net.Road>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            if (stopPrefabs.IsEmptyIgnoreFilter || edges.IsEmptyIgnoreFilter)
            {
                Result("stop-attach", false, "no stop prefab or no road edge in city");
                return;
            }

            NativeArray<Entity> prefabEnts = stopPrefabs.ToEntityArray(Allocator.Temp);
            NativeArray<Entity> edgeEnts = edges.ToEntityArray(Allocator.Temp);
            try
            {
                Entity prefabEntity = prefabEnts[0];
                if (!_prefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefab) || prefab == null)
                {
                    Result("stop-attach", false, "stop prefab unresolvable");
                    return;
                }

                Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(edgeEnts[0]);
                float3 onCurve = Colossal.Mathematics.MathUtils.Position(curve.m_Bezier, 0.5f);
                float3 pos = onCurve + new float3(4f, 0f, 0f); // curb-ish offset

                var cmd = new ObjectPlaceCommand
                {
                    SyncId = CS2M_SyncIdSystem.Allocate(),
                    PrefabType = prefab.GetType().Name,
                    PrefabName = prefab.name,
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    RotW = 1f,
                    OwnerX = onCurve.x, OwnerY = onCurve.y, OwnerZ = onCurve.z, // attach hint
                };
                _stopSyncId = cmd.SyncId;
                L($"[Auto] TEST stop INJECT name={cmd.PrefabName} pos=({pos.x:F0},{pos.z:F0}) hint=({onCurve.x:F0},{onCurve.z:F0}) syncId={cmd.SyncId}");
                RemotePlacementQueue.EnqueueObject(cmd);
            }
            finally
            {
                prefabEnts.Dispose();
                edgeEnts.Dispose();
            }
        }

        private void VerifyStop()
        {
            if (_stopSyncId == 0) { return; }
            if (!CS2M_SyncIdSystem.Map.TryGetValue(_stopSyncId, out Entity stop) || !EntityManager.Exists(stop))
            {
                Result("stop-attach", false, "stop entity not created");
                return;
            }

            bool hasAttached = EntityManager.HasComponent<Game.Objects.Attached>(stop);
            Entity parent = hasAttached
                ? EntityManager.GetComponentData<Game.Objects.Attached>(stop).m_Parent
                : Entity.Null;
            bool parentIsEdge = parent != Entity.Null && EntityManager.HasComponent<Game.Net.Edge>(parent);
            Result("stop-attach", parentIsEdge,
                $"attached={hasAttached} parentEdge={parentIsEdge} (stop object wired to the road edge)");
        }

        // ------- v50 steps 25-26: host-authoritative fire mirrored via FireSyncCommand -------

        private void ActFireStart()
        {
            // A NATIVE building from the save — the test-placed one sits on unzoned ground and the
            // sim condemns+demolishes it before this step runs (learned the hard way).
            EntityQuery buildings = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Destroyed>(),
                    ComponentType.ReadOnly<Game.Events.OnFire>(),
                },
            });

            _fireTarget = Entity.Null;
            _fireSyncId = 0;
            NativeArray<Entity> ents = buildings.ToEntityArray(Allocator.Temp);
            try
            {
                if (ents.Length == 0)
                {
                    Result("fire-start", false, "no native building alive to ignite");
                    return;
                }

                _fireTarget = ents[0];
            }
            finally { ents.Dispose(); }

            // Give it a sync id on the spot so the command resolves by id (the same first-touch
            // pattern native move/delete uses).
            _fireSyncId = CS2M_SyncIdSystem.Allocate();
            CS2M_SyncIdSystem.Register(EntityManager, _fireTarget, _fireSyncId);

            L($"[Auto] TEST fire INJECT start entity={_fireTarget.Index} syncId={_fireSyncId}");
            FireSync.Enqueue(new CS2M.Commands.Data.Game.FireSyncCommand
            {
                Kind = 0,
                TargetSyncId = _fireSyncId,
                Intensity = 5f,
            });
        }

        private void VerifyFireStart()
        {
            if (_fireTarget == Entity.Null) { return; }
            bool onFire = EntityManager.Exists(_fireTarget)
                          && EntityManager.HasComponent<Game.Events.OnFire>(_fireTarget);
            Result("fire-start", onFire, onFire ? "building has OnFire (host event mirrored)" : "OnFire missing");
        }

        private void ActFireCollapse()
        {
            if (_fireTarget == Entity.Null) { return; }
            L($"[Auto] TEST fire INJECT collapse syncId={_fireSyncId}");
            FireSync.Enqueue(new CS2M.Commands.Data.Game.FireSyncCommand
            {
                Kind = 2,
                TargetSyncId = _fireSyncId,
            });
        }

        private void VerifyFireCollapse()
        {
            if (_fireTarget == Entity.Null) { return; }
            bool destroyed = EntityManager.Exists(_fireTarget)
                             && EntityManager.HasComponent<Destroyed>(_fireTarget);
            bool fireGone = !EntityManager.Exists(_fireTarget)
                            || !EntityManager.HasComponent<Game.Events.OnFire>(_fireTarget);
            Result("fire-collapse", destroyed && fireGone,
                $"destroyed={destroyed} fireCleared={fireGone} (Destroy event → vanilla DestroySystem teardown)");
        }

        // ------- v51 step 27: T-junction — a synced road ending MID-SPAN on another must split it -------

        private float3 _teeJunction;

        private void ActTee()
        {
            _teeJunction = default;
            if (_edgeQuery.IsEmptyIgnoreFilter || !TryAnchor(out float3 anchor))
            {
                Result("net-tee", false, "no road/anchor available");
                return;
            }

            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            string prefabType, prefabName;
            try
            {
                Entity src = ents[0];
                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(src).m_Prefab,
                        out PrefabBase prefab) || prefab == null)
                {
                    Result("net-tee", false, "no road prefab");
                    return;
                }

                prefabType = prefab.GetType().Name;
                prefabName = prefab.name;
            }
            finally { ents.Dispose(); }

            // Road A: straight 80 m bar away from the other test spots; road B ends dead-center on it.
            var a0 = new float3(anchor.x + 260f, anchor.y, anchor.z + 260f);
            var a1 = new float3(anchor.x + 340f, anchor.y, anchor.z + 260f);
            _teeJunction = new float3((a0.x + a1.x) * 0.5f, anchor.y, a0.z);
            var b0 = new float3(_teeJunction.x, anchor.y, anchor.z + 320f);

            L($"[Auto] TEST net-tee INJECT name={prefabName} junction=({_teeJunction.x:F0},{_teeJunction.z:F0})");
            RemoteNetQueue.Enqueue(StraightNet(prefabType, prefabName, a0, a1));
            RemoteNetQueue.Enqueue(StraightNet(prefabType, prefabName, b0, _teeJunction));
        }

        private static CS2M.Commands.Data.Game.NetPlaceCommand StraightNet(string type, string name,
            float3 s, float3 e)
        {
            float3 b = math.lerp(s, e, 1f / 3f);
            float3 c = math.lerp(s, e, 2f / 3f);
            return new CS2M.Commands.Data.Game.NetPlaceCommand
            {
                SyncId = CS2M_SyncIdSystem.Allocate(),
                PrefabType = type, PrefabName = name,
                Ax = s.x, Ay = s.y, Az = s.z,
                Bx = b.x, By = b.y, Bz = b.z,
                Cx = c.x, Cy = c.y, Cz = c.z,
                Dx = e.x, Dy = e.y, Dz = e.z,
                RandomSeed = 0,
            };
        }

        private void VerifyTee()
        {
            if (_teeJunction.Equals(default(float3))) { return; }

            // The junction must hold ONE node with >= 3 live edges (A split in two + B).
            EntityQuery nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Node>(),
                    ComponentType.ReadOnly<Game.Net.ConnectedEdge>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            int bestEdges = 0;
            int nodesNear = 0;
            NativeArray<Entity> ents = nodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in ents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Net.Node>(n).m_Position;
                    float dx = p.x - _teeJunction.x, dz = p.z - _teeJunction.z;
                    if (dx * dx + dz * dz > 4f) { continue; }

                    nodesNear++;
                    int live = 0;
                    DynamicBuffer<Game.Net.ConnectedEdge> ce =
                        EntityManager.GetBuffer<Game.Net.ConnectedEdge>(n, true);
                    for (int i = 0; i < ce.Length; i++)
                    {
                        if (EntityManager.Exists(ce[i].m_Edge)
                            && !EntityManager.HasComponent<Deleted>(ce[i].m_Edge))
                        {
                            live++;
                        }
                    }

                    bestEdges = math.max(bestEdges, live);
                }
            }
            finally { ents.Dispose(); }

            bool ok = nodesNear == 1 && bestEdges >= 3;
            Result("net-tee", ok,
                $"nodesAtJunction={nodesNear} connectedEdges={bestEdges} (need exactly 1 node with >=3 — split+fusion wired)");
        }

        // ------- v55 step 28: X-crossing via the AUTHORITATIVE (HasNodes) path, DRIFTING centre -------
        // The real junction desync lives ONLY on the HasNodes path (host detection reads Edge->Node and
        // ships authoritative coords). Every earlier net test injected HasNodes=FALSE commands, so the
        // legacy guess path was all the harness ever exercised — the bot literally could not reach the
        // bug Bruno saw. Here four arms meet at one centre, but each carries a slightly DIFFERENT centre
        // coord — exactly what the host emits because its junction node re-centres as roads join. Before
        // the fix the 0.5 m share missed the moved sibling and forged a duplicate node per arm
        // (stacked sidewalks). The fix (FindJunctionNode, 3.5 m) must collapse them onto ONE shared node.
        private float3 _xCross;

        private void ActXCrossDrift()
        {
            _xCross = default;
            if (_edgeQuery.IsEmptyIgnoreFilter || !TryAnchor(out float3 anchor))
            {
                Result("net-xcross", false, "no road/anchor available");
                return;
            }

            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            string type, name;
            try
            {
                Entity src = ents[0];
                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(src).m_Prefab,
                        out PrefabBase prefab) || prefab == null)
                {
                    Result("net-xcross", false, "no road prefab");
                    return;
                }

                type = prefab.GetType().Name;
                name = prefab.name;
            }
            finally { ents.Dispose(); }

            // Clear quadrant, away from every other test spot (tee is at +260..340).
            var c = new float3(anchor.x - 280f, anchor.y, anchor.z + 280f);
            _xCross = c;

            // Four arm tips 50 m out (N, S, E, W); each is its own dead-end node (distinct, no sharing).
            var tips = new[]
            {
                new float3(c.x, c.y, c.z + 50f),
                new float3(c.x, c.y, c.z - 50f),
                new float3(c.x + 50f, c.y, c.z),
                new float3(c.x - 50f, c.y, c.z),
            };

            // Per-arm centre-node jitter = the junction-move the host emits as it re-centres. >0.5 m so
            // the old bit-exact/0.5 m share FAILS (forges duplicates); <3.5 m so the fix collapses them
            // onto ONE shared node (the whole point of FindJunctionNode).
            var jitter = new[]
            {
                new float3(0f, 0f, 0f),
                new float3(0.9f, 0f, 0.6f),
                new float3(-0.7f, 0f, 1.0f),
                new float3(1.1f, 0f, -0.8f),
            };

            L($"[Auto] TEST net-xcross INJECT centre=({c.x:F0},{c.z:F0}) 4 arms w/ drifting centre node (HasNodes path)");
            for (int i = 0; i < 4; i++)
            {
                RemoteNetQueue.Enqueue(AuthNet(type, name, tips[i], c + jitter[i]));
            }
        }

        /// <summary>A straight road on the AUTHORITATIVE path: HasNodes=true. On the client's Permanent
        /// path the node lands at the curve endpoint, so the node coords double as the curve endpoints
        /// (matching what the receiver does — snap the curve endpoint to the host node coord).</summary>
        private static CS2M.Commands.Data.Game.NetPlaceCommand AuthNet(string type, string name,
            float3 startNode, float3 endNode)
        {
            float3 s = startNode, e = endNode;
            float3 b = math.lerp(s, e, 1f / 3f);
            float3 cc = math.lerp(s, e, 2f / 3f);
            return new CS2M.Commands.Data.Game.NetPlaceCommand
            {
                SyncId = CS2M_SyncIdSystem.Allocate(),
                PrefabType = type, PrefabName = name,
                Ax = s.x, Ay = s.y, Az = s.z,
                Bx = b.x, By = b.y, Bz = b.z,
                Cx = cc.x, Cy = cc.y, Cz = cc.z,
                Dx = e.x, Dy = e.y, Dz = e.z,
                HasNodes = true,
                StartNodeX = s.x, StartNodeY = s.y, StartNodeZ = s.z,
                EndNodeX = e.x, EndNodeY = e.y, EndNodeZ = e.z,
                RandomSeed = 0,
            };
        }

        private void VerifyXCrossDrift()
        {
            if (_xCross.Equals(default(float3))) { return; }

            EntityQuery nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Node>(),
                    ComponentType.ReadOnly<Game.Net.ConnectedEdge>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            int nodesNear = 0;
            int bestEdges = 0;
            float3 theNode = default;
            NativeArray<Entity> ents = nodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in ents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Net.Node>(n).m_Position;
                    float dx = p.x - _xCross.x, dz = p.z - _xCross.z;
                    if (dx * dx + dz * dz > 25f) { continue; } // 5 m around the centre

                    nodesNear++;
                    int live = 0;
                    DynamicBuffer<Game.Net.ConnectedEdge> ce =
                        EntityManager.GetBuffer<Game.Net.ConnectedEdge>(n, true);
                    for (int i = 0; i < ce.Length; i++)
                    {
                        if (EntityManager.Exists(ce[i].m_Edge)
                            && !EntityManager.HasComponent<Deleted>(ce[i].m_Edge))
                        {
                            live++;
                        }
                    }

                    bestEdges = math.max(bestEdges, live);
                    theNode = p;
                }
            }
            finally { ents.Dispose(); }

            // 1 node (drifting arms fused, not duplicated) with all 4 arms wired, sitting at the junction
            // centre (within the jitter). Position hash-convergence to the host is a cross-machine concern
            // measured by the StateHash radar with 2 sims — not observable in a single instance.
            bool ok = nodesNear == 1 && bestEdges >= 4
                      && math.distance(theNode.xz, _xCross.xz) < 3f;
            Result("net-xcross", ok,
                $"nodesAtCentre={nodesNear} connectedEdges={bestEdges} nodeAt=({theNode.x:F1},{theNode.z:F1}) " +
                $"centre=({_xCross.x:F1},{_xCross.z:F1}) " +
                "(need exactly 1 node w/ >=4 near centre — drifting-centre junction fused, not duplicated)");
        }

        // ------- v55 step 29: delete ONE arm of the X-crossing -> cascade must rebuild the junction -------
        // Bruno: "apagar rua fica bugada" — a road delete left flat cut ends + orphan nodes on the client
        // because ApplyDelete only tagged the edge Deleted (no neighbour rebuild / orphan cleanup). Here
        // we delete the north arm of the fused X and require: the centre stays ONE node at degree 3 (the
        // junction re-capped, not stacked) AND the arm's outer dead-end node is gone (orphan cleaned).
        private void ActArmDelete()
        {
            if (_xCross.Equals(default(float3))) { return; }
            float3 tip0 = new float3(_xCross.x, _xCross.y, _xCross.z + 50f); // north arm tip
            L($"[Auto] TEST net-arm-delete INJECT delete north arm ({tip0.x:F0},{tip0.z:F0})->centre");
            RemoteNetDeleteQueue.Enqueue(new CS2M.Commands.Data.Game.NetDeleteCommand
            {
                StartX = tip0.x, StartY = tip0.y, StartZ = tip0.z,
                EndX = _xCross.x, EndY = _xCross.y, EndZ = _xCross.z,
            });
        }

        private void VerifyArmDelete()
        {
            if (_xCross.Equals(default(float3))) { return; }
            float3 tip0 = new float3(_xCross.x, _xCross.y, _xCross.z + 50f);

            EntityQuery nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Node>(),
                    ComponentType.ReadOnly<Game.Net.ConnectedEdge>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            int centreNodes = 0, centreDegree = 0, tipNodes = 0;
            NativeArray<Entity> ents = nodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in ents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Net.Node>(n).m_Position;
                    float dc = math.distance(p.xz, _xCross.xz);
                    float dt = math.distance(p.xz, tip0.xz);
                    if (dc < 5f)
                    {
                        centreNodes++;
                        int live = 0;
                        DynamicBuffer<Game.Net.ConnectedEdge> ce =
                            EntityManager.GetBuffer<Game.Net.ConnectedEdge>(n, true);
                        for (int i = 0; i < ce.Length; i++)
                        {
                            if (EntityManager.Exists(ce[i].m_Edge)
                                && !EntityManager.HasComponent<Deleted>(ce[i].m_Edge)) { live++; }
                        }
                        centreDegree = math.max(centreDegree, live);
                    }
                    else if (dt < 5f) { tipNodes++; }
                }
            }
            finally { ents.Dispose(); }

            bool ok = centreNodes == 1 && centreDegree == 3 && tipNodes == 0;
            Result("net-arm-delete", ok,
                $"centreNodes={centreNodes} centreDegree={centreDegree} orphanTipNodes={tipNodes} " +
                "(delete must re-cap the junction to degree 3 and clean the orphan dead-end)");
        }

        // ------- v55 step 30: split-original delete via FindCoveringEdge (the X-crossing mechanism) -------
        // On a real crossing the host SPLITS road A into A1+A2 and ships the pieces (HasNodes) + original A.
        // The receiver must build A1+A2 sharing the mid junction AND delete the un-split A (FindCoveringEdge,
        // since the position-addressed delete can miss). This is what closed Bruno's "roads stacked" overlap.
        // Inject A (full) then A1, A2 and require: A GONE (no full-length edge), A1+A2 meet at ONE mid node.
        private float3 _splitM, _splitP0, _splitP2;

        private void ActSplitFlow()
        {
            _splitM = default;
            if (_edgeQuery.IsEmptyIgnoreFilter || !TryAnchor(out float3 anchor))
            {
                Result("net-splitflow", false, "no road/anchor available");
                return;
            }

            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            string type, name;
            try
            {
                Entity src = ents[0];
                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(src).m_Prefab,
                        out PrefabBase prefab) || prefab == null)
                {
                    Result("net-splitflow", false, "no road prefab");
                    return;
                }

                type = prefab.GetType().Name;
                name = prefab.name;
            }
            finally { ents.Dispose(); }

            var M = new float3(anchor.x + 300f, anchor.y, anchor.z - 300f); // clear quadrant
            _splitM = M;
            _splitP0 = new float3(M.x - 70f, M.y, M.z);
            _splitP2 = new float3(M.x + 70f, M.y, M.z);

            L($"[Auto] TEST net-splitflow INJECT full A + halves A1/A2 at M=({M.x:F0},{M.z:F0}) (FindCoveringEdge must delete A)");
            RemoteNetQueue.Enqueue(AuthNet(type, name, _splitP0, _splitP2)); // the un-split original A
            RemoteNetQueue.Enqueue(AuthNet(type, name, _splitP0, M));        // A1 (A covers it -> A deleted)
            RemoteNetQueue.Enqueue(AuthNet(type, name, M, _splitP2));        // A2
        }

        private void VerifySplitFlow()
        {
            if (_splitM.Equals(default(float3))) { return; }

            EntityQuery nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.ReadOnly<Game.Net.ConnectedEdge>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            int midNodes = 0, midDegree = 0;
            NativeArray<Entity> nents = nodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in nents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Net.Node>(n).m_Position;
                    if (math.distance(p.xz, _splitM.xz) > 5f) { continue; }
                    midNodes++;
                    int live = 0;
                    DynamicBuffer<Game.Net.ConnectedEdge> ce = EntityManager.GetBuffer<Game.Net.ConnectedEdge>(n, true);
                    for (int i = 0; i < ce.Length; i++)
                    {
                        if (EntityManager.Exists(ce[i].m_Edge) && !EntityManager.HasComponent<Deleted>(ce[i].m_Edge)) { live++; }
                    }
                    midDegree = math.max(midDegree, live);
                }
            }
            finally { nents.Dispose(); }

            bool fullSurvives = false;
            NativeArray<Entity> ee = _allEdgesQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ee)
                {
                    if (!EntityManager.HasComponent<Game.Net.Curve>(e)) { continue; }
                    Colossal.Mathematics.Bezier4x3 bz = EntityManager.GetComponentData<Game.Net.Curve>(e).m_Bezier;
                    bool fwd = NearXZ(bz.a, _splitP0) && NearXZ(bz.d, _splitP2);
                    bool rev = NearXZ(bz.a, _splitP2) && NearXZ(bz.d, _splitP0);
                    if (fwd || rev) { fullSurvives = true; break; }
                }
            }
            finally { ee.Dispose(); }

            bool ok = midNodes == 1 && midDegree == 2 && !fullSurvives;
            Result("net-splitflow", ok,
                $"midNodes={midNodes} midDegree={midDegree} originalA_survives={fullSurvives} " +
                "(A1+A2 fuse at 1 mid node deg 2, and FindCoveringEdge deleted the un-split A)");
        }

        // ------- v55 step 31: MOVED junction — a 3rd road joins an existing junction whose coord shifted
        // 5 m (>3.5 m). Reproduces the "+nodes on busy incremental junctions" drift Bruno saw: without the
        // 8 m degree>=2 fallback in FindJunctionNode the 3rd road forges a DUPLICATE node.
        private float3 _movedJ;

        private void ActMovedJunction()
        {
            _movedJ = default;
            if (_edgeQuery.IsEmptyIgnoreFilter || !TryAnchor(out float3 anchor))
            {
                Result("net-moved-junction", false, "no road/anchor available");
                return;
            }

            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            string type, name;
            try
            {
                Entity src = ents[0];
                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(src).m_Prefab,
                        out PrefabBase prefab) || prefab == null)
                {
                    Result("net-moved-junction", false, "no road prefab");
                    return;
                }

                type = prefab.GetType().Name;
                name = prefab.name;
            }
            finally { ents.Dispose(); }

            var M = new float3(anchor.x - 400f, anchor.y, anchor.z - 400f);
            _movedJ = M;
            // A + B build a junction at M (degree 2).
            RemoteNetQueue.Enqueue(AuthNet(type, name, new float3(M.x, M.y, M.z + 50f), M)); // north arm -> M
            RemoteNetQueue.Enqueue(AuthNet(type, name, new float3(M.x + 50f, M.y, M.z), M)); // east arm  -> M
            // C joins the junction, but the host's coord for it landed 5 m away (the junction re-centred).
            var Mp = new float3(M.x + 5f, M.y, M.z);
            RemoteNetQueue.Enqueue(AuthNet(type, name, new float3(M.x, M.y, M.z - 50f), Mp)); // south arm -> M' (moved)
            L($"[Auto] TEST net-moved-junction INJECT junction M=({M.x:F0},{M.z:F0}), 3rd road to M+5m");
        }

        private void VerifyMovedJunction()
        {
            if (_movedJ.Equals(default(float3))) { return; }

            EntityQuery nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.ReadOnly<Game.Net.ConnectedEdge>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            int nodesNear = 0, bestDeg = 0;
            NativeArray<Entity> ents = nodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in ents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Net.Node>(n).m_Position;
                    if (math.distance(p.xz, _movedJ.xz) > 8f) { continue; }
                    nodesNear++;
                    int live = 0;
                    DynamicBuffer<Game.Net.ConnectedEdge> ce = EntityManager.GetBuffer<Game.Net.ConnectedEdge>(n, true);
                    for (int i = 0; i < ce.Length; i++)
                    {
                        if (EntityManager.Exists(ce[i].m_Edge) && !EntityManager.HasComponent<Deleted>(ce[i].m_Edge)) { live++; }
                    }
                    bestDeg = math.max(bestDeg, live);
                }
            }
            finally { ents.Dispose(); }

            bool ok = nodesNear == 1 && bestDeg >= 3;
            Result("net-moved-junction", ok,
                $"nodesNear={nodesNear} degree={bestDeg} " +
                "(existing junction shifted 5 m must fuse the 3rd road via the 8 m degree>=2 fallback, not duplicate)");
        }

        // ------- v55 steps 31-33: reroute a SAVE-loaded line (no SyncId) — must sync by prefab+number -------
        // Reproduces the reported gap: delete/color/rename of a save-line synced, but reroute was dropped
        // because the id-based detector skips lines without a CS2M_SyncId. We create a line, strip its
        // identity (exactly what a save-loaded line looks like) and drive a reroute-by-number through the
        // receiver path, asserting the geometry updated AND the by-number echo guard got stamped.
        private string _saveReroutePrefab, _saveReroutePrefabType;
        private const int SaveRerouteNumber = 91;
        private float _saveRerouteExpectedZ;

        private void ActSaveRerouteCreate()
        {
            _saveReroutePrefab = null;
            EntityQuery prefabs = GetEntityQuery(
                ComponentType.ReadOnly<Game.Prefabs.RouteData>(),
                ComponentType.ReadOnly<Game.Prefabs.TransportLineData>());
            NativeArray<Entity> ents = prefabs.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity p in ents)
                {
                    if (_prefabSystem.TryGetPrefab(p, out PrefabBase pb) && pb != null && pb.name.Contains("Bus"))
                    { _saveReroutePrefab = pb.name; _saveReroutePrefabType = pb.GetType().Name; break; }
                }

                if (_saveReroutePrefab == null && ents.Length > 0
                    && _prefabSystem.TryGetPrefab(ents[0], out PrefabBase first) && first != null)
                { _saveReroutePrefab = first.name; _saveReroutePrefabType = first.GetType().Name; }
            }
            finally { ents.Dispose(); }

            if (_saveReroutePrefab == null || !TryAnchor(out float3 a))
            {
                Result("route-reroute-saveline", false, "no transport prefab / anchor");
                _saveReroutePrefab = null;
                return;
            }

            L($"[Auto] TEST route-reroute-saveline CREATE prefab={_saveReroutePrefab} number={SaveRerouteNumber}");
            RouteSync.EnqueueCreate(new CS2M.Commands.Data.Game.RouteCreateCommand
            {
                SyncId = CS2M_SyncIdSystem.Allocate(),
                PrefabType = _saveReroutePrefabType, PrefabName = _saveReroutePrefab, Complete = false,
                ColorR = 32, ColorG = 200, ColorB = 96, ColorA = 255, Number = SaveRerouteNumber,
                WpX = new[] { a.x - 200f, a.x - 120f, a.x - 40f }, WpY = new[] { a.y, a.y, a.y },
                WpZ = new[] { a.z + 200f, a.z + 230f, a.z + 200f },
                WpHasConn = new byte[3], WpConnId = new ulong[3], WpConnX = new float[3], WpConnZ = new float[3],
            });
        }

        private void ActSaveReroute()
        {
            if (_saveReroutePrefab == null) { return; }

            Entity route = ResolveRouteByNumber(SaveRerouteNumber, out float3[] wp);
            if (route == Entity.Null || wp == null || wp.Length != 3)
            {
                Result("route-reroute-saveline", false, "line did not build");
                _saveReroutePrefab = null;
                return;
            }

            // Strip identity -> now indistinguishable from a save-loaded line (no SyncId / RemotePlaced).
            if (EntityManager.HasComponent<CS2M_SyncId>(route))
            {
                CS2M_SyncIdSystem.Map.Remove(EntityManager.GetComponentData<CS2M_SyncId>(route).m_Id);
                EntityManager.RemoveComponent<CS2M_SyncId>(route);
            }

            if (EntityManager.HasComponent<CS2M_RemotePlaced>(route))
            {
                EntityManager.RemoveComponent<CS2M_RemotePlaced>(route);
            }

            string key = RouteSync.DeleteKey(0, _saveReroutePrefab, SaveRerouteNumber);
            RouteSync.SnapshotByNumber.Remove(key); // so a re-stamp proves the by-number path ran

            _saveRerouteExpectedZ = wp[1].z + 80f; // shove the middle waypoint — an unmistakable reroute
            L($"[Auto] TEST route-reroute-saveline REROUTE by number={SaveRerouteNumber} midZ {wp[1].z:F0}->{_saveRerouteExpectedZ:F0}");
            RouteSync.EnqueueCreate(new CS2M.Commands.Data.Game.RouteCreateCommand
            {
                SyncId = 0, Replace = true, PrefabType = _saveReroutePrefabType, PrefabName = _saveReroutePrefab,
                Complete = false, ColorR = 32, ColorG = 200, ColorB = 96, ColorA = 255, Number = SaveRerouteNumber,
                WpX = new[] { wp[0].x, wp[1].x, wp[2].x }, WpY = new[] { wp[0].y, wp[1].y, wp[2].y },
                WpZ = new[] { wp[0].z, _saveRerouteExpectedZ, wp[2].z },
                WpHasConn = new byte[3], WpConnId = new ulong[3], WpConnX = new float[3], WpConnZ = new float[3],
            });
        }

        private void VerifySaveReroute()
        {
            if (_saveReroutePrefab == null) { return; }

            Entity route = ResolveRouteByNumber(SaveRerouteNumber, out float3[] wp);
            if (route == Entity.Null || wp == null || wp.Length != 3)
            {
                Result("route-reroute-saveline", false, "line missing after reroute");
                return;
            }

            bool applied = math.abs(wp[1].z - _saveRerouteExpectedZ) < 5f;
            bool guarded = RouteSync.SnapshotByNumber.ContainsKey(
                RouteSync.DeleteKey(0, _saveReroutePrefab, SaveRerouteNumber));
            Result("route-reroute-saveline", applied && guarded,
                $"midZ={wp[1].z:F0} expected~{_saveRerouteExpectedZ:F0} guardStamped={guarded} " +
                "(save-line resolved by prefab+number, reroute applied, echo guard set)");
        }

        /// <summary>Find a live transport line by its RouteNumber and read its waypoint positions.</summary>
        private Entity ResolveRouteByNumber(int number, out float3[] positions)
        {
            positions = null;
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadOnly<Game.Routes.RouteNumber>(),
                    ComponentType.ReadOnly<Game.Routes.RouteWaypoint>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (EntityManager.GetComponentData<Game.Routes.RouteNumber>(e).m_Number != number) { continue; }

                    DynamicBuffer<Game.Routes.RouteWaypoint> wps = EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(e, true);
                    var list = new List<float3>();
                    for (int i = 0; i < wps.Length; i++)
                    {
                        Entity w = wps[i].m_Waypoint;
                        if (w != Entity.Null && EntityManager.HasComponent<Game.Routes.Position>(w))
                        {
                            list.Add(EntityManager.GetComponentData<Game.Routes.Position>(w).m_Position);
                        }
                    }

                    positions = list.ToArray();
                    return e;
                }
            }
            finally { ents.Dispose(); }

            return Entity.Null;
        }

        // ------- v55 steps 34-35: JUNCTION upgrade (traffic lights) — a NODE-level Upgraded flag the edge
        // detector never saw, so it didn't sync. Reuse the moved-junction node from step 30, toggle traffic
        // lights on it through the node path, and confirm the node carries the flag. -------
        private float3 _nodeUpgradePos;
        private const uint TrafficLightsFlag = 0x400u; // CompositionFlags.General.TrafficLights

        private void ActNodeUpgrade()
        {
            _nodeUpgradePos = default;
            if (_movedJ.Equals(default(float3)))
            {
                Result("node-upgrade", true, "skipped: no junction from the moved-junction step to upgrade");
                return;
            }

            Entity node = FindNodeNear(_movedJ, 8f);
            if (node == Entity.Null)
            {
                Result("node-upgrade", false, "no junction node near the moved-junction site");
                return;
            }

            float3 p = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
            _nodeUpgradePos = p;
            L($"[Auto] TEST node-upgrade INJECT node={node.Index} pos=({p.x:F0},{p.z:F0}) TrafficLights=0x{TrafficLightsFlag:X}");
            RemoteNetUpgradeQueue.Enqueue(new NetUpgradeCommand
            {
                IsNode = true,
                StartX = p.x, StartY = p.y, StartZ = p.z,
                General = TrafficLightsFlag, Left = 0, Right = 0,
            });
        }

        private void VerifyNodeUpgrade()
        {
            if (_nodeUpgradePos.Equals(default(float3))) { return; }

            Entity node = FindNodeNear(_nodeUpgradePos, 8f);
            if (node == Entity.Null)
            {
                Result("node-upgrade", false, "junction node vanished after upgrade");
                return;
            }

            bool hasUpg = EntityManager.HasComponent<Game.Net.Upgraded>(node);
            uint gen = hasUpg
                ? (uint) EntityManager.GetComponentData<Game.Net.Upgraded>(node).m_Flags.m_General
                : 0;
            bool ok = (gen & TrafficLightsFlag) == TrafficLightsFlag;
            Result("node-upgrade", ok,
                $"node={node.Index} hasUpgraded={hasUpg} general=0x{gen:X} expected TrafficLights=0x{TrafficLightsFlag:X} " +
                "(node-level junction flag written by the node upgrade path)");
        }

        /// <summary>Nearest junction node (has ConnectedEdge) within <paramref name="radius"/> m XZ.</summary>
        private Entity FindNodeNear(float3 pos, float radius)
        {
            EntityQuery nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.ReadOnly<Game.Net.ConnectedEdge>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            Entity best = Entity.Null;
            float bestD = radius;
            NativeArray<Entity> ents = nodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in ents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Net.Node>(n).m_Position;
                    float d = math.distance(p.xz, pos.xz);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = n;
                    }
                }
            }
            finally { ents.Dispose(); }

            return best;
        }

        // ------- v55 steps 36-37: service FEE apply (SetFee on the City ServiceFee buffer). Distinct from
        // the funding % (budget). Validates the new FeeApplySystem end-to-end in-world. -------
        private int _feeResource = -1;
        private float _feeExpected;

        private void ActFee()
        {
            _feeResource = -1;
            if (!TryCity(out Entity city) || !EntityManager.HasBuffer<Game.City.ServiceFee>(city))
            {
                Result("fee", false, "no City/ServiceFee buffer");
                return;
            }

            DynamicBuffer<Game.City.ServiceFee> fees = EntityManager.GetBuffer<Game.City.ServiceFee>(city, true);
            for (int i = 0; i < fees.Length; i++)
            {
                if (fees[i].m_Resource == Game.City.PlayerResource.Parking) { continue; } // parking = policy
                _feeResource = (int) fees[i].m_Resource;
                float cur = fees[i].m_Fee;
                _feeExpected = cur > 0.5f ? cur - 0.25f : cur + 0.25f; // a distinct, in-range value
                break;
            }

            if (_feeResource < 0) { Result("fee", false, "no non-parking fee in buffer"); return; }
            L($"[Auto] TEST fee INJECT resource={_feeResource} -> {_feeExpected}");
            RemoteFeeQueue.Enqueue(new CS2M.Commands.Data.Game.FeeCommand { Resource = _feeResource, Fee = _feeExpected });
        }

        private void VerifyFee()
        {
            if (_feeResource < 0) { return; }
            if (!TryCity(out Entity city) || !EntityManager.HasBuffer<Game.City.ServiceFee>(city))
            {
                Result("fee", false, "city/buffer gone");
                return;
            }

            DynamicBuffer<Game.City.ServiceFee> fees = EntityManager.GetBuffer<Game.City.ServiceFee>(city, true);
            float actual = float.NaN;
            for (int i = 0; i < fees.Length; i++)
            {
                if ((int) fees[i].m_Resource == _feeResource) { actual = fees[i].m_Fee; break; }
            }

            bool ok = !float.IsNaN(actual) && System.Math.Abs(actual - _feeExpected) < 0.001f;
            Result("fee", ok, $"resource={_feeResource} actual={actual} expected={_feeExpected} (ServiceFeeSystem.SetFee applied to City buffer)");
        }

        // ------- v55 steps 38-40: MOVE a placed water source in place (relocation keeps the entity, so the
        // create/delete detectors never fired and remotes kept the source at the old spot). Validates the
        // new WaterCommand.Move path end-to-end. -------
        private float3 _waterMovePos;
        private float3 _waterMoveTarget;

        private void ActWaterMoveCreate()
        {
            _waterMovePos = default;
            if (!TryAnchor(out float3 a)) { Result("water-move", false, "no anchor"); return; }
            _waterMovePos = new float3(a.x - 300f, a.y, a.z - 300f);
            RemoteWaterQueue.Enqueue(new CS2M.Commands.Data.Game.WaterCommand
            {
                PosX = _waterMovePos.x, PosY = _waterMovePos.y, PosZ = _waterMovePos.z,
                Radius = 20f, Height = 5f, Multiplier = 1f, Polluted = 0f, ConstantDepth = 0,
            });
            L($"[Auto] TEST water-move CREATE at ({_waterMovePos.x:F0},{_waterMovePos.z:F0})");
        }

        private void ActWaterMove()
        {
            if (_waterMovePos.Equals(default(float3))) { return; }
            _waterMoveTarget = new float3(_waterMovePos.x + 60f, _waterMovePos.y, _waterMovePos.z + 60f);
            RemoteWaterQueue.Enqueue(new CS2M.Commands.Data.Game.WaterCommand
            {
                Move = true, OldX = _waterMovePos.x, OldZ = _waterMovePos.z,
                PosX = _waterMoveTarget.x, PosY = _waterMoveTarget.y, PosZ = _waterMoveTarget.z,
                Radius = 20f, Height = 5f, Multiplier = 1f, Polluted = 0f, ConstantDepth = 0,
            });
            L($"[Auto] TEST water-move MOVE ({_waterMovePos.x:F0},{_waterMovePos.z:F0})->({_waterMoveTarget.x:F0},{_waterMoveTarget.z:F0})");
        }

        private void VerifyWaterMove()
        {
            if (_waterMovePos.Equals(default(float3))) { return; }

            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Simulation.WaterSourceData>(), ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            int nearOld = 0, nearNew = 0;
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                    if (math.distance(p.xz, _waterMovePos.xz) < 8f) { nearOld++; }
                    if (math.distance(p.xz, _waterMoveTarget.xz) < 8f) { nearNew++; }
                }
            }
            finally { ents.Dispose(); }

            bool ok = nearNew >= 1 && nearOld == 0;
            Result("water-move", ok, $"nearOld={nearOld} nearNew={nearNew} (source repositioned in place, none left flooding the old spot)");
        }

        // ------- v55 steps 41-42: DISABLE a service-building extension (the power button on an installed
        // upgrade). The flag lives on the extension SUB-entity (Transform but no Building), so the policy
        // detector's kind-1 branch never matched it. Drives the new kind-4 path via the "Out of Service"
        // policy and checks ExtensionFlags.Disabled landed. Skips cleanly if the city has no extension. -------
        private Entity _extEntity;

        private void ActExtDisable()
        {
            _extEntity = Entity.Null;
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Extension>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            string name = null;
            float3 pos = default;
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if ((EntityManager.GetComponentData<Game.Buildings.Extension>(e).m_Flags
                         & Game.Buildings.ExtensionFlags.Disabled) != 0)
                    {
                        continue; // already disabled — pick one whose flag we can flip
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                            out PrefabBase pb) || pb == null)
                    {
                        continue;
                    }

                    _extEntity = e;
                    name = pb.name;
                    pos = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                    break;
                }
            }
            finally { ents.Dispose(); }

            if (_extEntity == Entity.Null)
            {
                Result("ext-disable", true, "skipped: no enabled extension in this city to toggle");
                return;
            }

            L($"[Auto] TEST ext-disable INJECT extension={name} at ({pos.x:F0},{pos.z:F0})");
            RemotePolicyQueue.Enqueue(new CS2M.Commands.Data.Game.PolicyCommand
            {
                PolicyType = "PolicyPrefab", PolicyName = "Out of Service", Active = true, Adjustment = 0f,
                TargetKind = 4, TargetName = name, TargetX = pos.x, TargetZ = pos.z,
            });
        }

        private void VerifyExtDisable()
        {
            if (_extEntity == Entity.Null) { return; }
            if (!EntityManager.Exists(_extEntity) || !EntityManager.HasComponent<Game.Buildings.Extension>(_extEntity))
            {
                Result("ext-disable", false, "extension entity vanished");
                return;
            }

            Game.Buildings.ExtensionFlags flags =
                EntityManager.GetComponentData<Game.Buildings.Extension>(_extEntity).m_Flags;
            bool ok = (flags & Game.Buildings.ExtensionFlags.Disabled) != 0;
            Result("ext-disable", ok, $"flags={flags} (kind-4 Modify by prefab+pos set ExtensionFlags.Disabled on the sub-object)");
        }

        // ------- v55 steps 43-44: rename the CITY (a managed CityConfigurationSystem property, not a
        // CustomName entity — the rename detector never scanned it). Drives kind-4 RenameApply and checks
        // the property changed; restores the original so the autosave stays clean. -------
        private string _cityNamePrev;
        private string _cityNameExpected;

        private void ActCityName()
        {
            var cc = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
            _cityNamePrev = cc.cityName;
            _cityNameExpected = "CS2M-SelfTest-" + (_cityNamePrev != null ? _cityNamePrev.Length : 0);
            L($"[Auto] TEST city-name INJECT \"{_cityNamePrev}\" -> \"{_cityNameExpected}\"");
            RenameSync.Enqueue(new CS2M.Commands.Data.Game.RenameCommand { TargetKind = 4, Name = _cityNameExpected });
        }

        private void VerifyCityName()
        {
            if (_cityNameExpected == null) { return; }
            var cc = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
            bool ok = cc.cityName == _cityNameExpected;
            Result("city-name", ok, $"cityName=\"{cc.cityName}\" expected=\"{_cityNameExpected}\" (kind-4 RenameApply set CityConfigurationSystem.cityName)");

            // Restore the original directly so the selftest autosave doesn't keep the test name.
            if (_cityNamePrev != null)
            {
                cc.cityName = _cityNamePrev;
                RenameSync.CityNameSnapshot = _cityNamePrev;
            }
        }

        // ------- v55 steps 45-47: RESHAPE a district in place. A reshape marks the area Updated (never
        // Applied), so it was invisible; and the apply always created a fresh entity → a duplicate. Creates
        // a district, then drives a Replace command at its centroid and checks it was rewritten in place
        // (bigger polygon) with NO duplicate. -------
        private float3 _drCenter;
        private string _drType, _drName;

        private void ActDistrictReshapeCreate()
        {
            _drCenter = default;
            if (!TryGetDistrictPrefab(out string type, out string name, out Entity _))
            {
                Result("district-reshape", false, "no District prefab");
                return;
            }

            if (!TryAnchor(out float3 a))
            {
                Result("district-reshape", false, "no anchor");
                return;
            }

            _drType = type;
            _drName = name;
            _drCenter = new float3(a.x - 500f, a.y, a.z - 500f);
            float y = _drCenter.y;
            var xs = new[] { _drCenter.x - 60f, _drCenter.x + 60f, _drCenter.x + 60f, _drCenter.x - 60f, _drCenter.x - 60f };
            var zs = new[] { _drCenter.z - 60f, _drCenter.z - 60f, _drCenter.z + 60f, _drCenter.z + 60f, _drCenter.z - 60f };
            var ys = new[] { y, y, y, y, y };
            L($"[Auto] TEST district-reshape CREATE name={name} center=({_drCenter.x:F0},{_drCenter.z:F0})");
            RemoteDistrictQueue.Enqueue(new DistrictCommand
            {
                PrefabType = type, PrefabName = name, OptionMask = 0u,
                Xs = xs, Ys = ys, Zs = zs, CenterX = _drCenter.x, CenterZ = _drCenter.z,
            });
        }

        private void ActDistrictReshape()
        {
            if (_drCenter.Equals(default(float3))) { return; }
            float y = _drCenter.y;
            // Bigger square (±120), addressed by the ORIGINAL centroid the receiver still holds.
            var xs = new[] { _drCenter.x - 120f, _drCenter.x + 120f, _drCenter.x + 120f, _drCenter.x - 120f, _drCenter.x - 120f };
            var zs = new[] { _drCenter.z - 120f, _drCenter.z - 120f, _drCenter.z + 120f, _drCenter.z + 120f, _drCenter.z - 120f };
            var ys = new[] { y, y, y, y, y };
            L($"[Auto] TEST district-reshape RESHAPE ->+-120 at ({_drCenter.x:F0},{_drCenter.z:F0})");
            RemoteDistrictQueue.Enqueue(new DistrictCommand
            {
                Replace = true, PrefabType = _drType, PrefabName = _drName, OptionMask = 0u,
                Xs = xs, Ys = ys, Zs = zs, CenterX = _drCenter.x, CenterZ = _drCenter.z,
            });
        }

        private void VerifyDistrictReshape()
        {
            if (_drCenter.Equals(default(float3))) { return; }

            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Areas.District>(), ComponentType.ReadOnly<Game.Areas.Node>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            int nearCount = 0;
            float minNodeX = float.MaxValue;
            bool hashStable = true;
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    DynamicBuffer<Game.Areas.Node> nb = EntityManager.GetBuffer<Game.Areas.Node>(e, true);
                    if (nb.Length == 0) { continue; }

                    float cx = 0f, cz = 0f;
                    for (int i = 0; i < nb.Length; i++) { cx += nb[i].m_Position.x; cz += nb[i].m_Position.z; }
                    cx /= nb.Length;
                    cz /= nb.Length;

                    if (math.distance(new float2(cx, cz), _drCenter.xz) < 50f)
                    {
                        nearCount++;
                        for (int i = 0; i < nb.Length; i++) { minNodeX = math.min(minNodeX, nb[i].m_Position.x); }

                        // Echo check: the game must not have mutated the boundary buffer after our rewrite,
                        // else the scanner would see a hash change and ping the reshape back forever.
                        if (DistrictReshapeSync.Snapshot.TryGetValue(e, out DistrictReshapeSync.Snap snap)
                            && snap.Hash != WorkAreaHash.Compute(nb))
                        {
                            hashStable = false;
                        }
                    }
                }
            }
            finally { ents.Dispose(); }

            bool reshaped = minNodeX < _drCenter.x - 100f; // reached ~C.x-120 (was C.x-60)
            bool ok = nearCount == 1 && reshaped && hashStable;
            Result("district-reshape", ok,
                $"nearCount={nearCount} minNodeX={minNodeX:F0} expected~{_drCenter.x - 120f:F0} hashStable={hashStable} " +
                "(reshape rewrote the SAME district in place — exactly 1, no duplicate, no echo)");
        }

        // ------- v55 steps 48-49: hide a transport LINE (HiddenRoute tag — no Updated/event, so the reroute/
        // color detectors never saw it). Drives the visibility apply and checks the tag landed. -------
        private Entity _visRoute;

        private void ActLineVisibility()
        {
            _visRoute = Entity.Null;
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadOnly<Game.Routes.RouteNumber>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            int number = 0;
            string pname = null;
            try
            {
                foreach (Entity e in ents)
                {
                    if (EntityManager.HasComponent<Game.Routes.HiddenRoute>(e)) { continue; } // pick a visible one
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                            out PrefabBase pb) || pb == null)
                    {
                        continue;
                    }

                    _visRoute = e;
                    number = EntityManager.GetComponentData<Game.Routes.RouteNumber>(e).m_Number;
                    pname = pb.name;
                    break;
                }
            }
            finally { ents.Dispose(); }

            if (_visRoute == Entity.Null)
            {
                Result("line-visibility", true, "skipped: no visible transport line to hide");
                return;
            }

            L($"[Auto] TEST line-visibility INJECT hide number={number}");
            RouteSync.EnqueueVisibility(new CS2M.Commands.Data.Game.RouteVisibilityCommand
            {
                SyncId = 0, PrefabName = pname, Number = number, Hidden = true,
            });
        }

        private void VerifyLineVisibility()
        {
            if (_visRoute == Entity.Null) { return; }
            if (!EntityManager.Exists(_visRoute))
            {
                Result("line-visibility", false, "route vanished");
                return;
            }

            bool hidden = EntityManager.HasComponent<Game.Routes.HiddenRoute>(_visRoute);
            Result("line-visibility", hidden, $"HiddenRoute={hidden} (visibility apply added the tag by prefab+number)");
        }

        // ------- v55 steps 50-51: MOVE an installed extension (owned sub-object). MoveDetector excluded
        // Owner, so relocating an upgrade didn't sync. Drives the new IsOwnedUpgrade move path and checks
        // the child's Transform moved. Skips cleanly if the city has no extension. -------
        private Entity _extMoveEntity;
        private float3 _extMoveExpected;

        private void ActExtMove()
        {
            _extMoveEntity = Entity.Null;
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Extension>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    Entity owner = EntityManager.GetComponentData<Owner>(e).m_Owner;
                    if (!EntityManager.Exists(owner) || !EntityManager.HasComponent<Game.Objects.Transform>(owner)
                        || !EntityManager.HasComponent<PrefabRef>(owner))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab, out PrefabBase pb) || pb == null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab, out PrefabBase opb) || opb == null)
                    {
                        continue;
                    }

                    Game.Objects.Transform et = EntityManager.GetComponentData<Game.Objects.Transform>(e);
                    Game.Objects.Transform ot = EntityManager.GetComponentData<Game.Objects.Transform>(owner);
                    _extMoveEntity = e;
                    _extMoveExpected = new float3(et.m_Position.x + 4f, et.m_Position.y, et.m_Position.z + 4f);
                    L($"[Auto] TEST ext-move INJECT {pb.name} ({et.m_Position.x:F0},{et.m_Position.z:F0})->({_extMoveExpected.x:F0},{_extMoveExpected.z:F0})");
                    RemoteEditQueue.EnqueueMove(new CS2M.Commands.Data.Game.MoveCommand
                    {
                        IsOwnedUpgrade = true,
                        OwnerSyncId = EntityManager.HasComponent<CS2M_SyncId>(owner)
                            ? EntityManager.GetComponentData<CS2M_SyncId>(owner).m_Id : 0,
                        OwnerPrefabName = opb.name, OwnerX = ot.m_Position.x, OwnerY = ot.m_Position.y, OwnerZ = ot.m_Position.z,
                        PrefabType = pb.GetType().Name, PrefabName = pb.name,
                        OldX = et.m_Position.x, OldY = et.m_Position.y, OldZ = et.m_Position.z,
                        PosX = _extMoveExpected.x, PosY = _extMoveExpected.y, PosZ = _extMoveExpected.z,
                        RotX = et.m_Rotation.value.x, RotY = et.m_Rotation.value.y, RotZ = et.m_Rotation.value.z, RotW = et.m_Rotation.value.w,
                    });
                    return;
                }
            }
            finally { ents.Dispose(); }

            Result("ext-move", true, "skipped: no extension in this city to relocate");
        }

        private void VerifyExtMove()
        {
            if (_extMoveEntity == Entity.Null) { return; }
            if (!EntityManager.Exists(_extMoveEntity) || !EntityManager.HasComponent<Game.Objects.Transform>(_extMoveEntity))
            {
                Result("ext-move", false, "extension vanished");
                return;
            }

            float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(_extMoveEntity).m_Position;
            bool ok = math.distance(p.xz, _extMoveExpected.xz) < 1f;
            Result("ext-move", ok, $"pos=({p.x:F1},{p.z:F1}) expected=({_extMoveExpected.x:F1},{_extMoveExpected.z:F1}) (owned-upgrade move applied via SubObject resolve)");
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
            if (_chatValidation)
            {
                Chat($"{(ok ? "PASS" : "FAIL")} — {name}" + (ok ? "" : $" ({detail})"));
            }
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

        // ------- CS2M_AP_CONCURRENT: both sims stamp the SAME road at the SAME spot -------
        // Each side APPLIES locally (RemoteNetQueue) AND ships over the wire, exactly like the real
        // game (the engine builds the road locally, the detector sends it). Convergence = the
        // dedup/merge collapses the two overlapping roads to one on BOTH sides with no [Hash] DRIFT.
        // This is the adversarial dup-edge race the bot could never reach (it had no world). isHost
        // only decides the send direction (SendToAll vs SendToServer).
        private void RunConcurrentStep(bool isHost)
        {
            if (_concStep >= 12) { return; }
            if (_concTimer > 0) { _concTimer--; return; }
            _concTimer = 90;

            if (_edgeQuery.IsEmptyIgnoreFilter || !TryAnchor(out float3 anchor))
            {
                if (_concStep == 0) { L("[Auto] CONCURRENT skip: no road/anchor in world"); }
                _concStep = 12;
                return;
            }

            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            string type = null, name = null;
            try
            {
                // Pick a real ROAD (not a train track / invisible) so the dedup path we care about
                // (CoveredByExistingEdge, the v54 fix) is the one under concurrent stress.
                foreach (Entity cand in ents)
                {
                    if (_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(cand).m_Prefab,
                            out PrefabBase p) && p != null
                        && p.name.Contains("Road") && !p.name.Contains("Invisible"))
                    {
                        type = p.GetType().Name;
                        name = p.name;
                        break;
                    }
                }
            }
            finally { ents.Dispose(); }

            if (name == null)
            {
                L("[Auto] CONCURRENT skip: no Road prefab in world");
                _concStep = 12;
                return;
            }

            // Both sides derive the SAME segment for step i from the shared world's anchor, so the two
            // placements land on top of each other — a genuine concurrent duplicate.
            int i = _concStep;
            var s = new float3(anchor.x + 400f, anchor.y, anchor.z + 400f + i * 30f);
            var e = new float3(anchor.x + 560f, anchor.y, anchor.z + 400f + i * 30f);
            NetPlaceCommand cmd = StraightNet(type, name, s, e);

            RemoteNetQueue.Enqueue(cmd);                       // apply on THIS side
            if (isHost) { Command.SendToAll?.Invoke(cmd); }    // ship to the other side...
            else { Command.SendToServer?.Invoke(cmd); }        // ...client -> host (host relays)

            L($"[Auto] CONCURRENT {(isHost ? "host" : "client")} road #{i} " +
              $"at ({s.x:F0},{s.z:F0})->({e.x:F0},{e.z:F0}) syncId={cmd.SyncId}");

            _concStep++;
            if (_concStep >= 12) { L("[Auto] CONCURRENT done (12 overlapping placements sent each side)"); }
        }

        // ------- original over-the-wire host sequence (for the 2-PC test) -------

        private void RunHostStep()
        {
            if (_testStep >= 29) { return; }
            if (_testTimer > 0) { _testTimer--; return; }

            switch (_testStep)
            {
                case 0: _treeSyncId = SendObject(_treeQuery, "tree", new float3(20f, 0f, 20f)); _testTimer = 300; break;
                case 1: _buildingSyncId = SendObject(_buildingQuery, "building", new float3(60f, 0f, 60f)); _testTimer = 300; break;
                case 2: SendNet(); InjectReplayRoad(); _testTimer = 300; break;
                case 3: if (_treeSyncId != 0) { var dcmd = new DeleteCommand { SyncId = _treeSyncId }; RemoteEditQueue.EnqueueDelete(dcmd); Command.SendToAll?.Invoke(dcmd); L($"[Auto] TEST delete SEND syncId={_treeSyncId}"); } _testTimer = 240; break;
                case 4: SendZone(); _testTimer = 300; break;
                case 5: SendWater(); _testTimer = 300; break;
                case 6: SendMove(); _testTimer = 300; break;
                case 7: SendDistrict(); _testTimer = 300; break;
                case 8: SendUpgrade(); _testTimer = 300; break;
                case 9: SendTerrain(); _testTimer = 300; break;
                case 10: SendPolicy(); _testTimer = 300; break;
                case 11: SendTile(); _testTimer = 300; break;
                case 12: SendDevTree(); _testTimer = 300; break;
                case 13: SendRoute(); _testTimer = 300; break;
                case 14: SendFire(); _testTimer = 300; break;
                case 15: SendJunctionStress(); _testTimer = 400; break;
                case 16: SendStressArmDelete(); _testTimer = 300; break;
                case 17: SendMovedJunctionStress(); _testTimer = 400; break;
                case 18: SendNodeUpgrade(); _testTimer = 400; break;
                case 19: SendFee2(); _testTimer = 300; break;
                case 20: SendWaterMove2Create(); _testTimer = 300; break;
                case 21: SendWaterMove2Move(); _testTimer = 300; break;
                case 22: SendDistrictReshape2Create(); _testTimer = 300; break;
                case 23: SendDistrictReshape2Reshape(); _testTimer = 400; break;
                case 24: SendCityName2(); _testTimer = 300; break;
                case 25: SendLineVisibility2(); _testTimer = 300; break;
                case 26: SendTax2(); _testTimer = 300; break;
                // v57: AtomicBatch over the REAL wire — the one seam no solo test covers. Host enqueues the
                // SAME fabricated batch locally AND ships it; both sides build the identical road from
                // identical input → client logs [Batch] RECV/APPLIED and roads/nodes hashes stay convergent.
                case 27: SendNetBatch2(); _testTimer = 400; break;
                case 28: L("[Auto] scripted test DONE (over the wire). Check CLIENT log VERIFY lines."); break;
            }

            _testStep++;
        }

        // v55: 2-sim (over-the-wire) exercises for the audit fixes. Each applies on the HOST and ships to
        // the CLIENT; the reinforced StateHash (FeeHash / AreaHash+count / node/route hashes) then confirms
        // convergence with NO [Hash] DRIFT — the cross-machine proof. Water position isn't hashed, so its
        // move is confirmed by the client's "[Water] APPLIED move" log line.
        private void SendFee2()
        {
            if (!TryCity(out Entity city) || !EntityManager.HasBuffer<Game.City.ServiceFee>(city))
            {
                L("[Auto] TEST fee SEND SKIP no ServiceFee buffer");
                return;
            }

            DynamicBuffer<Game.City.ServiceFee> fees = EntityManager.GetBuffer<Game.City.ServiceFee>(city, true);
            for (int i = 0; i < fees.Length; i++)
            {
                if (fees[i].m_Resource == Game.City.PlayerResource.Parking) { continue; }
                int res = (int) fees[i].m_Resource;
                float target = fees[i].m_Fee > 0.5f ? fees[i].m_Fee - 0.3f : fees[i].m_Fee + 0.3f;
                var cmd = new CS2M.Commands.Data.Game.FeeCommand { Resource = res, Fee = target };
                RemoteFeeQueue.Enqueue(cmd);     // host applies
                Command.SendToAll?.Invoke(cmd);  // client applies -> StateHash FeeHash converges
                L($"[Auto] TEST fee SEND resource={res} -> {target}");
                return;
            }
        }

        private float3 _wm2Pos;

        private void SendWaterMove2Create()
        {
            _wm2Pos = default;
            if (!TryAnchor(out float3 a)) { L("[Auto] TEST water-move SEND SKIP no anchor"); return; }
            _wm2Pos = new float3(a.x + 250f, a.y, a.z + 250f);
            var cmd = new CS2M.Commands.Data.Game.WaterCommand
            {
                PosX = _wm2Pos.x, PosY = _wm2Pos.y, PosZ = _wm2Pos.z, Radius = 20f, Height = 5f, Multiplier = 1f,
            };
            RemoteWaterQueue.Enqueue(cmd);
            Command.SendToAll?.Invoke(cmd);
            L($"[Auto] TEST water-move SEND create ({_wm2Pos.x:F0},{_wm2Pos.z:F0})");
        }

        private void SendWaterMove2Move()
        {
            if (_wm2Pos.Equals(default(float3))) { return; }
            var t = new float3(_wm2Pos.x + 70f, _wm2Pos.y, _wm2Pos.z + 70f);
            var cmd = new CS2M.Commands.Data.Game.WaterCommand
            {
                Move = true, OldX = _wm2Pos.x, OldZ = _wm2Pos.z,
                PosX = t.x, PosY = t.y, PosZ = t.z, Radius = 20f, Height = 5f, Multiplier = 1f,
            };
            RemoteWaterQueue.Enqueue(cmd);
            Command.SendToAll?.Invoke(cmd); // client "[Water] APPLIED move" = cross-machine proof
            L($"[Auto] TEST water-move SEND move ->({t.x:F0},{t.z:F0})");
        }

        private float3 _dr2Center;
        private string _dr2Type, _dr2Name;

        private void SendDistrictReshape2Create()
        {
            _dr2Center = default;
            if (!TryGetDistrictPrefab(out string type, out string name, out Entity _)) { L("[Auto] TEST district-reshape SEND SKIP no prefab"); return; }
            if (!TryAnchor(out float3 a)) { L("[Auto] TEST district-reshape SEND SKIP no anchor"); return; }
            _dr2Type = type;
            _dr2Name = name;
            _dr2Center = new float3(a.x + 600f, a.y, a.z + 600f);
            float y = _dr2Center.y;
            var xs = new[] { _dr2Center.x - 60f, _dr2Center.x + 60f, _dr2Center.x + 60f, _dr2Center.x - 60f, _dr2Center.x - 60f };
            var zs = new[] { _dr2Center.z - 60f, _dr2Center.z - 60f, _dr2Center.z + 60f, _dr2Center.z + 60f, _dr2Center.z - 60f };
            var ys = new[] { y, y, y, y, y };
            var cmd = new DistrictCommand
            {
                PrefabType = type, PrefabName = name, OptionMask = 0u,
                Xs = xs, Ys = ys, Zs = zs, CenterX = _dr2Center.x, CenterZ = _dr2Center.z,
            };
            RemoteDistrictQueue.Enqueue(cmd);
            Command.SendToAll?.Invoke(cmd);
            L($"[Auto] TEST district-reshape SEND create ({_dr2Center.x:F0},{_dr2Center.z:F0})");
        }

        private void SendTax2()
        {
            var taxSys = World.GetOrCreateSystemManaged<Game.Simulation.TaxSystem>();
            NativeArray<int> rates = taxSys.GetTaxRates();
            if (!rates.IsCreated || rates.Length == 0) { L("[Auto] TEST tax SEND SKIP no rates"); return; }
            var copy = new int[rates.Length];
            for (int i = 0; i < rates.Length; i++) { copy[i] = rates[i]; }
            copy[0] = copy[0] >= 15 ? copy[0] - 2 : copy[0] + 2; // nudge the main rate
            RemoteTaxQueue.Set(copy);                                                    // host applies
            Command.SendToAll?.Invoke(new CS2M.Commands.Data.Game.TaxSyncCommand { Rates = copy }); // client applies -> StateHash TaxHash converges
            L($"[Auto] TEST tax SEND main={copy[0]} (StateHash TaxHash checks convergence)");
        }

        // v55: extend cross-machine coverage to the cosmetic fixes that only had single-instance selftest.
        private void SendCityName2()
        {
            var cc = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
            string prev = cc.cityName;
            string newName = "CoopCity-" + (prev != null ? prev.Length : 0);
            cc.cityName = newName;                    // host applies directly
            RenameSync.CityNameSnapshot = newName;    // guard so the host detector doesn't re-broadcast
            Command.SendToAll?.Invoke(new CS2M.Commands.Data.Game.RenameCommand { TargetKind = 4, Name = newName });
            L($"[Auto] TEST city-name SEND \"{newName}\" (client [Rename] APPLIED city name = proof)");
        }

        private void SendLineVisibility2()
        {
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadOnly<Game.Routes.RouteNumber>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (EntityManager.HasComponent<Game.Routes.HiddenRoute>(e)) { continue; }
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab, out PrefabBase pb) || pb == null) { continue; }
                    int number = EntityManager.GetComponentData<Game.Routes.RouteNumber>(e).m_Number;
                    var cmd = new CS2M.Commands.Data.Game.RouteVisibilityCommand { SyncId = 0, PrefabName = pb.name, Number = number, Hidden = true };
                    RouteSync.EnqueueVisibility(cmd);  // host hides
                    Command.SendToAll?.Invoke(cmd);     // client hides -> StateHash RouteHash (folds HiddenRoute) converges
                    L($"[Auto] TEST line-visibility SEND hide number={number} (StateHash RouteHash checks convergence)");
                    return;
                }
            }
            finally { ents.Dispose(); }

            L("[Auto] TEST line-visibility SEND SKIP no visible route");
        }

        private void SendDistrictReshape2Reshape()
        {
            if (_dr2Center.Equals(default(float3))) { return; }
            float y = _dr2Center.y;
            var xs = new[] { _dr2Center.x - 120f, _dr2Center.x + 120f, _dr2Center.x + 120f, _dr2Center.x - 120f, _dr2Center.x - 120f };
            var zs = new[] { _dr2Center.z - 120f, _dr2Center.z - 120f, _dr2Center.z + 120f, _dr2Center.z + 120f, _dr2Center.z - 120f };
            var ys = new[] { y, y, y, y, y };
            var cmd = new DistrictCommand
            {
                Replace = true, PrefabType = _dr2Type, PrefabName = _dr2Name, OptionMask = 0u,
                Xs = xs, Ys = ys, Zs = zs, CenterX = _dr2Center.x, CenterZ = _dr2Center.z,
            };
            RemoteDistrictQueue.Enqueue(cmd);     // host reshapes in place
            Command.SendToAll?.Invoke(cmd);        // client reshapes -> StateHash: 1 district, same shape, no dup/echo
            L("[Auto] TEST district-reshape SEND reshape ->+-120 (StateHash checks 1 district, no dup)");
        }

        /// <summary>2-sim host roteiro: put traffic lights on the fused junction-stress node on BOTH sims.
        /// This is the only over-the-wire exercise of the NODE upgrade path (junction control), and the
        /// v55 StateHash node-hash (which now folds Upgraded.General) is what proves both sims carry the
        /// flag — a DRIFT here means the junction upgrade didn't cross the wire.</summary>
        private void SendNodeUpgrade()
        {
            if (_stressCenter.Equals(default(float3)))
            {
                L("[Auto] TEST node-upgrade SEND SKIP no junction");
                return;
            }

            var cmd = new NetUpgradeCommand
            {
                IsNode = true,
                StartX = _stressCenter.x, StartY = _stressCenter.y, StartZ = _stressCenter.z,
                General = 0x400u, Left = 0, Right = 0, // CompositionFlags.General.TrafficLights
            };
            RemoteNetUpgradeQueue.Enqueue(cmd);   // host applies to its fused node
            Command.SendToAll?.Invoke(cmd);        // client applies to the same coord -> node-hash converges
            L($"[Auto] TEST node-upgrade SEND TrafficLights at ({_stressCenter.x:F0},{_stressCenter.z:F0})");
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
                RemotePlacementQueue.EnqueueObject(cmd);   // host applies too, so the world CONVERGES (not just client)
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
                RemoteNetQueue.Enqueue(cmd);   // host applies too, so the road CONVERGES (not just client)
                Command.SendToAll?.Invoke(cmd);
            }
            finally { ents.Dispose(); }
        }

        /// <summary>v56 INPUT-REPLAY runtime de-risk: synthesize a straight-road NetToolReplayCommand in open
        /// ground (no snap) and enqueue it locally so NetToolReplaySystem schedules the game's real
        /// CreateDefinitionsJob. Success = "[Replay] APPLIED" logged + a new edge appears + NO crash in
        /// Player.log. Proves the crux (a mod CAN drive the tool's definition pipeline) before wiring capture.</summary>
        private void InjectReplayRoad()
        {
            if (_edgeQuery.IsEmptyIgnoreFilter) { L("[Auto] TEST replay INJECT SKIP noEdges"); return; }
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity src = ents[0];
                PrefabRef pr = EntityManager.GetComponentData<PrefabRef>(src);
                if (!_prefabSystem.TryGetPrefab(pr.m_Prefab, out PrefabBase prefab) || prefab == null) { return; }
                Colossal.Mathematics.Bezier4x3 b = EntityManager.GetComponentData<Game.Net.Curve>(src).m_Bezier;
                var off = new float3(0f, 0f, 80f); // parallel to an existing road, in open ground
                float3 a = b.a + off, d = b.d + off;
                float3 dir = math.normalizesafe(d - a, new float3(1f, 0f, 0f));
                quaternion rot = quaternion.LookRotationSafe(new float3(dir.x, 0f, dir.z), math.up());
                var cmd = new NetToolReplayCommand
                {
                    PrefabType = prefab.GetType().Name, PrefabName = prefab.name,
                    Mode = 0, RandomSeed = 12345, EditorMode = false, LeftHandTraffic = false,
                    RemoveUpgrade = false, ParallelOffset = 0f, ParallelCount = 0,
                    PosX = new[] { a.x, d.x }, PosY = new[] { a.y, d.y }, PosZ = new[] { a.z, d.z },
                    HitX = new[] { a.x, d.x }, HitY = new[] { a.y, d.y }, HitZ = new[] { a.z, d.z },
                    DirX = new[] { dir.x, dir.x }, DirZ = new[] { dir.z, dir.z },
                    HitDirX = new[] { dir.x, dir.x }, HitDirY = new[] { 0f, 0f }, HitDirZ = new[] { dir.z, dir.z },
                    RotX = new[] { rot.value.x, rot.value.x }, RotY = new[] { rot.value.y, rot.value.y },
                    RotZ = new[] { rot.value.z, rot.value.z }, RotW = new[] { rot.value.w, rot.value.w },
                    SnapPriX = new[] { 0f, 0f }, SnapPriY = new[] { 0f, 0f },
                    ElemIdxX = new[] { -1, -1 }, ElemIdxY = new[] { -1, -1 },
                    CurvePos = new[] { 0f, 0f }, Elev = new[] { 0f, 0f },
                    SnapPosX = new[] { 0f, 0f }, SnapPosZ = new[] { 0f, 0f },
                    SnapKind = new[] { 0, 0 }, SnapNodeId = new ulong[] { 0, 0 },
                };
                RemoteReplayQueue.Enqueue(cmd);           // host replays
                Command.SendToAll?.Invoke(cmd);           // client replays too -> topology must be IDENTICAL
                L($"[Auto] TEST replay INJECT road name={prefab.name} A=({a.x:F0},{a.z:F0}) B=({d.x:F0},{d.z:F0})");
            }
            finally { ents.Dispose(); }
        }

        /// <summary>2-sim host roteiro: place a water source locally AND ship it. StateHash counts water
        /// sources (WaterDesc), so a drift here means the water tool didn't converge cross-machine.</summary>
        private void SendWater()
        {
            if (!TryAnchor(out float3 anchor)) { L("[Auto] TEST water SEND SKIP noAnchor"); return; }
            var cmd = new WaterCommand
            {
                PosX = anchor.x + 150f, PosY = anchor.y, PosZ = anchor.z + 150f,
                Radius = 20f, Height = 5f, Multiplier = 1f, Polluted = 0f, ConstantDepth = 0,
            };
            RemoteWaterQueue.Enqueue(cmd);       // host places
            Command.SendToAll?.Invoke(cmd);      // client places -> StateHash water count converges
            L($"[Auto] TEST water SEND pos=({cmd.PosX:F0},{cmd.PosZ:F0})");
        }

        /// <summary>2-sim host roteiro: relocate the roteiro-placed building locally AND ship it. Buildings
        /// are position-hashed by StateHash, so a drift means the move didn't converge cross-machine.</summary>
        private void SendMove()
        {
            if (_buildingSyncId == 0 || !_idSystem.TryResolve(_buildingSyncId, out Entity e) || !EntityManager.Exists(e)
                || !EntityManager.HasComponent<Game.Objects.Transform>(e))
            {
                L("[Auto] TEST move SEND SKIP building unresolvable");
                return;
            }

            float3 cur = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
            float3 to = cur + new float3(15f, 0f, 15f);
            var cmd = new MoveCommand { SyncId = _buildingSyncId, PosX = to.x, PosY = to.y, PosZ = to.z, RotW = 1f };
            RemoteEditQueue.EnqueueMove(cmd);    // host moves
            Command.SendToAll?.Invoke(cmd);      // client moves -> StateHash building position converges
            L($"[Auto] TEST move SEND syncId={_buildingSyncId} to=({to.x:F0},{to.z:F0})");
        }

        /// <summary>2-sim host roteiro: draw a district polygon locally AND ship it. StateHash counts
        /// districts, so a drift means the district tool didn't converge cross-machine.</summary>
        private void SendDistrict()
        {
            if (!TryGetDistrictPrefab(out string type, out string name, out Entity _)) { L("[Auto] TEST district SEND SKIP noPrefab"); return; }
            if (!TryAnchor(out float3 anchor)) { L("[Auto] TEST district SEND SKIP noAnchor"); return; }
            float3 center = anchor + new float3(-200f, 0f, -200f); // clear quadrant
            float y = center.y;
            var xs = new[] { center.x - 60f, center.x + 60f, center.x + 60f, center.x - 60f, center.x - 60f };
            var zs = new[] { center.z - 60f, center.z - 60f, center.z + 60f, center.z + 60f, center.z - 60f };
            var ys = new[] { y, y, y, y, y };
            var cmd = new DistrictCommand { PrefabType = type, PrefabName = name, OptionMask = 0u, Xs = xs, Ys = ys, Zs = zs };
            RemoteDistrictQueue.Enqueue(cmd);    // host draws
            Command.SendToAll?.Invoke(cmd);      // client draws -> StateHash district count converges
            L($"[Auto] TEST district SEND name={name} center=({center.x:F0},{center.z:F0})");
        }

        /// <summary>2-sim host roteiro: upgrade a road (side trees) locally AND ship it. Composition is
        /// NOT position-hashed, so this validates the RECV/apply flow cross-machine (client materializes
        /// the upgrade) — the selftest already covers the receiver effect.</summary>
        private void SendUpgrade()
        {
            if (_edgeQuery.IsEmptyIgnoreFilter) { L("[Auto] TEST upgrade SEND SKIP noEdges"); return; }
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity e = Entity.Null;
                foreach (Entity cand in ents)
                {
                    if (_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(cand).m_Prefab,
                            out PrefabBase p) && p != null && p.name.Contains("Road") && !p.name.Contains("Invisible"))
                    { e = cand; break; }
                }

                if (e == Entity.Null) { L("[Auto] TEST upgrade SEND SKIP noRoad"); return; }
                Game.Net.Edge ed = EntityManager.GetComponentData<Game.Net.Edge>(e);
                if (!EntityManager.HasComponent<Game.Net.Node>(ed.m_Start) || !EntityManager.HasComponent<Game.Net.Node>(ed.m_End)) { return; }
                float3 s = EntityManager.GetComponentData<Game.Net.Node>(ed.m_Start).m_Position;
                float3 en = EntityManager.GetComponentData<Game.Net.Node>(ed.m_End).m_Position;
                var cmd = new NetUpgradeCommand
                {
                    StartX = s.x, StartY = s.y, StartZ = s.z, EndX = en.x, EndY = en.y, EndZ = en.z,
                    General = 0, Left = 0x1000u, Right = 0, // PrimaryBeautification (trees)
                };
                RemoteNetUpgradeQueue.Enqueue(cmd);  // host upgrades
                Command.SendToAll?.Invoke(cmd);       // client upgrades (RECV validation)
                L($"[Auto] TEST upgrade SEND edge={e.Index}");
            }
            finally { ents.Dispose(); }
        }

        /// <summary>2-sim host roteiro: raise terrain locally AND ship it (RECV-flow validation; terrain
        /// height is continuous, not in StateHash — selftest covers the receiver brush).</summary>
        private void SendTerrain()
        {
            if (!TryAnchor(out float3 a)) { L("[Auto] TEST terrain SEND SKIP noAnchor"); return; }
            var pos = new float3(a.x + 250f, a.y, a.z - 250f);
            var cmd = new TerrainCommand { Type = 0, PosX = pos.x, PosY = pos.y, PosZ = pos.z, Size = 40f, Strength = 2000f };
            RemoteTerrainQueue.Enqueue(cmd);     // host raises
            Command.SendToAll?.Invoke(cmd);      // client raises
            L($"[Auto] TEST terrain SEND pos=({pos.x:F0},{pos.z:F0})");
        }

        /// <summary>2-sim host roteiro: nudge a policy adjustment locally AND ship it (RECV-flow; city
        /// policy state is not in StateHash — selftest covers the receiver).</summary>
        private void SendPolicy()
        {
            if (!TryCity(out Entity city) || !EntityManager.HasBuffer<Game.Policies.Policy>(city)) { L("[Auto] TEST policy SEND SKIP noBuffer"); return; }
            DynamicBuffer<Game.Policies.Policy> buf = EntityManager.GetBuffer<Game.Policies.Policy>(city, true);
            if (buf.Length == 0) { L("[Auto] TEST policy SEND SKIP noPolicies"); return; }
            if (!_prefabSystem.TryGetPrefab(buf[0].m_Policy, out PrefabBase pb) || pb == null) { return; }
            bool active = (buf[0].m_Flags & Game.Policies.PolicyFlags.Active) != 0;
            var cmd = new PolicyCommand { PolicyType = pb.GetType().Name, PolicyName = pb.name, Active = active, Adjustment = buf[0].m_Adjustment + 13f };
            RemotePolicyQueue.Enqueue(cmd);      // host
            Command.SendToAll?.Invoke(cmd);      // client
            L($"[Auto] TEST policy SEND name={pb.name}");
        }

        /// <summary>2-sim host roteiro: buy a locked map tile locally AND ship it (both sides own it).</summary>
        private void SendTile()
        {
            EntityQuery locked = GetEntityQuery(ComponentType.ReadOnly<Game.Areas.MapTile>(),
                ComponentType.ReadOnly<Game.Common.Native>(), ComponentType.ReadOnly<Game.Areas.Geometry>());
            if (locked.IsEmptyIgnoreFilter) { L("[Auto] TEST tile SEND SKIP noLockedTiles"); return; }
            NativeArray<Entity> tiles = locked.ToEntityArray(Allocator.Temp);
            try
            {
                var center = EntityManager.GetComponentData<Game.Areas.Geometry>(tiles[0]).m_CenterPosition;
                var cmd = new CS2M.Commands.Data.Game.TilePurchaseCommand { Xs = new[] { center.x }, Zs = new[] { center.z } };
                TileSync.Enqueue(cmd);           // host buys
                Command.SendToAll?.Invoke(cmd);  // client buys
                L($"[Auto] TEST tile SEND center=({center.x:F0},{center.z:F0})");
            }
            finally { tiles.Dispose(); }
        }

        /// <summary>2-sim host roteiro: unlock a dev-tree node locally AND ship it (both sides unlock).</summary>
        private void SendDevTree()
        {
            EntityQuery nodes = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.DevTreeNodeData>());
            NativeArray<Entity> ents = nodes.ToEntityArray(Allocator.Temp);
            Entity node = Entity.Null;
            try
            {
                foreach (Entity n in ents)
                {
                    if (EntityManager.HasComponent<Game.Prefabs.Locked>(n)
                        && EntityManager.IsComponentEnabled<Game.Prefabs.Locked>(n)) { node = n; break; }
                }
            }
            finally { ents.Dispose(); }

            if (node == Entity.Null) { L("[Auto] TEST devtree SEND SKIP noLockedNode"); return; }
            if (!_prefabSystem.TryGetPrefab(node, out PrefabBase p) || p == null) { return; }
            var cmd = new CS2M.Commands.Data.Game.DevTreeCommand { NodeName = p.name };
            RemoteDevTreeQueue.Enqueue(cmd);     // host unlocks
            Command.SendToAll?.Invoke(cmd);      // client unlocks
            L($"[Auto] TEST devtree SEND node={p.name}");
        }

        /// <summary>2-sim host roteiro: create a bus line locally AND ship it, resolved by SyncId on both.
        /// Route CREATION (the reroute path is the registered save-line bug) validated cross-machine.</summary>
        private void SendRoute()
        {
            Entity linePrefab = Entity.Null;
            string ptype = null, pname = null;
            EntityQuery prefabs = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.RouteData>(),
                ComponentType.ReadOnly<Game.Prefabs.TransportLineData>());
            NativeArray<Entity> ents = prefabs.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity p in ents)
                {
                    if (_prefabSystem.TryGetPrefab(p, out PrefabBase pb) && pb != null && pb.name.Contains("Bus"))
                    { linePrefab = p; ptype = pb.GetType().Name; pname = pb.name; break; }
                }
                if (linePrefab == Entity.Null && ents.Length > 0 && _prefabSystem.TryGetPrefab(ents[0], out PrefabBase first) && first != null)
                { linePrefab = ents[0]; ptype = first.GetType().Name; pname = first.name; }
            }
            finally { ents.Dispose(); }

            if (linePrefab == Entity.Null) { L("[Auto] TEST route SEND SKIP noPrefab"); return; }
            if (!TryAnchor(out float3 a)) { L("[Auto] TEST route SEND SKIP noAnchor"); return; }
            ulong id = CS2M_SyncIdSystem.Allocate();
            var cmd = new CS2M.Commands.Data.Game.RouteCreateCommand
            {
                SyncId = id, PrefabType = ptype, PrefabName = pname, Complete = false,
                ColorR = 255, ColorG = 64, ColorB = 32, ColorA = 255, Number = 88,
                WpX = new[] { a.x + 10f, a.x + 90f, a.x + 170f }, WpY = new[] { a.y, a.y, a.y }, WpZ = new[] { a.z + 10f, a.z + 40f, a.z + 10f },
                WpHasConn = new byte[3], WpConnId = new ulong[3], WpConnX = new float[3], WpConnZ = new float[3],
            };
            RouteSync.EnqueueCreate(cmd);     // host creates
            Command.SendToAll?.Invoke(cmd);   // client creates
            L($"[Auto] TEST route SEND prefab={pname} id={id}");
        }

        /// <summary>2-sim host roteiro: ignite the roteiro's SYNCED building locally AND ship it — both
        /// sides resolve the same SyncId. Fire (OnFire) is host-authoritative; this validates the flow.</summary>
        private void SendFire()
        {
            if (_buildingSyncId == 0 || !_idSystem.TryResolve(_buildingSyncId, out Entity e) || !EntityManager.Exists(e)
                || EntityManager.HasComponent<Game.Events.OnFire>(e))
            {
                L("[Auto] TEST fire SEND SKIP building gone/already-onfire");
                return;
            }

            var cmd = new CS2M.Commands.Data.Game.FireSyncCommand { Kind = 0, TargetSyncId = _buildingSyncId, Intensity = 5f };
            FireSync.Enqueue(cmd);           // host ignites
            Command.SendToAll?.Invoke(cmd);  // client ignites (both resolve the same SyncId)
            L($"[Auto] TEST fire SEND syncId={_buildingSyncId}");
        }

        private float3 _stressCenter;

        /// <summary>2-sim host roteiro STRESS: inject the drifting-centre X-crossing on the AUTHORITATIVE
        /// (HasNodes) path on BOTH sims (host applies + ships the same 4 arms). This is the exact path the
        /// junction-overlap fix lives on — the radar confirms both sims fuse it to ONE node deterministically
        /// (a DRIFT here would be a non-determinism bug in FindJunctionNode). Simple SendNet uses the legacy
        /// path; this is the only 2-sim exercise of the HasNodes junction fix.</summary>
        private void SendJunctionStress()
        {
            if (_edgeQuery.IsEmptyIgnoreFilter || !TryAnchor(out float3 anchor)) { L("[Auto] TEST junction-stress SEND SKIP no road/anchor"); return; }
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            string type, name;
            try
            {
                Entity src = ents[0];
                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(src).m_Prefab, out PrefabBase prefab) || prefab == null) { return; }
                type = prefab.GetType().Name; name = prefab.name;
            }
            finally { ents.Dispose(); }

            var c = new float3(anchor.x + 350f, anchor.y, anchor.z + 350f);
            _stressCenter = c;
            var tips = new[] { new float3(c.x, c.y, c.z + 50f), new float3(c.x, c.y, c.z - 50f), new float3(c.x + 50f, c.y, c.z), new float3(c.x - 50f, c.y, c.z) };
            var jitter = new[] { new float3(0f, 0f, 0f), new float3(0.9f, 0f, 0.6f), new float3(-0.7f, 0f, 1.0f), new float3(1.1f, 0f, -0.8f) };
            for (int i = 0; i < 4; i++)
            {
                var cmd = AuthNet(type, name, tips[i], c + jitter[i]);
                RemoteNetQueue.Enqueue(cmd);       // host applies (HasNodes junction fusion)
                Command.SendToAll?.Invoke(cmd);    // client applies the same -> radar checks both fuse to 1 node
            }
            L($"[Auto] TEST junction-stress SEND X-cross 4 arms drifting-centre (HasNodes) at ({c.x:F0},{c.z:F0})");
        }

        /// <summary>2-sim host roteiro STRESS: delete ONE arm of the HasNodes junction on BOTH sims. This
        /// exercises the delete-cascade (RebuildAfterDelete re-caps the junction + cleans the orphan) AND
        /// junction fusion together, cross-machine — a DRIFT would mean the two sims re-cap differently.</summary>
        private void SendStressArmDelete()
        {
            if (_stressCenter.Equals(default(float3))) { return; }
            float3 tip0 = new float3(_stressCenter.x, _stressCenter.y, _stressCenter.z + 50f);
            var cmd = new CS2M.Commands.Data.Game.NetDeleteCommand
            {
                StartX = tip0.x, StartY = tip0.y, StartZ = tip0.z,
                EndX = _stressCenter.x, EndY = _stressCenter.y, EndZ = _stressCenter.z,
            };
            RemoteNetDeleteQueue.Enqueue(cmd);   // host deletes arm (cascade re-caps junction)
            Command.SendToAll?.Invoke(cmd);      // client deletes arm -> both re-cap identically -> radar checks
            L($"[Auto] TEST junction-arm-delete SEND north arm of stress-X at ({_stressCenter.x:F0},{_stressCenter.z:F0})");
        }

        /// <summary>2-sim host roteiro STRESS: the MOVED-junction scenario cross-machine — a junction whose
        /// 3rd road joins 5 m off (>3.5 m). Exercises the 8 m degree>=2 fallback in FindJunctionNode on BOTH
        /// sims; a DRIFT/extra node would mean the +nodes fix doesn't hold cross-machine.</summary>
        private void SendMovedJunctionStress()
        {
            if (_edgeQuery.IsEmptyIgnoreFilter || !TryAnchor(out float3 anchor)) { L("[Auto] TEST moved-junction SEND SKIP no road/anchor"); return; }
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            string type, name;
            try
            {
                Entity src = ents[0];
                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(src).m_Prefab, out PrefabBase prefab) || prefab == null) { return; }
                type = prefab.GetType().Name; name = prefab.name;
            }
            finally { ents.Dispose(); }

            var M = new float3(anchor.x - 500f, anchor.y, anchor.z + 400f); // clear spot
            var Mp = new float3(M.x + 5f, M.y, M.z);
            var cmds = new[]
            {
                AuthNet(type, name, new float3(M.x, M.y, M.z + 50f), M),   // A -> M
                AuthNet(type, name, new float3(M.x + 50f, M.y, M.z), M),   // B -> M (junction degree 2)
                AuthNet(type, name, new float3(M.x, M.y, M.z - 50f), Mp),  // C -> M+5m (must fuse via 8m fallback)
            };
            foreach (var cmd in cmds) { RemoteNetQueue.Enqueue(cmd); Command.SendToAll?.Invoke(cmd); }
            L($"[Auto] TEST moved-junction SEND M=({M.x:F0},{M.z:F0}) + 3rd road to M+5m (8m fallback) cross-machine");
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

            // v55: the JoinNotice sent AT the status transition (WAITING_TO_JOIN / LOADING_MAP->PLAYING)
            // can be dropped before the LiteNetLib link is ready, so the host never gets it, CompletedJoins
            // stays 0, and RunHostStep (the over-the-wire 2-sim roteiro) never fires — the smoke tests were
            // vacuous. Re-announce join true->false ONCE now that we've been PLAYING+stable and the link is up.
            ReAnnounceJoinOnce();

            if (_concurrent) { RunConcurrentStep(false); }
            LogCounts(status.ToString());
        }

        private void ReAnnounceJoinOnce()
        {
            if (_joinReAnnounced) { return; }
            _joinReannounceFrames++;
            if (_joinReannounceFrames == 120)
            {
                Command.SendToAll?.Invoke(new CS2M.Commands.Data.Game.JoinNoticeCommand { Username = "AutoClient", Joining = true });
            }
            else if (_joinReannounceFrames >= 150)
            {
                Command.SendToAll?.Invoke(new CS2M.Commands.Data.Game.JoinNoticeCommand { Username = "AutoClient", Joining = false });
                _joinReAnnounced = true;
                L("[Auto] CLIENT re-announced join (true->false) so the host over-the-wire roteiro can fire");
            }
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
