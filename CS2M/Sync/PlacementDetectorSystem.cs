using System.Collections.Generic;
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
using Unity.Mathematics;
using CS2M.API.Commands;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects objects the local player just placed with the Object/Line tool and
    ///     broadcasts a <see cref="ObjectPlaceCommand"/> so the other players recreate them.
    ///
    ///     The authoritative signal is <c>Game.Common.Applied</c> — a transient tag the
    ///     tool-apply pipeline adds to a freshly committed permanent object (growables,
    ///     trees-on-load, vehicles etc. never get it). The query is copied from Anarchy's
    ///     verified <c>AnarchyPlopSystem.m_AppliedQuery</c>, plus our own
    ///     <see cref="CS2M_RemotePlaced"/> exclusion so we never echo objects that arrived
    ///     from the other player.
    ///
    ///     Runs just before <c>ModificationEnd</c> (same slot Anarchy uses) where
    ///     <c>Applied</c> is reliably visible.
    /// </summary>
    public partial class PlacementDetectorSystem : GameSystemBase
    {
        private ToolSystem _toolSystem;
        private PrefabSystem _prefabSystem;
        private EntityQuery _appliedQuery;

        // Guards against sending the same entity twice if Applied lingers across frames.
        private readonly HashSet<Entity> _recentlySent = new HashSet<Entity>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _appliedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Applied>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Static>(),
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Game.Tools.EditorContainer>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),                 // drop sub-objects of a placed building
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),     // echo guard
                    ComponentType.ReadOnly<Game.Creatures.Animal>(),
                    ComponentType.ReadOnly<Game.Creatures.Pet>(),
                    ComponentType.ReadOnly<Game.Creatures.Creature>(),
                    ComponentType.ReadOnly<Moving>(),
                    ComponentType.ReadOnly<Game.Citizens.Household>(),
                    ComponentType.ReadOnly<Game.Vehicles.Vehicle>(),
                    ComponentType.ReadOnly<Game.Common.Event>(),
                    ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                    ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                    ComponentType.ReadOnly<Game.Routes.TramStop>(),
                    ComponentType.ReadOnly<Game.Routes.TrainStop>(),
                    ComponentType.ReadOnly<Game.Routes.AirplaneStop>(),
                    ComponentType.ReadOnly<Game.Routes.BusStop>(),
                    ComponentType.ReadOnly<Game.Routes.ShipStop>(),
                    ComponentType.ReadOnly<Game.Routes.TakeoffLocation>(),
                    ComponentType.ReadOnly<Game.Routes.TaxiStand>(),
                    ComponentType.ReadOnly<Game.Routes.Waypoint>(),
                    ComponentType.ReadOnly<Game.Routes.MailBox>(),
                    ComponentType.ReadOnly<Game.Routes.WaypointDefinition>(),
                },
            });

            RequireForUpdate(_appliedQuery);
            CS2M.Log.Info("[Place] PlacementDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // Periodically forget old entities so index reuse eventually resends.
            if (++_clearCounter >= 120)
            {
                _clearCounter = 0;
                _recentlySent.Clear();
            }

            ToolBaseSystem activeTool = _toolSystem.activeTool;
            string toolId = activeTool != null ? activeTool.toolID : null;

            NativeArray<Entity> applied = _appliedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity entity in applied)
                {
                    if (!_recentlySent.Add(entity))
                    {
                        continue; // already sent this one recently
                    }

                    if (!EntityManager.HasComponent<PrefabRef>(entity))
                    {
                        CS2M.Log.Info($"[Place] SKIP reason=noPrefabRef entity={entity.Index} tool={toolId}");
                        continue;
                    }

                    if (!EntityManager.HasComponent<Game.Objects.Transform>(entity))
                    {
                        CS2M.Log.Info($"[Place] SKIP reason=noTransform entity={entity.Index} tool={toolId}");
                        continue;
                    }

                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
                    if (!_prefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) || prefab == null)
                    {
                        CS2M.Log.Info($"[Place] SKIP reason=noPrefab entity={entity.Index} tool={toolId}");
                        continue;
                    }

                    Game.Objects.Transform transform =
                        EntityManager.GetComponentData<Game.Objects.Transform>(entity);

                    var cmd = new ObjectPlaceCommand
                    {
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        // Base-game content resolves by type+name; hash stays 0 (v6 for modded assets).
                        Hash0 = 0,
                        Hash1 = 0,
                        Hash2 = 0,
                        Hash3 = 0,
                        PosX = transform.m_Position.x,
                        PosY = transform.m_Position.y,
                        PosZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x,
                        RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z,
                        RotW = transform.m_Rotation.value.w,
                        RandomSeed = ReadSeed(entity),
                    };

                    if (EntityManager.HasComponent<Elevation>(entity))
                    {
                        Elevation elevation = EntityManager.GetComponentData<Elevation>(entity);
                        cmd.Elevation = elevation.m_Elevation;
                        cmd.ElevationFlags = (byte) elevation.m_Flags;
                    }

                    CS2M.Log.Info(
                        $"[Place] DETECT+RESOLVE-OK type={cmd.PrefabType} name={cmd.PrefabName} " +
                        $"pos=({cmd.PosX:F1},{cmd.PosY:F1},{cmd.PosZ:F1}) seed={cmd.RandomSeed} " +
                        $"elev={cmd.Elevation:F2} tool={toolId} entity={entity.Index}");

                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Info($"[Place] SEND name={cmd.PrefabName}");
                }
            }
            finally
            {
                applied.Dispose();
            }
        }

        private int ReadSeed(Entity entity)
        {
            if (EntityManager.HasComponent<PseudoRandomSeed>(entity))
            {
                return EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed;
            }

            return 0;
        }
    }
}
