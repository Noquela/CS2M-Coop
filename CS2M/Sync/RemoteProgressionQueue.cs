using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe latest-wins hand-off for the host's progression snapshot.</summary>
    public static class RemoteProgressionQueue
    {
        private static readonly object Lock = new object();
        private static bool _has;
        private static ProgressionSyncCommand _cmd;

        public static void Set(ProgressionSyncCommand cmd)
        {
            lock (Lock)
            {
                _cmd = cmd;
                _has = true;
            }
        }

        public static bool TryTake(out ProgressionSyncCommand cmd)
        {
            lock (Lock)
            {
                cmd = _cmd;
                bool had = _has;
                _has = false;
                return had;
            }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                _has = false;
            }
        }
    }
}
