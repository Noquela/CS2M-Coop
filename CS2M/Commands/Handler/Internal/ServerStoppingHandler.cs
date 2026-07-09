using CS2M.API;
using CS2M.API.Commands;
using CS2M.Commands.Data.Internal;
using CS2M.Networking;

namespace CS2M.Commands.Handler.Internal
{
    /// <summary>
    ///     Client-side: the host just told us it's stopping the session on purpose. Suppress the v50
    ///     auto-reconnect cycle BEFORE the actual disconnect happens (which normally follows within a
    ///     frame or two, once the host tears its socket down) and let the player know why, instead of
    ///     silently cycling through 24 blind reconnect attempts.
    /// </summary>
    public class ServerStoppingHandler : CommandHandler<ServerStoppingCommand>
    {
        public ServerStoppingHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false;
        }

        protected override void Handle(ServerStoppingCommand command)
        {
            // Same mechanism UserDisconnect() uses to mark a disconnect as intentional — set it
            // BEFORE the disconnect event fires so OnClientDisconnected() sees it and never starts
            // the auto-reconnect cycle.
            NetworkInterface.Instance.LocalPlayer.ServerAnnouncedStopping();

            Chat.Instance?.PrintGameMessage("[CS2M] - Host closed the session");
        }
    }
}
