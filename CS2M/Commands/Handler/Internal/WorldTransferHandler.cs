using CS2M.API.Commands;
using CS2M.Commands.Data.Internal;
using CS2M.Networking;

namespace CS2M.Commands.Handler.Internal
{
    public class WorldTransferHandler : CommandHandler<WorldTransferCommand>
    {
        public WorldTransferHandler()
        {
            RelayOnServer = false; // per-peer stream, never rebroadcast
        }

        protected override void Handle(WorldTransferCommand command)
        {
            NetworkInterface.Instance.LocalPlayer.SliceReceived(command);
        }
    }
}
