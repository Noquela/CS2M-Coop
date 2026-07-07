using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class EnvSyncHandler : CommandHandler<EnvSyncCommand>
    {
        public EnvSyncHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false; // issue #6: host-authoritative — a client never legitimately authors this
        }

        protected override void Handle(EnvSyncCommand command)
        {
            RemoteEnvQueue.Set(command);
        }
    }

    public class StateHashHandler : CommandHandler<StateHashCommand>
    {
        public StateHashHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(StateHashCommand command)
        {
            RemoteStateHashQueue.Set(command);
        }
    }
}
