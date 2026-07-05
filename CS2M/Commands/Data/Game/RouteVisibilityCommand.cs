using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player hides/shows a transport line in the Transportation Overview. Visibility
    ///     is the <c>Game.Routes.HiddenRoute</c> tag, added/removed by raw ECB with no Updated tag and no
    ///     Modify event — so the reroute/color detectors never saw it. The line is addressed exactly like
    ///     <see cref="RouteColorCommand"/> (SyncId first, else prefab name + RouteNumber); the receiver
    ///     adds or removes the tag to match.
    /// </summary>
    public class RouteVisibilityCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public string PrefabName { get; set; }
        public int Number { get; set; }
        public bool Hidden { get; set; }
    }
}
