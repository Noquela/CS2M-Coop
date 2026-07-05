using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class RouteCreateHandler : CommandHandler<RouteCreateCommand>
    {
        public RouteCreateHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(RouteCreateCommand command)
        {
            CS2M.Log.Verbose($"[Route] RECV create id={command.SyncId} prefab={command.PrefabName} " +
                             $"wps={command.WpX?.Length ?? 0} replace={command.Replace}");
            RouteSync.EnqueueCreate(command);
        }
    }

    public class RouteColorHandler : CommandHandler<RouteColorCommand>
    {
        public RouteColorHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(RouteColorCommand command)
        {
            CS2M.Log.Verbose($"[Route] RECV color id={command.SyncId} number={command.Number}");
            RouteSync.EnqueueColor(command);
        }
    }

    public class RouteVisibilityHandler : CommandHandler<RouteVisibilityCommand>
    {
        public RouteVisibilityHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(RouteVisibilityCommand command)
        {
            CS2M.Log.Verbose($"[Route] RECV visibility id={command.SyncId} number={command.Number} hidden={command.Hidden}");
            RouteSync.EnqueueVisibility(command);
        }
    }
}
