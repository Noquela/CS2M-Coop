using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class AreaSubObjectHandler : CommandHandler<AreaSubObjectCommand>
    {
        public AreaSubObjectHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(AreaSubObjectCommand command)
        {
            CS2M.Log.Verbose($"[AreaObj] RECV ops={command.Ops?.Length ?? 0} anchor={command.OwnerAnchorId}");
            RemoteAreaSubObjectQueue.Enqueue(command);
        }
    }
}
