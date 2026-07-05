using System.Collections.Generic;

namespace CS2M.Sync
{
    /// <summary>
    ///     Shared snapshot (PlayerResource int → fee) used to diff service fees and as the echo guard:
    ///     the apply refreshes it after SetFee, so the detector sees no diff and doesn't echo back.
    /// </summary>
    public static class FeeSync
    {
        public static readonly Dictionary<int, float> Snapshot = new Dictionary<int, float>();

        public static void Clear()
        {
            Snapshot.Clear();
        }
    }
}
