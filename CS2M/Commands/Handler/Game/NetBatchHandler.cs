using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class NetBatchHandler : CommandHandler<NetBatchCommand>
    {
        public NetBatchHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(NetBatchCommand command)
        {
            int nodes = command.NodeIds != null ? command.NodeIds.Length : 0;
            int edges = command.EdgeStartNodeIds != null ? command.EdgeStartNodeIds.Length : 0;
            int dels = command.DelStartNodeIds != null ? command.DelStartNodeIds.Length : 0;
            int boundary = command.BoundaryNodeIds != null ? command.BoundaryNodeIds.Length : 0;
            CS2M.Log.Info($"[Batch] RECV nodes={nodes} edges={edges} dels={dels} boundary={boundary}");
            RemoteNetBatchQueue.Enqueue(command);
        }
    }
}
