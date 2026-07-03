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
        private EntityQuery _appliedExtensions;

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
                    // v50: standalone stop OBJECTS (bus stop shelters, taxi stands, mailboxes…) are
                    // real placeable objects and DO sync now — only route ELEMENTS stay excluded.
                    ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                    ComponentType.ReadOnly<Game.Routes.Waypoint>(),
                    ComponentType.ReadOnly<Game.Routes.WaypointDefinition>(),
                },
            });

            // v44: service-building extensions — Applied objects WITH an Owner whose owner is a
            // building (the main query excludes Owner to skip auto sub-objects; extensions are the
            // one player-placed Owner case worth syncing).
            _appliedExtensions = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Applied>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            RequireAnyForUpdate(_appliedQuery, _appliedExtensions);
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

                    // v50: road-side attachments (stop shelters, taxi stands, mailboxes…) — ship the
                    // exact point on the parent edge's curve so the receiver re-attaches to the SAME
                    // edge. Reuses OwnerX/Y/Z, which extensions use for buildings (this entity has no
                    // Owner, so the receiver can't confuse the two: OwnerPrefabName stays empty).
                    if (EntityManager.HasComponent<Attached>(entity))
                    {
                        Attached att = EntityManager.GetComponentData<Attached>(entity);
                        if (att.m_Parent != Entity.Null
                            && EntityManager.HasComponent<Game.Net.Curve>(att.m_Parent))
                        {
                            Game.Net.Curve curve =
                                EntityManager.GetComponentData<Game.Net.Curve>(att.m_Parent);
                            float3 onCurve = Colossal.Mathematics.MathUtils.Position(
                                curve.m_Bezier, att.m_CurvePosition);
                            cmd.OwnerX = onCurve.x;
                            cmd.OwnerY = onCurve.y;
                            cmd.OwnerZ = onCurve.z;
                        }
                    }

                    // Stamp a cross-PC id on our own entity and ship it so the other PC's copy
                    // gets the same id (enables later move/delete of this object).
                    cmd.SyncId = CS2M_SyncIdSystem.Allocate();
                    CS2M_SyncIdSystem.Register(EntityManager, entity, cmd.SyncId);

                    CS2M.Log.Info(
                        $"[Place] DETECT+RESOLVE-OK type={cmd.PrefabType} name={cmd.PrefabName} " +
                        $"pos=({cmd.PosX:F1},{cmd.PosY:F1},{cmd.PosZ:F1}) seed={cmd.RandomSeed} " +
                        $"elev={cmd.Elevation:F2} syncId={cmd.SyncId} tool={toolId} entity={entity.Index}");

                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Info($"[Place] SEND name={cmd.PrefabName}");
                }
            }
            finally
            {
                applied.Dispose();
            }

            DetectExtensions(toolId);
        }

        /// <summary>
        ///     v44: service-building extensions (hospital wings etc.) — Applied objects with an Owner
        ///     that is a building. Shipped with the owner's identity (SyncId when synced, else
        ///     prefab+position) so the receiver re-attaches them; ServiceUpgradeSystem wires the rest.
        /// </summary>
        private void DetectExtensions(string toolId)
        {
            if (_appliedExtensions.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> applied = _appliedExtensions.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity entity in applied)
                {
                    if (!_recentlySent.Add(entity))
                    {
                        continue;
                    }

                    Entity owner = EntityManager.GetComponentData<Owner>(entity).m_Owner;
                    if (!EntityManager.HasComponent<Game.Buildings.Building>(owner)
                        || !EntityManager.HasComponent<Game.Objects.Transform>(owner)
                        || !EntityManager.HasComponent<PrefabRef>(owner))
                    {
                        continue; // only building extensions; other owned sub-objects are derived
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab,
                            out PrefabBase ownerPrefab) || ownerPrefab == null)
                    {
                        continue;
                    }

                    Game.Objects.Transform tf = EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                    Game.Objects.Transform ownerTf = EntityManager.GetComponentData<Game.Objects.Transform>(owner);

                    var cmd = new ObjectPlaceCommand
                    {
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        PosX = tf.m_Position.x, PosY = tf.m_Position.y, PosZ = tf.m_Position.z,
                        RotX = tf.m_Rotation.value.x, RotY = tf.m_Rotation.value.y,
                        RotZ = tf.m_Rotation.value.z, RotW = tf.m_Rotation.value.w,
                        RandomSeed = ReadSeed(entity),
                        OwnerSyncId = EntityManager.HasComponent<CS2M_SyncId>(owner)
                            ? EntityManager.GetComponentData<CS2M_SyncId>(owner).m_Id
                            : 0,
                        OwnerPrefabName = ownerPrefab.name,
                        OwnerX = ownerTf.m_Position.x,
                        OwnerY = ownerTf.m_Position.y,
                        OwnerZ = ownerTf.m_Position.z,
                    };

                    cmd.SyncId = CS2M_SyncIdSystem.Allocate();
                    CS2M_SyncIdSystem.Register(EntityManager, entity, cmd.SyncId);

                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Info(
                        $"[Place] DETECT+SEND extension name={cmd.PrefabName} owner={cmd.OwnerPrefabName} " +
                        $"ownerSyncId={cmd.OwnerSyncId} tool={toolId} entity={entity.Index}");
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
