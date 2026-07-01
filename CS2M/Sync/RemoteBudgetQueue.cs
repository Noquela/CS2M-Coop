using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote service-budget changes, drained by <see cref="BudgetApplySystem"/>.</summary>
    public static class RemoteBudgetQueue
    {
        private static readonly ConcurrentQueue<BudgetCommand> Q = new ConcurrentQueue<BudgetCommand>();

        public static void Enqueue(BudgetCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out BudgetCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
