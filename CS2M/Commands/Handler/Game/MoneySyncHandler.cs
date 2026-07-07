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
            RelayOnServer = false; // issue #6: host-authoritative — a client never legitimately authors this
        }

        protected override void Handle(MoneySyncCommand command)
        {
            CS2M.Log.Verbose($"[Money] RECV cash={command.Cash}");
            RemoteMoneyQueue.Set(command.Cash);
        }
    }
}
