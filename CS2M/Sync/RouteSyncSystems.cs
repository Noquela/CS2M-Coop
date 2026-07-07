using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Global toggle for save-loaded-line reroute sync (see <see cref="RouteDetectorSystem.DetectSaveRouteReroutes"/>).
    /// ON by default since 2026-07-07 — validated live in 2-sim (two save-loaded lines, reroute/move-stop
    /// each) + selftest 88 PASS/0 FAIL with every gated fix enabled together (no regression/echo/crash).
    /// Sends a Replace command addressed by prefab+RouteNumber (no SyncId) the moment ANY save-loaded
    /// line's <c>Updated</c> tag is observed. Set env <c>CS2M_ROUTEFIX=0</c> to disable.</summary>
    public static class RouteFix
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_ROUTEFIX") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     Mailboxes + waypoint-hash snapshot for transport-line sync. The snapshot (SyncId → hash of
    ///     the waypoint list) is the echo guard for re-route detection: the apply stores the hash it
    ///     just built, so the detector's next scan sees "unchanged" and stays quiet.
    /// </summary>
    public static class RouteSync
    {
        private static readonly Queue<RouteCreateCommand> Creates = new Queue<RouteCreateCommand>();
        private static readonly Queue<RouteColorCommand> Colors = new Queue<RouteColorCommand>();
        private static readonly Queue<RouteVisibilityCommand> Visibilities = new Queue<RouteVisibilityCommand>();
        private static readonly object Lock = new object();

        // v55: per-route HiddenRoute presence snapshot for the visibility diff + echo guard.
        public static readonly Dictionary<Entity, bool> VisibilitySnapshot = new Dictionary<Entity, bool>();

        public static readonly Dictionary<ulong, ulong> Snapshot = new Dictionary<ulong, ulong>();

        // v55: echo guard for SAVE-loaded lines rerouted by prefab+number (they have no SyncId, exactly
        // like color/delete which already address by number). Keyed "prefab#number" -> last content hash.
        // The receiver rebuilds to the sender's geometry and stamps this key, so its own reroute detector
        // sees an unchanged hash and never pings the command back — no id allocation, no identity race.
        public static readonly Dictionary<string, ulong> SnapshotByNumber = new Dictionary<string, ulong>();

        public static void EnqueueCreate(RouteCreateCommand cmd)
        {
            lock (Lock) { Creates.Enqueue(cmd); }
        }

        public static bool TryDequeueCreate(out RouteCreateCommand cmd)
        {
            lock (Lock)
            {
                if (Creates.Count > 0) { cmd = Creates.Dequeue(); return true; }
                cmd = null;
                return false;
            }
        }

        public static void EnqueueColor(RouteColorCommand cmd)
        {
            lock (Lock) { Colors.Enqueue(cmd); }
        }

        public static bool TryDequeueColor(out RouteColorCommand cmd)
        {
            lock (Lock)
            {
                if (Colors.Count > 0) { cmd = Colors.Dequeue(); return true; }
                cmd = null;
                return false;
            }
        }

        public static void EnqueueVisibility(RouteVisibilityCommand cmd)
        {
            lock (Lock) { Visibilities.Enqueue(cmd); }
        }

        public static bool TryDequeueVisibility(out RouteVisibilityCommand cmd)
        {
            lock (Lock)
            {
                if (Visibilities.Count > 0) { cmd = Visibilities.Dequeue(); return true; }
                cmd = null;
                return false;
            }
        }

        // Echo guard for deletes: remote-applied line deletions register a key here so the local
        // detector doesn't send them back (the RemotePlaced tag can't be the guard — lines created
        // remotely carry it forever, and deleting a friend's line MUST sync).
        private static readonly HashSet<string> DeleteEcho = new HashSet<string>();

        public static string DeleteKey(ulong syncId, string prefabName, int number)
        {
            return syncId != 0 ? syncId.ToString() : prefabName + "#" + number;
        }

        public static void MarkDeleteEcho(string key)
        {
            lock (Lock) { DeleteEcho.Add(key); }
        }

        public static bool ConsumeDeleteEcho(string key)
        {
            lock (Lock) { return DeleteEcho.Remove(key); }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                Creates.Clear();
                Colors.Clear();
                Visibilities.Clear();
                DeleteEcho.Clear();
            }

            Snapshot.Clear();
            SnapshotByNumber.Clear();
            VisibilitySnapshot.Clear();
        }

        /// <summary>FNV-1a over rounded waypoint positions + stop flags (+ Complete).</summary>
        public static ulong Hash(RouteCreateCommand cmd)
        {
            ulong h = 14695981039346656037UL;
            void Mix(long v)
            {
                for (int b = 0; b < 8; b++)
                {
                    h = (h ^ (ulong)((v >> (b * 8)) & 0xFF)) * 1099511628211UL;
                }
            }

            int n = cmd.WpX?.Length ?? 0;
            Mix(n);
            Mix(cmd.Complete ? 1 : 0);
            for (int i = 0; i < n; i++)
            {
                Mix((long)math.round(cmd.WpX[i] * 4f));
                Mix((long)math.round(cmd.WpZ[i] * 4f));
                Mix(cmd.WpHasConn != null && cmd.WpHasConn[i] != 0 ? 1 : 0);
            }

            return h;
        }
    }

    /// <summary>Resolves a transport line: SyncId first, else prefab name + RouteNumber (identical on
    /// every PC for save-loaded lines, and synced for lines created in-session).</summary>
    public static class RouteResolver
    {
        public static Entity Resolve(EntityManager em, EntityQuery routes,
            Game.Prefabs.PrefabSystem prefabSystem, ulong syncId, string prefabName, int number)
        {
            if (syncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(syncId, out Entity byId)
                && em.Exists(byId) && !em.HasComponent<Deleted>(byId))
            {
                return byId;
            }

            if (string.IsNullOrEmpty(prefabName) || number == 0)
            {
                return Entity.Null;
            }

            NativeArray<Entity> ents = routes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    if (em.GetComponentData<RouteNumber>(cand).m_Number != number)
                    {
                        continue;
                    }

                    if (prefabSystem.TryGetPrefab(em.GetComponentData<PrefabRef>(cand).m_Prefab,
                            out PrefabBase pb) && pb != null && pb.name == prefabName)
                    {
                        return cand;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return Entity.Null;
        }
    }

    /// <summary>
    ///     Detects locally created / re-routed transport lines and color changes.
    ///     Creation surfaces as <c>Created</c>+<c>Route</c>+<c>TransportLine</c> (the route tool applies
    ///     on every click, so extending a line arrives here as <c>Updated</c> re-routes — sent as
    ///     Replace commands, gated by the waypoint hash). Color changes surface as the UI's own
    ///     <c>Event</c>+<c>ColorUpdated</c> entities (apply-created events carry
    ///     <c>CS2M_RemotePlaced</c> → skipped).
    /// </summary>
    public partial class RouteDetectorSystem : GameSystemBase
    {
        private Game.Prefabs.PrefabSystem _prefabSystem;
        private EntityQuery _createdRoutes;
        private EntityQuery _updatedRoutes;
        private EntityQuery _updatedSaveRoutes; // v55: rerouted lines that came from the SAVE (no SyncId yet)
        private EntityQuery _colorEvents;
        private EntityQuery _allRoutes; // v55: for the visibility (HiddenRoute) snapshot diff
        private readonly HashSet<Entity> _sentEvents = new HashSet<Entity>();
        private int _eventClear;
        private int _visFrame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>();
            // v59: Any=[TransportLine, WorkRoute] instead of All=[TransportLine] — a WorkRoutePrefab
            // (harvest/service work route) goes through the SAME RouteToolSystem but never gets
            // TransportLine, only the empty WorkRoute tag (decomp Prefabs/WorkRoutePrefab.cs:40), so the
            // detector was fully blind to it: no create, no reroute, no delete (MATRIX P1 / dossier
            // route.md §6.1). Any (not plain Route) so an unknown future Route subtype can't leak in;
            // only the tool creates WorkRoutes (AreaLotSimulationSystem just READS them — no sim echo).
            _createdRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<RouteWaypoint>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Created>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<WorkRoute>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
            });
            _updatedRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<RouteWaypoint>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<WorkRoute>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Created>(),
                },
            });
            // v55: same as _updatedRoutes but for SAVE-loaded lines that never got a CS2M_SyncId (only
            // in-session creates do). Editing such a line was silently dropped (delete/color/rename fall
            // back to prefab+RouteNumber, but reroute required a SyncId). We assign+broadcast a SyncId on
            // the first edit so both sims share identity, then it follows the normal id-based path. Exclude
            // CS2M_RemotePlaced so a remotely-applied reroute (which stamps id+RemotePlaced) never re-enters.
            _updatedSaveRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<RouteWaypoint>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<WorkRoute>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
            });
            _colorEvents = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Event>(),
                    ComponentType.ReadOnly<ColorUpdated>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });
            _allRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<WorkRoute>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            CS2M.Log.Info("[Route] RouteDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_eventClear >= 120)
            {
                _eventClear = 0;
                _sentEvents.Clear();
            }

            DetectCreated();
            DetectRerouted();
            DetectSaveRouteReroutes();
            DetectColorChanges();

            if (++_visFrame >= 60)
            {
                _visFrame = 0;
                DetectVisibility();
            }
        }

        /// <summary>v55: hide/show a line in the Transportation Overview toggles the HiddenRoute tag with no
        /// Updated/event, so a snapshot diff (~1 Hz) is the only signal. Addressed like colour (SyncId else
        /// prefab+RouteNumber). The apply refreshes the snapshot, so a remotely-applied toggle isn't echoed.</summary>
        private void DetectVisibility()
        {
            NativeArray<Entity> ents = _allRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    bool hidden = EntityManager.HasComponent<HiddenRoute>(e);
                    if (RouteSync.VisibilitySnapshot.TryGetValue(e, out bool prev) && prev == hidden)
                    {
                        continue;
                    }

                    bool firstSight = !RouteSync.VisibilitySnapshot.ContainsKey(e);
                    RouteSync.VisibilitySnapshot[e] = hidden;
                    if (firstSight)
                    {
                        continue; // baseline silently (save load / freshly created)
                    }

                    GetIdentity(e, out ulong id, out string prefabName, out int number);
                    if (id == 0 && number == 0)
                    {
                        continue; // unresolvable on the other side
                    }

                    Command.SendToAll?.Invoke(new RouteVisibilityCommand
                    {
                        SyncId = id, PrefabName = prefabName, Number = number, Hidden = hidden,
                    });
                    CS2M.Log.Info($"[Route] DETECT+SEND visibility id={id} number={number} hidden={hidden}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }

        /// <summary>v55: a rerouted SAVE-loaded line has no CS2M_SyncId, so the id-based DetectRerouted
        /// skips it and the reroute was silently dropped (yet delete/color/rename synced — they address by
        /// prefab+number). Mirror that: send the reroute addressed by prefab+number (SyncId=0, Replace=true)
        /// and let the receiver resolve by RouteNumber. A per-(prefab#number) content-hash guard stops the
        /// received-then-Updated line from pinging the command back. No SyncId allocation → no identity race
        /// between two sims touching the same untouched line in the same frame. Gated by
        /// <see cref="RouteFix.Enabled"/> (CS2M_ROUTEFIX, ON by default since 2026-07-07).</summary>
        private void DetectSaveRouteReroutes()
        {
            if (!RouteFix.Enabled)
            {
                return; // CS2M_ROUTEFIX=0 disables — see RouteFix doc comment
            }

            if (_updatedSaveRoutes.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> ents = _updatedSaveRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    RouteCreateCommand cmd = BuildCommand(e, 0, replace: true);
                    if (cmd == null || cmd.Number == 0)
                    {
                        continue; // number==0 is unresolvable on the other side
                    }

                    string key = RouteSync.DeleteKey(0, cmd.PrefabName, cmd.Number);
                    ulong hash = RouteSync.Hash(cmd);
                    if (RouteSync.SnapshotByNumber.TryGetValue(key, out ulong prev) && prev == hash)
                    {
                        continue; // unchanged since our last send / a remote apply — not a real reroute
                    }

                    RouteSync.SnapshotByNumber[key] = hash;
                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Info($"[Route] DETECT+SEND reroute (save-line by number) number={cmd.Number} " +
                                  $"prefab={cmd.PrefabName} wps={cmd.WpX.Length}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }

        private void DetectCreated()
        {
            if (_createdRoutes.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> ents = _createdRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    RouteCreateCommand cmd = BuildCommand(e, 0, replace: false);
                    if (cmd == null)
                    {
                        continue;
                    }

                    ulong id = CS2M_SyncIdSystem.Allocate();
                    cmd.SyncId = id;
                    CS2M_SyncIdSystem.Register(EntityManager, e, id);
                    RouteSync.Snapshot[id] = RouteSync.Hash(cmd);
                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Info($"[Route] DETECT+SEND create id={id} prefab={cmd.PrefabName} " +
                                  $"wps={cmd.WpX.Length} number={cmd.Number} complete={cmd.Complete}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }

        private void DetectRerouted()
        {
            if (_updatedRoutes.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> ents = _updatedRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    ulong id = EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id;
                    if (id == 0)
                    {
                        continue;
                    }

                    RouteCreateCommand cmd = BuildCommand(e, id, replace: true);
                    if (cmd == null)
                    {
                        continue;
                    }

                    ulong hash = RouteSync.Hash(cmd);
                    if (RouteSync.Snapshot.TryGetValue(id, out ulong prev) && prev == hash)
                    {
                        continue; // Updated for some other reason (or our own apply's echo)
                    }

                    RouteSync.Snapshot[id] = hash;
                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Info($"[Route] DETECT+SEND reroute id={id} wps={cmd.WpX.Length} complete={cmd.Complete}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }

        private void DetectColorChanges()
        {
            if (_colorEvents.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> events = _colorEvents.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity ev in events)
                {
                    if (!_sentEvents.Add(ev))
                    {
                        continue;
                    }

                    Entity route = EntityManager.GetComponentData<ColorUpdated>(ev).m_Route;
                    if (route == Entity.Null || !EntityManager.Exists(route)
                        || !EntityManager.HasComponent<Game.Routes.Color>(route)
                        || !EntityManager.HasComponent<Route>(route))
                    {
                        continue;
                    }

                    GetIdentity(route, out ulong id, out string prefabName, out int number);
                    if (id == 0 && number == 0)
                    {
                        continue; // unresolvable on the other side
                    }

                    var c = EntityManager.GetComponentData<Game.Routes.Color>(route).m_Color;
                    Command.SendToAll?.Invoke(new RouteColorCommand
                    {
                        SyncId = id,
                        PrefabName = prefabName,
                        Number = number,
                        ColorR = c.r, ColorG = c.g, ColorB = c.b, ColorA = c.a,
                    });
                    CS2M.Log.Info($"[Route] DETECT+SEND color id={id} number={number} rgb=({c.r},{c.g},{c.b})");
                }
            }
            finally
            {
                events.Dispose();
            }
        }

        private void GetIdentity(Entity route, out ulong id, out string prefabName, out int number)
        {
            id = EntityManager.HasComponent<CS2M_SyncId>(route)
                ? EntityManager.GetComponentData<CS2M_SyncId>(route).m_Id
                : 0;
            number = EntityManager.HasComponent<RouteNumber>(route)
                ? EntityManager.GetComponentData<RouteNumber>(route).m_Number
                : 0;
            prefabName = null;
            if (_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(route).m_Prefab,
                    out PrefabBase pb) && pb != null)
            {
                prefabName = pb.name;
            }
        }

        /// <summary>Snapshot the route's prefab/color/flags/waypoints into a wire command.</summary>
        private RouteCreateCommand BuildCommand(Entity route, ulong id, bool replace)
        {
            if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(route).m_Prefab,
                    out PrefabBase prefab) || prefab == null)
            {
                return null;
            }

            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(route, true);
            int n = wps.Length;
            if (n == 0)
            {
                return null;
            }

            var cmd = new RouteCreateCommand
            {
                SyncId = id,
                Replace = replace,
                PrefabType = prefab.GetType().Name,
                PrefabName = prefab.name,
                Complete = (EntityManager.GetComponentData<Route>(route).m_Flags & RouteFlags.Complete) != 0,
                Number = EntityManager.HasComponent<RouteNumber>(route)
                    ? EntityManager.GetComponentData<RouteNumber>(route).m_Number
                    : 0,
                WpX = new float[n], WpY = new float[n], WpZ = new float[n],
                WpHasConn = new byte[n],
                WpConnId = new ulong[n],
                WpConnX = new float[n], WpConnZ = new float[n],
            };

            var color = EntityManager.GetComponentData<Game.Routes.Color>(route).m_Color;
            cmd.ColorR = color.r;
            cmd.ColorG = color.g;
            cmd.ColorB = color.b;
            cmd.ColorA = color.a;

            for (int i = 0; i < n; i++)
            {
                Entity wp = wps[i].m_Waypoint;
                if (wp == Entity.Null || !EntityManager.HasComponent<Position>(wp))
                {
                    return null; // buffer mid-rebuild; the next Updated pass will retry
                }

                float3 p = EntityManager.GetComponentData<Position>(wp).m_Position;
                cmd.WpX[i] = p.x;
                cmd.WpY[i] = p.y;
                cmd.WpZ[i] = p.z;

                if (!EntityManager.HasComponent<Connected>(wp))
                {
                    continue;
                }

                Entity conn = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                if (conn == Entity.Null || !EntityManager.Exists(conn))
                {
                    continue;
                }

                cmd.WpHasConn[i] = 1;
                if (EntityManager.HasComponent<CS2M_SyncId>(conn))
                {
                    cmd.WpConnId[i] = EntityManager.GetComponentData<CS2M_SyncId>(conn).m_Id;
                }

                if (EntityManager.HasComponent<Game.Objects.Transform>(conn))
                {
                    float3 cp = EntityManager.GetComponentData<Game.Objects.Transform>(conn).m_Position;
                    cmd.WpConnX[i] = cp.x;
                    cmd.WpConnZ[i] = cp.z;
                }
                else
                {
                    cmd.WpConnX[i] = p.x;
                    cmd.WpConnZ[i] = p.z;
                }
            }

            return cmd;
        }
    }

    /// <summary>
    ///     Applies remote transport-line commands by building REAL entities from the route prefab's
    ///     baked archetypes (which already include <c>Created</c>+<c>Updated</c>), mirroring what
    ///     GenerateWaypoints/GenerateRoutes + ApplyRoutes produce for a fresh line. The game's own
    ///     systems then take over: ReferencesSystem wires Owner, WaypointConnectionSystem finds lanes
    ///     and maintains the stops' ConnectedRoute buffers, RoutePathSystem paths the segments and
    ///     TransportLineSystem dispatches vehicles. Runs before Modification1 (creation-phase law).
    /// </summary>
    public partial class RouteApplySystem : GameSystemBase
    {
        private Game.Prefabs.PrefabSystem _prefabSystem;
        private EntityQuery _routesByNumber;
        private EntityQuery _stops;
        private readonly List<PendingNumber> _pendingNumbers = new List<PendingNumber>();

        private struct PendingNumber
        {
            public Entity Route;
            public int Number;
            public int Delay;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>();
            _routesByNumber = GetEntityQuery(
                ComponentType.ReadOnly<Route>(),
                ComponentType.ReadOnly<RouteNumber>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            // Anything a waypoint can connect to keeps a ConnectedRoute buffer (stops, platforms).
            _stops = GetEntityQuery(
                ComponentType.ReadOnly<ConnectedRoute>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            CS2M.Log.Info("[Route] RouteApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            ProcessPendingNumbers();

            // v50: a stop object and the line that connects to it can arrive in the same frame, and
            // the two apply systems' relative order is unspecified — if a waypoint references a stop
            // SyncId that hasn't materialized yet, park the command and retry next frame (max 5).
            if (_deferredCreates.Count > 0)
            {
                List<RouteCreateCommand> retry = _deferredCreates;
                _deferredCreates = new List<RouteCreateCommand>();
                foreach (RouteCreateCommand cmd in retry)
                {
                    try { ApplyCreate(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] route create apply failed: {ex.Message}"); }
                }
            }

            while (RouteSync.TryDequeueCreate(out RouteCreateCommand cmd))
            {
                try { ApplyCreate(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] route create apply failed: {ex.Message}"); }
            }

            while (RouteSync.TryDequeueColor(out RouteColorCommand cmd))
            {
                try { ApplyColor(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] route color apply failed: {ex.Message}"); }
            }

            while (RouteSync.TryDequeueVisibility(out RouteVisibilityCommand vcmd))
            {
                try { ApplyVisibility(vcmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] route visibility apply failed: {ex.Message}"); }
            }
        }

        private void ApplyVisibility(RouteVisibilityCommand cmd)
        {
            Entity route = RouteResolver.Resolve(EntityManager, _routesByNumber, _prefabSystem,
                cmd.SyncId, cmd.PrefabName, cmd.Number);
            if (route == Entity.Null)
            {
                CS2M.Log.Info($"[Route] SKIP visibility noTarget id={cmd.SyncId} number={cmd.Number}");
                return;
            }

            bool isHidden = EntityManager.HasComponent<HiddenRoute>(route);
            if (cmd.Hidden && !isHidden)
            {
                EntityManager.AddComponent<HiddenRoute>(route);
            }
            else if (!cmd.Hidden && isHidden)
            {
                EntityManager.RemoveComponent<HiddenRoute>(route);
            }

            RouteSync.VisibilitySnapshot[route] = cmd.Hidden; // echo guard before the detector's next scan
            CS2M.Log.Info($"[Route] APPLIED visibility id={cmd.SyncId} number={cmd.Number} hidden={cmd.Hidden} entity={route.Index}");
        }

        /// <summary>The game's InitializeSystem numbers Created routes this same frame — writing the
        /// sender's number a couple frames later wins without racing it.</summary>
        private void ProcessPendingNumbers()
        {
            for (int i = _pendingNumbers.Count - 1; i >= 0; i--)
            {
                PendingNumber pn = _pendingNumbers[i];
                if (--pn.Delay > 0)
                {
                    _pendingNumbers[i] = pn;
                    continue;
                }

                _pendingNumbers.RemoveAt(i);
                if (pn.Number > 0 && EntityManager.Exists(pn.Route)
                    && EntityManager.HasComponent<RouteNumber>(pn.Route)
                    && !EntityManager.HasComponent<Deleted>(pn.Route))
                {
                    EntityManager.SetComponentData(pn.Route, new RouteNumber { m_Number = pn.Number });
                    CS2M.Log.Verbose($"[Route] number set entity={pn.Route.Index} number={pn.Number}");
                }
            }
        }

        private System.Collections.Generic.List<RouteCreateCommand> _deferredCreates =
            new System.Collections.Generic.List<RouteCreateCommand>();
        private readonly System.Collections.Generic.Dictionary<ulong, int> _deferCounts =
            new System.Collections.Generic.Dictionary<ulong, int>();

        /// <summary>True when a waypoint references a synced stop that doesn't exist locally YET
        /// (same-frame ordering). Position-addressed connections (id 0) never defer.</summary>
        private bool ShouldDefer(RouteCreateCommand cmd)
        {
            if (cmd.WpConnId == null)
            {
                return false;
            }

            for (int i = 0; i < cmd.WpConnId.Length; i++)
            {
                ulong id = cmd.WpConnId[i];
                if (id != 0 && (!CS2M_SyncIdSystem.Map.TryGetValue(id, out Entity e)
                                || !EntityManager.Exists(e)))
                {
                    return true;
                }
            }

            return false;
        }

        private void ApplyCreate(RouteCreateCommand cmd)
        {
            int n = cmd.WpX?.Length ?? 0;
            if (n == 0)
            {
                return;
            }

            if (ShouldDefer(cmd))
            {
                _deferCounts.TryGetValue(cmd.SyncId, out int tries);
                if (tries < 5)
                {
                    _deferCounts[cmd.SyncId] = tries + 1;
                    _deferredCreates.Add(cmd);
                    CS2M.Log.Verbose($"[Route] DEFER create id={cmd.SyncId} (stop not materialized yet, try {tries + 1}/5)");
                    return;
                }

                CS2M.Log.Info($"[Route] proceeding with unresolved stop id after 5 tries id={cmd.SyncId}");
            }

            _deferCounts.Remove(cmd.SyncId);

            if (cmd.Replace)
            {
                Entity existing = RouteResolver.Resolve(EntityManager, _routesByNumber, _prefabSystem,
                    cmd.SyncId, cmd.PrefabName, cmd.Number);
                if (existing != Entity.Null)
                {
                    Rebuild(existing, cmd);
                    return;
                }

                CS2M.Log.Info($"[Route] reroute target missing id={cmd.SyncId} — creating fresh");
            }
            else if (cmd.SyncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(cmd.SyncId, out Entity dup)
                     && EntityManager.Exists(dup))
            {
                CS2M.Log.Info($"[Route] SKIP duplicate id={cmd.SyncId}");
                return;
            }

            if (!TryGetRouteData(cmd, out Entity prefabEntity, out RouteData rd))
            {
                return;
            }

            Entity route = EntityManager.CreateEntity();
            EntityManager.SetArchetype(route, rd.m_RouteArchetype);
            SetOrAdd(route, new Route { m_Flags = cmd.Complete ? RouteFlags.Complete : (RouteFlags)0 });
            SetOrAdd(route, new PrefabRef(prefabEntity));
            SetOrAdd(route, new Game.Routes.Color(new UnityEngine.Color32(cmd.ColorR, cmd.ColorG, cmd.ColorB, cmd.ColorA)));
            if (EntityManager.HasComponent<TransportLineData>(prefabEntity))
            {
                SetOrAdd(route, new TransportLine(EntityManager.GetComponentData<TransportLineData>(prefabEntity)));
            }
            else if (EntityManager.HasComponent<WorkRouteData>(prefabEntity)
                     && !EntityManager.HasComponent<WorkRoute>(route))
            {
                // v59: work route — the baked archetype already carries the empty WorkRoute tag
                // (WorkRoutePrefab.GetArchetypeComponents), this is just a belt-and-braces guarantee.
                EntityManager.AddComponent<WorkRoute>(route);
            }

            BuildElements(route, prefabEntity, rd, cmd);

            // v50: do NOT add Applied to the route — RouteBufferSystem only initializes render
            // buffers for chunks with Created && !Applied. With Applied the route kept the
            // archetype-default RouteBufferIndex(0), which aliased another line's buffer and
            // NRE'd the renderer when no line existed yet (the "critical" the host saw).
            // Born with -1 ("no buffer") the init path assigns a real slot and the line DRAWS.
            if (EntityManager.HasComponent<Game.Rendering.RouteBufferIndex>(route))
            {
                EntityManager.SetComponentData(route, new Game.Rendering.RouteBufferIndex { m_Index = -1 });
            }

            EntityManager.AddComponent<CS2M_RemotePlaced>(route);
            if (cmd.SyncId != 0)
            {
                CS2M_SyncIdSystem.Register(EntityManager, route, cmd.SyncId);
                RouteSync.Snapshot[cmd.SyncId] = RouteSync.Hash(cmd);
            }

            _pendingNumbers.Add(new PendingNumber { Route = route, Number = cmd.Number, Delay = 3 });
            CS2M.Log.Info($"[Route] APPLIED create id={cmd.SyncId} prefab={cmd.PrefabName} wps={n} entity={route.Index}");
        }

        /// <summary>Replace path: mark the old elements Deleted, build the new set, rewrite the
        /// buffers in place (what the game's ApplyRoutes.Update does for a modified line).</summary>
        private void Rebuild(Entity route, RouteCreateCommand cmd)
        {
            if (!TryGetRouteData(cmd, out Entity prefabEntity, out RouteData rd))
            {
                return;
            }

            var old = new List<Entity>();
            DynamicBuffer<RouteWaypoint> wpsBuf = EntityManager.GetBuffer<RouteWaypoint>(route, true);
            for (int i = 0; i < wpsBuf.Length; i++)
            {
                if (wpsBuf[i].m_Waypoint != Entity.Null) { old.Add(wpsBuf[i].m_Waypoint); }
            }

            DynamicBuffer<RouteSegment> segsBuf = EntityManager.GetBuffer<RouteSegment>(route, true);
            for (int i = 0; i < segsBuf.Length; i++)
            {
                if (segsBuf[i].m_Segment != Entity.Null) { old.Add(segsBuf[i].m_Segment); }
            }

            foreach (Entity e in old)
            {
                if (EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e))
                {
                    EntityManager.AddComponent<Deleted>(e);
                }
            }

            BuildElements(route, prefabEntity, rd, cmd);

            Route r = EntityManager.GetComponentData<Route>(route);
            r.m_Flags = cmd.Complete ? (r.m_Flags | RouteFlags.Complete) : (r.m_Flags & ~RouteFlags.Complete);
            EntityManager.SetComponentData(route, r);

            if (!EntityManager.HasComponent<Updated>(route))
            {
                EntityManager.AddComponent<Updated>(route);
            }

            if (cmd.SyncId != 0)
            {
                RouteSync.Snapshot[cmd.SyncId] = RouteSync.Hash(cmd);
            }
            else if (cmd.Number != 0 && !string.IsNullOrEmpty(cmd.PrefabName))
            {
                // v55: save-line reroute addressed by prefab+number. Stamp the by-number guard to the
                // geometry we just rebuilt so this receiver's DetectSaveRouteReroutes sees the freshly
                // Updated line as unchanged and doesn't ping the reroute back (echo loop). No SyncId is
                // registered — the line stays number-addressed and either player can reroute it again.
                RouteSync.SnapshotByNumber[RouteSync.DeleteKey(0, cmd.PrefabName, cmd.Number)] =
                    RouteSync.Hash(cmd);
            }

            CS2M.Log.Info($"[Route] APPLIED reroute id={cmd.SyncId} number={cmd.Number} " +
                          $"wps={cmd.WpX.Length} entity={route.Index}");
        }

        /// <summary>Creates the waypoint/segment entities and rewrites the route's buffers.
        /// Owner is set explicitly (the game's ReferencesSystem only wires Created routes).</summary>
        private void BuildElements(Entity route, Entity prefabEntity, RouteData rd, RouteCreateCommand cmd)
        {
            int n = cmd.WpX.Length;
            var wpEntities = new Entity[n];
            for (int i = 0; i < n; i++)
            {
                bool wantConn = cmd.WpHasConn != null && cmd.WpHasConn[i] != 0;
                Entity conn = Entity.Null;
                if (wantConn)
                {
                    conn = ResolveConnection(cmd.WpConnId[i], cmd.WpConnX[i], cmd.WpConnZ[i]);
                    if (conn == Entity.Null)
                    {
                        CS2M.Log.Info($"[Route] WARN stop connection unresolved wp={i} " +
                                      $"at=({cmd.WpConnX[i]:F0},{cmd.WpConnZ[i]:F0}) — creating plain waypoint");
                    }
                }

                Entity wp = EntityManager.CreateEntity();
                EntityManager.SetArchetype(wp, conn != Entity.Null ? rd.m_ConnectedArchetype : rd.m_WaypointArchetype);
                SetOrAdd(wp, new Waypoint(i));
                SetOrAdd(wp, new Position(new float3(cmd.WpX[i], cmd.WpY[i], cmd.WpZ[i])));
                SetOrAdd(wp, new PrefabRef(prefabEntity));
                SetOrAdd(wp, new Owner(route));
                if (conn != Entity.Null)
                {
                    SetOrAdd(wp, new Connected(conn));
                }

                if (!EntityManager.HasComponent<Applied>(wp))
                {
                    EntityManager.AddComponent<Applied>(wp);
                }

                EntityManager.AddComponent<CS2M_RemotePlaced>(wp);
                wpEntities[i] = wp;
            }

            int segCount = cmd.Complete ? n : n - 1;
            if (n < 2)
            {
                segCount = 0;
            }

            var segEntities = new Entity[segCount < 0 ? 0 : segCount];
            for (int i = 0; i < segEntities.Length; i++)
            {
                Entity seg = EntityManager.CreateEntity();
                EntityManager.SetArchetype(seg, rd.m_SegmentArchetype);
                SetOrAdd(seg, new Segment(i));
                SetOrAdd(seg, new PrefabRef(prefabEntity));
                SetOrAdd(seg, new Owner(route));
                if (!EntityManager.HasComponent<Applied>(seg))
                {
                    EntityManager.AddComponent<Applied>(seg);
                }

                EntityManager.AddComponent<CS2M_RemotePlaced>(seg);
                segEntities[i] = seg;
            }

            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(route);
            wps.ResizeUninitialized(n);
            for (int i = 0; i < n; i++)
            {
                wps[i] = new RouteWaypoint(wpEntities[i]);
            }

            DynamicBuffer<RouteSegment> segs = EntityManager.GetBuffer<RouteSegment>(route);
            segs.ResizeUninitialized(segEntities.Length);
            for (int i = 0; i < segEntities.Length; i++)
            {
                segs[i] = new RouteSegment(segEntities[i]);
            }
        }

        private void ApplyColor(RouteColorCommand cmd)
        {
            Entity route = RouteResolver.Resolve(EntityManager, _routesByNumber, _prefabSystem,
                cmd.SyncId, cmd.PrefabName, cmd.Number);
            if (route == Entity.Null || !EntityManager.HasComponent<Game.Routes.Color>(route))
            {
                CS2M.Log.Info($"[Route] SKIP color noTarget id={cmd.SyncId} number={cmd.Number}");
                return;
            }

            var color = new UnityEngine.Color32(cmd.ColorR, cmd.ColorG, cmd.ColorB, cmd.ColorA);
            EntityManager.SetComponentData(route, new Game.Routes.Color(color));

            // Vehicles carry their own Color copy (what the ColorSection UI does).
            if (EntityManager.HasBuffer<RouteVehicle>(route))
            {
                DynamicBuffer<RouteVehicle> vehicles = EntityManager.GetBuffer<RouteVehicle>(route, true);
                var list = new List<Entity>();
                for (int i = 0; i < vehicles.Length; i++)
                {
                    if (vehicles[i].m_Vehicle != Entity.Null && EntityManager.Exists(vehicles[i].m_Vehicle))
                    {
                        list.Add(vehicles[i].m_Vehicle);
                    }
                }

                foreach (Entity v in list)
                {
                    SetOrAdd(v, new Game.Routes.Color(color));
                }
            }

            // Same notification event the UI raises so the renderer refreshes; tagged so our own
            // detector never echoes it back.
            Entity ev = EntityManager.CreateEntity();
            EntityManager.AddComponent<Event>(ev);
            EntityManager.AddComponentData(ev, new ColorUpdated(route));
            EntityManager.AddComponent<CS2M_RemotePlaced>(ev);

            CS2M.Log.Info($"[Route] APPLIED color id={cmd.SyncId} number={cmd.Number} rgb=({cmd.ColorR},{cmd.ColorG},{cmd.ColorB})");
        }

        private bool TryGetRouteData(RouteCreateCommand cmd, out Entity prefabEntity, out RouteData rd)
        {
            prefabEntity = Entity.Null;
            rd = default(RouteData);
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out prefabEntity))
            {
                CS2M.Log.Info($"[Route] RESOLVE-FAIL prefab type={cmd.PrefabType} name={cmd.PrefabName}");
                return false;
            }

            if (!EntityManager.HasComponent<RouteData>(prefabEntity))
            {
                CS2M.Log.Info($"[Route] RESOLVE-FAIL no RouteData name={cmd.PrefabName}");
                return false;
            }

            rd = EntityManager.GetComponentData<RouteData>(prefabEntity);
            return rd.m_RouteArchetype.Valid && rd.m_WaypointArchetype.Valid && rd.m_SegmentArchetype.Valid;
        }

        /// <summary>Stop connections: SyncId when the stop is a synced object, else the nearest
        /// entity holding a ConnectedRoute buffer (stops/platforms) within ~2.5 m.</summary>
        private Entity ResolveConnection(ulong syncId, float x, float z)
        {
            if (syncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(syncId, out Entity byId)
                && EntityManager.Exists(byId) && !EntityManager.HasComponent<Deleted>(byId))
            {
                return byId;
            }

            Entity best = Entity.Null;
            float bestD = 6.25f; // squared meters
            NativeArray<Entity> ents = _stops.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - x;
                    float dz = p.z - z;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = cand;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return best;
        }

        private void SetOrAdd<T>(Entity e, T data) where T : unmanaged, IComponentData
        {
            if (EntityManager.HasComponent<T>(e))
            {
                EntityManager.SetComponentData(e, data);
            }
            else
            {
                EntityManager.AddComponentData(e, data);
            }
        }
    }
}
