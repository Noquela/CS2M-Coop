using System;

namespace CS2M.Sync
{
    /// <summary>
    ///     v55: shared co-op sync-health flag. The StateHash detector sets it when the worlds drift
    ///     (and clears it when they converge); <see cref="SyncStatusSystem"/> reads it to drive the
    ///     on-screen sync badge. Turns our unique divergence detection into a user-facing trust signal.
    ///     The drift auto-expires after 30 s of no fresh DRIFT so a one-off blip doesn't stick red.
    /// </summary>
    public static class SyncHealth
    {
        private static readonly object Lock = new object();
        private static bool _drifting;
        private static string _info = "";
        private static DateTime _lastDrift;

        public static void SetDrift(bool drifting, string info)
        {
            lock (Lock)
            {
                _drifting = drifting;
                _info = info ?? "";
                if (drifting)
                {
                    _lastDrift = DateTime.UtcNow;
                }
            }
        }

        public static void Get(out bool drifting, out string info)
        {
            lock (Lock)
            {
                if (_drifting && (DateTime.UtcNow - _lastDrift).TotalSeconds > 30.0)
                {
                    _drifting = false;
                    _info = "";
                }

                drifting = _drifting;
                info = _info;
            }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                _drifting = false;
                _info = "";
            }
        }
    }
}
