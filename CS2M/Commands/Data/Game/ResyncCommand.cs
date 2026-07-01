using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Host → clients: prepare to receive a fresh copy of the world (on-demand full resync). The
    ///     host follows this immediately with the same WorldTransferCommand slices used at join.
    /// </summary>
    public class ResyncCommand : CommandBase
    {
    }
}
