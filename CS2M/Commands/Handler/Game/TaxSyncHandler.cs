using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class TaxSyncHandler : CommandHandler<TaxSyncCommand>
    {
        public TaxSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(TaxSyncCommand command)
        {
            // Shape-driven, independent of our own CS2M_TAXFIX: a populated Indices means the sender
            // used the granular path, so route to the delta queue (every delta applied, never just
            // latest-wins) regardless of whether we have the gate on locally.
            if (command.Indices != null && command.Indices.Length > 0)
            {
                CS2M.Log.Info($"[Tax] RECV granular count={command.Indices.Length}");
                RemoteTaxDeltaQueue.Enqueue(command);
                return;
            }

            CS2M.Log.Info($"[Tax] RECV rates={(command.Rates == null ? 0 : command.Rates.Length)}");
            RemoteTaxQueue.Set(command.Rates);
        }
    }
}
