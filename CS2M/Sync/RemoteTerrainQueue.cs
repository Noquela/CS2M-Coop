using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote terraforming strokes, drained by <see cref="TerrainApplySystem"/>.</summary>
    public static class RemoteTerrainQueue
    {
        private static readonly ConcurrentQueue<TerrainCommand> Q = new ConcurrentQueue<TerrainCommand>();

        public static void Enqueue(TerrainCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out TerrainCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
