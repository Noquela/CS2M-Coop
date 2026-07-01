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
            CS2M.Log.Info($"[Tax] RECV rates={(command.Rates == null ? 0 : command.Rates.Length)}");
            RemoteTaxQueue.Set(command.Rates);
        }
    }
}
