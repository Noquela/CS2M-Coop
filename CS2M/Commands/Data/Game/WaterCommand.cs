using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player places OR removes a water source (river/lake/sea spring, drain).
    ///     A water source is a plain ECS entity with <c>WaterSourceData</c> + a <c>Transform</c>; the
    ///     receiver recreates it at the same XZ but anchors Y to ITS OWN terrain height (terrain sync
    ///     is best-effort, so an absolute Y could float above local ground and flood a neighborhood —
    ///     seen in the field). v50: removals sync too; before, a deleted source lived forever on the
    ///     other PCs, flooding "out of nowhere".
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

        /// <summary>v50: true = remove the source nearest to (PosX, PosZ) instead of creating.</summary>
        public bool Delete { get; set; }

        /// <summary>v55: true = MOVE the source nearest to (OldX, OldZ) to (PosX, PosZ) in place — a
        /// relocation keeps the same entity, so the create/delete detectors never fired and the source
        /// stayed at the old spot (still simulating) on every remote.</summary>
        public bool Move { get; set; }

        public float OldX { get; set; }
        public float OldZ { get; set; }
    }
}
