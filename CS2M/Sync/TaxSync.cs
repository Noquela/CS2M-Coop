namespace CS2M.Sync
{
    /// <summary>
    ///     Shared snapshot of the tax-rate array, used both as the diff baseline by
    ///     <see cref="TaxDetectorSystem"/> and as the echo guard: <see cref="TaxApplySystem"/> refreshes
    ///     it after applying a remote change, so the detector sees no diff and doesn't echo it back.
    /// </summary>
    public static class TaxSync
    {
        public static int[] Snapshot;

        public static void Clear()
        {
            Snapshot = null;
        }
    }
}
