using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class ProgressionSyncHandler : CommandHandler<ProgressionSyncCommand>
    {
        public ProgressionSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(ProgressionSyncCommand command)
        {
            CS2M.Log.Info($"[Prog] RECV xp={command.Xp} milestone={command.AchievedMilestone}");
            RemoteProgressionQueue.Set(command);
        }
    }
}
