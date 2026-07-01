using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class MoveHandler : CommandHandler<MoveCommand>
    {
        public MoveHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(MoveCommand command)
        {
            CS2M.Log.Info($"[Move] RECV id={command.SyncId} pos=({command.PosX:F1},{command.PosY:F1},{command.PosZ:F1})");
            RemoteEditQueue.EnqueueMove(command);
        }
    }
}
