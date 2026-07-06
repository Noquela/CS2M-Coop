using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Applies a remote district-assignment edit (see <see cref="ServiceDistrictDetectorSystem"/>).
    ///     Resolves the target building by <c>CS2M_SyncId</c>, else nearest same-prefab building within
    ///     ~3 m (native save buildings never get a SyncId — same fallback
    ///     <see cref="RemotePlacementApplySystem"/>.ResolveOwner / <see cref="RemoteEditApplySystem"/>.FindNative
    ///     use), resolves each served district via <see cref="DistrictResolver.FindByCenter"/>, and
    ///     REWRITES the building's <c>ServiceDistrict</c> buffer to the sender's full list (idempotent —
    ///     safe to re-apply). Refreshes <see cref="ServiceDistrictSync.Snapshot"/> so this PC's own
    ///     detector doesn't echo the rewrite back.
    /// </summary>
    public partial class ServiceDistrictApplySystem : GameSystemBase
    {
        private CS2M_SyncIdSystem _idSystem;
        private PrefabSystem _prefabSystem;
        private EntityQuery _buildings;
        private EntityQuery _districts;

        protected override void OnCreate()
        {
            base.OnCreate();
            _idSystem = World.GetOrCreateSystemManaged<CS2M_SyncIdSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _buildings = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ServiceDistrict>(),
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _districts = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<District>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            CS2M.Log.Info("[ServiceDistrict] ServiceDistrictApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteServiceDistrictQueue.TryDequeue(out ServiceDistrictCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] service-district apply failed: {ex.Message}"); }
            }
        }

        private void ApplyOne(ServiceDistrictCommand cmd)
        {
            Entity building = ResolveBuilding(cmd);
            if (building == Entity.Null || !EntityManager.HasBuffer<ServiceDistrict>(building))
            {
                CS2M.Log.Info($"[ServiceDistrict] SKIP noTarget building={cmd.BuildingPrefabName}");
                return;
            }

            DynamicBuffer<ServiceDistrict> buf = EntityManager.GetBuffer<ServiceDistrict>(building);
            buf.Clear();

            int n = cmd.DistrictPrefabNames?.Length ?? 0;
            int applied = 0;
            for (int i = 0; i < n; i++)
            {
                Entity district = DistrictResolver.FindByCenter(EntityManager, _districts, _prefabSystem,
                    cmd.DistrictCenterXs[i], cmd.DistrictCenterZs[i], cmd.DistrictPrefabNames[i]);
                if (district == Entity.Null)
                {
                    CS2M.Log.Info($"[ServiceDistrict] WARN district unresolved name={cmd.DistrictPrefabNames[i]}");
                    continue;
                }

                buf.Add(new ServiceDistrict(district));
                applied++;
            }

            if (ServiceDistrictSync.TryComputeHash(EntityManager, _prefabSystem, building, out int hash))
            {
                ServiceDistrictSync.Snapshot[building] = hash; // echo guard
            }

            CS2M.Log.Info($"[ServiceDistrict] APPLIED building={cmd.BuildingPrefabName} districts={applied}/{n} entity={building.Index}");
        }

        /// <summary>SyncId first (player-placed this session), else nearest same-prefab building within
        /// ~3 m — native service buildings from the save carry no SyncId.</summary>
        private Entity ResolveBuilding(ServiceDistrictCommand cmd)
        {
            if (cmd.BuildingSyncId != 0 && _idSystem.TryResolve(cmd.BuildingSyncId, out Entity byId)
                && EntityManager.Exists(byId))
            {
                return byId;
            }

            Entity best = Entity.Null;
            float bestD = 9f; // 3 m squared
            NativeArray<Entity> ents = _buildings.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(cand).m_Prefab,
                            out PrefabBase pb) || pb == null || pb.name != cmd.BuildingPrefabName)
                    {
                        continue;
                    }

                    var p = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - cmd.BuildingX;
                    float dz = p.z - cmd.BuildingZ;
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
    }
}
