using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Simulation;
using Unity.Collections;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects local tax-rate changes by diffing <c>TaxSystem.GetTaxRates()</c> against a shared
    ///     snapshot and broadcasting the whole array. First sight caches the baseline silently. Any
    ///     player can change taxes (it's a shared city setting), so this runs on host and clients alike;
    ///     the apply system refreshes the same snapshot, so a remote-applied change is not echoed back.
    /// </summary>
    public partial class TaxDetectorSystem : GameSystemBase
    {
        private TaxSystem _taxSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _taxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            CS2M.Log.Info("[Tax] TaxDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            NativeArray<int> rates = _taxSystem.GetTaxRates();
            if (!rates.IsCreated || rates.Length == 0)
            {
                return;
            }

            int n = rates.Length;
            if (TaxSync.Snapshot == null || TaxSync.Snapshot.Length != n)
            {
                TaxSync.Snapshot = new int[n];
                for (int i = 0; i < n; i++)
                {
                    TaxSync.Snapshot[i] = rates[i];
                }

                return; // first sight: cache baseline, don't send
            }

            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (rates[i] != TaxSync.Snapshot[i])
                {
                    changed = true;
                    break;
                }
            }

            if (!changed)
            {
                return;
            }

            var copy = new int[n];
            for (int i = 0; i < n; i++)
            {
                copy[i] = rates[i];
                TaxSync.Snapshot[i] = rates[i];
            }

            Command.SendToAll?.Invoke(new TaxSyncCommand { Rates = copy });
            CS2M.Log.Info($"[Tax] DETECT+SEND rates={n} main={copy[0]}");
        }
    }
}
