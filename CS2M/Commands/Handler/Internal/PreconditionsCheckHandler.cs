using System.Collections.Generic;
using System.Linq;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Internal;
using CS2M.Mods;
using CS2M.Networking;
using CS2M.Util;
using LiteNetLib;

namespace CS2M.Commands.Handler.Internal
{
    public class PreconditionsCheckHandler : CommandHandler<PreconditionsCheckCommand>
    {
        public PreconditionsCheckHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false;
        }

        protected override void Handle(PreconditionsCheckCommand command)
        {
        }

        public void HandleOnServer(PreconditionsCheckCommand command, NetPeer peer)
        {
            Log.Debug($"Received Preconditions Check [PeerId: {peer.Id}]");

            PreconditionsUtil.Result result = PreconditionsUtil.CheckPreconditions(command);

            // Check the client username to see if anyone on the server already have a username
            if (NetworkInterface.Instance.PlayerListConnected.Any(p => p.Username.Equals(command.Username)))
            {
                Log.Debug($"[Preconditions Check] Username '{command.Username}' is already connected.");
                result.Errors |= PreconditionsUtil.Errors.USERNAME_NOT_AVAILABLE;
            }

            // Check the password to see if it matches (only if the server has provided a password).
            if (!string.IsNullOrEmpty(NetworkInterface.Instance.LocalPlayer.GetConnectionPassword()))
            {
                if (!NetworkInterface.Instance.LocalPlayer.GetConnectionPassword().Equals(command.Password))
                {
                    Log.Debug($"[Preconditions Check] Password '{command.Password}' is not correct.");
                    result.Errors |= PreconditionsUtil.Errors.PASSWORD_INCORRECT;
                }
            }

            if (result.Errors == PreconditionsUtil.Errors.NONE)
            {
                // Issue #14: guarantee SyncId-namespace uniqueness. The client's nonce is a random
                // 24-bit draw seeded from tick count — two processes CAN draw the same one. The host
                // knows every nonce in the session, so a collision (or a missing nonce from an old
                // sender) gets a fresh host-assigned replacement shipped back with the success.
                HashSet<ulong> nonces = NetworkInterface.Instance.SessionNonces;
                if (nonces.Count == 0)
                {
                    nonces.Add(Sync.CS2M_SyncIdSystem.Nonce); // host's own namespace
                }

                ulong assigned = 0;
                ulong clientNonce = command.SessionNonce & 0xFFFFFF;
                if (clientNonce == 0 || nonces.Contains(clientNonce))
                {
                    var rng = new System.Random();
                    do
                    {
                        assigned = (ulong)(rng.Next() & 0xFFFFFF);
                    } while (assigned == 0 || nonces.Contains(assigned));

                    nonces.Add(assigned);
                    Log.Info($"[Id] NONCE collision for '{command.Username}' ({clientNonce}) — assigned {assigned}");
                }
                else
                {
                    nonces.Add(clientNonce);
                }

                NetworkInterface.Instance.LocalPlayer.SendToClient(peer,
                    new PreconditionsSuccessCommand { AssignedNonce = assigned });

                // Add the new player as a connected player
                NetworkInterface.Instance.PlayerConnected(new RemotePlayer(peer, command.Username, PlayerType.CLIENT));
            }
            else
            {
                NetworkInterface.Instance.LocalPlayer.SendToClient(peer, new PreconditionsErrorCommand()
                {
                    Errors = result.Errors,
                    ModVersion = VersionUtil.GetModVersion(),
                    GameVersion = VersionUtil.GetGameVersion(),
                    Mods = ModSupport.Instance.RequiredModsForSync,
                    DlcIds = DlcCompat.RequiredDLCsForSync,
                });
            }
        }
    }
}
