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
