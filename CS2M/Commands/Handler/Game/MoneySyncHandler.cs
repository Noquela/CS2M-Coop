using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class MoneySyncHandler : CommandHandler<MoneySyncCommand>
    {
        public MoneySyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(MoneySyncCommand command)
        {
            CS2M.Log.Info($"[Money] RECV cash={command.Cash}");
            RemoteMoneyQueue.Set(command.Cash);
        }
    }
}
