using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe hand-off for remote NetSet commands, drained by <see cref="NetSetApplySystem"/>.
    /// Clone of <see cref="RemoteZoneBlockQueue"/> for the new type.</summary>
    public static class RemoteNetSetQueue
    {
        private static readonly ConcurrentQueue<NetSetCommand> Sets = new ConcurrentQueue<NetSetCommand>();

        public static void Enqueue(NetSetCommand c) => Sets.Enqueue(c);

        public static bool TryDequeue(out NetSetCommand c) => Sets.TryDequeue(out c);

        public static void Clear()
        {
            while (Sets.TryDequeue(out _)) { }
        }
    }
}
