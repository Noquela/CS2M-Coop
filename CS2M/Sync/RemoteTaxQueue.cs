namespace CS2M.Sync
{
    /// <summary>
    ///     Thread-safe hand-off for the latest received tax-rate array (latest wins — taxes are a
    ///     shared snapshot). Handler sets; <see cref="TaxApplySystem"/> takes.
    /// </summary>
    public static class RemoteTaxQueue
    {
        private static readonly object Lock = new object();
        private static bool _has;
        private static int[] _rates;

        public static void Set(int[] rates)
        {
            lock (Lock)
            {
                _rates = rates;
                _has = true;
            }
        }

        public static bool TryTake(out int[] rates)
        {
            lock (Lock)
            {
                rates = _rates;
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
                _rates = null;
            }
        }
    }
}
