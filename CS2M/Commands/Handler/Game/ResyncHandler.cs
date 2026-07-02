using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Networking;

namespace CS2M.Commands.Handler.Game
{
    public class ResyncHandler : CommandHandler<ResyncCommand>
    {
        public ResyncHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false;
        }

        protected override void Handle(ResyncCommand command)
        {
            CS2M.Log.Info("[Resync] RECV — preparing to reload the world from host");
            NetworkInterface.Instance.LocalPlayer.PrepareResync();
        }
    }
}
