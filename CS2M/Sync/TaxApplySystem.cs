using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Simulation;
using Unity.Collections;

namespace CS2M.Sync
{
    /// <summary>
    ///     Applies remote tax-rate changes into the live <c>TaxSystem.GetTaxRates()</c> and refreshes the
    ///     shared snapshot so our own detector doesn't echo them back. (<c>GetTaxRates()</c> returns the
    ///     system's live NativeArray, so index writes take effect immediately.)
    ///     Drains two independent channels: <see cref="RemoteTaxQueue"/> (legacy whole-array replace,
    ///     latest-wins — also used directly by the selftest bot) and <see cref="RemoteTaxDeltaQueue"/>
    ///     (CS2M_TAXFIX granular per-index deltas — every delta is applied, never just the latest, since
    ///     each covers a disjoint set of indices). Which channel fires is decided by the SENDER's shape
    ///     (<see cref="TaxSyncCommand.Indices"/>), so this system works regardless of our own
    ///     <see cref="TaxFix"/> setting.
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

            if (RemoteTaxQueue.TryTake(out int[] incoming) && incoming != null)
            {
                ApplyFull(incoming);
            }

            while (RemoteTaxDeltaQueue.TryDequeue(out TaxSyncCommand delta))
            {
                ApplyGranular(delta);
            }
        }

        private void ApplyFull(int[] incoming)
        {
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

        /// <summary>CS2M_TAXFIX: write only the indices carried by this delta, in both the live rates
        /// AND the snapshot, so an unrelated index changed locally in the same window survives.</summary>
        private void ApplyGranular(TaxSyncCommand delta)
        {
            if (delta.Indices == null || delta.Rates == null)
            {
                return;
            }

            NativeArray<int> rates = _taxSystem.GetTaxRates();
            if (!rates.IsCreated || rates.Length == 0)
            {
                return;
            }

            if (TaxSync.Snapshot == null || TaxSync.Snapshot.Length != rates.Length)
            {
                TaxSync.Snapshot = new int[rates.Length];
                for (int i = 0; i < rates.Length; i++)
                {
                    TaxSync.Snapshot[i] = rates[i];
                }
            }

            int n = System.Math.Min(delta.Indices.Length, delta.Rates.Length);
            for (int i = 0; i < n; i++)
            {
                int idx = delta.Indices[i];
                if (idx < 0 || idx >= rates.Length)
                {
                    continue;
                }

                rates[idx] = delta.Rates[i];
                TaxSync.Snapshot[idx] = delta.Rates[i]; // echo guard — only THIS index
            }

            CS2M.Log.Info($"[Tax] APPLIED granular count={n}");
        }
    }
}
