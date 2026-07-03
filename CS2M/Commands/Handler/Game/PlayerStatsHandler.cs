using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>
    ///     v50: receives the host's ~1 Hz player roster (names + latency) and refreshes the
    ///     in-game player panel. Host-originated broadcast — never relayed.
    /// </summary>
    public class PlayerStatsHandler : CommandHandler<PlayerStatsCommand>
    {
        public PlayerStatsHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false;
        }

        protected override void Handle(PlayerStatsCommand command)
        {
            PlayerStatsSync.Set(command);
            UI.ChatPanel.RefreshPlayerList();
        }
    }
}
