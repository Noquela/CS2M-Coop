using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.City;
using Game.Simulation;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Host-only: broadcasts the city's authoritative cash ~once per second (and on change) so
    ///     clients cancel the tiny per-PC economy drift. Money is one shared value for the co-op city.
    /// </summary>
    public partial class MoneySyncSenderSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 64; // ~1 Hz at 60 fps

        private CitySystem _citySystem;
        private int _lastSent = int.MinValue;
        private int _frame;
        private bool _activeLogged;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            CS2M.Log.Info("[Money] MoneySyncSenderSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // Only the host is authoritative over money. Gate on the network-layer PlayerType (the
            // reliable source) — the old Command.CurrentRole gate silently killed this sender for
            // the entire first 2-PC session because CurrentRole was never assigned.
            if (NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            if (!_activeLogged)
            {
                _activeLogged = true;
                CS2M.Log.Info("[Money] sender active (host)");
            }

            if (++_frame < SendEveryNFrames)
            {
                return;
            }

            _frame = 0;

            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<PlayerMoney>(city))
            {
                return;
            }

            PlayerMoney pm = EntityManager.GetComponentData<PlayerMoney>(city);
            if (pm.m_Unlimited)
            {
                return; // don't broadcast a sandbox value
            }

            int cash = pm.money;
            if (cash == _lastSent)
            {
                return;
            }

            _lastSent = cash;
            Command.SendToAll?.Invoke(new MoneySyncCommand { Cash = cash });
            CS2M.Log.Info($"[Money] SEND cash={cash}");
        }
    }
}
