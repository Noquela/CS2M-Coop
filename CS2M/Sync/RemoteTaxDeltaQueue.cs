using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>
    ///     Thread-safe queue of remote GRANULAR tax-rate deltas (CS2M_TAXFIX — see <see cref="TaxFix"/>),
    ///     drained by <see cref="TaxApplySystem"/>. Kept separate from <see cref="RemoteTaxQueue"/>
    ///     (whole-array, latest-wins) on purpose: a delta must never be dropped in favor of a newer one —
    ///     each delta covers a disjoint set of indices, so all of them need to be applied, not just the
    ///     last.
    /// </summary>
    public static class RemoteTaxDeltaQueue
    {
        private static readonly ConcurrentQueue<TaxSyncCommand> Q = new ConcurrentQueue<TaxSyncCommand>();

        public static void Enqueue(TaxSyncCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out TaxSyncCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
