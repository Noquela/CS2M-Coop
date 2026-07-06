using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class ZoneBlockAuthorityHandler : CommandHandler<ZoneBlockAuthorityCommand>
    {
        public ZoneBlockAuthorityHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(ZoneBlockAuthorityCommand command)
        {
            int blocks = command.EdgeStartIds != null ? command.EdgeStartIds.Length : 0;
            CS2M.Log.Info($"[ZoneAuth] RECV blocks={blocks}");
            RemoteZoneBlockQueue.Enqueue(command);
        }
    }
}
