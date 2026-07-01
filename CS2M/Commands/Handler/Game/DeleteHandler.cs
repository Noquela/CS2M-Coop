using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class DeleteHandler : CommandHandler<DeleteCommand>
    {
        public DeleteHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(DeleteCommand command)
        {
            CS2M.Log.Info($"[Del] RECV id={command.SyncId}");
            RemoteEditQueue.EnqueueDelete(command);
        }
    }
}
