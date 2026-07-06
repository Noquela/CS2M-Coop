using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player edits a transport line/work-route's allowed vehicle models — the
    ///     Select Vehicles panel's "selectVehicles"/"deselectVehicles" triggers (decomp
    ///     <c>Game.UI.InGame.SelectVehiclesSection.cs:218-304</c>), both writing
    ///     <c>Game.Routes.VehicleModel</c> directly with no Created/Updated/event signal.
    ///
    ///     Carries a FULL, idempotent snapshot of the buffer as parallel arrays (index i = one
    ///     <c>VehicleModel</c> slot's primary+secondary prefab; an empty name marks an <c>Entity.Null</c>
    ///     slot) — a duplicate/reordered packet just rewrites to the same end state, in the same ORDER the
    ///     sender had (unlike district assignment this buffer is index-paired, so order is preserved, not
    ///     re-sorted). The line/route itself is addressed exactly like <see cref="RouteColorCommand"/>/
    ///     <see cref="DeleteCommand"/> (SyncId, else prefab + RouteNumber) via the existing
    ///     <c>RouteResolver</c>.
    /// </summary>
    public class VehicleModelCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public string PrefabName { get; set; }
        public int Number { get; set; }

        public string[] PrimaryTypes { get; set; }
        public string[] PrimaryNames { get; set; }
        public string[] SecondaryTypes { get; set; }
        public string[] SecondaryNames { get; set; }
    }
}
