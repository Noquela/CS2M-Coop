using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class ServiceDistrictSyncHandler : CommandHandler<ServiceDistrictCommand>
    {
        public ServiceDistrictSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(ServiceDistrictCommand command)
        {
            CS2M.Log.Info($"[ServiceDistrict] RECV building={command.BuildingPrefabName} " +
                          $"districts={(command.DistrictPrefabNames?.Length ?? 0)}");
            RemoteServiceDistrictQueue.Enqueue(command);
        }
    }
}
