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

        // CS2M_AP_TEST=3: TRIREPRO — host solo-draws a triangle+diagonal of real (capturable) road,
        // no human, to reproduce the zone-divergence bug automatically. See RunTriReproStep.
        // PHASE1 = the original triangle+mid-span-diagonal+zone repro. PHASE2 "X-CROSS" adds two long
        // straight roads crossing mid-span (both sides — a real X, not a T at a node). PHASE3 "CURVA"
        // adds a 4-control-point S-curve through the same area (see TriRepro_Phase3_Curve doc for why
        // no change to NetToolReplaySystems.cs was needed). PHASE4 "OVERDRAW" redraws exactly over half
        // of a PHASE1 triangle side (same endpoints as an existing edge). PHASE5 paints zoning around
        // the X-CROSS. PHASE6 "FARM" places a real agricultural extractor BUILDING (the same
        // ObjectPlaceCommand primitive the selftest's "object:building" step uses) so the game's own
        // AreaSpawnSystem generates an Extractor work-area field around it — the "campo de fazenda não
        // bate" repro (docs/game-map/dossiers/area.md) — then the final DONE marker every runner/bot
        // greps for is logged by TriRepro_Finish.
        private bool _triRepro;
        private int _triStep;
        private int _triTimer;
        private int _triPhasesDone;
        private string _triRoadType;
        private string _triRoadName;
        private string _triZoneName; // zone name PHASE1/PHASE5 actually painted with — PHASE7 REPAINT
                                      // reads this back so it can pick a DIFFERENT zone for the same cells
                                      // (repainting with the SAME name would never touch Cell.m_Zone at all).
        private float3 _triAnchor;
        private float3 _triOrigin; // chosen free quadrant's origin — every phase derives its offsets
                                    // from this ONE field (see TriRepro_Setup's quadrant search), so
                                    // there is exactly one place that decides where the scene lives.
        private float3 _triA, _triB, _triC, _triMid;
        private ulong _triNodeIdA, _triNodeIdB, _triNodeIdC, _triNodeIdMid;
        private float3 _xcCenter, _xcA1, _xcD1, _xcA2, _xcD2;

        // CS2M_AP_TEST=4: CLIENT-FARM — a SEPARATE scene from TRIREPRO. TRIREPRO's PHASE6 plants its
        // farm HOST-side via the "fabricate an ObjectPlaceCommand + RemotePlacementQueue.EnqueueObject"
        // primitive, which never exercises a genuinely LOCAL client build — the real "campo de fazenda
        // não bate" field report (fix 59fcd25) is specifically about a farm planted BY THE CLIENT. This
        // scene only acts when role==CLIENT (see RunClientFarmStep; the host stays idle — see
        // ClientFarm_HostIdle in UpdateHost). It direct-archetype-instantiates the extractor building
        // LOCALLY on the client (same steps RemotePlacementApplySystem.ApplyOne uses to materialize a
        // remote command) but deliberately WITHOUT CS2M_RemotePlaced and WITH Applied stamped by hand —
        // exactly what NetToolReplaySystems.cs:436 does after driving a headless CreateDefinitionsJob,
        // for the same reason: Applied is the tag PlacementDetectorSystem's _appliedQuery keys off. The
        // ALWAYS-ON PlacementDetectorSystem (not something autopilot drives) then picks this entity up
        // on its own very next OnUpdate, reads the entity's OWN live Transform/PrefabRef/seed to build
        // its OWN ObjectPlaceCommand, and calls Command.SendToAll — which for a CLIENT role resolves to
        // "send to the server" (NetworkInterface.SendToAll -> LocalPlayer.SendToAll; see
        // NetworkInterface.cs:95-98 / CommandInternal.SendToAll), landing on the host's
        // ObjectPlaceHandler -> RemotePlacementQueue -> RemotePlacementApplySystem.ApplyOne, which
        // creates the REMOTE copy there (tagged CS2M_RemotePlaced on the host). That LOCAL-on-client /
        // REMOTE-on-host shape is exactly what the real bug needs to reproduce the area/field
        // divergence when AreaSpawnSystem later grows the Extractor work area on both machines.
        private bool _clientFarm;
        private int _cfStep;
        private int _cfTimer = 120; // ~2s settle after the join handshake before planting
        private bool _cfHostIdleLogged;
        // Set by ClientFarm_Plant once the CreationDefinition/ObjectDefinition batch is submitted;
        // polled every frame by RunClientFarmStep until the REAL building (GenerateObjectsSystem +
        // ApplyObjectsSystem's output) is found, at which point PLACED-REAL logs and this is cleared.
        private Entity _cfPendingPrefab = Entity.Null;
        private float3 _cfPendingPos;
        private string _cfPendingName;
        private bool _cfRealLogged;
        // Set the first frame TryLogClientFarmReal finds the pending Temp entity (pre-Applied) — gates
        // the one-shot "[Auto] CLIENTFARM TEMP" diagnostic so it doesn't spam every poll frame while the
        // Temp entity is still pending promotion.
        private bool _cfTempLogged;
        // The building CreationDefinition entity ClientFarm_Plant creates each SUBMIT — kept around only
        // so TryStripClientFarmWarning can report whether GenerateObjectsSystem has consumed/deleted it
        // yet (definition entities are transient, gone once Modification1 processes them).
        private Entity _cfDefEntity = Entity.Null;

        // CS2M_AP_TEST=5: CLIENT-ACTIONS — a THIRD scene, separate from TRIREPRO (host-solo) and
        // CLIENT-FARM (client-solo), built to exercise two audit gap-fixes that specifically need a
        // CLIENT acting on something the HOST created:
        //   DELFIX (CS2M_DELFIX=1, see WaterDetectorSystem's class doc): the host plants a real water
        //   source LOCALLY (no CS2M_RemotePlaced — mirrors a human's water-tool click, same idea as
        //   ClientFarm_Plant mirroring an Object-tool click), which the ALWAYS-ON WaterDetectorSystem
        //   picks up and ships to the client on its own next OnUpdate — no manual Command.SendToAll
        //   needed here, same reasoning ClientFarm's doc comment gives for PlacementDetectorSystem.
        //   WaterApplySystem materializes the REMOTE copy on the client, tagged CS2M_RemotePlaced. The
        //   client then bulldozes THAT entity (AddComponent<Deleted> — the same local-delete primitive
        //   VerifyWater's own cleanup already uses for water) and, gated CS2M_DELFIX=1, the client's
        //   own WaterDetectorSystem now tracks CS2M_RemotePlaced sources too, so it observes the vanish
        //   and ships a delete back to the host. Off the gate, the client's detector never saw the
        //   source in the first place (query excludes CS2M_RemotePlaced), so the delete is never
        //   observed and the host keeps the source forever — the exact field-reported gap.
        //   TAXFIX (CS2M_TAXFIX=1, see TaxDetectorSystem's class doc): host and client each write a
        //   DIFFERENT index of the SAME live NativeArray TaxSystem.GetTaxRates() returns (exactly what
        //   ActTax/TaxApplySystem already do in production — Game.City.TaxRate.ResidentialOffset=1 for
        //   the host, .CommercialOffset=2 for the client; TaxSystem itself has no decompiled source to
        //   confirm SetTaxRate's accessibility from a concrete-typed field, so this reuses the
        //   ALREADY-PROVEN direct-array route instead of guessing at that API — see
        //   docs/game-map/dossiers/ui-economy.md §7), at roughly the same wall-clock offset from their
        //   own "join settled" signal (host's _testStarted gate / client's _joinReAnnounced — both fire
        //   within ~1-2s of each other in real time, the same assumption RunConcurrentStep already
        //   relies on for its own concurrent-edit race). Off the gate, TaxDetectorSystem broadcasts+
        //   TaxApplySystem overwrites the WHOLE rates array, so whichever change lands second on each
        //   machine stomps the other category back to its stale value; gated on, only the changed
        //   indices travel, so both edits survive on both sides.
        // Convergence for BOTH fixes is read by the existing StateHash (WaterHash/WaterSources + the
        // tax-rates hash, StateHashSystems.cs) — this scene only drives the actions and logs markers;
        // it does not implement its own diff.
        private bool _test5;
        private int _t5HostStep;
        private int _t5HostTimer = 120; // ~2s settle after the join handshake before creating the water
        private float3 _t5WaterPos;
        private bool _t5ClientTaxDone;
        private int _t5ClientTaxTimer = 420; // ~7s after join-settle — same order of magnitude as the
                                              // host's own water+tax schedule below, so the two tax
                                              // writes land close enough in wall-clock time to race.
        private bool _t5ClientWaterDone;
        private int _t5ClientWaterTimer = 300; // ~5s — gives the host's water time to be created,
                                                // detected, sent and applied before polling starts.
        private int _t5ClientWaterPollFrames;
        private bool _t5ClientFinishLogged;

        // CS2M_AP_TEST=6: FIVE-GAP — a single 2-sim scene exercising the gated fixes that had no scene
        // of their own yet: route-reroute (CS2M_ROUTEFIX), building-policy prefab-filter
        // (CS2M_POLICYFIX), devtree negative-balance race (CS2M_DEVTREEFIX), building move w/
        // SubNet/SubArea children + rotation (CS2M_MOVEFIX) and, v60, the legacy-net-path node-position
        // reconciliation (CS2M_NODEHEAL — see RunTest6NodeHeal). All five sub-scenarios are HOST-AUTHORED
        // using the SAME "dual-apply" pattern every other 2-sim Send* helper in this class already uses
        // (RemoteXQueue.Enqueue so the host's OWN world applies exactly what a genuinely remote command
        // would, plus Command.SendToAll so the client applies the identical command) — see
        // SendZone/SendMove/SendPolicy/SendDevTree for the established precedent this scene follows. No
        // client-side SCRIPTED action is needed for any of the five (the client's own ALWAYS-ON apply
        // systems — RouteApplySystem/PolicyApplySystem/DevTreeApplySystem/RemoteEditApplySystem/
        // NetPlaceApplySystem — consume the shipped commands on their own, exactly the code path each fix
        // lives on) EXCEPT one delayed READ: DEVTREEFIX's negative-balance race can only be confirmed by
        // also reading the CLIENT's own DevTreePoints singleton some time after the host's purchase
        // propagates (see RunTest6ClientStep's "TEST6 DEVTREE CLIENT" log) — a host-only read can't tell
        // whether the client's mirrored deduction actually went negative or was floored. Five independent
        // step machines (dev/pol/move/route/nodeheal) run in parallel off one shared join-settle timer;
        // the FINAL "TEST6 DONE" marker only fires once all five report done AND an extra settle window
        // has elapsed — the TEST5 lesson: FINAL must be measured AFTER propagation, never before.
        private bool _test6;
        private int _t6HostTimer = 150; // ~2.5s settle after the join handshake, same order as TEST5's 120f

        private bool _t6DevDone;
        private int _t6DevTimer;
        private int _t6DevStep;              // v60: 0 = force+buy, 1 = delayed FINAL read — see RunTest6Dev
        private int _t6DevBefore;            // forced points balance right before the buy (for the FINAL log)
        private int _t6DevCost;
        private string _t6DevNodeName;

        // v60: the CLIENT's own delayed read of DevTreePoints — see RunTest6ClientStep. Fires once, off a
        // fixed frame count from the client's own first RunTest6ClientStep call (not off any host signal;
        // this process never sees the host's clock) chosen generous enough to cover the host's own
        // settle(150f) + delayed FINAL(300f) plus network round-trip — the same "host/client fire within
        // ~1-2s of each other in real time" assumption TEST5 already established for this file.
        private bool _t6DevClientLogged;
        private int _t6DevClientTimer = 480; // ~8s

        private bool _t6PolDone;
        private int _t6PolStep;
        private int _t6PolTimer = 30;
        private Entity _t6PolBuilding;
        private Entity _t6PolPolicy;
        private string _t6PolPrefabName;
        private string _t6PolPrefabType;
        private ulong _t6PolDecoySyncId;   // v56: real DIFFERENT-prefab decoy the naive proximity-only
                                            // resolve should wrongly prefer — see Test6_PolicySetup
        private float3 _t6PolClickPos;     // deliberately-ambiguous sent coords (NOT the target's own
                                            // exact center) — closer to the decoy than to the target

        private bool _t6MoveDone;
        private int _t6MoveStep;
        private int _t6MoveTimer = 60;
        private Entity _t6MoveBuilding;
        private int _t6MoveFallbackSettle = -1;

        private bool _t6RouteDone;
        private int _t6RouteTimer = 90;
        private int _t6RouteStep;          // v56: 0 = find + baseline settle, 1 = act (move + mark Updated)
        private Entity _t6RouteEntity;
        private string _t6RoutePrefabName;
        private int _t6RouteNumber;

        // v60: NODEHEAL (CS2M_NODEHEAL) — see RunTest6NodeHeal's doc comment for the full scenario.
        private bool _t6NodeHealDone;
        private int _t6NodeHealStep;         // 0 = build road1, 1 = send road2 (id reuse + 15 m offset), 2 = FINAL
        private int _t6NodeHealTimer = 60;   // ~1s own settle on top of the shared _t6HostTimer
        private string _t6NodeHealType;
        private string _t6NodeHealName;
        private ulong _t6NodeHealIdA;        // road1's start node id (fresh, unused after setup — logged for completeness)
        private ulong _t6NodeHealIdB;        // road1's end node id — REUSED by road2's start at a moved position
        private ulong _t6NodeHealIdD;        // road2's own far end (fresh)
        private float3 _t6NodeHealP1Start;
        private float3 _t6NodeHealP1End;     // road1's end position == idB's ORIGINAL position
        private float3 _t6NodeHealP2;        // idB's DECLARED position on road2 == P1End + ~15 m (the drift)

        private bool _t6FinalLogged;
        private int _t6FinalTimer = 600; // ~10s settle after every sub-scenario reports done before DONE

        private bool _t6ClientIdleLogged;

        private GameMode _gameMode = GameMode.Other;
        private PrefabSystem _prefabSystem;
        private CitySystem _citySystem;
        private SimulationSystem _sim;
        private CS2M_SyncIdSystem _idSystem;
        private TaxSystem _taxSystem;
        private CityServiceBudgetSystem _budgetSystem;
        private TerrainSystem _terrain;
        private UpdateSystem _updateSystem;

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
            _triRepro = Environment.GetEnvironmentVariable("CS2M_AP_TEST") == "3";
            _clientFarm = Environment.GetEnvironmentVariable("CS2M_AP_TEST") == "4";
            _test5 = Environment.GetEnvironmentVariable("CS2M_AP_TEST") == "5";
            _test6 = Environment.GetEnvironmentVariable("CS2M_AP_TEST") == "6";

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
            _updateSystem = World.GetOrCreateSystemManaged<UpdateSystem>();

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
                else if (_triRepro)
                {
                    RunTriReproStep();
                }
                else if (_clientFarm)
                {
                    ClientFarm_HostIdle(); // test=4 drives the CLIENT side only — host runs no roteiro
                }
                else if (_test5)
                {
                    RunTest5HostStep();
                }
                else if (_test6)
                {
                    RunTest6HostStep();
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

        /// <summary>Finds any real ZonePrefab (ZoneData) with a non-zero index and a resolvable name — the
        /// FIRST such match in query order, which is why repeated calls (PHASE1, PHASE5) deterministically
        /// return the SAME zone every time. <paramref name="exclude"/> (optional) skips a name already in
        /// use, so PHASE7 REPAINT can ask for a DIFFERENT zone than whatever PHASE1/PHASE5 painted with —
        /// without it, "repaint" would pick the identical zone and never actually rewrite
        /// Cell.m_Zone.</summary>
        private bool TryGetZone(out string name, out ushort index, string exclude = null)
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
                        if (!string.IsNullOrEmpty(exclude) && pb.name == exclude) { continue; }
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

        // ------- CS2M_AP_TEST=3: TRIREPRO — host solo-builds the zone-divergence repro scenario -------
        //
        // Zero-human reproduction of the zone-hash drift: the host draws a triangle of 3 straight "Small
        // Road" segments (corner-connected), then a diagonal from one side's midpoint to the opposite
        // corner (a genuine mid-span T-junction), then paints residential zoning around the side that
        // carries that junction. Roads MUST be born as ordinary LOCAL construction — Applied & Created,
        // WITHOUT CS2M_RemotePlaced and WITHOUT a RemoteNetEcho mark — or NetDetectorSystem/
        // NetBatchCaptureSystem (both gated on exactly that: see their EntityQueryDesc.None lists in
        // NetDetectorSystem.cs / NetBatchCaptureSystem.cs) silently skip them and nothing ships to the
        // client. The two paths that DO create geometry in this file were checked first:
        //   - ActNet() (RemoteNetQueue -> NetPlaceApplySystem.EmitCourse) marks RemoteNetEcho.Mark(segHash)
        //     on every course it emits (NetPlaceApplySystem.cs EmitCourse) — that IS the inject-remote
        //     path (it is the exact receive-side apply a real network command runs through), so its
        //     output is deliberately invisible to the capture systems. Not usable here.
        //   - NetToolReplaySystem.ReplayOne (NetToolReplaySystems.cs) drives the game's OWN
        //     NetToolSystem.CreateDefinitionsJob and stamps CreationFlags.Permanent on the resulting
        //     CreationDefinition — it never touches RemoteNetEcho and never adds CS2M_RemotePlaced. This
        //     is the primitive InjectReplayRoad() already uses (selftest step 0) to prove the job runs;
        //     here we enqueue to RemoteReplayQueue LOCALLY ONLY (no Command.SendToAll for the replay
        //     command itself) so the resulting edges are indistinguishable from a real mouse-driven build.
        //     NetDetectorSystem then captures them next frame (Applied edge, no CS2M_RemotePlaced, no
        //     echo mark) and ships a genuine NetPlaceCommand to the client — the same code path a real
        //     player's road would take, and the one under suspicion for the zone drift.
        //
        // Extended for the "chaotic human drawing" shapes the triangle-only PHASE1 didn't cover (curves,
        // mid-span X crossings, overdraw-on-existing-road, rapid-fire junctions): PHASE2 "X-CROSS" (steps
        // 5-6) draws two ~250m roads crossing mid-span in a fresh quadrant; PHASE3 "CURVA" (step 7) draws
        // a true 4-control-point S-curve through that same area (see TriRepro_Phase3_Curve for why the
        // existing N-control-point replay primitive already covers this with no changes needed to
        // NetToolReplaySystems.cs); PHASE4 "OVERDRAW" (step 8) redraws exactly over half of PHASE1's
        // triangle; PHASE5 (step 9) paints zoning around the X-CROSS; PHASE6 "FARM" (step 10) places a
        // real agricultural extractor building to reproduce the AreaSpawnSystem field-divergence bug;
        // PHASE7 "REPAINT" (step 11) repaints the SAME blocks PHASE1/PHASE5 already zoned, with a
        // DIFFERENT ZonePrefab, to reproduce the user report "quando eu pinto uma zona que JÁ EXISTIA, ela
        // não sinca" (as opposed to painting a fresh Unzoned cell, which PHASE1/PHASE5 already cover);
        // PHASE8 "XSESSION-OVERDRAW" (step 12) redraws exactly over a PRE-EXISTING edge left behind by a
        // PREVIOUS ci round (found by querying Edge+Curve for a midpoint far outside this round's
        // quadrant), at snap=0 on both ends — the "draw a road exactly on top of a road synced in an
        // earlier session" case that round 8's BOUND-MERGE/DUP-MISMATCH fixes (commit ce54b5f) were
        // written for but, until now, never actually exercised (their counters read 0 in every later run);
        // TriRepro_Finish (step 13) logs the final DONE marker.
        private void RunTriReproStep()
        {
            if (_triStep > 13) { return; }
            if (_triTimer > 0) { _triTimer--; return; }
            _triTimer = 180; // ~3s between traces, as requested (phases can override this — see PHASE6)

            switch (_triStep)
            {
                case 0: TriRepro_Setup(); break;
                case 1: TriRepro_Side2(); break;
                case 2: TriRepro_Side3(); break;
                case 3: TriRepro_Diagonal(); break;
                case 4: TriRepro_Phase1_ZoneAndDone(); break;
                case 5: TriRepro_Phase2_XCrossLine1(); break;
                case 6: TriRepro_Phase2_XCrossLine2(); break;
                case 7: TriRepro_Phase3_Curve(); break;
                case 8: TriRepro_Phase4_Overdraw(); break;
                case 9: TriRepro_Phase5_ZoneAndFinish(); break;
                case 10: TriRepro_Phase6_FarmPlace(); break;
                case 11: TriRepro_Phase7_Repaint(); break;
                case 12: TriRepro_Phase8_XSessionOverdraw(); break;
                case 13: TriRepro_Finish(); break;
            }

            _triStep++;
        }

        /// <summary>Step 0: pick the "Small Road" prefab, lay out an equilateral-ish triangle (~200 m
        /// side) in an empty quadrant well clear of CONCURRENT's/RunHostStep's offsets (which top out
        /// around anchor+600), and draw side 1 (A-&gt;B) in open ground (no snap — a fresh dead-end pair
        /// of nodes at both ends, exactly like a player's first drag).</summary>
        private void TriRepro_Setup()
        {
            if (!TryAnchor(out float3 anchor))
            {
                L("[Auto] TRIREPRO SKIP no anchor point in city");
                _triStep = 8; // -> Phase5(SKIP)->Phase6(SKIP)->Phase7(SKIP)->Phase8(SKIP)->Finish(DONE);
                              // RunTriReproStep's trailing _triStep++ makes this land on case 9 (Phase5)
                              // next call, not case 8 again.
                return;
            }

            if (!TryGetRoadPrefab(out _triRoadType, out _triRoadName))
            {
                L("[Auto] TRIREPRO SKIP no Road prefab found");
                _triStep = 8; // same landing as the no-anchor SKIP above
                return;
            }

            _triAnchor = anchor; // reused by every later phase — DON'T re-call TryAnchor once roads
                                  // exist: its edge-query fallback would then pick up OUR OWN edges.

            // Far quadrant, clear of every other scene's offsets (CONCURRENT: anchor+400..560/+400..730;
            // RunHostStep sends top out around anchor+600). ~(anchor.x+900, anchor.z+1200) — the example
            // "perto de (600,1600)" the task gave, shifted so it never overlaps.
            //
            // BUT every CI round leaves its triangle/X-CROSS/curve behind (the save accumulates — nothing
            // here is ever deleted), so re-running against that SAME fixed offset draws right on top of
            // the previous round's roads: raw duplicate edges at snap=0 (no node to fuse onto, since
            // TagNodeNear only looks near where WE expect our own corners) plus identity chaos downstream.
            // PHASE4 already covers "player redraws over an existing road" on purpose, in a controlled way
            // — Setup itself needs genuinely virgin ground. Walk a deterministic quadrant grid instead:
            // candidate k = baseOrigin + (k%8)*(700,0) + (k/8)*(0,700) for k=0,1,2,...  (step 700m is
            // bigger than the ~500x500 the scene spans once PHASE2's X-CROSS +400 X shift is counted, so
            // consecutive quadrants never touch). A quadrant is FREE if no edge (Edge+Curve, no
            // Temp/Deleted — see _edgeQuery) has its curve midpoint within 600m of the scene's rough
            // center (candidate + (150,200), generous radius for the same reason). First free k wins.
            const float side = 200f;
            var baseOrigin = new float3(anchor.x + 900f, anchor.y, anchor.z + 1200f);
            const int maxK = 40;
            const float quadrantStep = 700f;
            const float freeRadius = 600f;
            float3 chosenOrigin = baseOrigin;
            bool foundFree = false;

            for (int k = 0; k < maxK; k++)
            {
                var candidate = baseOrigin + new float3((k % 8) * quadrantStep, 0f, (k / 8) * quadrantStep);
                var sceneCenter = candidate + new float3(150f, 0f, 200f);
                if (IsQuadrantFree(sceneCenter, freeRadius))
                {
                    chosenOrigin = candidate;
                    foundFree = true;
                    L($"[Auto] TRIREPRO quadrant k={k} origem=({candidate.x:F0},{candidate.z:F0})");
                    break;
                }
            }

            if (!foundFree)
            {
                L($"[Auto] TRIREPRO WARN no free quadrant found in {maxK} tries — falling back to k=0 " +
                  "(old behavior: may overdraw on a previous round)");
                chosenOrigin = baseOrigin;
            }

            _triOrigin = chosenOrigin;
            _triA = _triOrigin;
            _triB = _triOrigin + new float3(side, 0f, 0f);
            _triC = _triOrigin + new float3(side * 0.5f, 0f, side * 0.866f); // equilateral apex
            _triMid = (_triA + _triB) * 0.5f;
            _triNodeIdA = 0; _triNodeIdB = 0; _triNodeIdC = 0;

            L($"[Auto] TRIREPRO start road='{_triRoadName}' A=({_triA.x:F0},{_triA.z:F0}) " +
              $"B=({_triB.x:F0},{_triB.z:F0}) C=({_triC.x:F0},{_triC.z:F0})");

            ReplayRoadSegmentLocal(_triRoadType, _triRoadName, _triA, _triB, 5001,
                0, 0, float3.zero, 0, 0, float3.zero);
        }

        /// <summary>Quadrant-freeness check for <see cref="TriRepro_Setup"/>'s virgin-ground search:
        /// true iff no live edge (Edge+Curve, no Temp/Deleted — reuses <c>_edgeQuery</c>, which already
        /// excludes those plus CS2M_RemotePlaced; every TRIREPRO edge is born as ordinary LOCAL
        /// construction, so a previous round's roads DO show up here) has its curve midpoint within
        /// <paramref name="radius"/> meters (XZ only) of <paramref name="center"/>.</summary>
        private bool IsQuadrantFree(float3 center, float radius)
        {
            if (_edgeQuery.IsEmptyIgnoreFilter) { return true; }

            float r2 = radius * radius;
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    Colossal.Mathematics.Bezier4x3 bz = EntityManager.GetComponentData<Game.Net.Curve>(e).m_Bezier;
                    float3 mid = (bz.a + bz.d) * 0.5f;
                    float dx = mid.x - center.x;
                    float dz = mid.z - center.z;
                    if (dx * dx + dz * dz < r2) { return false; }
                }

                return true;
            }
            finally { ents.Dispose(); }
        }

        /// <summary>Step 1: tag the two dead-end nodes side 1 just built (by proximity — this is the
        /// FIRST time they get a stable id, so identity vs. position doesn't matter yet), then draw side
        /// 2 (B-&gt;C) snapped onto the real node at B so it fuses instead of stacking a second node on
        /// top.</summary>
        private void TriRepro_Side2()
        {
            _triNodeIdA = TagNodeNear(_triA, 8f);
            _triNodeIdB = TagNodeNear(_triB, 8f);
            if (_triNodeIdB == 0)
            {
                L("[Auto] TRIREPRO WARN side1 endpoint B not found (open-ground fallback for side2)");
            }

            ReplayRoadSegmentLocal(_triRoadType, _triRoadName, _triB, _triC, 5002,
                _triNodeIdB != 0 ? 1 : 0, _triNodeIdB, _triB, 0, 0, float3.zero);
        }

        /// <summary>Step 2: tag the apex node C, then close the triangle with side 3 (C-&gt;A), snapped
        /// at BOTH ends onto the real nodes so all three corners share exactly one node each (a real
        /// closed triangle, not three coincident-but-separate dead ends).</summary>
        private void TriRepro_Side3()
        {
            _triNodeIdC = TagNodeNear(_triC, 8f);
            if (_triNodeIdC == 0)
            {
                L("[Auto] TRIREPRO WARN side2 endpoint C not found (open-ground fallback for side3 start)");
            }

            ReplayRoadSegmentLocal(_triRoadType, _triRoadName, _triC, _triA, 5003,
                _triNodeIdC != 0 ? 1 : 0, _triNodeIdC, _triC,
                _triNodeIdA != 0 ? 1 : 0, _triNodeIdA, _triA);
        }

        /// <summary>Step 3: cut the interior with a diagonal from the MIDPOINT of side 1 (A-B) to the
        /// apex C — a genuine mid-span snap (kind=2, edge) at one end that forces a real T-junction split
        /// of side 1, and a node snap (kind=1) at the other end onto the existing apex. This is the
        /// topology shape (triangle + a road ending mid-span on one of its sides) that stresses the
        /// legacy receiver's own split/snap guess the most.</summary>
        private void TriRepro_Diagonal()
        {
            ReplayRoadSegmentLocal(_triRoadType, _triRoadName, _triMid, _triC, 5004,
                2, 0, _triMid, // kind=2: nearest EDGE near the midpoint of side A-B
                _triNodeIdC != 0 ? 1 : 0, _triNodeIdC, _triC);
            L($"[Auto] TRIREPRO diagonal mid=({_triMid.x:F0},{_triMid.z:F0}) -> C=({_triC.x:F0},{_triC.z:F0})");
        }

        /// <summary>Step 4: paint residential zoning on whatever zoning blocks landed near side 1 (the
        /// side carrying the mid-span T-junction) — skips gracefully if none exist yet (some net prefabs
        /// have no zoneable variant, or the block hasn't been generated this soon). Then logs the PHASE1
        /// done marker (the scenario continues into PHASE2+ below; the final "TRIREPRO DONE" marker the
        /// operator watches for now comes from <see cref="TriRepro_Phase5_ZoneAndFinish"/>).</summary>
        private void TriRepro_Phase1_ZoneAndDone()
        {
            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);
            if (_blockQuery.IsEmptyIgnoreFilter || !TryGetZone(out string zoneName, out ushort _))
            {
                L("[Auto] TRIREPRO zone SKIP (no blocks or no ZonePrefab yet — flags still diverge even in Unzoned cells)");
            }
            else
            {
                int painted = PaintZoneNear(_triMid, 60f, zoneName);
                _triZoneName = zoneName; // PHASE7 REPAINT needs this to pick a DIFFERENT zone later
                L($"[Auto] TRIREPRO zone painted cells={painted} near side A-B (name='{zoneName}')");
            }

            _triPhasesDone = 1;
            L("[Auto] TRIREPRO PHASE1 DONE");
        }

        /// <summary>Step 5: PHASE2 "X-CROSS" stroke 1 — a ~250 m straight road through
        /// <c>_xcCenter</c> (triangle origin +400 on X, per the task's "quadrante livre deslocado"), open
        /// ground (kind=0) at BOTH ends, diagonal along (1,1)/sqrt(2) so the two X-CROSS strokes meet at
        /// 90°. Nothing here terminates at a node of anything else — this is a genuine mid-span crossing,
        /// not a T at an existing junction, which is exactly the "traço cruzando no MEIO" shape that a
        /// node-guessing receiver (rather than one that replays the real tool job, as this one does) would
        /// get wrong.</summary>
        private void TriRepro_Phase2_XCrossLine1()
        {
            // Triangle origin is _triOrigin (the free quadrant TriRepro_Setup picked); X-CROSS quadrant
            // is that SAME origin shifted +400 on X, well clear of the triangle (side 200) and of side1's
            // diagonal. Deriving from _triOrigin (not re-deriving from _triAnchor) keeps every phase tied
            // to the one quadrant Setup actually chose.
            _xcCenter = new float3(_triOrigin.x + 400f, _triOrigin.y, _triOrigin.z);

            const float half = 88.3883f; // 125 * cos(45deg) -> total segment length 250m
            _xcA1 = _xcCenter + new float3(-half, 0f, -half);
            _xcD1 = _xcCenter + new float3(half, 0f, half);

            ReplayRoadSegmentLocal(_triRoadType, _triRoadName, _xcA1, _xcD1, 5010,
                0, 0, float3.zero, 0, 0, float3.zero);
            L($"[Auto] TRIREPRO PHASE2 X-CROSS line1 center=({_xcCenter.x:F0},{_xcCenter.z:F0}) " +
              $"A=({_xcA1.x:F0},{_xcA1.z:F0}) D=({_xcD1.x:F0},{_xcD1.z:F0})");
        }

        /// <summary>Step 6: PHASE2 stroke 2 — the perpendicular diagonal (1,-1)/sqrt(2) through the SAME
        /// <c>_xcCenter</c>, also ~250 m, also open ground at both ends. Its midpoint sits exactly on top
        /// of stroke 1's midpoint but neither stroke's ENDPOINT is anywhere near the other — the crossing
        /// is purely mid-span on both roads, forcing the receiver to split both at once from the same
        /// intersection-detection pass the real game runs after GenerateEdges (not from our replay code).</summary>
        private void TriRepro_Phase2_XCrossLine2()
        {
            const float half = 88.3883f;
            _xcA2 = _xcCenter + new float3(-half, 0f, half);
            _xcD2 = _xcCenter + new float3(half, 0f, -half);

            ReplayRoadSegmentLocal(_triRoadType, _triRoadName, _xcA2, _xcD2, 5011,
                0, 0, float3.zero, 0, 0, float3.zero);
            _triPhasesDone = 2;
            L($"[Auto] TRIREPRO PHASE2 X-CROSS line2 A=({_xcA2.x:F0},{_xcA2.z:F0}) D=({_xcD2.x:F0},{_xcD2.z:F0})");
            L("[Auto] TRIREPRO PHASE2 X-CROSS DONE");
        }

        /// <summary>Step 7: PHASE3 "CURVA" — an S-shaped road (genuine inflection, not a single bulge)
        /// crossing PHASE2's line1 mid-span, at a point on that line other than the X center (a THIRD,
        /// independent crossing, not just piling onto the same spot).
        ///
        /// Signature check done before writing this (see NetToolReplaySystems.cs): NetToolReplayCommand's
        /// ControlPoint arrays are already fully N-length ("Arrays are parallel and all the same length N
        /// = the control-point count", per that file's class doc), NetToolReplaySystem.ReplayOne loops
        /// `for (i = 0; i &lt; n; i++)` with no length cap, and the game's OWN
        /// NetToolSystem.CreateDefinitionsJob.Execute dispatches on m_ControlPoints.Length itself:
        /// length==4 under Mode.ComplexCurve (decomp NetToolSystem.cs ~2647-2660) calls CreateComplexCurve,
        /// which builds an actual cubic Bezier from all 4 raw control points (decomp ~4315:
        /// `new Bezier4x3(controlPoint, controlPoint2, controlPoint3, controlPoint4)`) — no interpretation
        /// of "drag gesture" involved, just the 4 points we hand it. So the primitive ALREADY accepts 3+
        /// (in fact N) control points and needed ZERO changes to NetToolReplaySystems.cs: this phase ships
        /// Mode=2 (ComplexCurve) with 4 explicit points, start/end open ground, and the two inner points
        /// offset to OPPOSITE sides of the start-end line — that opposite-side offset is what makes the
        /// job produce a true S (one bend one way, one the other) instead of a single-direction bulge.</summary>
        private void TriRepro_Phase3_Curve()
        {
            // Point on X-CROSS line1 at t=60 along its (1,1)/sqrt2 direction (half-length is ~88.4, so
            // this is well inside the segment, ~28m from the X center — a distinct crossing point).
            const float t = 60f;
            const float inv = 0.70710678f; // 1/sqrt(2)
            var crossPoint = new float3(_xcCenter.x + t * inv, _xcCenter.y, _xcCenter.z + t * inv);

            float3 s0 = crossPoint + new float3(0f, 0f, -100f);
            float3 p1 = crossPoint + new float3(40f, 0f, -33f);   // elbow 1: bend to +X
            float3 p2 = crossPoint + new float3(-40f, 0f, 33f);   // elbow 2: bend to -X (opposite -> S)
            float3 s3 = crossPoint + new float3(0f, 0f, 100f);

            ReplayRoadCurveLocal(_triRoadType, _triRoadName, new[] { s0, p1, p2, s3 }, 5012,
                (int) NetToolSystem.Mode.ComplexCurve, 0, 0, float3.zero, 0, 0, float3.zero);
            _triPhasesDone = 3;
            L($"[Auto] TRIREPRO PHASE3 CURVA s0=({s0.x:F0},{s0.z:F0}) p1=({p1.x:F0},{p1.z:F0}) " +
              $"p2=({p2.x:F0},{p2.z:F0}) s3=({s3.x:F0},{s3.z:F0}) crossing X-CROSS line1 at " +
              $"({crossPoint.x:F0},{crossPoint.z:F0})");
            L("[Auto] TRIREPRO PHASE3 CURVA DONE");
        }

        /// <summary>Step 8: PHASE4 "OVERDRAW" — redraw exactly over the A-&gt;mid half of the PHASE1
        /// triangle's side 1 (the half that carries the diagonal's T-junction, split out of the original
        /// A-B edge back in <see cref="TriRepro_Diagonal"/>). Same two endpoints as that existing edge
        /// (node A, node at the mid-span split point) — the "player redraws a road on top of an existing
        /// one" case. Tags the split's node fresh here (it didn't exist yet when PHASE1 ran).</summary>
        private void TriRepro_Phase4_Overdraw()
        {
            _triNodeIdMid = TagNodeNear(_triMid, 8f);
            if (_triNodeIdMid == 0)
            {
                L("[Auto] TRIREPRO PHASE4 WARN mid-split node not found (diagonal may not have split side1) " +
                  "— overdraw falls back to open ground at that end");
            }

            ReplayRoadSegmentLocal(_triRoadType, _triRoadName, _triA, _triMid, 5013,
                _triNodeIdA != 0 ? 1 : 0, _triNodeIdA, _triA,
                _triNodeIdMid != 0 ? 1 : 0, _triNodeIdMid, _triMid);
            _triPhasesDone = 4;
            L($"[Auto] TRIREPRO PHASE4 OVERDRAW A=({_triA.x:F0},{_triA.z:F0}) -> " +
              $"mid=({_triMid.x:F0},{_triMid.z:F0}) (same endpoints as existing side1 A-mid edge)");
            L("[Auto] TRIREPRO PHASE4 OVERDRAW DONE");
        }

        /// <summary>Step 9: PHASE5 — paint zoning around the X-CROSS intersection (reuses the same
        /// <see cref="PaintZoneNear"/> helper PHASE1 used near the triangle). The scenario continues into
        /// PHASE6 (farm building, step 10) and PHASE7 (repaint, step 11); the final "TRIREPRO DONE" marker
        /// now comes from <see cref="TriRepro_Finish"/> (step 12), not from here.</summary>
        private void TriRepro_Phase5_ZoneAndFinish()
        {
            if (_triRoadName == null)
            {
                // Reached here via an early SKIP in TriRepro_Setup (no anchor / no Road prefab) — nothing
                // was ever built, so there is nothing to zone. Log and fall through to PHASE6/Finish.
                L("[Auto] TRIREPRO PHASE5 SKIP (setup never ran — nothing to zone)");
                return;
            }

            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);
            if (_blockQuery.IsEmptyIgnoreFilter || !TryGetZone(out string zoneName, out ushort _))
            {
                L("[Auto] TRIREPRO PHASE5 zone SKIP (no blocks or no ZonePrefab yet)");
            }
            else
            {
                int painted = PaintZoneNear(_xcCenter, 60f, zoneName);
                _triZoneName = zoneName; // same deterministic first-match as PHASE1 — kept here too so
                                          // REPAINT still has a value even if PHASE1 SKIPped (no blocks yet)
                                          // but PHASE5 didn't.
                L($"[Auto] TRIREPRO PHASE5 zone painted cells={painted} near X-CROSS (name='{zoneName}')");
            }

            _triPhasesDone = 5;
        }

        /// <summary>Step 10: PHASE6 "FARM" — place a real resource-extractor BUILDING (BuildingPrefab
        /// whose name contains "Agricultur"/"Farm"/"Extractor") using the exact same primitive the
        /// selftest's "object:building" step exercises: build an <see cref="ObjectPlaceCommand"/> by hand
        /// (SyncId + PrefabType/PrefabName + world transform + seed) and hand it to
        /// <see cref="RemotePlacementQueue"/>.EnqueueObject — see <see cref="InjectObject"/> (line ~622,
        /// used by selftest step 3 "object:building") and <see cref="SendObject"/> (line ~4105, the 2-sim
        /// variant that also does Command.SendToAll) for the two existing call sites of this exact
        /// primitive. RemotePlacementApplySystem.ApplyOne (the queue's only consumer) then
        /// direct-archetype-instantiates the prefab — the same Created+Updated archetype instantiation
        /// path the vanilla ObjectToolSystem/BuildingConstructionSystem uses for a real player build — so
        /// on the client side (over the wire) this is indistinguishable from a mouse-driven placement.
        ///
        /// Placing an extractor building is what makes the game's OWN AreaSpawnSystem
        /// (GameSimulation phase, every 64 frames — decomp Simulation/AreaSpawnSystem.cs:755-758) generate
        /// a Game.Areas.Extractor work-area polygon around it next frame-batch, using its OWN per-process
        /// RandomSeed.Next() (AreaSpawnSystem.cs:811) — the exact non-deterministic step
        /// docs/game-map/dossiers/area.md pins as the suspected root of "campo de fazenda não bate". This
        /// phase only PLACES the building (host-authoritative); it does not touch CS2M_AREASUPPRESS or
        /// any area-shape rewrite — that is what the bug being reproduced is supposed to fix downstream.
        /// Skips gracefully (WARN + no-op, falls through to Finish) if no matching BuildingPrefab exists
        /// in this ruleset build, or if Setup never ran.</summary>
        private void TriRepro_Phase6_FarmPlace()
        {
            if (_triRoadName == null)
            {
                L("[Auto] TRIREPRO PHASE6 FARM SKIP (setup never ran)");
                return;
            }

            if (!TryGetFarmPrefab(out Entity _, out string farmType, out string farmName))
            {
                L("[Auto] TRIREPRO PHASE6 FARM WARN skipped (no Agricultur/Farm/Extractor " +
                  "BuildingPrefab found in this ruleset)");
                return;
            }

            // A quadrant-local spot south-west of _triOrigin: clear of the triangle (PHASE1, east/north
            // of origin), the X-CROSS (PHASE2, origin+400 on X) and the curve (PHASE3, same area as
            // X-CROSS). IsQuadrantFree (built for TriRepro_Setup's virgin-ground search) only checks
            // road-edge midpoints, but that is exactly what surrounds this scene, so it doubles here as a
            // "not sitting on top of one of our own roads" check with a smaller radius (buildings are much
            // smaller than a fresh quadrant). If a previous CI round's farm building already occupies the
            // default spot, walk a small local grid (same shape as Setup's quadrant search, tighter step)
            // until a free spot is found.
            const float placeRadius = 150f;
            float3 candidate = _triOrigin + new float3(200f, 0f, -300f);
            for (int k = 0; k < 8 && !IsQuadrantFree(candidate, placeRadius); k++)
            {
                candidate = _triOrigin + new float3(200f + (k % 4) * 180f, 0f, -300f - (k / 4) * 180f);
            }

            TerrainHeightData hd = _terrain.GetHeightData(true);
            float y = TerrainUtils.SampleHeight(ref hd, candidate);
            var pos = new float3(candidate.x, y, candidate.z);

            var cmd = new ObjectPlaceCommand
            {
                SyncId = CS2M_SyncIdSystem.Allocate(),
                PrefabType = farmType, PrefabName = farmName,
                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                RotX = 0f, RotY = 0f, RotZ = 0f, RotW = 1f, // identity rotation — footprint/RNG matter, not facing
                RandomSeed = 5099,
            };
            L($"[Auto] TRIREPRO PHASE6 farm object INJECT name={cmd.PrefabName} " +
              $"pos=({pos.x:F0},{pos.y:F0},{pos.z:F0}) syncId={cmd.SyncId}");
            Command.SendToAll?.Invoke(cmd);          // client builds the SAME farm over the wire
            RemotePlacementQueue.EnqueueObject(cmd); // host applies too, so BOTH sides spawn a field

            _triPhasesDone = 6;
            L($"[Auto] TRIREPRO FARM PLACED name={farmName} pos=({pos.x:F0},{pos.z:F0}) syncId={cmd.SyncId}");

            // Give AreaSpawnSystem (every 64f) a couple of passes to generate + settle the Extractor
            // field before PHASE7/Finish — 600f (~10s) instead of the usual 180f (~3s) gap.
            _triTimer = 600;
        }

        /// <summary>Step 11: PHASE7 "REPAINT" — the actual bug report this scenario was extended for:
        /// "quando eu pinto uma zona que JÁ EXISTIA, ela não sinca". PHASE1/PHASE5 only ever paint
        /// Unzoned (index 0) cells, which is a DIFFERENT code path from repainting a cell that already
        /// carries a real zone index — this phase is the first one in TRIREPRO to exercise that path.
        ///
        /// It repaints the EXACT SAME region(s) PHASE1 (near side A-B, center <c>_triMid</c>) and PHASE5
        /// (near the X-CROSS, center <c>_xcCenter</c>) already zoned, reusing the identical center+radius
        /// (60 m) pair each of them used. <see cref="PaintZoneNear"/> selects blocks purely by block
        /// POSITION distance to that center — blocks don't move once GenerateEdges creates them — so this
        /// finds the SAME Block entities, and for each one writes CellIndices 0..cells.Length-1 (same as
        /// PHASE1/PHASE5), i.e. every cell in the block again. That is a genuine overwrite of
        /// already-zoned cells, not a paint onto virgin ground.
        ///
        /// The zone name itself is deliberately NOT the one PHASE1/PHASE5 used: repainting a cell with the
        /// SAME zone it already has would be a no-op as far as Cell.m_Zone is concerned and would prove
        /// nothing about the reported bug. <see cref="TryGetZone"/>'s new <c>exclude</c> parameter walks
        /// the same ZoneData registry PHASE1/PHASE5 read but skips <see cref="_triZoneName"/> (the name
        /// they painted with, captured when either phase succeeded), returning the first OTHER real zone
        /// type in this ruleset (e.g. residential -&gt; commercial/office/industrial, whichever sorts
        /// first) — skips gracefully if only one zone type exists.</summary>
        private void TriRepro_Phase7_Repaint()
        {
            if (_triRoadName == null)
            {
                L("[Auto] TRIREPRO PHASE7 REPAINT SKIP (setup never ran)");
                return;
            }

            if (string.IsNullOrEmpty(_triZoneName))
            {
                L("[Auto] TRIREPRO PHASE7 REPAINT SKIP (PHASE1/PHASE5 never painted a zone — nothing to overwrite)");
                return;
            }

            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);
            if (!TryGetZone(out string zoneName, out ushort _, _triZoneName))
            {
                L($"[Auto] TRIREPRO PHASE7 REPAINT SKIP (no OTHER ZonePrefab besides '{_triZoneName}' in this ruleset)");
                return;
            }

            int painted = PaintZoneNear(_triMid, 60f, zoneName);      // same spot as PHASE1
            painted += PaintZoneNear(_xcCenter, 60f, zoneName);       // same spot as PHASE5

            _triPhasesDone = 7;
            L($"[Auto] TRIREPRO REPAINTED zone='{zoneName}' cells={painted}");
        }

        /// <summary>Step 12: PHASE8 "XSESSION-OVERDRAW" — the deliberate exercise of the BOUND-MERGE/
        /// DUP-MISMATCH fixes (commit ce54b5f). Those fixes were born from round 8 stumbling, BY ACCIDENT,
        /// onto "draw a road exactly on top of a road already synced in a PREVIOUS session" (the saved
        /// world accumulates every prior CI round's triangles/X-CROSS/curve — nothing here is ever
        /// deleted, see <see cref="TriRepro_Setup"/>'s quadrant-search comment), and have had their
        /// counters read 0 in every run since (nothing deliberately re-triggers that exact collision).
        /// This phase finds a genuinely PRE-EXISTING edge — one whose curve midpoint sits far outside THIS
        /// round's quadrant (<see cref="_triOrigin"/>), i.e. it can only have been born in an earlier
        /// session — and redraws a fresh road with the SAME two endpoints, snap=0 (open ground) on BOTH
        /// ends: no TagNodeNear help, no snap hint, exactly like a player who free-hand drags a road right
        /// on top of one that's already there. That forces the raw pipeline (NetDetectorSystem capture ->
        /// client apply) to deal with the resulting duplicate/overlapping geometry with zero assistance,
        /// which is precisely the collision BOUND-MERGE/DUP-MISMATCH guard against.</summary>
        private void TriRepro_Phase8_XSessionOverdraw()
        {
            if (_triRoadName == null)
            {
                L("[Auto] TRIREPRO PHASE8 XSESSION-OVERDRAW SKIP (setup never ran)");
                return;
            }

            if (!TryFindPriorSessionEdge(out float3 a, out float3 d))
            {
                L("[Auto] TRIREPRO PHASE8 XSESSION-OVERDRAW WARN no pre-existing edge found " +
                  ">800m from this round's quadrant (fresh world / first CI round?) — skipping");
                return;
            }

            ReplayRoadSegmentLocal(_triRoadType, _triRoadName, a, d, 5014,
                0, 0UL, float3.zero, 0, 0UL, float3.zero);

            _triPhasesDone = 8;
            float3 mid = (a + d) * 0.5f;
            _triTimer = 300;
            L($"[Auto] TRIREPRO XSESSION-OVERDRAW done at=({mid.x:F0},{mid.z:F0})");
        }

        /// <summary>Finds the FIRST live road edge (Edge+Curve, no Temp/Deleted — reuses <c>_edgeQuery</c>,
        /// same query <see cref="IsQuadrantFree"/> uses, which already excludes CS2M_RemotePlaced; every
        /// TRIREPRO edge in every round is born as ordinary LOCAL construction, so a previous round's
        /// roads DO show up here) whose curve midpoint is farther than 800m (XZ only) from
        /// <see cref="_triOrigin"/> — i.e. outside THIS round's quadrant, so it can only be a leftover from
        /// an earlier session. Returns that edge's raw Bezier endpoints (<c>m_Bezier.a</c>/<c>.d</c>)
        /// unmodified — no snap, no nearest-node search — so the caller redraws the exact same span.
        /// Returns false (a/d left at default) if no candidate qualifies.</summary>
        private bool TryFindPriorSessionEdge(out float3 a, out float3 d)
        {
            a = default;
            d = default;
            if (_edgeQuery.IsEmptyIgnoreFilter) { return false; }

            const float minDist2 = 800f * 800f;
            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    Colossal.Mathematics.Bezier4x3 bz = EntityManager.GetComponentData<Game.Net.Curve>(e).m_Bezier;
                    float3 mid = (bz.a + bz.d) * 0.5f;
                    float dx = mid.x - _triOrigin.x;
                    float dz = mid.z - _triOrigin.z;
                    if (dx * dx + dz * dz <= minDist2) { continue; } // still inside THIS round's quadrant

                    a = bz.a;
                    d = bz.d;
                    return true;
                }

                return false;
            }
            finally { ents.Dispose(); }
        }

        /// <summary>Step 13: final step of the TRIREPRO scenario — logs the "TRIREPRO DONE" marker every
        /// runner/bot greps for (moved here from the old PHASE5, then past PHASE6/PHASE7/PHASE8, so the
        /// farm placement's 600-frame settle wait, the PHASE7 repaint and the PHASE8 cross-session
        /// overdraw all run BEFORE the scenario is declared done), plus the phase-count summary line.</summary>
        private void TriRepro_Finish()
        {
            L("[Auto] TRIREPRO DONE");
            L($"[Auto] TRIREPRO PHASES={_triPhasesDone}");
        }

        /// <summary>Any live NetGeometryData prefab named "Small Road" (falls back to any non-invisible
        /// "Road" prefab if that exact name isn't in this ruleset build).</summary>
        private bool TryGetRoadPrefab(out string type, out string name)
        {
            type = null;
            name = null;
            string fbType = null, fbName = null;
            EntityQuery q = GetEntityQuery(ComponentType.ReadOnly<NetGeometryData>());
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_prefabSystem.TryGetPrefab(e, out PrefabBase pb) || pb == null) { continue; }
                    if (pb.name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                    if (pb.name.IndexOf("Small Road", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        type = pb.GetType().Name;
                        name = pb.name;
                        return true;
                    }

                    if (fbName == null && pb.name.IndexOf("Road", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fbType = pb.GetType().Name;
                        fbName = pb.name;
                    }
                }
            }
            finally { ents.Dispose(); }

            if (fbName == null) { return false; }
            type = fbType;
            name = fbName;
            return true;
        }

        /// <summary>Any live BuildingPrefab (Game.Prefabs.BuildingData query, mirroring TryGetRoadPrefab's
        /// NetGeometryData query for nets) whose name contains "Agricultur"/"Farm"/"Extractor" — the
        /// vanilla resource-extractor buildings that own a Game.Areas.Extractor work area grown by
        /// AreaSpawnSystem (docs/game-map/dossiers/area.md). Logs every candidate found (via the
        /// "[Auto] TRIREPRO farm prefab=" line, per the task's requirement) and returns the FIRST one that
        /// also has a valid placeable ObjectData archetype (what RemotePlacementApplySystem.ApplyOne
        /// needs to direct-archetype-instantiate it).</summary>
        private bool TryGetFarmPrefab(out Entity prefabEntity, out string type, out string name)
        {
            prefabEntity = Entity.Null;
            type = null;
            name = null;
            EntityQuery q = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.BuildingData>());
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_prefabSystem.TryGetPrefab(e, out PrefabBase pb) || pb == null) { continue; }

                    bool matches =
                        pb.name.IndexOf("Agricultur", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pb.name.IndexOf("Farm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pb.name.IndexOf("Extractor", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matches) { continue; }

                    L($"[Auto] TRIREPRO farm prefab='{pb.name}' type={pb.GetType().Name}");

                    if (type != null) { continue; } // already locked in the first VALID candidate below —
                                                      // keep looping only to finish logging the rest

                    if (!EntityManager.HasComponent<Game.Prefabs.ObjectData>(e)) { continue; }
                    Game.Prefabs.ObjectData od = EntityManager.GetComponentData<Game.Prefabs.ObjectData>(e);
                    if (!od.m_Archetype.Valid) { continue; }

                    prefabEntity = e;
                    type = pb.GetType().Name;
                    name = pb.name;
                }
            }
            finally { ents.Dispose(); }

            return type != null;
        }

        /// <summary>Enqueues a straight 2-point NetToolReplayCommand into RemoteReplayQueue LOCALLY (never
        /// Command.SendToAll for this command itself — see the RunTriReproStep header comment for why).
        /// NetToolReplaySystem drives the real CreateDefinitionsJob off it next frame, which is what makes
        /// the resulting edge indistinguishable from a mouse-driven build. Optional per-endpoint snap:
        /// kind 0=none (open ground), 1=node (by stable id), 2=edge (nearest edge to snapPos).</summary>
        private void ReplayRoadSegmentLocal(string type, string name, float3 a, float3 d, int seed,
            int startSnapKind, ulong startSnapNodeId, float3 startSnapPos,
            int endSnapKind, ulong endSnapNodeId, float3 endSnapPos)
        {
            float3 dir = math.normalizesafe(d - a, new float3(1f, 0f, 0f));
            quaternion rot = quaternion.LookRotationSafe(new float3(dir.x, 0f, dir.z), math.up());
            var cmd = new NetToolReplayCommand
            {
                PrefabType = type, PrefabName = name,
                Mode = 0, RandomSeed = seed, EditorMode = false, LeftHandTraffic = false,
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
                SnapPosX = new[] { startSnapPos.x, endSnapPos.x },
                SnapPosZ = new[] { startSnapPos.z, endSnapPos.z },
                SnapKind = new[] { startSnapKind, endSnapKind },
                SnapNodeId = new[] { startSnapNodeId, endSnapNodeId },
            };
            RemoteReplayQueue.Enqueue(cmd); // HOST-LOCAL ONLY — see header comment
            L($"[Auto] TRIREPRO road-replay name={name} A=({a.x:F0},{a.z:F0}) D=({d.x:F0},{d.z:F0}) " +
              $"snap=({startSnapKind}:{startSnapNodeId},{endSnapKind}:{endSnapNodeId})");
        }

        /// <summary>PHASE3-only generalization of <see cref="ReplayRoadSegmentLocal"/> to N &gt;= 2 control
        /// points (needed for curves — see <see cref="TriRepro_Phase3_Curve"/>'s doc for why
        /// NetToolReplayCommand/NetToolReplaySystem already support this with no changes to
        /// NetToolReplaySystems.cs). Only the FIRST and LAST points carry snap info (open ground/node/edge,
        /// same 3 kinds as the 2-point helper); every interior point is open ground, and its direction is
        /// estimated from its neighbours (central difference) instead of the single start-&gt;end direction
        /// the straight helper uses, so mid-curve pylons/lanes get a sane per-point tangent.</summary>
        private void ReplayRoadCurveLocal(string type, string name, float3[] pts, int seed, int mode,
            int startSnapKind, ulong startSnapNodeId, float3 startSnapPos,
            int endSnapKind, ulong endSnapNodeId, float3 endSnapPos)
        {
            int n = pts.Length;
            var posX = new float[n]; var posY = new float[n]; var posZ = new float[n];
            var dirX = new float[n]; var dirZ = new float[n];
            var hitDirX = new float[n]; var hitDirZ = new float[n];
            var rotX = new float[n]; var rotY = new float[n]; var rotZ = new float[n]; var rotW = new float[n];
            var snapPriX = new float[n]; var snapPriY = new float[n];
            var elemIdxX = new int[n]; var elemIdxY = new int[n];
            var curvePos = new float[n]; var elev = new float[n];
            var snapPosX = new float[n]; var snapPosZ = new float[n];
            var snapKind = new int[n]; var snapNodeId = new ulong[n];

            for (int i = 0; i < n; i++)
            {
                float3 p = pts[i];
                float3 prev = i > 0 ? pts[i - 1] : pts[i];
                float3 next = i < n - 1 ? pts[i + 1] : pts[i];
                float3 dir = math.normalizesafe(next - prev, new float3(1f, 0f, 0f));
                quaternion rot = quaternion.LookRotationSafe(new float3(dir.x, 0f, dir.z), math.up());

                posX[i] = p.x; posY[i] = p.y; posZ[i] = p.z;
                dirX[i] = dir.x; dirZ[i] = dir.z;
                hitDirX[i] = dir.x; hitDirZ[i] = dir.z;
                rotX[i] = rot.value.x; rotY[i] = rot.value.y; rotZ[i] = rot.value.z; rotW[i] = rot.value.w;
                elemIdxX[i] = -1; elemIdxY[i] = -1;
                snapKind[i] = 0; // interior points are always open ground
                snapPosX[i] = 0f; snapPosZ[i] = 0f; snapNodeId[i] = 0UL;
            }

            snapKind[0] = startSnapKind; snapNodeId[0] = startSnapNodeId;
            snapPosX[0] = startSnapPos.x; snapPosZ[0] = startSnapPos.z;
            snapKind[n - 1] = endSnapKind; snapNodeId[n - 1] = endSnapNodeId;
            snapPosX[n - 1] = endSnapPos.x; snapPosZ[n - 1] = endSnapPos.z;

            var cmd = new NetToolReplayCommand
            {
                PrefabType = type, PrefabName = name,
                Mode = mode, RandomSeed = seed, EditorMode = false, LeftHandTraffic = false,
                RemoveUpgrade = false, ParallelOffset = 0f, ParallelCount = 0,
                PosX = posX, PosY = posY, PosZ = posZ,
                HitX = posX, HitY = posY, HitZ = posZ,
                DirX = dirX, DirZ = dirZ,
                HitDirX = hitDirX, HitDirY = new float[n], HitDirZ = hitDirZ,
                RotX = rotX, RotY = rotY, RotZ = rotZ, RotW = rotW,
                SnapPriX = snapPriX, SnapPriY = snapPriY,
                ElemIdxX = elemIdxX, ElemIdxY = elemIdxY,
                CurvePos = curvePos, Elev = elev,
                SnapPosX = snapPosX, SnapPosZ = snapPosZ,
                SnapKind = snapKind, SnapNodeId = snapNodeId,
            };
            RemoteReplayQueue.Enqueue(cmd); // HOST-LOCAL ONLY — see ReplayRoadSegmentLocal's header comment
            L($"[Auto] TRIREPRO curve-replay name={name} points={n} mode={mode} " +
              $"snap=({startSnapKind}:{startSnapNodeId},{endSnapKind}:{endSnapNodeId})");
        }

        /// <summary>Finds the live net Node nearest <paramref name="pos"/> within <paramref name="maxDist"/>
        /// and Ensures it a stable CS2M_NodeSyncId (idempotent), returning that id (0 if none nearby).</summary>
        private ulong TagNodeNear(float3 pos, float maxDist)
        {
            EntityQuery nodesQ = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Node>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            NativeArray<Entity> ents = nodesQ.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestSq = maxDist * maxDist;
                foreach (Entity e in ents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Net.Node>(e).m_Position;
                    float dx = p.x - pos.x, dz = p.z - pos.z;
                    float dsq = dx * dx + dz * dz;
                    if (dsq < bestSq) { bestSq = dsq; best = e; }
                }

                return best != Entity.Null ? CS2M_NodeSyncIds.Ensure(EntityManager, best) : 0UL;
            }
            finally { ents.Dispose(); }
        }

        /// <summary>Paints <paramref name="zoneName"/> on every cell of every zoning block within
        /// <paramref name="radius"/> of <paramref name="center"/>, both applying locally AND shipping to
        /// the client (same dual dispatch as every other 2-sim host roteiro in this file, e.g. SendZone).
        /// Returns the number of cells painted (0 if no block was in range).</summary>
        private int PaintZoneNear(float3 center, float radius, string zoneName)
        {
            int painted = 0;
            float rSq = radius * radius;
            NativeArray<Entity> blocks = _blockQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity block in blocks)
                {
                    Block b = EntityManager.GetComponentData<Block>(block);
                    float dx = b.m_Position.x - center.x, dz = b.m_Position.z - center.z;
                    if (dx * dx + dz * dz > rSq) { continue; }

                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(block, true);
                    if (cells.Length == 0) { continue; }

                    var idx = new int[cells.Length];
                    var names = new string[cells.Length];
                    for (int i = 0; i < cells.Length; i++) { idx[i] = i; names[i] = zoneName; }

                    var cmd = new ZonePaintCommand
                    {
                        BlockX = b.m_Position.x, BlockZ = b.m_Position.z,
                        DirX = b.m_Direction.x, DirZ = b.m_Direction.y,
                        SizeX = b.m_Size.x, SizeY = b.m_Size.y,
                        CellIndices = idx, ZoneNames = names,
                    };
                    RemoteZoneQueue.Enqueue(cmd);      // host paints
                    Command.SendToAll?.Invoke(cmd);    // client paints -> StateHash checks zone convergence
                    painted += cells.Length;
                }
            }
            finally { blocks.Dispose(); }

            return painted;
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

        // ------------------------- CS2M_AP_TEST=4: CLIENT-FARM (client-only scene) -------------------------

        /// <summary>Host side of the CLIENT-FARM scene: intentionally does nothing scripted. The whole
        /// point of this scene is to isolate "client plants a farm -> host receives it as remote" with no
        /// other host-authored traffic in flight, so a divergence can only come from that one build. The
        /// host's normal ALWAYS-ON systems (RemotePlacementApplySystem, PlacementDetectorSystem,
        /// AreaSpawnSystem…) keep running exactly as they would for a real human host — only the
        /// autopilot's own scripted roteiro (RunHostStep/RunConcurrentStep/RunTriReproStep) is suppressed
        /// here.</summary>
        private void ClientFarm_HostIdle()
        {
            if (_cfHostIdleLogged) { return; }
            _cfHostIdleLogged = true;
            L("[Auto] CLIENTFARM host idle (test=4 drives the CLIENT side only — waiting for the " +
              "client's farm to arrive as a normal remote ObjectPlaceCommand)");
        }

        /// <summary>Client-side pacing for the CLIENT-FARM scene: step 0 plants the farm locally once the
        /// join handshake has fully settled (<see cref="_joinReAnnounced"/> — same signal RunHostStep's
        /// 2-sim roteiro waits on), then step 1 (~900f / ~15s later, per the task's spec — long enough for
        /// the host to receive+apply the remote build, AreaSpawnSystem (every 64f) to grow the Extractor
        /// work area on both machines, and any area-sync rewrite to settle) logs the DONE marker every
        /// runner/bot greps for.</summary>
        private void RunClientFarmStep()
        {
            if (_cfStep > 1 || !_joinReAnnounced) { return; }

            // Independent of the step timer below: once step 0 has submitted the CreationDefinition
            // batch, poll every frame for the REAL entity GenerateObjectsSystem+ApplyObjectsSystem
            // materialize from it (proof this scene drove the actual tool pipeline, not a stand-in).
            if (_cfPendingPrefab != Entity.Null && !_cfRealLogged)
            {
                TryLogClientFarmReal();
            }

            if (_cfTimer > 0) { _cfTimer--; return; }
            _cfTimer = 900;

            switch (_cfStep)
            {
                case 0: ClientFarm_Plant(); break;
                case 1: ClientFarm_Finish(); break;
            }

            _cfStep++;
        }

        /// <summary>Looks for the real building entity (PrefabRef+Transform+Applied, no CreationDefinition
        /// — i.e. past GenerateObjectsSystem/ApplyObjectsSystem, not the transient definition entity)
        /// matching <see cref="_cfPendingPrefab"/> near <see cref="_cfPendingPos"/>. Logs PLACED-REAL the
        /// first frame it's found — this is the proof the client's local build went through the same
        /// GenerateObjectsSystem→FindOwnersSystem→ApplyObjectsSystem pipeline a human's Object-tool click
        /// does (and, by construction, that AreaSpawnSystem now has a real Extractor building + work-area
        /// Lot to grow crops in — see ClientFarm_Plant's doc comment for the full chain).
        ///
        /// Before searching, this PUMPS <c>SystemUpdatePhase.ApplyTool</c> by hand
        /// (<see cref="_updateSystem"/>.Update(ApplyTool)) — without this the Temp entities
        /// GenerateObjectsSystem/GenerateAreasSystem create from our hand-built definitions would sit as
        /// Temp FOREVER and PLACED-REAL would never fire. Root cause (confirmed in decomp, not the
        /// ApplyObjectsSystem-gates-on-tool-state hypothesis this scene's own doc originally assumed):
        /// <c>ToolOutputSystem.OnUpdate</c> (decomp/Game/Game/Tools/ToolOutputSystem.cs:19-31) is what
        /// actually invokes the ClearTool/ApplyTool phase groups each frame, and it does so via a
        /// <c>switch (m_ToolSystem.applyMode)</c> that only pumps <c>ApplyTool</c> when
        /// <c>ApplyMode.Apply</c> and only pumps <c>ClearTool</c> when <c>ApplyMode.Clear</c> — for
        /// <c>ApplyMode.None</c> (ToolSystem.cs:149-159: <c>m_LastTool == null</c> or whatever
        /// <c>ToolBaseSystem.applyMode</c> the last-active tool reports, ApplyMode.None by default with no
        /// tool ever activated) it pumps NEITHER. ApplyObjectsSystem/ApplyAreasSystem themselves
        /// (ApplyObjectsSystem.cs:911-927, ApplyAreasSystem.cs:~273) have no applyMode/tool-state gate at
        /// all — they just RequireForUpdate on a Temp query and run unconditionally whenever scheduled;
        /// they simply never GET scheduled because ToolOutputSystem never asks for the ApplyTool phase
        /// with no tool active. Modification1..ModificationEnd (GenerateObjectsSystem, GenerateAreasSystem,
        /// FindOwnersSystem, ValidationSystem) are NOT phase-gated this way and run every frame regardless,
        /// which is why SUBMIT always logs but PLACED-REAL never did: the Temp entities got created and
        /// validated, then stranded — ToolClearSystem (decomp/Game/Game/Tools/ToolClearSystem.cs:218-220,
        /// same ToolOutputSystem gate) never ran either, so they weren't even cleaned up, just silently
        /// accumulated as orphaned Temp entities every SUBMIT.
        ///
        /// Calling <c>UpdateSystem.Update(SystemUpdatePhase.ApplyTool)</c> directly (public API,
        /// decomp/Game/Game/UpdateSystem.cs:183-221) is exactly what ToolOutputSystem itself does — it
        /// just runs every system registered at that phase (SystemOrder.cs:710-719: ToolApplySystem,
        /// ApplyZonesSystem, ApplyObjectsSystem, ApplyNetSystem, ApplyNotificationsSystem, ApplyAreasSystem,
        /// ApplyBrushesSystem, ApplyRoutesSystem, ApplyWaterSourcesSystem). Each has its own
        /// RequireForUpdate on a Temp-shaped query, so calling this when nothing is pending (most frames,
        /// before GenerateObjectsSystem has run for a given SUBMIT, or after PLACED-REAL already fired) is
        /// a cheap no-op — safe to call every frame from this poll. ApplyObjectsSystem's Create() path
        /// (ApplyObjectsSystem.cs:669-685) both strips Temp AND adds Applied+Created+Updated in the same
        /// pass for a fresh build, so no separate ClearTool pump is needed afterward.
        ///
        /// UPDATE (last harness gap): the ApplyTool pump above is necessary but was NOT sufficient — the
        /// Temp entity still never reached Applied. Root cause, confirmed in decomp (not the
        /// ApplyObjectsSystem-skips-errors hypothesis this scene originally assumed — ApplyObjectsSystem
        /// never once reads Error/ErrorSeverity; grep decomp/Game/Game/Tools/ApplyObjectsSystem.cs, zero
        /// hits): the farm/extractor <c>BuildingPrefab</c> carries <c>Game.Prefabs.BuildingFlags.RequireRoad</c>,
        /// and this scene's hand-built definition (deliberately a freestanding "brand new top-level
        /// object", no Attach/Owner — see <see cref="ClientFarm_Plant"/>'s doc comment) never sets
        /// <c>Building.m_RoadEdge</c>. <c>Game.Buildings.ValidationHelpers.ValidateBuilding</c>
        /// (decomp/Game/Game/Buildings/ValidationHelpers.cs:16-33) enqueues <c>ErrorType.NoRoadAccess</c> at
        /// <c>ErrorSeverity.Warning</c> every single frame the Temp exists (ValidationSystem runs every
        /// Modification2, unconditionally — same "not phase-gated" category as GenerateObjectsSystem).
        /// ValidationSystem's <c>UpdateComponentsJob</c> (ValidationSystem.cs, the
        /// <c>case ErrorSeverity.Warning</c> branch around line 557) turns that into a literal
        /// <c>Game.Tools.Warning</c> TAG COMPONENT added directly onto the Temp entity (not just an icon).
        /// <c>ToolApplySystem</c> — scheduled FIRST in the ApplyTool phase group, BEFORE ApplyObjectsSystem
        /// (SystemOrder.cs:711 vs :713) — then does, unconditionally, for any chunk whose archetype carries
        /// Warning: <c>if (archetypeChunk.Has(ref m_WarningType)) { m_CommandBuffer.AddComponent&lt;Deleted&gt;
        /// (nativeArray); }</c> (ToolApplySystem.cs:50-53). Both systems queue into the SAME
        /// ToolOutputBarrier ECB, so by the time it's played back the entity has BOTH Deleted (from
        /// ToolApplySystem) AND Applied/Created/Updated (from ApplyObjectsSystem.Create()) — it never
        /// survives as a stable Applied building, which is exactly why PLACED-REAL never fired even after
        /// the ApplyTool pump fix. <see cref="TryStripClientFarmWarning"/> (called first thing below, every
        /// poll frame — Warning gets re-added every frame as long as the Temp exists, since m_RoadEdge
        /// never changes) removes the Warning tag before ToolApplySystem can see it. NoRoadAccess is a
        /// soft/cosmetic warning in real play too (players routinely plant buildings ahead of road
        /// construction — the building just sits unconnected until a road reaches it), so stripping it for
        /// THIS scene's own entity (matched by prefab+position, same tolerance as the PLACED-REAL match
        /// below) is safe and narrowly scoped — not a blanket "ignore all validation" change.</summary>
        private void TryLogClientFarmReal()
        {
            TryStripClientFarmWarning();
            _updateSystem.Update(SystemUpdatePhase.ApplyTool);

            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                None = new[] { ComponentType.ReadOnly<Game.Tools.CreationDefinition>() },
            });

            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(e).m_Prefab != _cfPendingPrefab) { continue; }

                    float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                    float dx = p.x - _cfPendingPos.x, dz = p.z - _cfPendingPos.z;
                    if (dx * dx + dz * dz > 4f) { continue; } // 2m — same building, not a coincidence

                    _cfRealLogged = true;
                    L($"[Auto] CLIENTFARM PLACED-REAL name={_cfPendingName} " +
                      $"pos=({p.x:F0},{p.y:F0},{p.z:F0}) entity={e.Index} (materialized by " +
                      "GenerateObjectsSystem/ApplyObjectsSystem — real tool pipeline, PlacementDetectorSystem " +
                      "will ship it to the host next ModificationEnd)");
                    return;
                }
            }
            finally { ents.Dispose(); }
        }

        /// <summary>Instrumentation + fix, called first thing every poll frame by
        /// <see cref="TryLogClientFarmReal"/> (before its ApplyTool pump): finds the pending building's
        /// Temp entity — matched the same way PLACED-REAL matches the post-Applied entity (PrefabRef ==
        /// <see cref="_cfPendingPrefab"/>, position within 2m of <see cref="_cfPendingPos"/>), except this
        /// query has NO Applied/None-CreationDefinition filter so it also finds the entity while it's
        /// still mid-pipeline. First frame it's found, logs
        /// <c>[Auto] CLIENTFARM TEMP entity=N flags=0x... comps=A,B,C defAlive=bool</c> — TempFlags in hex,
        /// every component type currently on the entity (proof of exactly what ValidationSystem/
        /// GenerateObjectsSystem attached, no guessing), and whether <see cref="_cfDefEntity"/> (the
        /// CreationDefinition entity from ClientFarm_Plant) still exists. Then, every frame (not just the
        /// first — see below), strips <c>Game.Tools.Warning</c> if present: see
        /// <see cref="TryLogClientFarmReal"/>'s doc comment for the full decomp trail on why a bare Warning
        /// tag here is fatal (ToolApplySystem.cs:50-53 deletes the whole chunk before ApplyObjectsSystem
        /// can promote it to Applied) and why it's safe to remove for this scene's own entity. The strip
        /// must be unconditional every poll frame, not one-shot, because ValidationSystem
        /// (Modification2, unconditional) re-enqueues ErrorType.NoRoadAccess — and therefore re-adds
        /// Warning — every single frame the entity is still Temp, since <c>Building.m_RoadEdge</c> never
        /// changes (this scene never attaches the definition to a road).</summary>
        private void TryStripClientFarmWarning()
        {
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
            });

            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(e).m_Prefab != _cfPendingPrefab) { continue; }

                    float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                    float dx = p.x - _cfPendingPos.x, dz = p.z - _cfPendingPos.z;
                    if (dx * dx + dz * dz > 4f) { continue; } // 2m — same building, not a coincidence

                    if (!_cfTempLogged)
                    {
                        _cfTempLogged = true;
                        Game.Tools.Temp temp = EntityManager.GetComponentData<Game.Tools.Temp>(e);

                        NativeArray<ComponentType> comps = EntityManager.GetComponentTypes(e, Allocator.Temp);
                        var names = new System.Text.StringBuilder();
                        for (int i = 0; i < comps.Length; i++)
                        {
                            if (i > 0) { names.Append(','); }
                            System.Type mt = comps[i].GetManagedType();
                            names.Append(mt != null ? mt.Name : comps[i].ToString());
                        }
                        comps.Dispose();

                        bool defAlive = _cfDefEntity != Entity.Null && EntityManager.Exists(_cfDefEntity);
                        L($"[Auto] CLIENTFARM TEMP entity={e.Index} flags=0x{(uint)temp.m_Flags:X} " +
                          $"comps={names} defAlive={defAlive}");
                    }

                    if (EntityManager.HasComponent<Game.Tools.Warning>(e))
                    {
                        EntityManager.RemoveComponent<Game.Tools.Warning>(e);
                    }

                    return;
                }
            }
            finally { ents.Dispose(); }
        }

        /// <summary>Step 0: plants a real agricultural extractor BUILDING LOCALLY on the client the SAME
        /// way a human player's Object-tool click does — by submitting the definition entities
        /// <c>ObjectToolBaseSystem.CreateDefinitionsJob</c> would have produced (that job itself is a
        /// PRIVATE nested struct — decomp/Game/Game/Tools/ObjectToolBaseSystem.cs:35 — so unlike
        /// NetToolReplaySystem (which drives NetToolSystem's PUBLIC CreateDefinitionsJob directly), it
        /// can't be invoked here; this hand-builds the same definition SHAPE instead) — and letting the
        /// game's own systems consume them:
        ///   1. <c>CreationDefinition</c> + <c>ObjectDefinition</c> + <c>Updated</c> on a bare entity
        ///      (mirrors ObjectToolBaseSystem.cs:1095-1251's UpdateObject, "brand new top-level object"
        ///      branch: no Original/Owner/Attach/Upgrade/Relocate — a freestanding new build) →
        ///      <c>GenerateObjectsSystem</c> (decomp/Game/Game/Tools/GenerateObjectsSystem.cs:1696-1715,
        ///      query All&lt;CreationDefinition,Updated&gt;/Any&lt;ObjectDefinition,NetCourse&gt;) creates the
        ///      REAL building entity from <c>ObjectData.m_Archetype</c> with <c>Temp(TempFlags.Create)</c>
        ///      (GenerateObjectsSystem.cs:1077,1225-1266 — Permanent is deliberately NOT set, unlike
        ///      NetToolReplaySystem, so this stays on the pipeline's normal rails instead of bypassing it).
        ///   2. One <c>CreationDefinition</c> + <c>OwnerDefinition</c> + <c>Game.Areas.Node</c> buffer per
        ///      entry in the prefab's <c>Game.Prefabs.SubArea</c>/<c>SubAreaNode</c> buffers (mirrors
        ///      ObjectToolBaseSystem.cs:1926-2010's UpdateSubAreas) → <c>GenerateAreasSystem</c>
        ///      (SystemOrder.cs:98, same Modification1 phase) creates the work-area Lot, Temp too.
        ///      THIS is the piece the old direct-archetype version skipped entirely — without it
        ///      <c>AreaSpawnSystem</c> (decomp/Game/Game/Simulation/AreaSpawnSystem.cs:768-770: query
        ///      All&lt;Area,Geometry,SubObject&gt;, RequireForUpdate) has no Area entity to grow crops in at
        ///      all, which is why the old scene never spawned an Extractor field.
        ///   3. <c>FindOwnersSystem</c> (SystemOrder.cs:137, Modification3 — same frame, after both of the
        ///      above) resolves the area's OwnerDefinition back to the building by EXACT prefab+position+
        ///      rotation match against Temp candidates (FindOwnersSystem.cs:78-86) — this is why the
        ///      building's ObjectDefinition and the area's OwnerDefinition below share the literal same
        ///      <c>pos</c>/<c>rot</c> values, and why the building must ALSO still be Temp at this point
        ///      (mixing a direct-Applied building with a definition-based area would flag-mismatch here
        ///      and leave the area unowned).
        ///   4. <c>ApplyObjectsSystem</c>/<c>ApplyAreasSystem</c> (SystemOrder.cs:713,716, ApplyTool phase,
        ///      same frame, after FindOwnersSystem) promote both Temp entities to Applied+Created+Updated
        ///      (ApplyObjectsSystem.cs:432,669-685) — exactly what PlacementDetectorSystem's
        ///      <c>_appliedQuery</c> requires, with Temp itself stripped by end of frame (ClearTool phase)
        ///      so the query's <c>None: Temp</c> is satisfied by the time ModificationEnd next sees it.
        ///
        /// No custom "apply" stand-in system is needed here (unlike NetToolReplayApplySystem for roads):
        /// because Permanent is never set, the definitions stay on the STOCK apply pipeline the whole way.
        ///
        /// Prefab lookup reuses <see cref="TryGetFarmPrefab"/>. Position: a fixed spot far from every
        /// other autopilot scene's offsets, reusing <see cref="IsQuadrantFree"/> to walk a small local
        /// grid if a previous CI round's farm already sits there. <see cref="TryLogClientFarmReal"/>
        /// (polled every frame by <see cref="RunClientFarmStep"/>) confirms the real building actually
        /// materializes before logging PLACED-REAL.</summary>
        private void ClientFarm_Plant()
        {
            if (!TryAnchor(out float3 anchor))
            {
                L("[Auto] CLIENTFARM SKIP no anchor point in city");
                return;
            }

            if (!TryGetFarmPrefab(out Entity prefabEntity, out string farmType, out string farmName))
            {
                L("[Auto] CLIENTFARM SKIP no Agricultur/Farm/Extractor BuildingPrefab found in this ruleset");
                return;
            }

            const float placeRadius = 150f;
            float3 candidate = anchor + new float3(1500f, 0f, -300f);
            for (int k = 0; k < 8 && !IsQuadrantFree(candidate, placeRadius); k++)
            {
                candidate = anchor + new float3(1500f + (k % 4) * 180f, 0f, -300f - (k / 4) * 180f);
            }

            if (!EntityManager.HasComponent<Game.Prefabs.ObjectData>(prefabEntity))
            {
                L($"[Auto] CLIENTFARM SKIP prefab {farmName} has no ObjectData/archetype");
                return;
            }

            Game.Prefabs.ObjectData objectData = EntityManager.GetComponentData<Game.Prefabs.ObjectData>(prefabEntity);
            if (!objectData.m_Archetype.Valid)
            {
                L($"[Auto] CLIENTFARM SKIP prefab {farmName} archetype is invalid");
                return;
            }

            TerrainHeightData hd = _terrain.GetHeightData(true);
            float y = TerrainUtils.SampleHeight(ref hd, candidate);
            var pos = new float3(candidate.x, y, candidate.z);
            // Identity rotation matters beyond "facing": FindOwnersSystem below resolves ownership by
            // EXACT float equality of position AND rotation between the building's ObjectDefinition and
            // the area's OwnerDefinition, and the area-node transform math further down assumes
            // world = pos + local (valid only because rot is identity — see that comment).
            quaternion rot = quaternion.identity;

            // ---- 1. Building definition — GenerateObjectsSystem consumes this next Modification1 ----
            var buildCd = default(Game.Tools.CreationDefinition);
            buildCd.m_Prefab = prefabEntity;
            buildCd.m_RandomSeed = 5199;

            var buildOd = default(Game.Tools.ObjectDefinition);
            buildOd.m_Position = pos;
            buildOd.m_Rotation = rot;
            buildOd.m_LocalPosition = pos;
            buildOd.m_LocalRotation = rot;
            buildOd.m_Scale = new float3(1f, 1f, 1f);
            buildOd.m_Intensity = 1f;
            buildOd.m_Probability = 100;
            buildOd.m_PrefabSubIndex = -1;
            buildOd.m_ParentMesh = -1; // ground building, not attached to a parent mesh/net
            buildOd.m_Elevation = 0f;  // flat terrain (height already baked into pos.y above)

            Entity buildDef = EntityManager.CreateEntity();
            EntityManager.AddComponentData(buildDef, buildCd);
            EntityManager.AddComponentData(buildDef, buildOd);
            EntityManager.AddComponent<Updated>(buildDef);
            _cfDefEntity = buildDef;

            // ---- 2. Work-area definition(s) from the prefab's own SubArea/SubAreaNode template ----
            var ownerDef = default(Game.Tools.OwnerDefinition);
            ownerDef.m_Prefab = prefabEntity;
            ownerDef.m_Position = pos;
            ownerDef.m_Rotation = rot;

            int areasCreated = 0;
            if (EntityManager.HasBuffer<Game.Prefabs.SubArea>(prefabEntity)
                && EntityManager.HasBuffer<Game.Prefabs.SubAreaNode>(prefabEntity))
            {
                DynamicBuffer<Game.Prefabs.SubArea> subAreas =
                    EntityManager.GetBuffer<Game.Prefabs.SubArea>(prefabEntity);
                DynamicBuffer<Game.Prefabs.SubAreaNode> subAreaNodes =
                    EntityManager.GetBuffer<Game.Prefabs.SubAreaNode>(prefabEntity);

                for (int i = 0; i < subAreas.Length; i++)
                {
                    Game.Prefabs.SubArea subArea = subAreas[i];
                    Entity areaPrefab = subArea.m_Prefab;
                    if (areaPrefab == Entity.Null) { continue; }

                    // Some work-area prefabs are a placeholder LIST (crop-type visual variants) instead of
                    // one concrete area prefab; the real tool weighted-randomly picks one via
                    // AreaUtils.SelectAreaPrefab (undecompiled/inaccessible from here — see class doc).
                    // Deterministically taking the first variant gives the same shape/behavior for
                    // AreaSpawnSystem; only the crop-texture RNG differs, which this scene doesn't verify.
                    if (EntityManager.HasBuffer<Game.Prefabs.PlaceholderObjectElement>(areaPrefab))
                    {
                        DynamicBuffer<Game.Prefabs.PlaceholderObjectElement> placeholders =
                            EntityManager.GetBuffer<Game.Prefabs.PlaceholderObjectElement>(areaPrefab);
                        if (placeholders.Length == 0) { continue; }
                        areaPrefab = placeholders[0].m_Object;
                    }

                    var areaCd = default(Game.Tools.CreationDefinition);
                    areaCd.m_Prefab = areaPrefab;
                    areaCd.m_RandomSeed = 5200 + i;
                    if (EntityManager.HasComponent<Game.Prefabs.AreaGeometryData>(areaPrefab))
                    {
                        Game.Prefabs.AreaGeometryData geo =
                            EntityManager.GetComponentData<Game.Prefabs.AreaGeometryData>(areaPrefab);
                        if (geo.m_Type != Game.Areas.AreaType.Lot)
                        {
                            areaCd.m_Flags |= Game.Tools.CreationFlags.Hidden;
                        }
                    }

                    int start = subArea.m_NodeRange.x;
                    int end = subArea.m_NodeRange.y;
                    int count = end - start + 1;
                    if (count <= 0 || end >= subAreaNodes.Length) { continue; }

                    Entity areaDef = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(areaDef, areaCd);
                    EntityManager.AddComponentData(areaDef, ownerDef);
                    EntityManager.AddComponent<Updated>(areaDef);

                    DynamicBuffer<Game.Areas.Node> nodeBuf = EntityManager.AddBuffer<Game.Areas.Node>(areaDef);
                    nodeBuf.ResizeUninitialized(count);
                    for (int j = start; j <= end; j++)
                    {
                        Game.Prefabs.SubAreaNode localNode = subAreaNodes[j];
                        // rot is identity here, so world = pos + local (no math.mul needed).
                        float3 worldPos = pos + localNode.m_Position;
                        float elevation = localNode.m_ParentMesh >= 0 ? localNode.m_Position.y : float.MinValue;
                        nodeBuf[j - start] = new Game.Areas.Node(worldPos, elevation);
                    }

                    areasCreated++;
                }
            }

            _cfPendingPrefab = prefabEntity;
            _cfPendingPos = pos;
            _cfPendingName = farmName;
            _cfRealLogged = false;
            _cfTempLogged = false;

            L($"[Auto] CLIENTFARM SUBMIT name={farmName} pos=({pos.x:F0},{pos.y:F0},{pos.z:F0}) " +
              $"areaDefs={areasCreated} defEntity={buildDef.Index} (CreationDefinition/ObjectDefinition " +
              "submitted — GenerateObjectsSystem+GenerateAreasSystem materialize the real building+field " +
              "next Modification1, same as a player's Object-tool click)");
        }

        /// <summary>Step 1: final marker every runner/bot greps for. By now (~15s after PLACED) the host
        /// should have received the client's build over the wire (RemotePlacementApplySystem.ApplyOne,
        /// tagged CS2M_RemotePlaced there), and AreaSpawnSystem should have grown an Extractor work area
        /// around it independently on BOTH machines — the actual convergence this scene exists to
        /// exercise. This phase only logs; comparing the two sides' state is the job of whatever drove
        /// this scene (bot/StateHash), same as every other autopilot scenario in this file.</summary>
        private void ClientFarm_Finish()
        {
            L("[Auto] CLIENTFARM DONE");
        }

        // ------------------------- CS2M_AP_TEST=5: CLIENT-ACTIONS (DELFIX + TAXFIX) -------------------------

        /// <summary>Host side of the CLIENT-ACTIONS scene. Step 0 plants a real water source LOCALLY
        /// (once the 2-peer join gate that guards every non-selftest roteiro — see UpdateHost — is
        /// healthy); step 1 bumps the RESIDENTIAL tax-rate index; step 2 logs the final read-back plus
        /// the "TEST5 DONE" marker every runner/bot greps for (mirrors TriRepro_Finish's "TRIREPRO
        /// DONE"). Both actions are genuinely LOCAL — see the class-level TEST5 doc comment (near
        /// _test5's declaration) for why no manual Command.SendToAll call is needed for either one.</summary>
        private void RunTest5HostStep()
        {
            if (_t5HostStep > 2) { return; }
            if (_t5HostTimer > 0) { _t5HostTimer--; return; }

            switch (_t5HostStep)
            {
                case 0: Test5_HostCreateWater(); _t5HostTimer = 300; break;
                case 1: Test5_HostSetTax(); _t5HostTimer = 900; break;
                case 2: Test5_HostFinish(); break;
            }

            _t5HostStep++;
        }

        /// <summary>Plants a plain WaterSourceData+Transform entity — the same shape
        /// <c>WaterApplySystem.ApplyOne</c> materializes for a REMOTE command, minus
        /// CS2M_RemotePlaced (this is the host's own LOCAL action, not something that arrived over the
        /// wire). WaterSourceInitializeSystem only fixes up entities that ALSO carry PrefabRef
        /// (decomp/Game/Game/Simulation/WaterSourceInitializeSystem.cs:87 requires it in its query), so
        /// — exactly like ApplyOne — every field the sim needs is stamped by hand here, including
        /// m_Modifier=1f (without it the source's effective radius is zero, per ApplyOne's own comment).
        /// Position: own quadrant, far from every other scene's offsets (TRIREPRO's quadrant search,
        /// CLIENT-FARM's anchor+(1500,-300), CONCURRENT's anchor+(400,400+i*30) all live much closer in).</summary>
        private void Test5_HostCreateWater()
        {
            if (!TryAnchor(out float3 anchor))
            {
                L("[Auto] TEST5 SKIP no anchor point in city (water)");
                return;
            }

            var target = anchor + new float3(-1800f, 0f, 1200f);
            TerrainHeightData hd = _terrain.GetHeightData(true);
            float y = TerrainUtils.SampleHeight(ref hd, target);
            var pos = new float3(target.x, y, target.z);

            Entity e = EntityManager.CreateEntity();
            EntityManager.AddComponentData(e, new Game.Simulation.WaterSourceData
            {
                m_Radius = 20f,
                m_Height = 5f,
                m_Multiplier = 1f,
                m_Polluted = 0f,
                m_ConstantDepth = 0,
                m_Modifier = 1f,
            });
            EntityManager.AddComponentData(e, new Game.Objects.Transform(pos, quaternion.identity));
            EntityManager.AddComponent<Created>(e);
            EntityManager.AddComponent<Updated>(e);

            _t5WaterPos = pos;
            L($"[Auto] TEST5 HOST-CREATED-WATER pos=({pos.x:F0},{pos.y:F0},{pos.z:F0}) entity={e.Index}");
        }

        /// <summary>Bumps the RESIDENTIAL slot of the SAME live NativeArray TaxDetectorSystem diffs
        /// (<c>TaxSystem.GetTaxRates()</c>) — index <c>Game.City.TaxRate.ResidentialOffset</c>=1, per
        /// decomp/Game/Game/City/TaxRate.cs. This is the exact primitive ActTax/TaxApplySystem already
        /// use in production (index 0 = Main there); TaxSystem itself has no decompiled source to
        /// confirm a concrete-typed SetTaxRate/GetTaxRate call would even compile (see the class-level
        /// TEST5 doc comment), so this scene deliberately stays on the already-proven route.</summary>
        private void Test5_HostSetTax()
        {
            NativeArray<int> rates = _taxSystem.GetTaxRates();
            int idx = (int)TaxRate.ResidentialOffset;
            if (!rates.IsCreated || rates.Length <= idx)
            {
                L("[Auto] TEST5 HOST-TAX SKIP no tax rates");
                return;
            }

            int before = rates[idx];
            int after = before + 3;
            rates[idx] = after; // live array write — TaxDetectorSystem sees this exactly like a slider drag
            L($"[Auto] TEST5 HOST-TAX res={after} (was {before}, idx={idx})");
        }

        /// <summary>Final step: reads back both tax indices and the live water-source count near the
        /// spot TEST5 planted (host's own local view only — the real cross-machine convergence check is
        /// StateHash's job, see the class-level TEST5 doc comment), then logs the DONE marker.</summary>
        private void Test5_HostFinish()
        {
            NativeArray<int> rates = _taxSystem.GetTaxRates();
            int res = rates.IsCreated && rates.Length > (int)TaxRate.ResidentialOffset ? rates[(int)TaxRate.ResidentialOffset] : int.MinValue;
            int com = rates.IsCreated && rates.Length > (int)TaxRate.CommercialOffset ? rates[(int)TaxRate.CommercialOffset] : int.MinValue;
            int waterNear = CountWaterNear(_t5WaterPos, 2500f);
            L($"[Auto] TEST5 FINAL host res={res} com={com} waterSourcesNear={waterNear}");
            L("[Auto] TEST5 DONE");
        }

        /// <summary>Counts live (non-Temp/Deleted) water sources within <paramref name="radius"/> of
        /// <paramref name="center"/> — used only for the host's own FINAL log line above; the real
        /// convergence check is StateHash's WaterHash/WaterSources (StateHashSystems.cs).</summary>
        private int CountWaterNear(float3 center, float radius)
        {
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Simulation.WaterSourceData>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            float r2 = radius * radius;
            int n = 0;
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                    float dx = p.x - center.x, dz = p.z - center.z;
                    if (dx * dx + dz * dz <= r2) { n++; }
                }
            }
            finally { ents.Dispose(); }

            return n;
        }

        /// <summary>Client side of the CLIENT-ACTIONS scene: two INDEPENDENT timers, not a linear step
        /// machine — the whole point is that the tax edit and the water delete are unrelated and should
        /// race the host's own actions, not queue behind each other. Gated the same way CLIENT-FARM
        /// gates its own pacing (<see cref="_joinReAnnounced"/>).</summary>
        private void RunTest5ClientStep()
        {
            if (!_joinReAnnounced) { return; }

            if (!_t5ClientTaxDone)
            {
                if (_t5ClientTaxTimer > 0) { _t5ClientTaxTimer--; }
                else { Test5_ClientSetTax(); _t5ClientTaxDone = true; }
            }

            if (!_t5ClientWaterDone)
            {
                if (_t5ClientWaterTimer > 0) { _t5ClientWaterTimer--; }
                else { Test5_ClientTryDeleteWater(); }
            }

            if (_t5ClientTaxDone && _t5ClientWaterDone && !_t5ClientFinishLogged)
            {
                _t5ClientFinishLogged = true;
                NativeArray<int> rates = _taxSystem.GetTaxRates();
                int res = rates.IsCreated && rates.Length > (int)TaxRate.ResidentialOffset ? rates[(int)TaxRate.ResidentialOffset] : int.MinValue;
                int com = rates.IsCreated && rates.Length > (int)TaxRate.CommercialOffset ? rates[(int)TaxRate.CommercialOffset] : int.MinValue;
                L($"[Auto] TEST5 FINAL client res={res} com={com}");
                L("[Auto] TEST5 CLIENT DONE");
            }
        }

        /// <summary>Bumps the COMMERCIAL slot (the host bumps RESIDENTIAL — see Test5_HostSetTax) of the
        /// same live tax-rates array, at roughly the same wall-clock offset from its own join-settled
        /// signal as the host's — close enough in real time (loopback/LAN latency is a few ms) to race
        /// the two edits, which is the whole point of TAXFIX.</summary>
        private void Test5_ClientSetTax()
        {
            NativeArray<int> rates = _taxSystem.GetTaxRates();
            int idx = (int)TaxRate.CommercialOffset;
            if (!rates.IsCreated || rates.Length <= idx)
            {
                L("[Auto] TEST5 CLIENT-TAX SKIP no tax rates");
                return;
            }

            int before = rates[idx];
            int after = before - 3;
            rates[idx] = after;
            L($"[Auto] TEST5 CLIENT-TAX com={after} (was {before}, idx={idx})");
        }

        /// <summary>Polls (every frame, once the initial settle timer above elapses) for the water
        /// source <see cref="Test5_HostCreateWater"/> planted on the host — identified the same way
        /// CleanupOrphanTestWater finds stray test sources: WaterSourceData + CS2M_RemotePlaced, no
        /// Temp/Deleted (this is a fresh scene, so the first such entity found IS the host's). Deletes
        /// it the same way a bulldoze does (AddComponent&lt;Deleted&gt; — the exact primitive
        /// VerifyWater's own cleanup already uses for water). Gives up with a WARN after ~30s so the
        /// scene can't stall forever if the host's water never arrived (a missing arrival would be a
        /// CREATE-path problem, not the delete-propagation gap this scene targets).</summary>
        private void Test5_ClientTryDeleteWater()
        {
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Simulation.WaterSourceData>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            if (q.IsEmptyIgnoreFilter)
            {
                if (++_t5ClientWaterPollFrames >= 1800) // ~30s
                {
                    L("[Auto] TEST5 CLIENT WARN no remote water source arrived after 30s — giving up");
                    _t5ClientWaterDone = true;
                }

                return;
            }

            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            Entity target = ents[0];
            float3 pos = EntityManager.HasComponent<Game.Objects.Transform>(target)
                ? EntityManager.GetComponentData<Game.Objects.Transform>(target).m_Position
                : float3.zero;
            ents.Dispose();

            EntityManager.AddComponent<Deleted>(target);
            _t5ClientWaterDone = true;
            L($"[Auto] TEST5 CLIENT-DELETED-WATER pos=({pos.x:F0},{pos.y:F0},{pos.z:F0}) entity={target.Index}");
        }

        // ------------------------- CS2M_AP_TEST=6: FIVE-GAP (ROUTEFIX/POLICYFIX/DEVTREEFIX/MOVEFIX/NODEHEAL) -------------------------

        /// <summary>Host driver: one shared join-settle timer, then five independent step machines run
        /// every frame in parallel (they touch disjoint entities, so there is no ordering dependency
        /// between them). Logs the "TEST6 DONE" marker only once ALL FIVE report done AND an extra
        /// settle window has elapsed, so the client's apply systems have had time to consume every
        /// shipped command before anything downstream reads the client's state as final.</summary>
        private void RunTest6HostStep()
        {
            if (_t6HostTimer > 0) { _t6HostTimer--; return; }

            if (!_t6DevDone) { RunTest6Dev(); }
            if (!_t6PolDone) { RunTest6Policy(); }
            if (!_t6MoveDone) { RunTest6Move(); }
            if (!_t6RouteDone) { RunTest6Route(); }
            if (!_t6NodeHealDone) { RunTest6NodeHeal(); }

            if (_t6DevDone && _t6PolDone && _t6MoveDone && _t6RouteDone && _t6NodeHealDone && !_t6FinalLogged)
            {
                if (_t6FinalTimer > 0) { _t6FinalTimer--; return; }
                _t6FinalLogged = true;
                L("[Auto] TEST6 DONE");
            }
        }

        /// <summary>DEVTREE sub-scenario (CS2M_DEVTREEFIX): buys a locked node the SAME way the normal
        /// roteiro's SendDevTree does (host applies via the real RemoteDevTreeQueue/DevTreeApplySystem
        /// primitive, then ships the identical command), then reads back the points balance so a runner
        /// can confirm it doesn't get floored to 0 when the mirrored XP hasn't crossed the milestone yet
        /// on one side — see DevTreeApplySystem's class doc for the exact race.
        ///
        /// v56: a fresh/near-fresh save has the host sitting at DevTreePoints=0, so a 0-cost-or-covered
        /// purchase floors to 0 whether or not CS2M_DEVTREEFIX is on — 0-cost=0 either way, never
        /// exercising the no-floor branch. Force a real negative-balance race the same way the doc
        /// comment on DevTreeFix describes it happening naturally (mirrored purchase lands before the
        /// mirrored XP crosses the milestone): set DevTreePoints — the EXACT singleton
        /// DevTreeApplySystem.ApplyOne itself reads/writes — to LESS than the chosen node's cost right
        /// before buying, so the deduction goes negative with the fix on (or is silently floored/erased
        /// with it off).
        ///
        /// v60 FIX: the old version read the balance back IMMEDIATELY after Enqueue/SendToAll — but
        /// RemoteDevTreeQueue is drained by DevTreeApplySystem's OWN OnUpdate, a DIFFERENT system that may
        /// not have run yet this frame (or even this tick, depending on system-group order relative to
        /// AutopilotSystem). Two live runs measured "2->2" — a no-op read that never saw the deduction,
        /// so the scene validated nothing. Split into two steps: buy, then a DELAYED read ~5s later — the
        /// same "measure after propagation, never before" lesson TEST5/TEST6-ROUTE already teach this
        /// file — and log DevTreeFix's live gate value at read time so a runner knows which floor/negative
        /// outcome to expect without cross-referencing source.</summary>
        private void RunTest6Dev()
        {
            if (_t6DevTimer > 0) { _t6DevTimer--; return; }

            if (_t6DevStep == 0) { Test6_DevBuy(); return; }
            if (_t6DevStep == 1) { Test6_DevFinal(); }
        }

        private void Test6_DevBuy()
        {
            EntityQuery nodes = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.DevTreeNodeData>());
            Entity node = Entity.Null;
            NativeArray<Entity> ents = nodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity n in ents)
                {
                    if (EntityManager.HasComponent<Game.Prefabs.Locked>(n)
                        && EntityManager.IsComponentEnabled<Game.Prefabs.Locked>(n))
                    {
                        node = n;
                        break;
                    }
                }
            }
            finally { ents.Dispose(); }

            if (node == Entity.Null)
            {
                L("[Auto] TEST6 DEVTREE SKIP no locked node left to buy in this save");
                _t6DevDone = true;
                return;
            }

            if (!_prefabSystem.TryGetPrefab(node, out PrefabBase p) || p == null)
            {
                L("[Auto] TEST6 DEVTREE SKIP no prefab for locked node");
                _t6DevDone = true;
                return;
            }

            int cost = EntityManager.HasComponent<Game.Prefabs.DevTreeNodeData>(node)
                ? EntityManager.GetComponentData<Game.Prefabs.DevTreeNodeData>(node).m_Cost
                : 0;
            if (cost <= 0)
            {
                L($"[Auto] TEST6 DEVTREE SKIP node={p.name} has cost={cost} — can't force the no-floor " +
                  "path without a positive cost (0-0 floors to 0 with or without the fix)");
                _t6DevDone = true;
                return;
            }

            EntityQuery pointsQuery = GetEntityQuery(ComponentType.ReadWrite<DevTreePoints>());
            if (pointsQuery.IsEmptyIgnoreFilter)
            {
                L("[Auto] TEST6 DEVTREE SKIP no DevTreePoints singleton in this save");
                _t6DevDone = true;
                return;
            }

            int before = pointsQuery.GetSingleton<DevTreePoints>().m_Points;
            int forcedPoints = Math.Max(1, cost / 2); // strictly less than cost -> deduction MUST go negative
            pointsQuery.SetSingleton(new DevTreePoints { m_Points = forcedPoints });
            L($"[Auto] TEST6 DEVTREE forced points {before}->{forcedPoints} (cost={cost}) node={p.name} " +
              $"gate CS2M_DEVTREEFIX={DevTreeFix.Enabled}");

            var cmd = new CS2M.Commands.Data.Game.DevTreeCommand { NodeName = p.name };
            RemoteDevTreeQueue.Enqueue(cmd);     // host buys (same primitive SendDevTree/DevTreeApplySystem use)
            Command.SendToAll?.Invoke(cmd);      // client buys -> exercises DEVTREEFIX's negative-balance path

            _t6DevNodeName = p.name;
            _t6DevCost = cost;
            _t6DevBefore = forcedPoints;
            _t6DevStep = 1;
            _t6DevTimer = 300; // ~5s — see v60 doc above: DevTreeApplySystem drains the queue on its own
                                // OnUpdate, not synchronously here, so reading immediately can miss the
                                // deduction entirely ("2->2").
        }

        private void Test6_DevFinal()
        {
            EntityQuery pointsQuery = GetEntityQuery(ComponentType.ReadOnly<DevTreePoints>());
            int finalPoints = pointsQuery.IsEmptyIgnoreFilter
                ? int.MinValue
                : pointsQuery.GetSingleton<DevTreePoints>().m_Points;

            L($"[Auto] TEST6 DEVTREE FINAL hostPoints={finalPoints} forcedBefore={_t6DevBefore} " +
              $"cost={_t6DevCost} node={_t6DevNodeName} gate={DevTreeFix.Enabled} " +
              $"expectFloorAt0={!DevTreeFix.Enabled} expectNegative={DevTreeFix.Enabled}");
            _t6DevDone = true;
        }

        /// <summary>POLICY sub-scenario (CS2M_POLICYFIX): toggles a BUILDING-scope policy (TargetKind=1)
        /// on a native (save-loaded, no CS2M_SyncId) target, sent with a DELIBERATELY-ambiguous click
        /// position so the naive proximity-only resolve (PolicyApplySystem.ResolveTarget's legacy
        /// fallback) picks the WRONG building unless PolicyFix's prefab filter is on.
        ///
        /// v56: the old version placed a SAME-prefab neighbor and sent the TARGET's own exact
        /// coordinates — since exact coords always win proximity (dist=0 beats any neighbor a few
        /// meters away) AND a same-prefab neighbor can never be excluded by the prefab filter anyway
        /// (both candidates match the filter, so fix-on and fix-off would pick the identical winner),
        /// that setup could never expose the gap: <see cref="PolicyFix"/>.Enabled changing the outcome
        /// requires a candidate that proximity-alone would wrongly prefer AND that prefab-filtering
        /// correctly excludes — i.e. a DIFFERENT-prefab decoy placed CLOSER to the sent click than the
        /// real (correct-prefab) target. Step 0 finds the target plus a real different-prefab building
        /// already in this save to use as the decoy's prefab, plants a real decoy instance of it near a
        /// deliberately-offset click point (dual-apply), and settles. Step 1 raises the real
        /// {Event,Modify} pair PoliciesUISystem.ModifyPolicy raises for a UI click (decomp-confirmed
        /// shape), addressed at the ambiguous click — not the target's own center. Step 2 reads back
        /// each candidate's real <c>Game.Policies.Policy</c> buffer (the same buffer
        /// BuildingModifierInitializeSystem/ModifiedSystem maintain) to confirm which one the policy
        /// actually landed on.</summary>
        private void RunTest6Policy()
        {
            if (_t6PolTimer > 0) { _t6PolTimer--; return; }

            if (_t6PolStep == 0)
            {
                Test6_PolicySetup();
                _t6PolTimer = 120;
                _t6PolStep = 1;
                return;
            }

            if (_t6PolStep == 1)
            {
                Test6_PolicyAct();
                _t6PolTimer = 60;
                _t6PolStep = 2;
                return;
            }

            if (_t6PolStep == 2)
            {
                Test6_PolicyVerify();
            }
        }

        private void Test6_PolicySetup()
        {
            // v57 FIX: this scene picked the FIRST native building in the save as its target — but so
            // does TEST6 MOVE's Test6_MoveFindOrPlant (an independent sub-scenario the class doc claims
            // "touch[es] disjoint entities"). On a save where that first building happens to have a
            // SubNet/SubArea (e.g. an extractor/hub), BOTH sub-scenarios grabbed the SAME entity: MOVE
            // relocated it ~28 m away between this setup and Test6_PolicyAct, so the (stationary) decoy
            // ended up the only building left inside the click's 3 m search radius and "won" by naive
            // proximity after the (correct!) prefab filter found nothing there anymore — a false
            // "WRONG resolve" caused by the scene, not by PolicyApplySystem. Fix: never pick a building
            // TEST6 MOVE could also claim — exclude SubNet/SubArea bearers (MOVE's own Any-filter) so
            // the two sub-scenarios' entity pools are actually disjoint, as intended.
            EntityQuery bq = GetEntityQuery(new EntityQueryDesc
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
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(), ComponentType.ReadOnly<CS2M_SyncId>(),
                },
            });

            // v58: a exclusão por COMPONENTE (None: SubNet/SubArea) esvaziou o pool neste save — TODO
            // prédio nativo tem driveway (SubNet). A disjunção com TEST6 MOVE tem que ser por ENTIDADE:
            // replicar a escolha do MOVE (primeiro prédio do query Any<SubNet,SubArea>, ver
            // Test6_MoveFindOrPlant) e PULAR exatamente esse prédio na seleção abaixo.
            Entity moveWouldPick = Entity.Null;
            EntityQuery mq = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Net.SubNet>(),
                    ComponentType.ReadOnly<Game.Areas.SubArea>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });
            if (!mq.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> ments = mq.ToEntityArray(Allocator.Temp);
                try { moveWouldPick = ments[0]; } finally { ments.Dispose(); }
            }

            EntityQuery pq = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.BuildingOptionData>());

            if (bq.IsEmptyIgnoreFilter || pq.IsEmptyIgnoreFilter)
            {
                L($"[Auto] TEST6 POLICY SKIP noNativeBuilding={bq.IsEmptyIgnoreFilter} noBuildingPolicyPrefab={pq.IsEmptyIgnoreFilter}");
                _t6PolDone = true;
                return;
            }

            NativeArray<Entity> bents = bq.ToEntityArray(Allocator.Temp);
            NativeArray<Entity> pents = pq.ToEntityArray(Allocator.Temp);
            string decoyPrefabName = null, decoyPrefabType = null;
            try
            {
                // v58: nunca escolher o prédio que o TEST6 MOVE vai mover (disjunção por entidade).
                _t6PolBuilding = Entity.Null;
                for (int i = 0; i < bents.Length; i++)
                {
                    if (bents[i] != moveWouldPick) { _t6PolBuilding = bents[i]; break; }
                }

                if (_t6PolBuilding == Entity.Null)
                {
                    L("[Auto] TEST6 POLICY SKIP only candidate is MOVE's target");
                    _t6PolDone = true;
                    return;
                }

                _t6PolPolicy = pents[0];

                PrefabRef pr0 = EntityManager.GetComponentData<PrefabRef>(_t6PolBuilding);
                _prefabSystem.TryGetPrefab(pr0.m_Prefab, out PrefabBase tgtPrefab0);
                string tgtName0 = tgtPrefab0?.name;

                // v56: a REAL building with a DIFFERENT prefab already in this save — see class doc on
                // why a same-prefab neighbor can never expose the gap. v58: skip the target itself AND
                // MOVE's pick (um decoy que se move no meio do teste falsearia o VERIFY igual).
                for (int i = 0; i < bents.Length; i++)
                {
                    if (bents[i] == _t6PolBuilding || bents[i] == moveWouldPick) { continue; }
                    if (!EntityManager.HasComponent<PrefabRef>(bents[i])) { continue; }
                    PrefabRef pri = EntityManager.GetComponentData<PrefabRef>(bents[i]);
                    if (!_prefabSystem.TryGetPrefab(pri.m_Prefab, out PrefabBase cand) || cand == null
                        || cand.name == tgtName0)
                    {
                        continue;
                    }

                    decoyPrefabName = cand.name;
                    decoyPrefabType = cand.GetType().Name;
                    break;
                }
            }
            finally
            {
                bents.Dispose();
                pents.Dispose();
            }

            PrefabRef pr = EntityManager.GetComponentData<PrefabRef>(_t6PolBuilding);
            _prefabSystem.TryGetPrefab(pr.m_Prefab, out PrefabBase bldPrefab);
            if (bldPrefab == null)
            {
                L("[Auto] TEST6 POLICY SKIP target has no resolvable prefab");
                _t6PolDone = true;
                return;
            }

            _t6PolPrefabName = bldPrefab.name;
            _t6PolPrefabType = bldPrefab.GetType().Name;

            if (decoyPrefabName == null)
            {
                L("[Auto] TEST6 POLICY SKIP only one distinct building prefab in this save — can't force " +
                  "the wrong-neighbor ambiguity PolicyFix's gap needs (a same-prefab neighbor can't " +
                  "disambiguate — see class doc)");
                _t6PolDone = true;
                return;
            }

            Game.Objects.Transform t1 = EntityManager.GetComponentData<Game.Objects.Transform>(_t6PolBuilding);
            // Click 2.6 m from the target's own center (still inside FindNearest's 3 m / 9 m^2 radius) —
            // the decoy is planted 3.2 m from the target on the same axis, i.e. only 0.6 m from the
            // click, deliberately CLOSER than the real target. Proximity-alone must pick the decoy;
            // prefab-filtering must exclude it (different prefab) and fall through to the target.
            _t6PolClickPos = new float3(t1.m_Position.x + 2.6f, t1.m_Position.y, t1.m_Position.z);
            var decoyPos = new float3(t1.m_Position.x + 3.2f, t1.m_Position.y, t1.m_Position.z);

            _t6PolDecoySyncId = CS2M_SyncIdSystem.Allocate();
            var placeCmd = new ObjectPlaceCommand
            {
                SyncId = _t6PolDecoySyncId,
                PrefabType = decoyPrefabType, PrefabName = decoyPrefabName,
                PosX = decoyPos.x, PosY = decoyPos.y, PosZ = decoyPos.z,
                RotX = t1.m_Rotation.value.x, RotY = t1.m_Rotation.value.y,
                RotZ = t1.m_Rotation.value.z, RotW = t1.m_Rotation.value.w,
                RandomSeed = 0,
            };
            RemotePlacementQueue.EnqueueObject(placeCmd);  // host places the DIFFERENT-prefab decoy
            Command.SendToAll?.Invoke(placeCmd);            // client places the same decoy at the same spot

            L($"[Auto] TEST6 POLICY setup target={_t6PolBuilding.Index} targetPrefab={_t6PolPrefabName} " +
              $"pos=({t1.m_Position.x:F0},{t1.m_Position.z:F0}) decoyPrefab={decoyPrefabName} " +
              $"decoyPos=({decoyPos.x:F0},{decoyPos.z:F0}) click=({_t6PolClickPos.x:F0},{_t6PolClickPos.z:F0}) " +
              $"distClickTarget={math.distance(_t6PolClickPos, t1.m_Position):F1}m " +
              $"distClickDecoy={math.distance(_t6PolClickPos, decoyPos):F1}m (decoy closer -> naive " +
              "proximity would pick the WRONG one)");
        }

        private void Test6_PolicyAct()
        {
            if (_t6PolBuilding == Entity.Null || !EntityManager.Exists(_t6PolBuilding)
                || !EntityManager.HasComponent<Game.Objects.Transform>(_t6PolBuilding))
            {
                L("[Auto] TEST6 POLICY SKIP target vanished before act");
                _t6PolDone = true;
                return;
            }

            _prefabSystem.TryGetPrefab(_t6PolPolicy, out PrefabBase policyPb);
            var cmd = new PolicyCommand
            {
                PolicyType = policyPb?.GetType().Name, PolicyName = policyPb?.name,
                Active = true, Adjustment = 0f,
                TargetKind = 1, TargetSyncId = 0,
                // v56: the AMBIGUOUS click, not the target's own exact center — see Test6_PolicySetup.
                TargetX = _t6PolClickPos.x, TargetZ = _t6PolClickPos.z,
                TargetName = _t6PolPrefabName,
            };

            RemotePolicyQueue.Enqueue(cmd);       // host toggles (same primitive SendPolicy/PolicyApplySystem use)
            Command.SendToAll?.Invoke(cmd);       // client toggles -> exercises PolicyFix's prefab-filtered resolve

            L($"[Auto] TEST6 POLICY act target={_t6PolBuilding.Index} prefab={_t6PolPrefabName} " +
              $"click=({_t6PolClickPos.x:F0},{_t6PolClickPos.z:F0}) gate CS2M_POLICYFIX={PolicyFix.Enabled}");
        }

        /// <summary>Reads back the REAL per-building <c>Game.Policies.Policy</c> buffer on both the
        /// target and the decoy to confirm which one the resolve actually landed on — the concrete,
        /// observable pass/fail signal for this sub-scenario.</summary>
        private void Test6_PolicyVerify()
        {
            bool onTarget = HasActivePolicy(_t6PolBuilding, _t6PolPolicy);
            bool decoyResolved = CS2M_SyncIdSystem.Map.TryGetValue(_t6PolDecoySyncId, out Entity decoy)
                && EntityManager.Exists(decoy);
            bool onDecoy = decoyResolved && HasActivePolicy(decoy, _t6PolPolicy);

            string verdict = onTarget && !onDecoy ? "CORRECT resolve"
                : onDecoy ? "WRONG resolve (landed on the decoy -- exactly the gap PolicyFix closes)"
                : "policy not found on either (Modify may not be consumed yet, or no Policy buffer)";

            L($"[Auto] TEST6 POLICY VERIFY target={_t6PolBuilding.Index} onTarget={onTarget} " +
              $"decoy={(decoyResolved ? decoy.Index.ToString() : "unresolved")} onDecoy={onDecoy} " +
              $"gate CS2M_POLICYFIX={PolicyFix.Enabled} -> {verdict}");
            _t6PolDone = true;
        }

        private bool HasActivePolicy(Entity target, Entity policy)
        {
            if (target == Entity.Null || !EntityManager.Exists(target)
                || !EntityManager.HasBuffer<Game.Policies.Policy>(target))
            {
                return false;
            }

            DynamicBuffer<Game.Policies.Policy> buf = EntityManager.GetBuffer<Game.Policies.Policy>(target, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_Policy == policy && (buf[i].m_Flags & Game.Policies.PolicyFlags.Active) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>MOVE sub-scenario (CS2M_MOVEFIX): relocates AND rotates a building that owns SubNet
        /// (private driveway) or SubArea (work-area lot) children — the exact gap
        /// RemoteEditApplySystem.ApplyChildTransformDelta closes (without it those children stay at the
        /// OLD absolute position on the receiver; see MoveFix's class doc). Prefers an EXISTING native
        /// building from the save; if none has SubNet/SubArea, falls back to planting a real extractor
        /// via the SAME genuinely-local CreationDefinition/ObjectDefinition recipe CLIENT-FARM (test=4)
        /// already validated (<see cref="ClientFarm_Plant"/>), which grows a real SubArea work-area
        /// field around it. Dual-apply, same pattern as every other Send* helper:
        /// RemoteEditQueue.EnqueueMove so the host's OWN world also exercises the child-transform
        /// recompute (useful for a host-vs-client statediff), plus Command.SendToAll so the client
        /// applies the identical command. SyncId is 0 for a save-native target (resolved on the
        /// receiver by prefab+OLD position, the same "first-touch" contract MoveDetectorSystem's
        /// DetectNativeMoves already uses) or the real id when the fallback farm already got one from
        /// PlacementDetectorSystem.</summary>
        private void RunTest6Move()
        {
            if (_t6MoveStep == 1)
            {
                Test6_MovePollFallback(); // polls every frame, ignores the shared step timer
                return;
            }

            if (_t6MoveTimer > 0) { _t6MoveTimer--; return; }

            if (_t6MoveStep == 0)
            {
                Test6_MoveFindOrPlant();
                return;
            }

            if (_t6MoveStep == 2)
            {
                Test6_MoveAct();
                _t6MoveDone = true;
            }
        }

        private void Test6_MoveFindOrPlant()
        {
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Net.SubNet>(),
                    ComponentType.ReadOnly<Game.Areas.SubArea>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            if (!q.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
                try { _t6MoveBuilding = ents[0]; } finally { ents.Dispose(); }

                L($"[Auto] TEST6 MOVE found existing building entity={_t6MoveBuilding.Index} " +
                  $"hasSubNet={EntityManager.HasBuffer<Game.Net.SubNet>(_t6MoveBuilding)} " +
                  $"hasSubArea={EntityManager.HasBuffer<Game.Areas.SubArea>(_t6MoveBuilding)} (from save)");
                _t6MoveStep = 2;
                _t6MoveTimer = 30;
                return;
            }

            L("[Auto] TEST6 MOVE no existing SubNet/SubArea building in this save — planting an extractor " +
              "via the CLIENT-FARM (test=4) recipe (ClientFarm_Plant) to get one");
            ClientFarm_Plant();
            _t6MoveStep = 1;
        }

        /// <summary>Polls every frame for the fallback extractor <see cref="Test6_MoveFindOrPlant"/>
        /// planted (same match technique as <see cref="TryLogClientFarmReal"/>: PrefabRef+Applied, no
        /// CreationDefinition, within 2 m of the submitted position), then waits a short extra settle so
        /// its SubArea buffer/CS2M_SyncId (from PlacementDetectorSystem) finish wiring before the move
        /// acts on it.</summary>
        private void Test6_MovePollFallback()
        {
            if (_t6MoveFallbackSettle < 0)
            {
                if (_cfPendingPrefab == Entity.Null) { return; } // ClientFarm_Plant SKIP'd — nothing to poll for

                // Same forced ApplyTool pump TryLogClientFarmReal needs — see that method's doc comment
                // for the full root-cause writeup. ToolOutputSystem only pumps ClearTool/ApplyTool when a
                // real tool is active; with none active here, the Temp entities GenerateObjectsSystem/
                // GenerateAreasSystem create from ClientFarm_Plant's hand-built definitions would never be
                // promoted to Applied on their own.
                _updateSystem.Update(SystemUpdatePhase.ApplyTool);

                Entity found = Entity.Null;
                EntityQuery q = GetEntityQuery(new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Transform>(),
                        ComponentType.ReadOnly<Applied>(),
                    },
                    None = new[] { ComponentType.ReadOnly<Game.Tools.CreationDefinition>() },
                });
                NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity e in ents)
                    {
                        if (EntityManager.GetComponentData<PrefabRef>(e).m_Prefab != _cfPendingPrefab) { continue; }
                        float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                        float dx = p.x - _cfPendingPos.x, dz = p.z - _cfPendingPos.z;
                        if (dx * dx + dz * dz > 4f) { continue; }
                        found = e;
                        break;
                    }
                }
                finally { ents.Dispose(); }

                if (found == Entity.Null) { return; } // keep polling

                _t6MoveBuilding = found;
                _t6MoveFallbackSettle = 60;
                L($"[Auto] TEST6 MOVE fallback extractor materialized entity={found.Index}");
                return;
            }

            if (_t6MoveFallbackSettle > 0) { _t6MoveFallbackSettle--; return; }

            L($"[Auto] TEST6 MOVE fallback building ready entity={_t6MoveBuilding.Index} " +
              $"hasSubNet={EntityManager.HasBuffer<Game.Net.SubNet>(_t6MoveBuilding)} " +
              $"hasSubArea={EntityManager.HasBuffer<Game.Areas.SubArea>(_t6MoveBuilding)}");
            _t6MoveStep = 2;
        }

        private void Test6_MoveAct()
        {
            if (_t6MoveBuilding == Entity.Null || !EntityManager.Exists(_t6MoveBuilding)
                || !EntityManager.HasComponent<Game.Objects.Transform>(_t6MoveBuilding))
            {
                L("[Auto] TEST6 MOVE SKIP building vanished before act");
                return;
            }

            Game.Objects.Transform oldTf = EntityManager.GetComponentData<Game.Objects.Transform>(_t6MoveBuilding);
            quaternion deltaRot = quaternion.RotateY(math.radians(40f));
            quaternion newRot = math.mul(deltaRot, oldTf.m_Rotation);
            var newPos = new float3(oldTf.m_Position.x + 20f, oldTf.m_Position.y, oldTf.m_Position.z + 20f);

            bool hasSubNet = EntityManager.HasBuffer<Game.Net.SubNet>(_t6MoveBuilding);
            bool hasSubArea = EntityManager.HasBuffer<Game.Areas.SubArea>(_t6MoveBuilding);

            string prefabType = null, prefabName = null;
            if (EntityManager.HasComponent<PrefabRef>(_t6MoveBuilding)
                && _prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(_t6MoveBuilding).m_Prefab, out PrefabBase pb)
                && pb != null)
            {
                prefabType = pb.GetType().Name;
                prefabName = pb.name;
            }

            ulong syncId = EntityManager.HasComponent<CS2M_SyncId>(_t6MoveBuilding)
                ? EntityManager.GetComponentData<CS2M_SyncId>(_t6MoveBuilding).m_Id : 0;

            var cmd = new MoveCommand
            {
                SyncId = syncId,
                PrefabType = prefabType, PrefabName = prefabName,
                PosX = newPos.x, PosY = newPos.y, PosZ = newPos.z,
                RotX = newRot.value.x, RotY = newRot.value.y, RotZ = newRot.value.z, RotW = newRot.value.w,
                HasOldTransform = true,
                OldX = oldTf.m_Position.x, OldY = oldTf.m_Position.y, OldZ = oldTf.m_Position.z,
                OldRotX = oldTf.m_Rotation.value.x, OldRotY = oldTf.m_Rotation.value.y,
                OldRotZ = oldTf.m_Rotation.value.z, OldRotW = oldTf.m_Rotation.value.w,
            };

            L($"[Auto] TEST6 MOVE building={_t6MoveBuilding.Index} old=({oldTf.m_Position.x:F0},{oldTf.m_Position.z:F0}) " +
              $"new=({newPos.x:F0},{newPos.z:F0}) hasSubNet={hasSubNet} hasSubArea={hasSubArea}");

            RemoteEditQueue.EnqueueMove(cmd);   // host moves (+ child transform delta if MOVEFIX on)
            Command.SendToAll?.Invoke(cmd);     // client moves the same building -> exercises MOVEFIX's deltaRot path
        }

        /// <summary>ROUTE-REROUTE sub-scenario (CS2M_ROUTEFIX): reroutes a SAVE-loaded transport line (no
        /// CS2M_SyncId) by moving one of its waypoints directly — the exact case
        /// RouteDetectorSystem.DetectSaveRouteReroutes exists for (a save-line's reroute is otherwise
        /// silently dropped; see RouteFix's class doc). Only meaningful if the loaded city actually HAS a
        /// pre-existing line: an in-session-created line always gets a CS2M_SyncId immediately
        /// (RouteDetectorSystem.DetectCreated sees its Created tag the same frame), so its later reroute
        /// would take the ALREADY-proven id-based DetectRerouted path instead of the gated one —
        /// fabricating a line here as a "setup" step would look like it exercises ROUTEFIX but actually
        /// wouldn't, so this sub-scenario SKIPs cleanly (reported, not papered over) when the save has no
        /// such line, rather than inventing a fallback that can't reach the gate.
        ///
        /// v56: the first live run moved the waypoint and marked Updated but the host log never showed
        /// "[Route] DETECT+SEND" — a live check of DetectSaveRouteReroutes' query
        /// (Route+TransportLine+RouteWaypoint+PrefabRef+Updated, no Temp/Deleted/Created/RemotePlaced/
        /// CS2M_SyncId) confirms this scene's edit DOES satisfy it (same exclusions this scene's own
        /// find-query already checks before ever acting), and RouteFix.Enabled is read fresh on every
        /// scan (no caching bug either) — so a gate-off run (CS2M_ROUTEFIX not exported to the process)
        /// reproduces the exact silent symptom observed. This scene can't set that env var for itself
        /// (it's read once per process before the mod even loads), so instead it: (a) logs the gate's
        /// live value right at the point of the edit, so a runner instantly knows whether "no DETECT+SEND"
        /// means "gate off" or "still a real gap"; (b) splits into find-then-settle-then-act (baseline the
        /// line's identity for a full 1.5s BEFORE touching it, the same "measure after propagation, never
        /// before" lesson TEST5 already taught this codebase); and (c) marks Updated on BOTH the route
        /// (what the detector's own query requires) AND the moved waypoint (what the game's real
        /// WaypointConnectionSystem watches — decomp Game/Routes/WaypointConnectionSystem.cs ~L1402 stamps
        /// Updated back onto the owning route once an Updated waypoint's connections are recomputed), so
        /// detection does not depend on guessing which of the two paths actually feeds it.</summary>
        private void RunTest6Route()
        {
            if (_t6RouteTimer > 0) { _t6RouteTimer--; return; }

            if (_t6RouteStep == 0)
            {
                Test6_RouteFindAndBaseline();
                return;
            }

            if (_t6RouteStep == 1)
            {
                Test6_RouteAct();
            }
        }

        private void Test6_RouteFindAndBaseline()
        {
            EntityQuery q = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                    ComponentType.ReadOnly<Game.Routes.RouteWaypoint>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(), ComponentType.ReadOnly<CS2M_SyncId>(),
                },
            });

            if (q.IsEmptyIgnoreFilter)
            {
                L("[Auto] TEST6 ROUTE-REROUTE SKIP no save-loaded transit line in this city (needs a save " +
                  "with at least one pre-existing bus/tram/etc. line — an in-session-created line gets a " +
                  "SyncId immediately and would exercise the already-proven id-based reroute path instead " +
                  "of ROUTEFIX)");
                _t6RouteDone = true;
                return;
            }

            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try { _t6RouteEntity = ents[0]; } finally { ents.Dispose(); }

            _prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(_t6RouteEntity).m_Prefab, out PrefabBase pb);
            _t6RoutePrefabName = pb?.name;
            _t6RouteNumber = EntityManager.HasComponent<Game.Routes.RouteNumber>(_t6RouteEntity)
                ? EntityManager.GetComponentData<Game.Routes.RouteNumber>(_t6RouteEntity).m_Number : 0;

            L($"[Auto] TEST6 ROUTE-REROUTE line={_t6RoutePrefabName}#{_t6RouteNumber} entity={_t6RouteEntity.Index} " +
              $"gate CS2M_ROUTEFIX={RouteFix.Enabled} — baselined, settling ~1.5s before the real move so " +
              "the line has been seen untouched first (never measure/mutate before a baseline exists)");

            _t6RouteStep = 1;
            _t6RouteTimer = 90; // ~1.5s baseline settle before touching anything
        }

        private void Test6_RouteAct()
        {
            if (_t6RouteEntity == Entity.Null || !EntityManager.Exists(_t6RouteEntity)
                || !EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(_t6RouteEntity))
            {
                L("[Auto] TEST6 ROUTE-REROUTE SKIP line vanished before act");
                _t6RouteDone = true;
                return;
            }

            DynamicBuffer<Game.Routes.RouteWaypoint> wps = EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(_t6RouteEntity, true);
            Entity movedWp = Entity.Null;
            for (int i = 0; i < wps.Length; i++)
            {
                Entity wp = wps[i].m_Waypoint;
                if (wp == Entity.Null || !EntityManager.HasComponent<Game.Routes.Position>(wp)) { continue; }

                float3 p = EntityManager.GetComponentData<Game.Routes.Position>(wp).m_Position;
                EntityManager.SetComponentData(wp, new Game.Routes.Position(new float3(p.x + 30f, p.y, p.z + 30f)));
                movedWp = wp;
                break;
            }

            if (movedWp == Entity.Null)
            {
                L("[Auto] TEST6 ROUTE-REROUTE SKIP no waypoint with a resolvable Position found");
                _t6RouteDone = true;
                return;
            }

            // Mark BOTH: the route (what DetectSaveRouteReroutes' query requires) and the waypoint that
            // actually moved (what a real drag stamps — see class doc).
            // v59 PHASE-TRAP FIX (2 runs medidos: gate=True, waypoint movido, e ZERO [Route] DETECT+SEND):
            // Updated é tag de 1 frame — estampada AQUI (fase do Autopilot), o RouteDetectorSystem que
            // roda numa fase ANTERIOR só a veria no frame seguinte, mas o CleanUpSystem a remove no fim
            // DESTE frame. DeferredUpdated (DeferredUpdateMarker.cs) estampa no INÍCIO do frame seguinte,
            // antes de Mod1 — todos os detectores daquele frame enxergam. Mesmo remédio que
            // ZoneBlockAuthorityApplySystem.Heal e ZoneOrderTiebreak já usam.
            DeferredUpdated.Enqueue(movedWp);
            DeferredUpdated.Enqueue(_t6RouteEntity);

            L($"[Auto] TEST6 ROUTE-REROUTE moved a waypoint on line={_t6RoutePrefabName}#{_t6RouteNumber} " +
              $"gate CS2M_ROUTEFIX={RouteFix.Enabled} — the always-on RouteDetectorSystem." +
              "DetectSaveRouteReroutes should ship it on its own next scan (watch for [Route] DETECT+SEND " +
              "reroute in the host log; gate=false here means it never will, regardless of this scene)");
            _t6RouteDone = true;
        }

        /// <summary>NODEHEAL sub-scenario (CS2M_NODEHEAL): synthesizes the SENDER-reused-node-identity
        /// drift the legacy net path's NetPlaceApplySystem.HealNodePosition exists for, but which never
        /// happens on its own in these scenes (real drift needs a mid-draw split/merge on the sender —
        /// see NodeHeal's class doc — and no other TEST6/TRIREPRO scene edits a road that way). Built with
        /// the SAME dual-apply primitive every other Send* helper in this class uses
        /// (RemoteNetQueue.Enqueue + Command.SendToAll), on the LEGACY identity path (HasNodes=true,
        /// StartNodeId/EndNodeId set, no AtomicBatch) — the exact path ResolveNode/HealNodePosition live
        /// on, and the one the real NetDetectorSystem.DetectPlaced ships for by calling
        /// CS2M_NodeSyncIds.Ensure on the local node's own entity (see that system's DETECT+SEND site).
        /// This scene has no local node entity to Ensure() from (nothing was drawn with the real tool), so
        /// it mints ids directly via CS2M_SyncIdSystem.Allocate() — the same thing Ensure does internally
        /// on a first touch, and the same shortcut every other synthetic Send* helper here already takes
        /// (see StraightNet/AuthNet: SyncId = CS2M_SyncIdSystem.Allocate()).
        ///
        /// Step 0 places a short 50 m road (idA -> idB, fresh ids) — the SAME real primitive a placed road
        /// uses, so idB ends up registered to a REAL Game.Net.Node entity on both host and client (each
        /// process keeps its own CS2M_NodeSyncIds.Map — there is no cross-process state here, exactly like
        /// every other dual-applied sub-scenario in this file). Step 1 sends a SECOND road whose start
        /// REUSES idB but declares its position ~15 m away from where idB's node actually sits — the exact
        /// shape a real sender emits when its own net editor folds a split/reused endpoint's identity onto
        /// a node that has since moved (see HealNodePosition's doc comment: "a split/derived node's
        /// identity can be handed off to a DIFFERENT physical node... once TryResolve hit, the freshly-
        /// declared pos was silently discarded"). 15 m sits ABOVE NodeHealMergeDist (10 m) but nothing else
        /// is registered near the declared position, so NodeHeal.Enabled — if the search for a merge
        /// survivor finds nothing — falls through to the SNAP branch and logs HEAL-LARGE (dist>10m warning)
        /// immediately followed by HEAL-SNAP (the node actually moves); with the gate OFF, ResolveNode
        /// returns the STALE node untouched and NEITHER log line appears — the drift persists exactly as
        /// field-reported. Both log lines are emitted by NetPlaceApplySystem itself (this class only sends
        /// the command and reports what it sent); a runner confirms the fix by grepping each PC's own log
        /// for "[Net] HEAL-SNAP" / "[Net] HEAL-MERGE".
        ///
        /// The host applies BOTH commands to its OWN world too (RemoteNetQueue.Enqueue), same as every
        /// other Send* helper here — there is no special "client-only" send primitive in this codebase (no
        /// Command.SendToAll-without-local-apply exists; Command.SendToAll always ships to every OTHER
        /// connected peer, and RemoteNetQueue.Enqueue is how THIS process applies its own copy — the two
        /// calls target disjoint audiences by construction, not by an opt-out flag). This is not a side
        /// effect to work around: HealNodePosition is the exact same code path on host and client, so the
        /// host reusing/moving idB's node exercises (and reports on) the identical mechanic, deterministically,
        /// on both sides — precisely the cross-machine confirmation every other 2-sim STRESS helper in this
        /// class (SendJunctionStress, SendMovedJunctionStress) already relies on host-applies-too for.</summary>
        private void RunTest6NodeHeal()
        {
            if (_t6NodeHealTimer > 0) { _t6NodeHealTimer--; return; }

            if (_t6NodeHealStep == 0) { Test6_NodeHealBuildRoad1(); return; }
            if (_t6NodeHealStep == 1) { Test6_NodeHealSendRoad2(); return; }
            if (_t6NodeHealStep == 2) { Test6_NodeHealFinal(); }
        }

        private void Test6_NodeHealBuildRoad1()
        {
            if (_edgeQuery.IsEmptyIgnoreFilter || !TryAnchor(out float3 anchor))
            {
                L("[Auto] TEST6 NODEHEAL SKIP no road/anchor available to source a prefab from");
                _t6NodeHealDone = true;
                return;
            }

            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            string type, name;
            try
            {
                Entity src = ents[0];
                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(src).m_Prefab, out PrefabBase prefab)
                    || prefab == null)
                {
                    L("[Auto] TEST6 NODEHEAL SKIP no resolvable road prefab");
                    _t6NodeHealDone = true;
                    return;
                }

                type = prefab.GetType().Name;
                name = prefab.name;
            }
            finally { ents.Dispose(); }

            _t6NodeHealType = type;
            _t6NodeHealName = name;

            // Clear quadrant, well away from anything else TEST6 touches — POLICY/MOVE/ROUTE all act on
            // whatever native building/line the save already has (unknown position), not on world
            // geometry this scene places, so any offset clear of the anchor is disjoint from them by
            // construction. +400/-400 keeps it clear of TRIREPRO's own STRESS offsets too (that scene
            // never runs concurrently with test=6, but reusing distinct coordinates avoids any confusion
            // reading a combined log).
            _t6NodeHealP1Start = new float3(anchor.x + 400f, anchor.y, anchor.z - 400f);
            _t6NodeHealP1End = new float3(anchor.x + 450f, anchor.y, anchor.z - 400f);

            _t6NodeHealIdA = CS2M_SyncIdSystem.Allocate();
            _t6NodeHealIdB = CS2M_SyncIdSystem.Allocate();

            NetPlaceCommand cmd = IdNet(type, name, _t6NodeHealP1Start, _t6NodeHealIdA, _t6NodeHealP1End, _t6NodeHealIdB);
            RemoteNetQueue.Enqueue(cmd);      // host applies (same dual-apply primitive every Send* helper here uses)
            Command.SendToAll?.Invoke(cmd);   // client applies -> both sides register idB on their OWN real node

            L($"[Auto] TEST6 NODEHEAL road1 idA={_t6NodeHealIdA} idB={_t6NodeHealIdB} prefab={name} " +
              $"start=({_t6NodeHealP1Start.x:F1},{_t6NodeHealP1Start.z:F1}) " +
              $"end=({_t6NodeHealP1End.x:F1},{_t6NodeHealP1End.z:F1})");

            _t6NodeHealStep = 1;
            // ~2s settle: GenerateNodesSystem must build the real node from last frame's course and
            // ProcessPendingStamps must stamp idB onto it (ages out after only 6 frames if it never does —
            // see NetPlaceApplySystem.ProcessPendingStamps) before road2 can reuse idB as an ALREADY-
            // resolvable identity, the same "measure/mutate after propagation, never before" lesson TEST5
            // established.
            _t6NodeHealTimer = 120;
        }

        private void Test6_NodeHealSendRoad2()
        {
            // idD: a brand-new fresh far end — never referenced before, so it always resolves to a fresh
            // node (ResolveNode's Null branch), keeping this step's ONLY variable the reused idB.
            _t6NodeHealIdD = CS2M_SyncIdSystem.Allocate();
            _t6NodeHealP2 = new float3(_t6NodeHealP1End.x, _t6NodeHealP1End.y, _t6NodeHealP1End.z + 15f);
            var p3 = new float3(_t6NodeHealP2.x, _t6NodeHealP2.y, _t6NodeHealP2.z + 40f);

            NetPlaceCommand cmd = IdNet(_t6NodeHealType, _t6NodeHealName, _t6NodeHealP2, _t6NodeHealIdB, p3, _t6NodeHealIdD);

            L($"[Auto] TEST6 NODEHEAL SENT id={_t6NodeHealIdB} from=({_t6NodeHealP1End.x:F1},{_t6NodeHealP1End.z:F1}) " +
              $"to=({_t6NodeHealP2.x:F1},{_t6NodeHealP2.z:F1}) dist={math.distance(_t6NodeHealP1End, _t6NodeHealP2):F1}m " +
              $"gate CS2M_NODEHEAL={NodeHeal.Enabled}");

            RemoteNetQueue.Enqueue(cmd);      // host applies too — see class doc: same code path, expected
                                                // to converge identically on both sides, not a side effect
            Command.SendToAll?.Invoke(cmd);   // client applies -> exercises HEAL-SNAP/HEAL-MERGE (or nothing, gate off)

            _t6NodeHealStep = 2;
            _t6NodeHealTimer = 90; // ~1.5s settle before reading back the final position
        }

        private void Test6_NodeHealFinal()
        {
            bool resolved = CS2M_NodeSyncIds.TryResolve(EntityManager, _t6NodeHealIdB, out Entity node);
            float3 pos = resolved ? EntityManager.GetComponentData<Game.Net.Node>(node).m_Position : default;
            float movedFromOriginal = resolved ? math.distance(pos, _t6NodeHealP1End) : -1f;

            L($"[Auto] TEST6 NODEHEAL FINAL id={_t6NodeHealIdB} resolved={resolved} entity={(resolved ? node.Index : -1)} " +
              $"pos=({pos.x:F1},{pos.z:F1}) movedFromP1End={movedFromOriginal:F1}m gate CS2M_NODEHEAL={NodeHeal.Enabled} " +
              "(host-side reading; expectMoved~15 with the gate on, expectMoved~0 with it off — cross-check " +
              "against [Net] HEAL-SNAP/HEAL-MERGE in THIS PC's own log and the client's for the full picture)");

            _t6NodeHealDone = true;
        }

        /// <summary>A road on the identity (StartNodeId/EndNodeId) path — the SAME primitive
        /// NetDetectorSystem.DetectPlaced ships for a real placed segment (see that system's DETECT+SEND
        /// site: CS2M_NodeSyncIds.Ensure on each live endpoint), used here by RunTest6NodeHeal to drive
        /// NetPlaceApplySystem.ResolveNode/HealNodePosition directly. Distinct from AuthNet (position-only
        /// fusion, ids left at 0) — this scene specifically needs the id-reuse path AuthNet does not
        /// exercise.</summary>
        private static NetPlaceCommand IdNet(string type, string name, float3 startNode, ulong startId,
            float3 endNode, ulong endId)
        {
            float3 s = startNode, e = endNode;
            float3 b = math.lerp(s, e, 1f / 3f);
            float3 c = math.lerp(s, e, 2f / 3f);
            return new NetPlaceCommand
            {
                SyncId = CS2M_SyncIdSystem.Allocate(),
                PrefabType = type, PrefabName = name,
                Ax = s.x, Ay = s.y, Az = s.z,
                Bx = b.x, By = b.y, Bz = b.z,
                Cx = c.x, Cy = c.y, Cz = c.z,
                Dx = e.x, Dy = e.y, Dz = e.z,
                HasNodes = true,
                StartNodeX = s.x, StartNodeY = s.y, StartNodeZ = s.z,
                EndNodeX = e.x, EndNodeY = e.y, EndNodeZ = e.z,
                StartNodeId = startId, EndNodeId = endId,
                RandomSeed = 0,
            };
        }

        /// <summary>Client side of the FIVE-GAP scene: nothing SCRIPTED for four of the five. All five
        /// sub-scenarios are host-authored and dual-applied (see the class-level TEST6 doc comment), so
        /// the client's own always-on apply systems consume every shipped command with no scripted action
        /// needed here — this just logs once so the log makes that explicit instead of looking silently
        /// idle. The exception is DEVTREE: a delayed READ (not a scripted action) of the client's own
        /// DevTreePoints singleton, fired off a fixed timer (see _t6DevClientTimer's doc), because only
        /// the client's OWN balance can confirm its mirrored deduction actually went negative instead of
        /// being floored — the host's own "TEST6 DEVTREE FINAL" log cannot see the client's state.</summary>
        private void RunTest6ClientStep()
        {
            if (!_t6ClientIdleLogged)
            {
                _t6ClientIdleLogged = true;
                L("[Auto] TEST6 client idle (test=6 is fully host-authored/dual-applied — the client's own " +
                  "always-on apply systems consume the shipped commands with no scripted action needed " +
                  "here, except a delayed DEVTREE points read below)");
            }

            if (_t6DevClientLogged) { return; }
            if (_t6DevClientTimer > 0) { _t6DevClientTimer--; return; }

            EntityQuery pointsQuery = GetEntityQuery(ComponentType.ReadOnly<DevTreePoints>());
            int points = pointsQuery.IsEmptyIgnoreFilter ? int.MinValue : pointsQuery.GetSingleton<DevTreePoints>().m_Points;
            L($"[Auto] TEST6 DEVTREE CLIENT points={points} gate CS2M_DEVTREEFIX={DevTreeFix.Enabled}");
            _t6DevClientLogged = true;
        }

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
            else if (_clientFarm) { RunClientFarmStep(); }
            else if (_test5) { RunTest5ClientStep(); }
            else if (_test6) { RunTest6ClientStep(); }

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
