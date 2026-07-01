using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>
    ///     Receives a remote player's cursor position and stores it so
    ///     <c>PlayerCursorSystem</c> can render it in the world.
    /// </summary>
    public class PlayerCursorHandler : CommandHandler<PlayerCursorCommand>
    {
        public PlayerCursorHandler()
        {
            // Apply immediately on receive (not part of a transaction batch).
            TransactionCmd = false;
        }

        protected override void Handle(PlayerCursorCommand command)
        {
            RemotePlayerCursors.Update(command.SenderId, command.X, command.Y, command.Z, command.Valid,
                command.Username);
        }
    }
}
