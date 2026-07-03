using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v50: "look here!" map ping — a player marks a world position for everyone (chat command
    ///     /ping pings where the mouse points). Receivers draw a pulsing marker there for a few
    ///     seconds and print a chat line. Relayed by the host so it reaches all players.
    /// </summary>
    public class MapPingCommand : CommandBase
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public string Username { get; set; }
    }
}
