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

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects a player editing a transport line/work-route's allowed vehicle models (see
    ///     <see cref="VehicleModelCommand"/> for the two UI triggers that write
    ///     <c>Game.Routes.VehicleModel</c> directly). Neither raises Created/Updated/an event, so — like
    ///     <see cref="ServiceDistrictDetectorSystem"/> and the district-reshape scanner — a periodic
    ///     content-hash diff per route is the only detection signal. ~1 Hz throttle: rare edit, buffer is
    ///     tiny, and the query spans every transport line + work-route in the city.
    /// </summary>
    public partial class VehicleModelDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _routes;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _routes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<RouteNumber>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<VehicleModel>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            CS2M.Log.Info("[VehicleModel] VehicleModelDetectorSystem created");
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

            NativeArray<Entity> routes = _routes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity route in routes)
                {
                    if (!VehicleModelSync.TryComputeHash(EntityManager, route, out ulong hash))
                    {
                        continue;
                    }

                    if (!VehicleModelSync.Snapshot.TryGetValue(route, out ulong prev))
                    {
                        VehicleModelSync.Snapshot[route] = hash; // first sight: baseline silently
                        continue;
                    }

                    if (prev == hash)
                    {
                        continue;
                    }

                    VehicleModelSync.Snapshot[route] = hash;
                    VehicleModelCommand cmd = BuildCommand(route);
                    if (cmd == null)
                    {
                        continue;
                    }

                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Info($"[VehicleModel] DETECT+SEND id={cmd.SyncId} number={cmd.Number} " +
                                  $"entries={cmd.PrimaryNames.Length}");
                }
            }
            finally
            {
                routes.Dispose();
            }
        }

        private VehicleModelCommand BuildCommand(Entity route)
        {
            ulong id = EntityManager.HasComponent<CS2M_SyncId>(route)
                ? EntityManager.GetComponentData<CS2M_SyncId>(route).m_Id
                : 0;
            int number = EntityManager.GetComponentData<RouteNumber>(route).m_Number;

            if (id == 0 && number == 0)
            {
                return null; // unresolvable on the other side
            }

            if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(route).m_Prefab,
                    out PrefabBase routePrefab) || routePrefab == null)
            {
                return null;
            }

            DynamicBuffer<VehicleModel> buf = EntityManager.GetBuffer<VehicleModel>(route, true);
            var pTypes = new string[buf.Length];
            var pNames = new string[buf.Length];
            var sTypes = new string[buf.Length];
            var sNames = new string[buf.Length];
            for (int i = 0; i < buf.Length; i++)
            {
                DescribePrefab(buf[i].m_PrimaryPrefab, out pTypes[i], out pNames[i]);
                DescribePrefab(buf[i].m_SecondaryPrefab, out sTypes[i], out sNames[i]);
            }

            return new VehicleModelCommand
            {
                SyncId = id,
                PrefabName = routePrefab.name,
                Number = number,
                PrimaryTypes = pTypes,
                PrimaryNames = pNames,
                SecondaryTypes = sTypes,
                SecondaryNames = sNames,
            };
        }

        private void DescribePrefab(Entity e, out string type, out string name)
        {
            type = string.Empty;
            name = string.Empty;
            if (e == Entity.Null)
            {
                return;
            }

            if (_prefabSystem.TryGetPrefab(e, out PrefabBase pb) && pb != null)
            {
                type = pb.GetType().Name;
                name = pb.name;
            }
        }
    }
}
