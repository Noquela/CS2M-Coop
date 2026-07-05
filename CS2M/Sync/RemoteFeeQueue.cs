using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote service-fee changes, drained by <see cref="FeeApplySystem"/>.</summary>
    public static class RemoteFeeQueue
    {
        private static readonly ConcurrentQueue<FeeCommand> Q = new ConcurrentQueue<FeeCommand>();

        public static void Enqueue(FeeCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out FeeCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
