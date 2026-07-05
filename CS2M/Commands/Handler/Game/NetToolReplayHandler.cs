using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class NetToolReplayHandler : CommandHandler<NetToolReplayCommand>
    {
        public NetToolReplayHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(NetToolReplayCommand command)
        {
            CS2M.Log.Info($"[Replay] RECV name={command.PrefabName} mode={command.Mode} " +
                          $"points={(command.PosX?.Length ?? 0)}");
            RemoteReplayQueue.Enqueue(command);
        }
    }
}
