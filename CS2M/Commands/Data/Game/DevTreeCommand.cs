using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     A development-tree node purchase ("skill tree"). Addressed by the node prefab's name —
    ///     stable across PCs. The receiver mirrors the purchase: raises the same Unlock event the
    ///     game's <c>DevTreeSystem.Purchase</c> raises and deducts the node cost from its own
    ///     DevTreePoints (both sides earn identical points from the synced XP/milestones).
    /// </summary>
    public class DevTreeCommand : CommandBase
    {
        public string NodeName { get; set; }
    }
}
