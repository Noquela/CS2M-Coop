using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects when the local player bulldozes a synced object (one carrying <c>CS2M_SyncId</c>)
    ///     and broadcasts a <see cref="DeleteCommand"/>. Only top-level objects (no <c>Owner</c>) are
    ///     sent — the game cascades sub-object deletion. Objects we deleted from a remote command carry
    ///     <c>CS2M_RemotePlaced</c> and are excluded (echo guard).
    /// </summary>
    public partial class DeleteDetectorSystem : GameSystemBase
    {
        private EntityQuery _deletedQuery;
        private EntityQuery _deletedNativeQuery;
        private EntityQuery _deletedRouteQuery;
        private ToolSystem _toolSystem;
        private PrefabSystem _prefabSystem;
        private readonly HashSet<ulong> _recentlySent = new HashSet<ulong>();
        private readonly HashSet<Entity> _nativesSent = new HashSet<Entity>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _deletedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Static>(),
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            // v49: transport lines — the info panel deletes them with a bare AddComponent<Deleted>.
            // Addressed by SyncId or prefab + RouteNumber (save-loaded lines have no SyncId).
            // NOTE: no CS2M_RemotePlaced exclusion here — remotely-created lines carry it forever and
            // deleting one must still sync; the echo guard is RouteSync.ConsumeDeleteEcho instead.
            _deletedRouteQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // v42: NATIVE objects (from the save, no CS2M_SyncId) — addressed by prefab + position.
            _deletedNativeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Static>(),
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
            });
            RequireAnyForUpdate(_deletedQuery, _deletedNativeQuery, _deletedRouteQuery);
            CS2M.Log.Info("[Del] DeleteDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_clearCounter >= 120)
            {
                _clearCounter = 0;
                _recentlySent.Clear();
            }

            NativeArray<Entity> ents = _deletedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    ulong id = EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id;
                    if (id == 0 || !_recentlySent.Add(id))
                    {
                        continue;
                    }

                    Command.SendToAll?.Invoke(new DeleteCommand { SyncId = id });
                    CS2M_SyncIdSystem.Map.Remove(id);
                    CS2M.Log.Info($"[Del] DETECT+SEND id={id} entity={e.Index}");
                }
            }
            finally
            {
                ents.Dispose();
            }

            DetectNativeDeletes();
            DetectRouteDeletes();
        }

        /// <summary>v49: deleted transport lines. Waypoints/segments are NOT sent — both sides'
        /// ElementSystem cascades them from the route's Deleted.</summary>
        private void DetectRouteDeletes()
        {
            if (_deletedRouteQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> ents = _deletedRouteQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_nativesSent.Add(e))
                    {
                        continue;
                    }

                    ulong id = EntityManager.HasComponent<CS2M_SyncId>(e)
                        ? EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id
                        : 0;
                    int number = EntityManager.HasComponent<Game.Routes.RouteNumber>(e)
                        ? EntityManager.GetComponentData<Game.Routes.RouteNumber>(e).m_Number
                        : 0;
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    if (id == 0 && number == 0)
                    {
                        continue; // unresolvable remotely
                    }

                    if (RouteSync.ConsumeDeleteEcho(RouteSync.DeleteKey(id, prefab.name, number)))
                    {
                        if (id != 0)
                        {
                            CS2M_SyncIdSystem.Map.Remove(id);
                            RouteSync.Snapshot.Remove(id);
                        }

                        continue; // this deletion came FROM the network
                    }

                    if (id != 0)
                    {
                        CS2M_SyncIdSystem.Map.Remove(id);
                        RouteSync.Snapshot.Remove(id);
                    }

                    Command.SendToAll?.Invoke(new DeleteCommand
                    {
                        SyncId = id,
                        TargetKind = 1,
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        Number = number,
                    });
                    CS2M.Log.Info($"[Del] DETECT+SEND route id={id} number={number} name={prefab.name}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }

        /// <summary>
        ///     v42: bulldozed NATIVE objects (no CS2M_SyncId) sync by prefab + position. Gated on the
        ///     bulldoze tool being active so simulation-driven demolitions (growable level-ups,
        ///     condemned buildings) are NOT synced — each PC's sim manages its own emergent stock.
        /// </summary>
        private void DetectNativeDeletes()
        {
            if (_deletedNativeQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            string toolId = _toolSystem.activeTool != null ? _toolSystem.activeTool.toolID : null;
            bool bulldozing = toolId != null && toolId.Contains("Bulldoze");

            NativeArray<Entity> ents = _deletedNativeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_nativesSent.Add(e))
                    {
                        continue;
                    }

                    Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;

                    // v46.1: growables (SpawnableBuildingData prefabs) churn constantly under the sim
                    // (level-ups, condemned) — only sync those while the player is actively bulldozing.
                    // Everything else (service buildings, trees, props) is a player action from ANY
                    // path, including the info-panel delete button (no bulldozer in hand).
                    bool growable = EntityManager.HasComponent<SpawnableBuildingData>(prefabEntity);
                    if (growable && !bulldozing)
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    var pos = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                    Command.SendToAll?.Invoke(new DeleteCommand
                    {
                        SyncId = 0,
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    });
                    CS2M.Log.Info($"[Del] DETECT+SEND native name={prefab.name} pos=({pos.x:F1},{pos.z:F1}) entity={e.Index}");
                }
            }
            finally
            {
                ents.Dispose();
            }

            if (_clearCounter == 0)
            {
                _nativesSent.Clear();
            }
        }
    }
}
