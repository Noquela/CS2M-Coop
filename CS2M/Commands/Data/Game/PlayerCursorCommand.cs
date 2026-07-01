using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcasts the sending player's cursor world position (and name) so the
    ///     other players can render a marker + label where this player is pointing.
    /// </summary>
    /// <remarks>
    ///     Sent frequently (a few times per second) by <c>PlayerCursorSystem</c>.
    ///     Uses plain floats instead of float3 to keep MessagePack serialization
    ///     trivial and resolver-independent.
    /// </remarks>
    public class PlayerCursorCommand : CommandBase
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        /// <summary>Whether the cursor currently hovers a valid world point.</summary>
        public bool Valid { get; set; }

        /// <summary>Display name of the sending player.</summary>
        public string Username { get; set; }
    }
}
