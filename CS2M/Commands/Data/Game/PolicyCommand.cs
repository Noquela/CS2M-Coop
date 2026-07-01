using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Toggle a CITY-WIDE policy. Identified by prefab type+name (cross-PC stable). The receiver
    ///     raises the same policy-change event the game's UI raises. District policies are v2 (need
    ///     district sync first).
    /// </summary>
    public class PolicyCommand : CommandBase
    {
        public string PolicyType { get; set; }
        public string PolicyName { get; set; }
        public bool Active { get; set; }
        public float Adjustment { get; set; }
    }
}
