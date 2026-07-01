namespace CS2M.Sync
{
    /// <summary>Thread-safe hand-off for the latest received sim speed (latest wins). Handler sets;
    /// <see cref="SpeedSyncApplySystem"/> takes.</summary>
    public static class RemoteSpeedQueue
    {
        private static readonly object Lock = new object();
        private static bool _has;
        private static float _speed;

        public static void Set(float speed)
        {
            lock (Lock)
            {
                _speed = speed;
                _has = true;
            }
        }

        public static bool TryTake(out float speed)
        {
            lock (Lock)
            {
                speed = _speed;
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
