using System.Collections.Generic;

namespace CS2M.Sync
{
    /// <summary>
    ///     Shared snapshot (serviceName → funding %) used to diff service budgets and as the echo guard:
    ///     the apply refreshes it after setting a budget, so the detector sees no diff and doesn't echo.
    /// </summary>
    public static class BudgetSync
    {
        public static readonly Dictionary<string, int> Snapshot = new Dictionary<string, int>();

        public static void Clear()
        {
            Snapshot.Clear();
        }
    }
}
