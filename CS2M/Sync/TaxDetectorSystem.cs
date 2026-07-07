using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Simulation;
using Unity.Collections;

namespace CS2M.Sync
{
    /// <summary>Global toggle for granular per-index tax sync. ON by default since 2026-07-07 —
    /// validated live in 2-sim + selftest 88 PASS/0 FAIL with every gated fix enabled together (no
    /// regression/echo/crash).
    ///     CONFIRMED gap (legacy/gate-off path): <see cref="TaxDetectorSystem"/> broadcasts the WHOLE
    ///     tax-rate array on ANY change, and <see cref="TaxApplySystem"/> overwrites every local index +
    ///     the whole snapshot with it. If PC A raises residential tax and PC B lowers commercial tax at
    ///     nearly the same time, whichever apply lands second stomps every index with its own stale
    ///     copy — the other player's just-made edit is silently discarded, no retry, no log.
    ///     Gated ON: the detector sends only the indices that changed this tick
    ///     (<see cref="TaxSyncCommand.Indices"/>) and the apply writes only those indices (+ only those
    ///     snapshot entries), so edits to different categories can never race each other. Set env
    ///     <c>CS2M_TAXFIX=0</c> to disable (falls back to the legacy whole-array broadcast).</summary>
    public static class TaxFix
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_TAXFIX") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     Detects local tax-rate changes by diffing <c>TaxSystem.GetTaxRates()</c> against a shared
    ///     snapshot. First sight caches the baseline silently. Any player can change taxes (it's a
    ///     shared city setting), so this runs on host and clients alike; the apply system refreshes the
    ///     same snapshot, so a remote-applied change is not echoed back.
    ///     Legacy (<see cref="TaxFix"/> off): broadcasts the whole array on any change. Gated
    ///     (<c>CS2M_TAXFIX=1</c>): broadcasts only the indices that actually changed — see
    ///     <see cref="TaxFix"/> for the concurrent-edit bug this avoids.
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

            if (TaxFix.Enabled)
            {
                DetectGranular(rates, n);
                return;
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

        /// <summary>CS2M_TAXFIX: send only the indices that changed this tick, each tagged with its
        /// own index, so a concurrent edit to a different category on another PC is never clobbered.</summary>
        private void DetectGranular(NativeArray<int> rates, int n)
        {
            List<int> idx = null;
            List<int> val = null;
            for (int i = 0; i < n; i++)
            {
                if (rates[i] == TaxSync.Snapshot[i])
                {
                    continue;
                }

                (idx ??= new List<int>()).Add(i);
                (val ??= new List<int>()).Add(rates[i]);
                TaxSync.Snapshot[i] = rates[i];
            }

            if (idx == null)
            {
                return;
            }

            Command.SendToAll?.Invoke(new TaxSyncCommand { Indices = idx.ToArray(), Rates = val.ToArray() });
            CS2M.Log.Info($"[Tax] DETECT+SEND granular count={idx.Count} first=idx{idx[0]}={val[0]}");
        }
    }
}
