using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>Receives a <see cref="NodePosUpdateCommand"/> and hands it to
    /// <see cref="RemoteNodePosUpdateQueue"/>; <see cref="NetBatchApplySystem"/> drains it at the top of its
    /// OnUpdate and reconciles each node by identity.</summary>
    public class NodePosUpdateHandler : CommandHandler<NodePosUpdateCommand>
    {
        public NodePosUpdateHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(NodePosUpdateCommand command)
        {
            int n = command.Ids != null ? command.Ids.Length : 0;
            CS2M.Log.Verbose($"[Batch] RECV NodePosUpdate count={n}");
            RemoteNodePosUpdateQueue.Enqueue(command);
        }
    }
}

namespace CS2M.Sync
{
    using System.Collections.Concurrent;
    using CS2M.Commands.Data.Game;

    /// <summary>Thread-safe queue of remote node-position updates (the AtomicBatch settled-position stream),
    /// drained by <see cref="NetBatchApplySystem"/>. Cleared on teardown by that system's OnUpdate when the
    /// local player is no longer PLAYING (no LocalPlayer hook needed).</summary>
    public static class RemoteNodePosUpdateQueue
    {
        private static readonly ConcurrentQueue<NodePosUpdateCommand> Q = new ConcurrentQueue<NodePosUpdateCommand>();

        public static void Enqueue(NodePosUpdateCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out NodePosUpdateCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
