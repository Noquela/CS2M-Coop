using CS2M.API.Commands;

namespace CS2M.Commands.Data.Internal
{
    /// <summary>
    ///     Host -> clients: the host is about to stop the session (server shutting down / host
    ///     leaving). Sent right before the socket is torn down so clients can tell this apart from an
    ///     unexpected network drop and suppress the v50 auto-reconnect cycle instead of hammering a
    ///     dead host for up to 2 minutes (24 tries x ~5s).
    /// </summary>
    /// <remarks>
    /// Sent by: NetworkInterface.StopServer (host only)
    /// </remarks>
    public class ServerStoppingCommand : CommandBase
    {
    }
}
