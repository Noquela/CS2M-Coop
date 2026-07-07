using CS2M.API.Networking;
using CS2M.Networking;
using Game;
using Game.City;
using Game.Simulation;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Clients: snap local city cash to the host's authoritative value. Uses a delta-Add so we
    ///     never touch <c>m_Unlimited</c> (constructing a new PlayerMoney would clear it).
    /// </summary>
    public partial class MoneySyncApplySystem : GameSystemBase
    {
        private CitySystem _citySystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            CS2M.Log.Info("[Money] MoneySyncApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // Issue #6: host-authoritative — the host's own cash IS the truth, it must never absorb
            // a MoneySyncCommand (which only a buggy/foreign client could have authored). Same guard
            // SpeedSyncApplySystem already has. Drain the queue so stale values don't linger.
            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                RemoteMoneyQueue.TryTake(out _);
                return;
            }

            if (!RemoteMoneyQueue.TryTake(out int cash))
            {
                return;
            }

            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<PlayerMoney>(city))
            {
                return;
            }

            PlayerMoney pm = EntityManager.GetComponentData<PlayerMoney>(city);
            if (pm.m_Unlimited)
            {
                CS2M.Log.Info("[Money] SKIP local-unlimited");
                return;
            }

            int delta = cash - pm.money;
            if (delta != 0)
            {
                pm.Add(delta);
                EntityManager.SetComponentData(city, pm);
            }

            CS2M.Log.Info($"[Money] APPLIED cash={cash} (delta={delta})");
        }
    }
}
