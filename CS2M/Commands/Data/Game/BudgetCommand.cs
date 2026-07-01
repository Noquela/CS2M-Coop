using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player changes a service's funding slider. The service is identified by its
    ///     prefab type+name (cross-PC stable); the receiver calls the game's
    ///     <c>CityServiceBudgetSystem.SetServiceBudget(prefab, percentage)</c>.
    /// </summary>
    public class BudgetCommand : CommandBase
    {
        public string ServiceType { get; set; }
        public string ServiceName { get; set; }
        public int Percentage { get; set; }
    }
}
