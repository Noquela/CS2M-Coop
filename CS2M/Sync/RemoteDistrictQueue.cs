using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote district paints, drained by <see cref="DistrictApplySystem"/>.</summary>
    public static class RemoteDistrictQueue
    {
        private static readonly ConcurrentQueue<DistrictCommand> Q = new ConcurrentQueue<DistrictCommand>();

        public static void Enqueue(DistrictCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out DistrictCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
