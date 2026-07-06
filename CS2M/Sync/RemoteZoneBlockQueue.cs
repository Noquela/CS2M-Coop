using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe hand-off for remote ZoneBlockAuthority commands, drained by
    /// <see cref="ZoneBlockAuthorityApplySystem"/>. Clone of <see cref="RemoteZoneQueue"/> for the new type.</summary>
    public static class RemoteZoneBlockQueue
    {
        private static readonly ConcurrentQueue<ZoneBlockAuthorityCommand> Blocks = new ConcurrentQueue<ZoneBlockAuthorityCommand>();

        public static void Enqueue(ZoneBlockAuthorityCommand c) => Blocks.Enqueue(c);
        public static bool TryDequeue(out ZoneBlockAuthorityCommand c) => Blocks.TryDequeue(out c);

        public static void Clear()
        {
            while (Blocks.TryDequeue(out _)) { }
        }
    }
}
