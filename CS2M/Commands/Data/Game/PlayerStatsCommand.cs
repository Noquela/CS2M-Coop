using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v50: host-originated player roster, ~1 Hz — one entry per connected player (host included)
    ///     with the player id (0 = host, peer.Id+1 otherwise), display name and latency to the host
    ///     in ms. Feeds the in-game player panel on every client.
    /// </summary>
    public class PlayerStatsCommand : CommandBase
    {
        public int[] Ids { get; set; }
        public string[] Names { get; set; }
        public int[] Pings { get; set; }
    }
}
