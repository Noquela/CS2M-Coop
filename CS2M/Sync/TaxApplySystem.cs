using CS2M.API.Networking;
using CS2M.Networking;
using Game;
using Game.Simulation;
using Unity.Collections;

namespace CS2M.Sync
{
    /// <summary>
    ///     Applies a remote tax-rate change: writes the received array into the live
    ///     <c>TaxSystem.GetTaxRates()</c> and refreshes the shared snapshot so our own detector doesn't
    ///     echo it back. (<c>GetTaxRates()</c> returns the system's live NativeArray, so index writes
    ///     take effect immediately.)
    /// </summary>
    public partial class TaxApplySystem : GameSystemBase
    {
        private TaxSystem _taxSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _taxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            CS2M.Log.Info("[Tax] TaxApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (!RemoteTaxQueue.TryTake(out int[] incoming) || incoming == null)
            {
                return;
            }

            NativeArray<int> rates = _taxSystem.GetTaxRates();
            if (!rates.IsCreated || rates.Length == 0)
            {
                return;
            }

            int n = System.Math.Min(rates.Length, incoming.Length);
            for (int i = 0; i < n; i++)
            {
                rates[i] = incoming[i];
            }

            if (TaxSync.Snapshot == null || TaxSync.Snapshot.Length != rates.Length)
            {
                TaxSync.Snapshot = new int[rates.Length];
            }

            for (int i = 0; i < rates.Length; i++)
            {
                TaxSync.Snapshot[i] = rates[i];
            }

            CS2M.Log.Info($"[Tax] APPLIED rates={n} main={(n > 0 ? incoming[0] : 0)}");
        }
    }
}
