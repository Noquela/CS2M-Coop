using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class NetPlaceHandler : CommandHandler<NetPlaceCommand>
    {
        public NetPlaceHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(NetPlaceCommand command)
        {
            CS2M.Log.Info(
                $"[Net] RECV type={command.PrefabType} name={command.PrefabName} " +
                $"start=({command.Ax:F1},{command.Ay:F1},{command.Az:F1}) end=({command.Dx:F1},{command.Dy:F1},{command.Dz:F1}) seed={command.RandomSeed}");
            RemoteNetQueue.Enqueue(command);
        }
    }
}
