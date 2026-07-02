using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class LoanHandler : CommandHandler<LoanCommand>
    {
        public LoanHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(LoanCommand command)
        {
            CS2M.Log.Verbose($"[Loan] RECV amount={command.Amount}");
            LoanSync.Set(command);
        }
    }

    public class RenameHandler : CommandHandler<RenameCommand>
    {
        public RenameHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(RenameCommand command)
        {
            CS2M.Log.Verbose($"[Rename] RECV kind={command.TargetKind} name=\"{command.Name}\"");
            RenameSync.Enqueue(command);
        }
    }
}
