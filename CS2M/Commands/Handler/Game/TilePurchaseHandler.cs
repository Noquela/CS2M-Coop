using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class TilePurchaseHandler : CommandHandler<TilePurchaseCommand>
    {
        public TilePurchaseHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(TilePurchaseCommand command)
        {
            CS2M.Log.Verbose($"[Tile] RECV tiles={command.Xs?.Length ?? 0}");
            TileSync.Enqueue(command);
        }
    }
}
