using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote city-policy toggles, drained by <see cref="PolicyApplySystem"/>.</summary>
    public static class RemotePolicyQueue
    {
        private static readonly ConcurrentQueue<PolicyCommand> Q = new ConcurrentQueue<PolicyCommand>();

        public static void Enqueue(PolicyCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out PolicyCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
