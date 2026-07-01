using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class SpeedSyncHandler : CommandHandler<SpeedCommand>
    {
        public SpeedSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(SpeedCommand command)
        {
            RemoteSpeedQueue.Set(command.Speed);
        }
    }
}
