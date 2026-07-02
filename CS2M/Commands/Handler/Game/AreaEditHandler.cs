using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class AreaEditHandler : CommandHandler<AreaEditCommand>
    {
        public AreaEditHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(AreaEditCommand command)
        {
            CS2M.Log.Verbose($"[Area] RECV name={command.PrefabName} nodes={command.Xs?.Length ?? 0}");
            RemoteAreaQueue.Enqueue(command);
        }
    }
}
