using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast for a terraforming brush application (raise/lower/level/slope). The receiver replays
    ///     the same brush via the same dispatch the game's <c>ApplyBrushesSystem</c> uses. NOTE: terrain
    ///     edits are continuous brush strokes and the per-frame delta depends on each machine's frame
    ///     time, so this is <b>best-effort / approximate</b> — the on-demand full resync reconciles any
    ///     accumulated terrain drift.
    ///
    ///     v59 fidelity upgrade (MATRIX P1 / dossier terrain.md §6.1/2/3/4): the command used to carry
    ///     only Type+Pos+Size+Strength, so (a) painting Ore/Oil/FertileLand/GroundWater arrived as a
    ///     nonsense HEIGHT edit, (b) Slope had a zero direction vector (m_Start was never sent),
    ///     (c) the brush angle was dropped, (d) the falloff shape was a uniform white square.
    /// </summary>
    public class TerrainCommand : CommandBase
    {
        public int Type { get; set; }       // Game.Prefabs.TerraformingType (Shift/Level/Soften/Slope)
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float Size { get; set; }
        public float Strength { get; set; }

        /// <summary>v59: Game.Prefabs.TerraformingTarget (Height/Ore/Oil/FertileLand/GroundWater/
        /// Material). Old senders default to 0 = Height, which matches their behavior.</summary>
        public int Target { get; set; }

        /// <summary>v59: Brush.m_Start — the slope anchor point. Distinct from Pos (the drag target);
        /// TerrainSystem.cs:3839 derives the slope direction from (m_Target - m_Start), which was a
        /// zero vector on the receiver before. All-zero means "no anchor" (old sender) → receiver
        /// falls back to Pos.</summary>
        public float StartX { get; set; }
        public float StartY { get; set; }
        public float StartZ { get; set; }

        /// <summary>v59: Brush.m_Angle — footprint rotation (radians); was hardcoded 0 on apply.</summary>
        public float Angle { get; set; }

        /// <summary>v59: name of the BrushPrefab (falloff shape/texture), resolved per machine via
        /// PrefabSystem — identity by NAME per project law. Null/empty on old senders → uniform brush.</summary>
        public string BrushPrefab { get; set; }
    }
}
