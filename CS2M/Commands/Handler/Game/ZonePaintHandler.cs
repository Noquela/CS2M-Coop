using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class ZonePaintHandler : CommandHandler<ZonePaintCommand>
    {
        public ZonePaintHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(ZonePaintCommand command)
        {
            int n = command.CellIndices != null ? command.CellIndices.Length : 0;
            CS2M.Log.Verbose($"[Zone] RECV block=({command.BlockX:F0},{command.BlockZ:F0}) cells={n}");
            RemoteZoneQueue.Enqueue(command);
        }
    }
}
