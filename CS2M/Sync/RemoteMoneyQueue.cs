namespace CS2M.Sync
{
    /// <summary>
    ///     Thread-safe hand-off for the latest received city cash value (latest wins — money is a
    ///     snapshot, not a stream of deltas). Handler sets; <see cref="MoneySyncApplySystem"/> takes.
    /// </summary>
    public static class RemoteMoneyQueue
    {
        private static readonly object Lock = new object();
        private static bool _has;
        private static int _cash;

        public static void Set(int cash)
        {
            lock (Lock)
            {
                _cash = cash;
                _has = true;
            }
        }

        public static bool TryTake(out int cash)
        {
            lock (Lock)
            {
                cash = _cash;
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
