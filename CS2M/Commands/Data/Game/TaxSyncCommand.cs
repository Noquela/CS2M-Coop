using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when any player changes a tax rate. Carries the whole tax-rate array
    ///     (<c>TaxSystem.GetTaxRates()</c> — one int per index; the layout is deterministic for the same
    ///     game version, so plain indices are cross-PC stable) so every city's taxes match.
    /// </summary>
    public class TaxSyncCommand : CommandBase
    {
        public int[] Rates { get; set; }
    }
}
