using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>Bulldoze a synced object on the other PCs, named by its cross-PC <c>CS2M_SyncId</c>.</summary>
    public class DeleteCommand : CommandBase
    {
        public ulong SyncId { get; set; }
    }
}
