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
    ///     Detects local service-FEE changes by diffing the City's <c>ServiceFee</c> buffer (keyed by
    ///     PlayerResource) against a snapshot. Covers both the fee sliders AND the Reset button (which
    ///     rewrites every collected fee to its default — each shows up here as a per-resource diff). First
    ///     sight caches the baseline silently; the apply refreshes the snapshot so a remote-applied fee
    ///     isn't echoed. Any player can change a fee, so this runs on host and clients alike (as budget/tax
    ///     do). Fees are distinct from the funding percentage handled by <see cref="BudgetDetectorSystem"/>.
    /// </summary>
    public partial class FeeDetectorSystem : GameSystemBase
    {
        private CitySystem _citySystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            CS2M.Log.Info("[Fee] FeeDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<ServiceFee>(city))
            {
                return;
            }

            DynamicBuffer<ServiceFee> fees = EntityManager.GetBuffer<ServiceFee>(city, true);
            for (int i = 0; i < fees.Length; i++)
            {
                int res = (int) fees[i].m_Resource;
                float fee = fees[i].m_Fee;

                if (!FeeSync.Snapshot.TryGetValue(res, out float prev))
                {
                    FeeSync.Snapshot[res] = fee; // first sight: cache baseline silently
                    continue;
                }

                if (System.Math.Abs(prev - fee) < 0.0001f)
                {
                    continue;
                }

                FeeSync.Snapshot[res] = fee;
                Command.SendToAll?.Invoke(new FeeCommand { Resource = res, Fee = fee });
                CS2M.Log.Info($"[Fee] DETECT+SEND resource={res} fee={fee}");
            }
        }
    }
}
