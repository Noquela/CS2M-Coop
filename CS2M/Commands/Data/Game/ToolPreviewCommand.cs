using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v55: live tool preview. While a player DRAGS a road, the net tool creates a ghost
    ///     (Temp+Curve). This ships that in-progress curve ~12 Hz so everyone sees them drawing
    ///     BEFORE they commit — the thing Bruno missed from the old version. <c>Active=false</c> is
    ///     the one-shot "they stopped dragging" hide. The curve is the Bezier's four control points
    ///     a/b/c/d (each xyz).
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
    }
}
