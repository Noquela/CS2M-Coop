using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe hand-off for remote atomic net batches, drained by <see cref="NetBatchApplySystem"/>.</summary>
    public static class RemoteNetBatchQueue
    {
        private static readonly ConcurrentQueue<NetBatchCommand> Batches = new ConcurrentQueue<NetBatchCommand>();

        public static void Enqueue(NetBatchCommand c) => Batches.Enqueue(c);
        public static bool TryDequeue(out NetBatchCommand c) => Batches.TryDequeue(out c);

        public static void Clear()
        {
            while (Batches.TryDequeue(out _)) { }
        }
    }
}
