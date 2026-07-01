using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects local service-budget (funding slider) changes by polling
    ///     <c>CityServiceBudgetSystem.GetServiceBudget(prefab)</c> for every adjustable service prefab and
    ///     diffing against a snapshot. First sight caches the baseline silently; the apply refreshes the
    ///     snapshot so a remote-applied change isn't echoed.
    /// </summary>
    public partial class BudgetDetectorSystem : GameSystemBase
    {
        private CityServiceBudgetSystem _budgetSystem;
        private PrefabSystem _prefabSystem;
        private EntityQuery _services;

        protected override void OnCreate()
        {
            base.OnCreate();
            _budgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _services = GetEntityQuery(ComponentType.ReadOnly<ServiceData>());
            CS2M.Log.Info("[Budget] BudgetDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            NativeArray<Entity> ents = _services.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    ServiceData sd = EntityManager.GetComponentData<ServiceData>(e);
                    if (!sd.m_BudgetAdjustable)
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(e, out PrefabBase pb) || pb == null)
                    {
                        continue;
                    }

                    string name = pb.name;
                    int pct = _budgetSystem.GetServiceBudget(e);

                    if (!BudgetSync.Snapshot.TryGetValue(name, out int prev))
                    {
                        BudgetSync.Snapshot[name] = pct; // first sight: cache baseline
                        continue;
                    }

                    if (prev == pct)
                    {
                        continue;
                    }

                    BudgetSync.Snapshot[name] = pct;
                    Command.SendToAll?.Invoke(new BudgetCommand
                    {
                        ServiceType = pb.GetType().Name,
                        ServiceName = name,
                        Percentage = pct,
                    });
                    CS2M.Log.Info($"[Budget] DETECT+SEND name={name} pct={pct}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }
    }
}
