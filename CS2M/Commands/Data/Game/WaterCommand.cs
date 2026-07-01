using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player places a water source (river/lake/sea spring, drain). A water source
    ///     is a plain ECS entity with <c>WaterSourceData</c> + a <c>Transform</c>, so the receiver just
    ///     recreates it at the same world position with the same parameters.
    /// </summary>
    public class WaterCommand : CommandBase
    {
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float Radius { get; set; }
        public float Height { get; set; }
        public float Multiplier { get; set; }
        public float Polluted { get; set; }
        public int ConstantDepth { get; set; }
    }
}
