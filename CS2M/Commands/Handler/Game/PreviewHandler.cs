using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>
    ///     v67: receives a remote player's NATIVE build-preview stream and hands it to
    ///     <see cref="RemotePreviewInbox"/> (latest-wins per player). <see cref="PreviewApplySystem"/>
    ///     drains the inbox each frame and re-materializes the ghost through the game's own Generate*
    ///     pipeline. Relayed by the host so every client sees every OTHER client's preview; applied
    ///     immediately (not transaction-batched) since it is ephemeral.
    /// </summary>
    public class PreviewHandler : CommandHandler<PreviewCommand>
    {
        public PreviewHandler()
        {
            TransactionCmd = false;
            RelayOnServer = true; // every client should see every OTHER client's preview
        }

        protected override void Handle(PreviewCommand c)
        {
            RemotePreviewInbox.Put(c);
        }
    }
}
