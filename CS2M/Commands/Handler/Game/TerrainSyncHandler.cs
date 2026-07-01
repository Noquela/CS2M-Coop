using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class TerrainSyncHandler : CommandHandler<TerrainCommand>
    {
        public TerrainSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(TerrainCommand command)
        {
            RemoteTerrainQueue.Enqueue(command);
        }
    }
}
