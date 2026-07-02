using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class NetUpgradeHandler : CommandHandler<NetUpgradeCommand>
    {
        public NetUpgradeHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(NetUpgradeCommand command)
        {
            CS2M.Log.Verbose($"[NetEdit] RECV upgrade g={command.General} l={command.Left} r={command.Right}");
            RemoteNetUpgradeQueue.Enqueue(command);
        }
    }
}
