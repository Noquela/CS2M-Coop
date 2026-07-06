using System.Collections.Generic;
using CS2M.API.Commands;
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
    ///     Detects a player editing which districts serve a building (see <see cref="ServiceDistrictCommand"/>
    ///     for the two UI paths that write <c>Game.Areas.ServiceDistrict</c> directly). Neither path raises
    ///     Created/Updated/an event on the building, so — like <see cref="FeeDetectorSystem"/> and the
    ///     district-reshape scanner in <see cref="DistrictDetectorSystem"/> — a periodic content-hash diff
    ///     per building is the only detection signal. This naturally covers ADD and REMOVE uniformly: the
    ///     detector only sees "the buffer's content changed", not which UI path changed it.
    ///
    ///     ~1 Hz throttle: this edit is rare and the query can span every service building in a city
    ///     (police/fire/health/school/prison/deathcare/garbage/post/welfare/admin/depot all carry the
    ///     buffer), so a per-frame scan would be wasted work.
    /// </summary>
    public partial class ServiceDistrictDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _buildings;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
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
            CS2M.Log.Info("[ServiceDistrict] ServiceDistrictDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_frame < 60)
            {
                return;
            }

            _frame = 0;

            NativeArray<Entity> buildings = _buildings.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity building in buildings)
                {
                    if (!ServiceDistrictSync.TryComputeHash(EntityManager, _prefabSystem, building, out int hash))
                    {
                        continue;
                    }

                    if (!ServiceDistrictSync.Snapshot.TryGetValue(building, out int prev))
                    {
                        ServiceDistrictSync.Snapshot[building] = hash; // first sight: baseline silently
                        continue;
                    }

                    if (prev == hash)
                    {
                        continue;
                    }

                    ServiceDistrictSync.Snapshot[building] = hash;
                    ServiceDistrictCommand cmd = BuildCommand(building);
                    if (cmd == null)
                    {
                        continue;
                    }

                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Info($"[ServiceDistrict] DETECT+SEND building={cmd.BuildingPrefabName} " +
                                  $"districts={cmd.DistrictPrefabNames.Length}");
                }
            }
            finally
            {
                buildings.Dispose();
            }
        }

        private ServiceDistrictCommand BuildCommand(Entity building)
        {
            if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(building).m_Prefab,
                    out PrefabBase prefab) || prefab == null)
            {
                return null;
            }

            Game.Objects.Transform tf = EntityManager.GetComponentData<Game.Objects.Transform>(building);
            DynamicBuffer<ServiceDistrict> buf = EntityManager.GetBuffer<ServiceDistrict>(building, true);

            var names = new List<string>();
            var xs = new List<float>();
            var zs = new List<float>();
            for (int i = 0; i < buf.Length; i++)
            {
                if (!DistrictResolver.TryDescribe(EntityManager, _prefabSystem, buf[i].m_District,
                        out string dName, out float cx, out float cz))
                {
                    continue; // unresolvable on the wire — dropped, matches the Route/District best-effort precedent
                }

                names.Add(dName);
                xs.Add(cx);
                zs.Add(cz);
            }

            return new ServiceDistrictCommand
            {
                BuildingSyncId = EntityManager.HasComponent<CS2M_SyncId>(building)
                    ? EntityManager.GetComponentData<CS2M_SyncId>(building).m_Id
                    : 0,
                BuildingPrefabName = prefab.name,
                BuildingX = tf.m_Position.x,
                BuildingY = tf.m_Position.y,
                BuildingZ = tf.m_Position.z,
                DistrictPrefabNames = names.ToArray(),
                DistrictCenterXs = xs.ToArray(),
                DistrictCenterZs = zs.ToArray(),
            };
        }
    }
}
