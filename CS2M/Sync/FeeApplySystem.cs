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
    ///     Applies a remote service-fee change via the game's own static
    ///     <c>Game.Simulation.ServiceFeeSystem.SetFee(resource, cityFeeBuffer, amount)</c> — exactly what
    ///     the vanilla ServiceBudgetUISystem's setServiceFee trigger does. Refreshes the shared snapshot so
    ///     our detector doesn't echo it back.
    /// </summary>
    public partial class FeeApplySystem : GameSystemBase
    {
        private CitySystem _citySystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            CS2M.Log.Info("[Fee] FeeApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteFeeQueue.TryDequeue(out FeeCommand cmd))
            {
                ApplyOne(cmd);
            }
        }

        private void ApplyOne(FeeCommand cmd)
        {
            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<ServiceFee>(city))
            {
                CS2M.Log.Info($"[Fee] APPLY SKIP no city/buffer resource={cmd.Resource}");
                return;
            }

            try
            {
                DynamicBuffer<ServiceFee> fees = EntityManager.GetBuffer<ServiceFee>(city);
                ServiceFeeSystem.SetFee((PlayerResource) cmd.Resource, fees, cmd.Fee);
                FeeSync.Snapshot[cmd.Resource] = cmd.Fee; // echo guard
                CS2M.Log.Info($"[Fee] APPLIED resource={cmd.Resource} fee={cmd.Fee}");
            }
            catch (System.Exception ex)
            {
                // Never let a single SetFee failure disable the whole apply loop.
                CS2M.Log.Info($"[Fee] SetFee failed resource={cmd.Resource}: {ex.Message}");
            }
        }
    }
}
