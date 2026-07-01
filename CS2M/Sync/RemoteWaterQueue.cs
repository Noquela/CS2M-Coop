using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote water-source placements, drained by <see cref="WaterApplySystem"/>.</summary>
    public static class RemoteWaterQueue
    {
        private static readonly ConcurrentQueue<WaterCommand> Q = new ConcurrentQueue<WaterCommand>();

        public static void Enqueue(WaterCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out WaterCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
