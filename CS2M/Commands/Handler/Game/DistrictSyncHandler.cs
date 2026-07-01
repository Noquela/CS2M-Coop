using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class DistrictSyncHandler : CommandHandler<DistrictCommand>
    {
        public DistrictSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(DistrictCommand command)
        {
            CS2M.Log.Info($"[District] RECV name={command.PrefabName} points={(command.Xs == null ? 0 : command.Xs.Length)}");
            RemoteDistrictQueue.Enqueue(command);
        }
    }
}
