using System;
using System.IO;
using Colossal.Serialization.Entities;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Headless self-test driver so the whole sync pipeline can be exercised on ONE PC
    ///     with two game instances over 127.0.0.1 — no second human, no mouse.
    ///
    ///     It is completely inert unless the <c>CS2M_AUTOPILOT</c> environment variable is set,
    ///     so the build friends run is byte-for-byte identical to the normal mod.
    ///
    ///     Env vars (read once at create):
    ///       CS2M_AUTOPILOT = "host" | "client"   (role; anything else disables the system)
    ///       CS2M_AP_PORT   = port                (default 1111)
    ///       CS2M_AP_IP     = host ip for client  (default 127.0.0.1)
    ///       CS2M_AP_TEST   = "0" to skip the scripted placement test on the host (default on)
    ///       CS2M_AP_LOG    = path to a per-instance log file (the two game instances share the
    ///                        game's CS2M.log, so each writes its own [Auto] transcript here)
    ///
    ///     Flow:
    ///       host   -continuelastsave loads a city -> onGameLoadingComplete(Game) -> StartServer,
    ///              then once a client has joined, runs a scripted sequence (place a tree, place a
    ///              building, place a road, delete the tree) via the SAME commands the real
    ///              detectors emit, so Remote*ApplySystem on the client is put through its paces.
    ///       client onGameLoadingComplete(MainMenu) -> Connect(127.0.0.1) (retried) -> once PLAYING,
    ///              logs how many remote objects/nets it has materialized (proving the apply path).
    /// </summary>
    public partial class AutopilotSystem : GameSystemBase
    {
        private bool _disabled = true;
        private bool _isHost;
        private int _port = 1111;
        private string _ip = "127.0.0.1";
        private bool _testEnabled = true;
        private string _logPath;

        // Latest game mode seen via onGameLoadingComplete (menu vs in-city).
        private GameMode _gameMode = GameMode.Other;

        private PrefabSystem _prefabSystem;

        // Queries used by the host's scripted test to find sample things to clone,
        // and by the client to count what it has materialized.
        private EntityQuery _treeQuery;
        private EntityQuery _buildingQuery;
        private EntityQuery _edgeQuery;
        private EntityQuery _remotePlacedQuery;
        private EntityQuery _allEdgesQuery;

        // --- Host state ---
        private int _hostArmFrames = -1;   // >=0 while counting down to StartServer
        private bool _hosting;
        private bool _testStarted;
        private int _testStep;
        private int _testTimer;
        private ulong _treeSyncId;
        private ulong _buildingSyncId;

        // --- Client state ---
        private bool _connectRequested;
        private int _connectRetryFrames;
        private int _connectAttempts;
        private bool _clientPlayingLogged;
        private int _verifyFrames;
        private int _lastRemoteCount = -1;
        private int _lastEdgeCount = -1;

        protected override void OnCreate()
        {
            base.OnCreate();

            string role = Environment.GetEnvironmentVariable("CS2M_AUTOPILOT");
            if (string.IsNullOrEmpty(role))
            {
                // Not an autopilot run — stay completely inert (friends' normal build).
                return;
            }

            role = role.Trim().ToLowerInvariant();
            if (role == "host" || role == "server")
            {
                _isHost = true;
            }
            else if (role == "client" || role == "join")
            {
                _isHost = false;
            }
            else
            {
                CS2M.Log.Info($"[Auto] CS2M_AUTOPILOT='{role}' not understood (use host|client); disabled");
                return;
            }

            _disabled = false;
            _logPath = Environment.GetEnvironmentVariable("CS2M_AP_LOG");

            string portEnv = Environment.GetEnvironmentVariable("CS2M_AP_PORT");
            if (!string.IsNullOrEmpty(portEnv) && int.TryParse(portEnv, out int p))
            {
                _port = p;
            }

            string ipEnv = Environment.GetEnvironmentVariable("CS2M_AP_IP");
            if (!string.IsNullOrEmpty(ipEnv))
            {
                _ip = ipEnv.Trim();
            }

            _testEnabled = Environment.GetEnvironmentVariable("CS2M_AP_TEST") != "0";

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

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
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
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
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
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
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            _remotePlacedQuery = GetEntityQuery(ComponentType.ReadOnly<CS2M_RemotePlaced>());

            _allEdgesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Edge>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            GameManager.instance.onGameLoadingComplete += OnLoadingComplete;

            L($"[Auto] ENABLED role={(_isHost ? "HOST" : "CLIENT")} port={_port} ip={_ip} " +
              $"scriptedTest={_testEnabled} log={_logPath ?? "(game log only)"}");
        }

        protected override void OnDestroy()
        {
            if (!_disabled)
            {
                try { GameManager.instance.onGameLoadingComplete -= OnLoadingComplete; }
                catch { /* shutting down */ }
            }

            base.OnDestroy();
        }

        private void OnLoadingComplete(Purpose purpose, GameMode mode)
        {
            _gameMode = mode;
            if (_disabled)
            {
                return;
            }

            L($"[Auto] loadingComplete purpose={purpose} mode={mode}");

            if (_isHost && mode == GameMode.Game && !_hosting && _hostArmFrames < 0)
            {
                // City is loaded — arm the server start a couple of seconds from now so the
                // simulation is fully settled before a client can pull the world.
                _hostArmFrames = 120;
                L("[Auto] HOST city loaded -> arming StartServer");
            }
            else if (!_isHost && mode == GameMode.MainMenu && !_connectRequested)
            {
                // At the menu — try to connect right away (retried in OnUpdate if it ticks here).
                TryClientConnect();
            }
        }

        protected override void OnUpdate()
        {
            if (_disabled)
            {
                return;
            }

            if (_isHost)
            {
                UpdateHost();
            }
            else
            {
                UpdateClient();
            }
        }

        // ---------------------------------------------------------------- HOST

        private void UpdateHost()
        {
            if (!_hosting)
            {
                if (_hostArmFrames < 0)
                {
                    return; // waiting for the city to finish loading
                }

                if (--_hostArmFrames > 0)
                {
                    return;
                }

                NetworkInterface.Instance.UpdateLocalPlayerUsername("AutoHost");
                NetworkInterface.Instance.StartServer(new ConnectionConfig(_port));
                _hosting = true;
                L($"[Auto] HOST StartServer on :{_port} status={Status()}");
                return;
            }

            if (!_testEnabled)
            {
                return;
            }

            // Wait for a real client to have joined at the game level before testing.
            if (!_testStarted)
            {
                if (NetworkInterface.Instance.PlayerListJoined.Count <= 1)
                {
                    return;
                }

                _testStarted = true;
                _testTimer = 240; // ~4s settle after the client is in
                L($"[Auto] HOST client joined (joined={NetworkInterface.Instance.PlayerListJoined.Count}); " +
                  "scripted test will begin");
                return;
            }

            if (_testStep >= 5)
            {
                return; // test sequence finished
            }

            if (_testTimer > 0)
            {
                _testTimer--;
                return;
            }

            switch (_testStep)
            {
                case 0:
                    _treeSyncId = SendObjectClone(_treeQuery, "tree", new float3(20f, 0f, 20f));
                    _testTimer = 300;
                    break;
                case 1:
                    _buildingSyncId = SendObjectClone(_buildingQuery, "building", new float3(60f, 0f, 60f));
                    _testTimer = 300;
                    break;
                case 2:
                    SendNetClone();
                    _testTimer = 300;
                    break;
                case 3:
                    SendDelete(_treeSyncId, "tree");
                    _testTimer = 180;
                    break;
                case 4:
                    L($"[Auto] HOST scripted test DONE (treeSyncId={_treeSyncId} buildingSyncId={_buildingSyncId}). " +
                      "Check the CLIENT log for VERIFY / [Place] / [Net] APPLIED lines.");
                    break;
            }

            _testStep++;
        }

        /// <summary>
        ///     Finds a sample object of the given query, clones its prefab identity + transform
        ///     (offset so it doesn't sit exactly on the original) and broadcasts an
        ///     ObjectPlaceCommand — exactly what PlacementDetectorSystem would have sent.
        ///     Returns the allocated SyncId (0 if nothing was sent).
        /// </summary>
        private ulong SendObjectClone(EntityQuery query, string kind, float3 offset)
        {
            if (query.IsEmptyIgnoreFilter)
            {
                L($"[Auto] TEST object SKIP kind={kind} reason=noneInCity");
                return 0;
            }

            NativeArray<Entity> ents = query.ToEntityArray(Allocator.Temp);
            try
            {
                Entity src = ents[0];
                PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(src);
                if (!_prefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) || prefab == null)
                {
                    L($"[Auto] TEST object SKIP kind={kind} reason=noPrefab");
                    return 0;
                }

                Game.Objects.Transform t = EntityManager.GetComponentData<Game.Objects.Transform>(src);
                float3 pos = t.m_Position + offset;

                int seed = 0;
                if (EntityManager.HasComponent<PseudoRandomSeed>(src))
                {
                    seed = EntityManager.GetComponentData<PseudoRandomSeed>(src).m_Seed;
                }

                var cmd = new ObjectPlaceCommand
                {
                    SyncId = CS2M_SyncIdSystem.Allocate(),
                    PrefabType = prefab.GetType().Name,
                    PrefabName = prefab.name,
                    Hash0 = 0, Hash1 = 0, Hash2 = 0, Hash3 = 0,
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    RotX = t.m_Rotation.value.x,
                    RotY = t.m_Rotation.value.y,
                    RotZ = t.m_Rotation.value.z,
                    RotW = t.m_Rotation.value.w,
                    RandomSeed = seed,
                };

                L($"[Auto] TEST object SEND kind={kind} type={cmd.PrefabType} name={cmd.PrefabName} " +
                  $"pos=({cmd.PosX:F1},{cmd.PosY:F1},{cmd.PosZ:F1}) seed={seed} syncId={cmd.SyncId}");
                Command.SendToAll?.Invoke(cmd);
                return cmd.SyncId;
            }
            finally
            {
                ents.Dispose();
            }
        }

        /// <summary>Clones an existing edge into a parallel short course and broadcasts a NetPlaceCommand.</summary>
        private void SendNetClone()
        {
            if (_edgeQuery.IsEmptyIgnoreFilter)
            {
                L("[Auto] TEST net SKIP reason=noEdgesInCity");
                return;
            }

            NativeArray<Entity> ents = _edgeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                Entity src = ents[0];
                PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(src);
                if (!_prefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) || prefab == null)
                {
                    L("[Auto] TEST net SKIP reason=noPrefab");
                    return;
                }

                Colossal.Mathematics.Bezier4x3 b = EntityManager.GetComponentData<Game.Net.Curve>(src).m_Bezier;
                var off = new float3(0f, 0f, 40f); // shift the whole curve sideways so it's clearly a new road

                var cmd = new NetPlaceCommand
                {
                    SyncId = CS2M_SyncIdSystem.Allocate(),
                    PrefabType = prefab.GetType().Name,
                    PrefabName = prefab.name,
                    Hash0 = 0, Hash1 = 0, Hash2 = 0, Hash3 = 0,
                    Ax = b.a.x + off.x, Ay = b.a.y, Az = b.a.z + off.z,
                    Bx = b.b.x + off.x, By = b.b.y, Bz = b.b.z + off.z,
                    Cx = b.c.x + off.x, Cy = b.c.y, Cz = b.c.z + off.z,
                    Dx = b.d.x + off.x, Dy = b.d.y, Dz = b.d.z + off.z,
                    RandomSeed = 0,
                };

                L($"[Auto] TEST net SEND name={cmd.PrefabName} start=({cmd.Ax:F1},{cmd.Az:F1}) " +
                  $"end=({cmd.Dx:F1},{cmd.Dz:F1}) syncId={cmd.SyncId}");
                Command.SendToAll?.Invoke(cmd);
            }
            finally
            {
                ents.Dispose();
            }
        }

        private void SendDelete(ulong syncId, string kind)
        {
            if (syncId == 0)
            {
                L($"[Auto] TEST delete SKIP kind={kind} reason=noSyncId");
                return;
            }

            L($"[Auto] TEST delete SEND kind={kind} syncId={syncId}");
            Command.SendToAll?.Invoke(new DeleteCommand { SyncId = syncId });
        }

        // ---------------------------------------------------------------- CLIENT

        private void UpdateClient()
        {
            PlayerStatus status = NetworkInterface.Instance.LocalPlayer.PlayerStatus;

            if (status != PlayerStatus.PLAYING)
            {
                // Keep trying to connect while we're idle at the menu and the first attempt
                // hasn't taken (host maybe not up yet). Only retry from the INACTIVE state.
                if (_gameMode == GameMode.MainMenu && status == PlayerStatus.INACTIVE)
                {
                    if (_connectRetryFrames > 0)
                    {
                        _connectRetryFrames--;
                    }
                    else if (_connectAttempts < 30)
                    {
                        TryClientConnect();
                        _connectRetryFrames = 240; // ~4s between attempts
                    }
                }

                return;
            }

            if (!_clientPlayingLogged)
            {
                _clientPlayingLogged = true;
                L("[Auto] CLIENT PLAYING — connected & synced. Watching for remote placements.");
            }

            // Report counts on a heartbeat AND immediately whenever they change, so the log
            // shows the tree/building/road appearing and the tree disappearing on delete.
            int remote = _remotePlacedQuery.CalculateEntityCount();
            int edges = _allEdgesQuery.CalculateEntityCount();
            bool changed = remote != _lastRemoteCount || edges != _lastEdgeCount;

            if (++_verifyFrames >= 180 || changed)
            {
                _verifyFrames = 0;
                string tag = changed ? "CHANGED" : "heartbeat";
                L($"[Auto] VERIFY {tag} remoteObjects={remote} (was {_lastRemoteCount}) " +
                  $"totalEdges={edges} (was {_lastEdgeCount}) status={status}");
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

        /// <summary>Log to the game's CS2M.log AND (if configured) this instance's own file.</summary>
        private void L(string msg)
        {
            CS2M.Log.Info(msg);
            if (string.IsNullOrEmpty(_logPath))
            {
                return;
            }

            try
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
            catch
            {
                // Never let logging break the run.
            }
        }
    }
}
