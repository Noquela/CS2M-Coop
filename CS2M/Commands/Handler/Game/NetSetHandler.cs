using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class NetSetHandler : CommandHandler<NetSetCommand>
    {
        public NetSetHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false; // host-authoritative — a client never legitimately authors this
        }

        protected override void Handle(NetSetCommand command)
        {
            int nodes = command.NodeIds != null ? command.NodeIds.Length : 0;
            int edges = command.EdgeIds != null ? command.EdgeIds.Length : 0;
            CS2M.Log.Info($"[NetSet] RECV nodes={nodes} edges={edges}");
            RemoteNetSetQueue.Enqueue(command);
        }
    }
}
