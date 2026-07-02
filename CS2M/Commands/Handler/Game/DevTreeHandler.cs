using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class DevTreeHandler : CommandHandler<DevTreeCommand>
    {
        public DevTreeHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(DevTreeCommand command)
        {
            CS2M.Log.Info($"[DevTree] RECV node={command.NodeName}");
            RemoteDevTreeQueue.Enqueue(command);
        }
    }
}
