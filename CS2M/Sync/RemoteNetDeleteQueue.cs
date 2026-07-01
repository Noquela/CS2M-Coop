using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote net-delete commands, drained by <see cref="NetEditApplySystem"/>.</summary>
    public static class RemoteNetDeleteQueue
    {
        private static readonly ConcurrentQueue<NetDeleteCommand> Q = new ConcurrentQueue<NetDeleteCommand>();

        public static void Enqueue(NetDeleteCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out NetDeleteCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
