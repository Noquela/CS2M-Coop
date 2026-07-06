using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Applies a remote vehicle-model selection edit (see <see cref="VehicleModelDetectorSystem"/>).
    ///     Resolves the target route with the existing <c>RouteResolver</c> (SyncId else prefab +
    ///     RouteNumber — the same identity color/delete/reroute already use) and REWRITES the route's
    ///     <c>VehicleModel</c> buffer to the sender's exact ordered list (idempotent). Refreshes
    ///     <see cref="VehicleModelSync.Snapshot"/> so this PC's own detector doesn't echo the rewrite back.
    /// </summary>
    public partial class VehicleModelApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _routesByNumber;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _routesByNumber = GetEntityQuery(
                ComponentType.ReadOnly<Route>(),
                ComponentType.ReadOnly<RouteNumber>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            CS2M.Log.Info("[VehicleModel] VehicleModelApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteVehicleModelQueue.TryDequeue(out VehicleModelCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] vehicle-model apply failed: {ex.Message}"); }
            }
        }

        private void ApplyOne(VehicleModelCommand cmd)
        {
            Entity route = RouteResolver.Resolve(EntityManager, _routesByNumber, _prefabSystem,
                cmd.SyncId, cmd.PrefabName, cmd.Number);
            if (route == Entity.Null || !EntityManager.HasBuffer<VehicleModel>(route))
            {
                CS2M.Log.Info($"[VehicleModel] SKIP noTarget id={cmd.SyncId} number={cmd.Number}");
                return;
            }

            DynamicBuffer<VehicleModel> buf = EntityManager.GetBuffer<VehicleModel>(route);
            buf.Clear();

            int n = cmd.PrimaryNames?.Length ?? 0;
            for (int i = 0; i < n; i++)
            {
                Entity primary = ResolvePrefab(cmd.PrimaryTypes[i], cmd.PrimaryNames[i]);
                Entity secondary = ResolvePrefab(cmd.SecondaryTypes[i], cmd.SecondaryNames[i]);
                buf.Add(new VehicleModel { m_PrimaryPrefab = primary, m_SecondaryPrefab = secondary });
            }

            if (VehicleModelSync.TryComputeHash(EntityManager, route, out ulong hash))
            {
                VehicleModelSync.Snapshot[route] = hash; // echo guard
            }

            CS2M.Log.Info($"[VehicleModel] APPLIED id={cmd.SyncId} number={cmd.Number} entries={n} entity={route.Index}");
        }

        private Entity ResolvePrefab(string type, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Entity.Null;
            }

            var id = new PrefabID(type, name, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(id, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out Entity entity))
            {
                CS2M.Log.Info($"[VehicleModel] RESOLVE-FAIL prefab type={type} name={name}");
                return Entity.Null;
            }

            return entity;
        }
    }
}
