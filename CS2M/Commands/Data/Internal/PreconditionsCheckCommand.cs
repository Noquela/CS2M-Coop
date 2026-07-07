namespace CS2M.Commands.Data.Internal
{
    public class PreconditionsCheckCommand : PreconditionsDataCommand
    {
        /// <summary>
        ///     The username this user will be playing as, important
        ///     as the server will keep track of this user.
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        ///     An optional password if the server is set up to
        ///     require a password.
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        ///     v62 (issue #14): this client's 24-bit SyncId-namespace nonce. The host checks it against
        ///     every nonce already in the session and assigns a fresh one on collision (see
        ///     PreconditionsSuccessCommand.AssignedNonce) — id collisions become impossible instead of
        ///     just improbable.
        /// </summary>
        public ulong SessionNonce { get; set; }
    }
}
