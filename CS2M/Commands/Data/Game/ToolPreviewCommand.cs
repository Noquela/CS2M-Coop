using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v55: live tool preview. While a player DRAGS a road, the net tool creates a ghost
    ///     (Temp+Curve). This ships that in-progress curve ~12 Hz so everyone sees them drawing
    ///     BEFORE they commit — the thing Bruno missed from the old version. <c>Active=false</c> is
    ///     the one-shot "they stopped dragging" hide. The curve is the Bezier's four control points
    ///     a/b/c/d (each xyz).
    ///
    ///     v56: extended to OBJECT placement ghosts (building/farm/service/prop — anything the object
    ///     tool is positioning, identified by Temp+PrefabRef+Transform+ObjectGeometry rather than
    ///     Temp+Curve+Edge). Up to <c>MaxPreviewObjects</c> footprints ride along as parallel arrays
    ///     (ObjPosX[i]/ObjPosY[i]/ObjPosZ[i]/ObjRotY[i]/ObjSizeX[i]/ObjSizeZ[i]). These are ADDITIVE:
    ///     a peer running the old build simply never populates them (null/empty), and a new peer
    ///     receiving an old packet just sees zero objects — the road preview (Active/A../D..) is
    ///     untouched and still works standalone either way.
    /// </summary>
    public class ToolPreviewCommand : CommandBase
    {
        public bool Active { get; set; }
        public string Username { get; set; }
        public float Ax { get; set; }
        public float Ay { get; set; }
        public float Az { get; set; }
        public float Bx { get; set; }
        public float By { get; set; }
        public float Bz { get; set; }
        public float Cx { get; set; }
        public float Cy { get; set; }
        public float Cz { get; set; }
        public float Dx { get; set; }
        public float Dy { get; set; }
        public float Dz { get; set; }

        // v56: object-placement footprints (parallel arrays, length = object count, 0..MaxPreviewObjects).
        // Null/empty on old senders or when no object ghost is active — draw nothing extra in that case.
        public float[] ObjPosX { get; set; }
        public float[] ObjPosY { get; set; }
        public float[] ObjPosZ { get; set; }
        public float[] ObjRotY { get; set; } // yaw only (radians) — footprints don't need pitch/roll
        public float[] ObjSizeX { get; set; }
        public float[] ObjSizeZ { get; set; }
    }
}
