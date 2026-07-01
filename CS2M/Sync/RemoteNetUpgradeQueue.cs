using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote net-upgrade commands, drained by <see cref="NetEditApplySystem"/>.</summary>
    public static class RemoteNetUpgradeQueue
    {
        private static readonly ConcurrentQueue<NetUpgradeCommand> Q = new ConcurrentQueue<NetUpgradeCommand>();

        public static void Enqueue(NetUpgradeCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out NetUpgradeCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
