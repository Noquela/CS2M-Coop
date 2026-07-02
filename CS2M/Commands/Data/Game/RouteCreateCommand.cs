using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     A transport line created (or, with <see cref="Replace"/>, re-routed) by a player.
    ///     Carries the full waypoint list as parallel arrays; the receiver rebuilds the route from the
    ///     prefab's baked archetypes (route/waypoint/segment) — the game's runtime systems then wire
    ///     Owner references, stop bookkeeping, lane connections and vehicles on their own.
    ///     Stop connections resolve by the stop object's SyncId when it has one, else by position
    ///     (station platforms / native stops exist on every PC at identical coordinates).
    /// </summary>
    public class RouteCreateCommand : CommandBase
    {
        public ulong SyncId { get; set; }

        /// <summary>True = the route already exists remotely; rebuild its waypoints/segments in place.</summary>
        public bool Replace { get; set; }

        public string PrefabType { get; set; }
        public string PrefabName { get; set; }

        /// <summary>Closed loop (first waypoint == last on the sender).</summary>
        public bool Complete { get; set; }

        public byte ColorR { get; set; }
        public byte ColorG { get; set; }
        public byte ColorB { get; set; }
        public byte ColorA { get; set; }

        /// <summary>The sender's RouteNumber ("Bus Line 3") so both PCs show the same line number.</summary>
        public int Number { get; set; }

        public float[] WpX { get; set; }
        public float[] WpY { get; set; }
        public float[] WpZ { get; set; }

        /// <summary>1 = this waypoint is a stop (had a Connected target on the sender).</summary>
        public byte[] WpHasConn { get; set; }

        /// <summary>SyncId of the connected stop object, 0 = resolve by position.</summary>
        public ulong[] WpConnId { get; set; }

        public float[] WpConnX { get; set; }
        public float[] WpConnZ { get; set; }
    }
}
