using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class PolicySyncHandler : CommandHandler<PolicyCommand>
    {
        public PolicySyncHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(PolicyCommand command)
        {
            CS2M.Log.Info($"[Policy] RECV name={command.PolicyName} active={command.Active}");
            RemotePolicyQueue.Enqueue(command);
        }
    }
}
