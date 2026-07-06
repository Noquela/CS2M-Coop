using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class VehicleModelSyncHandler : CommandHandler<VehicleModelCommand>
    {
        public VehicleModelSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(VehicleModelCommand command)
        {
            CS2M.Log.Info($"[VehicleModel] RECV id={command.SyncId} number={command.Number} " +
                          $"primary={(command.PrimaryNames?.Length ?? 0)}");
            RemoteVehicleModelQueue.Enqueue(command);
        }
    }
}
