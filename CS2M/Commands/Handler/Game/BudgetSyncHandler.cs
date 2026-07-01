using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class BudgetSyncHandler : CommandHandler<BudgetCommand>
    {
        public BudgetSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(BudgetCommand command)
        {
            CS2M.Log.Info($"[Budget] RECV name={command.ServiceName} pct={command.Percentage}");
            RemoteBudgetQueue.Enqueue(command);
        }
    }
}
