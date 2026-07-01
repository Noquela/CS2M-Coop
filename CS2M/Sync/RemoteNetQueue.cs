using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe hand-off for remote net placements, drained by <see cref="NetPlaceApplySystem"/>.</summary>
    public static class RemoteNetQueue
    {
        private static readonly ConcurrentQueue<NetPlaceCommand> Nets = new ConcurrentQueue<NetPlaceCommand>();

        public static void Enqueue(NetPlaceCommand c) => Nets.Enqueue(c);
        public static bool TryDequeue(out NetPlaceCommand c) => Nets.TryDequeue(out c);

        public static void Clear()
        {
            while (Nets.TryDequeue(out _)) { }
        }
    }
}
