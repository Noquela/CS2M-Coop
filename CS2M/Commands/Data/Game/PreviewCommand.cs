using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v67: NATIVE build-preview streaming. While a player has the ROAD or BUILDING tool active and
    ///     the game shows its own ghost (real mesh / nodes / snapping, tagged <c>Game.Tools.Temp</c>),
    ///     this ships the tool's raw input ~12 Hz so every other PC re-materializes the SAME native ghost
    ///     through the game's own Generate* pipeline — WITHOUT <c>CreationFlags.Permanent</c> and WITHOUT
    ///     running any apply, so it can never become a real build (stays Temp, receiver-managed lifecycle).
    ///
    ///     Ephemeral / relay-only (Infra), never a WorldContract: nothing here mutates persistent world
    ///     state — the real build rides the separate NetBatch/placement path when the player CONFIRMS.
    ///
    ///     <c>Kind</c>: 0 = hide (tool released / deactivated), 1 = road, 2 = building.
    ///       • Road    (Kind==1): prefab identity + tool config + the net tool's <c>ControlPoint</c> list as
    ///                            parallel arrays (index = control point). Snapped node/edge is re-resolved
    ///                            on the receiver by position (SnapKind + SnapPos) — no stable id is minted
    ///                            for a throwaway ghost.
    ///       • Building(Kind==2): prefab identity + a single world Transform (BPos* / BRot*).
    /// </summary>
    public class PreviewCommand : CommandBase
    {
        public int Kind { get; set; }          // 0 = hide, 1 = road, 2 = building
        public string Username { get; set; }

        // Prefab identity (same shape as NetPlaceCommand/ObjectPlaceCommand — base content resolves by
        // type+name, hash stays 0).
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }

        // --- Road tool config consumed by NetToolSystem.CreateDefinitionsJob ---
        public int Mode { get; set; }          // NetToolSystem.Mode
        public int RandomSeed { get; set; }
        public bool EditorMode { get; set; }
        public bool LeftHandTraffic { get; set; }
        public bool RemoveUpgrade { get; set; }
        public float ParallelOffset { get; set; }
        public int ParallelCount { get; set; }

        // --- Road ControlPoint fields, parallel arrays (index = control point) ---
        public float[] PosX { get; set; }      // m_Position
        public float[] PosY { get; set; }
        public float[] PosZ { get; set; }
        public float[] HitX { get; set; }      // m_HitPosition
        public float[] HitY { get; set; }
        public float[] HitZ { get; set; }
        public float[] DirX { get; set; }      // m_Direction (float2)
        public float[] DirZ { get; set; }
        public float[] HitDirX { get; set; }   // m_HitDirection (float3)
        public float[] HitDirY { get; set; }
        public float[] HitDirZ { get; set; }
        public float[] RotX { get; set; }      // m_Rotation (quaternion)
        public float[] RotY { get; set; }
        public float[] RotZ { get; set; }
        public float[] RotW { get; set; }
        public float[] SnapPriX { get; set; }  // m_SnapPriority (float2)
        public float[] SnapPriY { get; set; }
        public int[] ElemIdxX { get; set; }    // m_ElementIndex (int2)
        public int[] ElemIdxY { get; set; }
        public float[] CurvePos { get; set; }  // m_CurvePosition
        public float[] Elev { get; set; }      // m_Elevation
        public float[] SnapPosX { get; set; }  // position of the snapped node/edge (re-resolve by pos)
        public float[] SnapPosZ { get; set; }
        public int[] SnapKind { get; set; }    // 0 = none, 1 = node, 2 = edge

        // --- Building (Kind==2): single world Transform ---
        public float BPosX { get; set; }
        public float BPosY { get; set; }
        public float BPosZ { get; set; }
        public float BRotX { get; set; }
        public float BRotY { get; set; }
        public float BRotZ { get; set; }
        public float BRotW { get; set; }
    }
}
