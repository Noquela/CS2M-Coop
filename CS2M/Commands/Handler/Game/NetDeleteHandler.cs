using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class NetDeleteHandler : CommandHandler<NetDeleteCommand>
    {
        public NetDeleteHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(NetDeleteCommand command)
        {
            CS2M.Log.Info($"[NetEdit] RECV delete start=({command.StartX:F0},{command.StartZ:F0}) end=({command.EndX:F0},{command.EndZ:F0})");
            RemoteNetDeleteQueue.Enqueue(command);
        }
    }
}
