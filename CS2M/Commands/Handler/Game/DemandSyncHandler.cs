using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>v51: stores the host's RCI demand snapshot for DemandSyncSystems to mirror.</summary>
    public class DemandSyncHandler : CommandHandler<DemandSyncCommand>
    {
        public DemandSyncHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false; // host-originated broadcast
        }

        protected override void Handle(DemandSyncCommand command)
        {
            DemandSync.Set(command);
        }
    }
}
