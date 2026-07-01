using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class WaterSyncHandler : CommandHandler<WaterCommand>
    {
        public WaterSyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(WaterCommand command)
        {
            CS2M.Log.Info($"[Water] RECV pos=({command.PosX:F0},{command.PosZ:F0}) r={command.Radius}");
            RemoteWaterQueue.Enqueue(command);
        }
    }
}
