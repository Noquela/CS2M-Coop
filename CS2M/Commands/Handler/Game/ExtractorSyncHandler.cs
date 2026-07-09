using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>Host-originated broadcast: stash the Extractor batch for ExtractorApplySystem to mirror onto
    /// the client's areas. RelayOnServer=false — the host authored it (same as DemandSyncHandler).</summary>
    public class ExtractorSyncHandler : CommandHandler<ExtractorSyncCommand>
    {
        public ExtractorSyncHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false; // host-originated broadcast
        }

        protected override void Handle(ExtractorSyncCommand command)
        {
            CS2M.Log.Verbose($"[Extractor] RECV areas={command.AreaIds?.Length ?? 0}");
            RemoteExtractorQueue.Enqueue(command);
        }
    }
}
