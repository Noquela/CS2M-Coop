using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Applies a remote city-policy change by raising a Modify event: a new entity with
    ///     <c>Event</c> + <c>Modify(City, policyPrefab, active, adjustment)</c> — the same components the
    ///     game's UI writes (the UI's own SetCityPolicy can't be reused here: it creates the event in the
    ///     EndFrameBarrier's command buffer, which isn't allowed from the Modification5 phase). The
    ///     game's <c>PolicyModifiedSystem</c> consumes the event and updates the Policy buffer + effects.
    /// </summary>
    public partial class PolicyApplySystem : GameSystemBase
    {
        private CitySystem _citySystem;
        private PrefabSystem _prefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            CS2M.Log.Info("[Policy] PolicyApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemotePolicyQueue.TryDequeue(out PolicyCommand cmd))
            {
                ApplyOne(cmd);
            }
        }

        private void ApplyOne(PolicyCommand cmd)
        {
            Entity city = _citySystem.City;
            if (city == Entity.Null)
            {
                return;
            }

            var prefabId = new PrefabID(cmd.PolicyType, cmd.PolicyName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info($"[Policy] RESOLVE-FAIL type={cmd.PolicyType} name={cmd.PolicyName}");
                return;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out Entity policyEntity))
            {
                CS2M.Log.Info($"[Policy] RESOLVE-FAIL no entity name={cmd.PolicyName}");
                return;
            }

            PolicySync.MarkApplied(cmd.PolicyName, cmd.Active);

            Entity e = EntityManager.CreateEntity();
            EntityManager.AddComponent<Event>(e);
            EntityManager.AddComponentData(e, new Modify(city, policyEntity, cmd.Active, cmd.Adjustment));

            CS2M.Log.Info($"[Policy] APPLIED name={cmd.PolicyName} active={cmd.Active} adj={cmd.Adjustment}");
        }
    }
}
