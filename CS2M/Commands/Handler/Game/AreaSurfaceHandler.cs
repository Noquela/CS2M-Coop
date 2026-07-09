using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class AreaSurfaceHandler : CommandHandler<AreaSurfaceCommand>
    {
        public AreaSurfaceHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(AreaSurfaceCommand command)
        {
            CS2M.Log.Verbose($"[AreaSurf] RECV ops={command.Ops?.Length ?? 0} anchor={command.OwnerAnchorId}");
            RemoteAreaSurfaceQueue.Enqueue(command);
        }
    }
}
