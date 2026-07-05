using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class FeeSyncHandler : CommandHandler<FeeCommand>
    {
        public FeeSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(FeeCommand command)
        {
            CS2M.Log.Info($"[Fee] RECV resource={command.Resource} fee={command.Fee}");
            RemoteFeeQueue.Enqueue(command);
        }
    }
}
