using CS2M.API.Commands;
using CS2M.Commands.Data.Internal;
using CS2M.Networking;

namespace CS2M.Commands.Handler.Internal
{
    public class PreconditionsSuccessHandler : CommandHandler<PreconditionsSuccessCommand>
    {
        public PreconditionsSuccessHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false;
        }

        protected override void Handle(PreconditionsSuccessCommand command)
        {
            // Issue #14: adopt the host-assigned nonce (0 = ours didn't collide, keep it).
            if (command.AssignedNonce != 0)
            {
                Sync.CS2M_SyncIdSystem.OverrideNonce(command.AssignedNonce);
            }

            NetworkInterface.Instance.LocalPlayer.WaitingToJoin();
        }
    }
}
