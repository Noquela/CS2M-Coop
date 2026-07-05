using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     INPUT-REPLAY (v56): instead of shipping the finished edges and re-deriving topology on the
    ///     receiver (which drifted: phantom junction nodes, mismatched zone blocks, differently-shaped
    ///     sub-areas), we ship the player's raw TOOL INPUT — the net tool's <c>ControlPoint</c> list plus
    ///     the prefab/mode/seed/config — and the receiver replays it through the game's OWN
    ///     <c>NetToolSystem.CreateDefinitionsJob</c>. The game then does the snapping / splitting / junction
    ///     merging identically on every PC, so the worlds match by construction with no reconstruction.
    ///
    ///     Every ControlPoint field is world-space and cross-machine stable EXCEPT <c>m_OriginalEntity</c>
    ///     (the snapped node/edge, machine-local): we ship its snap position + kind + a stable node id
    ///     (<c>CS2M_NodeSyncId</c>) so the receiver re-resolves the SAME local entity before replaying.
    ///     Arrays are parallel and all the same length N = the control-point count.
    /// </summary>
    public class NetToolReplayCommand : CommandBase
    {
        // Prefab identity (same shape as NetPlaceCommand).
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public uint Hash0 { get; set; }
        public uint Hash1 { get; set; }
        public uint Hash2 { get; set; }
        public uint Hash3 { get; set; }

        // Tool config consumed by CreateDefinitionsJob.
        public int Mode { get; set; }              // NetToolSystem.Mode
        public int RandomSeed { get; set; }
        public bool EditorMode { get; set; }
        public bool LeftHandTraffic { get; set; }
        public bool RemoveUpgrade { get; set; }
        public float ParallelOffset { get; set; }
        public int ParallelCount { get; set; }

        // --- ControlPoint fields, parallel arrays (index = control point) ---
        public float[] PosX { get; set; }          // m_Position
        public float[] PosY { get; set; }
        public float[] PosZ { get; set; }
        public float[] HitX { get; set; }          // m_HitPosition
        public float[] HitY { get; set; }
        public float[] HitZ { get; set; }
        public float[] DirX { get; set; }          // m_Direction (float2)
        public float[] DirZ { get; set; }
        public float[] HitDirX { get; set; }       // m_HitDirection (float3)
        public float[] HitDirY { get; set; }
        public float[] HitDirZ { get; set; }
        public float[] RotX { get; set; }          // m_Rotation (quaternion)
        public float[] RotY { get; set; }
        public float[] RotZ { get; set; }
        public float[] RotW { get; set; }
        public float[] SnapPriX { get; set; }      // m_SnapPriority (float2)
        public float[] SnapPriY { get; set; }
        public int[] ElemIdxX { get; set; }        // m_ElementIndex (int2)
        public int[] ElemIdxY { get; set; }
        public float[] CurvePos { get; set; }      // m_CurvePosition
        public float[] Elev { get; set; }          // m_Elevation

        // m_OriginalEntity translation: snap position + kind + stable node id.
        public float[] SnapPosX { get; set; }      // position of the snapped node/edge (for re-resolve)
        public float[] SnapPosZ { get; set; }
        public int[] SnapKind { get; set; }        // 0 = none, 1 = node, 2 = edge
        public ulong[] SnapNodeId { get; set; }    // CS2M_NodeSyncId of the snapped node (0 if edge/none)
    }
}
