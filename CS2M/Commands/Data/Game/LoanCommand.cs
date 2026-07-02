using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     City loan changed (take/repay). Any player can manage the loan; the HOST mirrors the money
    ///     delta (its authoritative balance then propagates via the money sync), other clients mirror
    ///     only the Loan component so the debt/interest display matches everywhere.
    /// </summary>
    public class LoanCommand : CommandBase
    {
        public int Amount { get; set; }
    }
}
