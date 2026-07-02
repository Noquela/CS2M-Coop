using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Latest-wins mailbox for the host's environment scalars (weather + clock).</summary>
    public static class RemoteEnvQueue
    {
        private static readonly object Lock = new object();
        private static EnvSyncCommand _latest;

        public static void Set(EnvSyncCommand cmd)
        {
            lock (Lock) { _latest = cmd; }
        }

        public static bool TryTake(out EnvSyncCommand cmd)
        {
            lock (Lock)
            {
                cmd = _latest;
                _latest = null;
                return cmd != null;
            }
        }

        public static void Clear()
        {
            lock (Lock) { _latest = null; }
        }
    }

    /// <summary>Latest-wins mailbox for the host's world-state fingerprint.</summary>
    public static class RemoteStateHashQueue
    {
        private static readonly object Lock = new object();
        private static StateHashCommand _latest;

        public static void Set(StateHashCommand cmd)
        {
            lock (Lock) { _latest = cmd; }
        }

        public static bool TryTake(out StateHashCommand cmd)
        {
            lock (Lock)
            {
                cmd = _latest;
                _latest = null;
                return cmd != null;
            }
        }

        public static void Clear()
        {
            lock (Lock) { _latest = null; }
        }
    }
}
