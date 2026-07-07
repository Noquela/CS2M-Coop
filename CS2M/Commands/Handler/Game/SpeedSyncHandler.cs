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
            RelayOnServer = false; // issue #6: host-authoritative — a client never legitimately authors this
        }

        protected override void Handle(SpeedCommand command)
        {
            RemoteSpeedQueue.Set(command.Speed);
        }
    }
}
