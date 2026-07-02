using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     A transport line's color changed (ColorSection UI). Resolution: SyncId first, then
    ///     prefab name + RouteNumber (save-loaded lines carry identical numbers on every PC).
    /// </summary>
    public class RouteColorCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public string PrefabName { get; set; }
        public int Number { get; set; }

        public byte ColorR { get; set; }
        public byte ColorG { get; set; }
        public byte ColorB { get; set; }
        public byte ColorA { get; set; }
    }
}
