using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>v50: queues host fire events for FireApplySystem (main-thread apply).</summary>
    public class FireSyncHandler : CommandHandler<FireSyncCommand>
    {
        public FireSyncHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false; // host-originated broadcast
        }

        protected override void Handle(FireSyncCommand command)
        {
            FireSync.Enqueue(command);
        }
    }
}
