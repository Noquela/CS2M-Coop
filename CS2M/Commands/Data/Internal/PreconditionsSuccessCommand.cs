using CS2M.API.Commands;

namespace CS2M.Commands.Data.Internal
{
    public class PreconditionsSuccessCommand : CommandBase
    {
        /// <summary>
        ///     v62 (issue #14): non-zero when the client's SyncId-namespace nonce collided with one
        ///     already in the session — the client must adopt this host-assigned replacement before
        ///     allocating any id (CS2M_SyncIdSystem.OverrideNonce). 0 = keep your own.
        /// </summary>
        public ulong AssignedNonce { get; set; }
    }
}
