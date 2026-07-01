using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Applies a remote service-budget change via the game's own
    ///     <c>CityServiceBudgetSystem.SetServiceBudget(prefab, percentage)</c>. Refreshes the shared
    ///     snapshot so our detector doesn't echo it back.
    /// </summary>
    public partial class BudgetApplySystem : GameSystemBase
    {
        private CityServiceBudgetSystem _budgetSystem;
        private PrefabSystem _prefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _budgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            CS2M.Log.Info("[Budget] BudgetApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteBudgetQueue.TryDequeue(out BudgetCommand cmd))
            {
                ApplyOne(cmd);
            }
        }

        private void ApplyOne(BudgetCommand cmd)
        {
            var prefabId = new PrefabID(cmd.ServiceType, cmd.ServiceName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info($"[Budget] RESOLVE-FAIL type={cmd.ServiceType} name={cmd.ServiceName}");
                return;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out Entity serviceEntity))
            {
                CS2M.Log.Info($"[Budget] RESOLVE-FAIL no entity name={cmd.ServiceName}");
                return;
            }

            try
            {
                _budgetSystem.SetServiceBudget(serviceEntity, cmd.Percentage);
                BudgetSync.Snapshot[cmd.ServiceName] = cmd.Percentage;
                CS2M.Log.Info($"[Budget] APPLIED name={cmd.ServiceName} pct={cmd.Percentage}");
            }
            catch (System.Exception ex)
            {
                // Don't let an occasional SetServiceBudget failure disable the whole apply system.
                CS2M.Log.Info($"[Budget] SetServiceBudget failed name={cmd.ServiceName}: {ex.Message}");
            }
        }
    }
}
