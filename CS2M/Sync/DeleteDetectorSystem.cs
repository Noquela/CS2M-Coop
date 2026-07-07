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
    ///     <c>CS2M_RemoteDeleted</c> (stamped at remote-apply time by CascadeDeleteUtil) and are
    ///     excluded (delete-echo guard — v56: was CS2M_RemotePlaced, which wrongly also swallowed a
    ///     local player's delete of remote-built objects, since that tag marks CREATION and never dies).
    ///
    ///     v56: EXCEPTION for installed service-building extensions (hospital wings etc.) — the
    ///     upgrades panel's "delete" button (<c>UpgradesSection.OnDelete</c>) marks <c>Deleted</c>
    ///     DIRECTLY on the extension entity, which carries <c>Owner</c> (the building). That is a
    ///     genuine top-level player action, not derived-sub-object cascade, and the extension already
    ///     got a <c>CS2M_SyncId</c> when it was planted (<see cref="PlacementDetectorSystem.DetectExtensions"/>),
    ///     so <see cref="_deletedExtensionQuery"/> re-admits Owner but gates on the SAME prefab check
    ///     used at plant time (<c>ServiceUpgradeData</c>/<c>BuildingExtensionData</c>) so ordinary
    ///     derived sub-objects (walls, pipes, decorations — never allocated a SyncId) still don't leak.
    ///
    ///     PHASE (v57 fix — see Mod.cs registration): runs at <c>SystemUpdatePhase.Rendering</c>, not
    ///     ModificationEnd. Abandoned-building teardown (<c>Game.Simulation.CollapsedBuildingSystem</c>)
    ///     and the level-up old-building delete (<c>Game.Simulation.ZoneSpawnSystem</c>) both mark
    ///     <c>Deleted</c> at <c>SystemUpdatePhase.GameSimulation</c> — AFTER ModificationEnd in the
    ///     phase enum, so a detector sitting at ModificationEnd never saw them before
    ///     <c>Game.Common.CleanUpSystem</c> (phase Cleanup, the LAST phase) physically destroyed the
    ///     entity that same frame. Rendering runs after GameSimulation and before Cleanup, so all four
    ///     queries below still catch the tag. Player-driven deletes (bulldoze/info-panel), which mark
    ///     Deleted at some Modification1-9 phase, are unaffected — Rendering is strictly later in the
    ///     same frame than ModificationEnd was, so nothing that used to be caught is now missed.
    /// </summary>
    public partial class DeleteDetectorSystem : GameSystemBase
    {
        private EntityQuery _deletedQuery;
        private EntityQuery _deletedNativeQuery;
        private EntityQuery _deletedRouteQuery;
        private EntityQuery _deletedExtensionQuery;
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
                    // Delete-echo tag (stamped by CascadeDeleteUtil on remote-applied deletes). NOT
                    // CS2M_RemotePlaced: that marks remote CREATION and lives forever, so excluding it
                    // here swallowed a local player's bulldoze of anything a remote player had built.
                    ComponentType.ReadOnly<CS2M_RemoteDeleted>(),
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
                    ComponentType.ReadOnly<CS2M_RemoteDeleted>(), // delete-echo, not creation-echo (see above)
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
            });
            // v56: installed service-upgrade extensions — deleted directly via the upgrades panel
            // (Owner points at the building). Re-admits Owner (excluded above) but the prefab gate in
            // DetectExtensionDeletes keeps ordinary owned sub-objects (which never got a CS2M_SyncId
            // in the first place) from leaking through.
            _deletedExtensionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<CS2M_RemoteDeleted>(), // delete-echo, not creation-echo (see above)
                },
            });

            RequireAnyForUpdate(_deletedQuery, _deletedNativeQuery, _deletedRouteQuery, _deletedExtensionQuery);
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
            DetectExtensionDeletes();
        }

        /// <summary>
        ///     v56: installed service-upgrade extensions deleted straight from the upgrades panel
        ///     (<c>UpgradesSection.OnDelete</c> → bare <c>AddComponent&lt;Deleted&gt;</c> on the
        ///     extension entity, which has <c>Owner</c> == the building). The main <see cref="_deletedQuery"/>
        ///     deliberately excludes <c>Owner</c> so automatic sub-object cascade doesn't get resent —
        ///     this is the one owned case that IS a top-level player action. Gated on the same prefab
        ///     check <see cref="PlacementDetectorSystem.DetectExtensions"/> uses at plant time
        ///     (<c>ServiceUpgradeData</c>/<c>BuildingExtensionData</c>) so plain derived sub-objects
        ///     (which never got a <c>CS2M_SyncId</c> to begin with) can't leak through even if some
        ///     future path stamped one on them.
        /// </summary>
        private void DetectExtensionDeletes()
        {
            if (_deletedExtensionQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> ents = _deletedExtensionQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (!EntityManager.HasComponent<ServiceUpgradeData>(prefabEntity)
                        && !EntityManager.HasComponent<BuildingExtensionData>(prefabEntity))
                    {
                        continue; // not a building extension — leave it to the vanilla cascade
                    }

                    ulong id = EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id;
                    if (id == 0 || !_recentlySent.Add(id))
                    {
                        continue;
                    }

                    // Same command top-level objects use — the receiver resolves by SyncId regardless
                    // of the target having an Owner (RemoteEditApplySystem.ApplyDelete never filters
                    // on Owner, only on CS2M_SyncId resolution).
                    Command.SendToAll?.Invoke(new DeleteCommand { SyncId = id });
                    CS2M_SyncIdSystem.Map.Remove(id);
                    CS2M.Log.Info($"[Del] DETECT+SEND extension id={id} entity={e.Index}");
                }
            }
            finally
            {
                ents.Dispose();
            }
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
                    // v50: with host-authoritative growables the HOST's sim owns the stock, so its
                    // sim-driven demolitions (abandoned teardown…) DO sync; the client keeps the gate.
                    bool growable = EntityManager.HasComponent<SpawnableBuildingData>(prefabEntity);
                    bool hostOwnsGrowables = GrowableDetectorSystem.Enabled_
                        && NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER;
                    if (growable && !bulldozing && !hostOwnsGrowables)
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
