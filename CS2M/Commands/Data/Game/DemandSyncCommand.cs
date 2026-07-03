using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v51: host-authoritative RCI demand bars. Demand derives from each PC's local citizen
    ///     simulation (not syncable by design), so the bars drifted apart ("I had low residential
    ///     demand and he didn't"). Since growable spawning already follows the HOST's demand, the
    ///     host now broadcasts its numbers (~0.5 Hz) and clients mirror them into the UI.
    /// </summary>
    public class DemandSyncCommand : CommandBase
    {
        public int Household { get; set; }
        public int ResLow { get; set; }
        public int ResMedium { get; set; }
        public int ResHigh { get; set; }
        public int Commercial { get; set; }
        public int CommercialBuilding { get; set; }
        public int Industrial { get; set; }
        public int IndustrialBuilding { get; set; }
        public int Storage { get; set; }
        public int StorageBuilding { get; set; }
        public int Office { get; set; }
        public int OfficeBuilding { get; set; }
    }
}
