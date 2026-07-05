using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player changes a service FEE (Economy &gt; Services fee sliders, or the Reset
    ///     button which rewrites every collected fee to its default). Fees are separate from the funding
    ///     PERCENTAGE handled by <see cref="BudgetCommand"/> and drive consumption/happiness/income, so a
    ///     divergence desyncs the simulation. The fee is identified by its <c>PlayerResource</c> enum value
    ///     (cross-PC stable, no SyncId needed); the receiver calls the game's
    ///     <c>Game.Simulation.ServiceFeeSystem.SetFee</c> on the City's <c>ServiceFee</c> buffer.
    /// </summary>
    public class FeeCommand : CommandBase
    {
        public int Resource { get; set; }
        public float Fee { get; set; }
    }
}
