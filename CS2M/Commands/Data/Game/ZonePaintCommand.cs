using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     A change to a zoning Block's cells. Blocks are deterministic children of roads, so we name
    ///     the block by its world position/direction/size (matches on both PCs once roads are synced),
    ///     and carry the changed cell indices + their new zone as ZonePrefab names ("" = dezone/None).
    ///     Index-based cell diffing avoids reconstructing paint rectangles.
    /// </summary>
    public class ZonePaintCommand : CommandBase
    {
        // Block identity (deterministic from roads).
        public float BlockX { get; set; }
        public float BlockZ { get; set; }
        public float DirX { get; set; }
        public float DirZ { get; set; }
        public int SizeX { get; set; }
        public int SizeY { get; set; }

        // Changed cells (parallel arrays).
        public int[] CellIndices { get; set; }
        public string[] ZoneNames { get; set; }
    }
}
