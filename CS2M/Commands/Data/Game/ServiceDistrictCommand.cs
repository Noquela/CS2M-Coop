using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player edits which districts serve a building — the Districts info-panel's
    ///     "removeDistrict" trigger (decomp <c>Game.UI.InGame.DistrictsSection.cs:79-99</c>, a direct
    ///     <c>buffer.RemoveAt</c> on <c>Game.Areas.ServiceDistrict</c>) and its companion "add" flow, the
    ///     generic <c>SelectionToolSystem</c> in <c>SelectionType.ServiceDistrict</c> mode (toggled by the
    ///     same section's "toggleSelectionTool"), which writes the clicked district straight into the same
    ///     buffer (decomp <c>Game.Tools.SelectionToolSystem.cs</c> UpdateServiceDistricts/CopyServiceDistricts,
    ///     ~lines 387-416/1043). Both are plain buffer writes with no Created/Updated/event signal.
    ///
    ///     Carries a FULL, idempotent snapshot of the buffer's current contents (not a delta) — a
    ///     duplicate/reordered packet just rewrites to the same end state. The building is addressed by
    ///     <c>CS2M_SyncId</c> when it was placed this session, else by prefab + position (native save
    ///     buildings never get a SyncId — same fallback <c>MoveCommand</c>/<c>ObjectPlaceCommand</c>'s
    ///     Owner fields use). Each served district is addressed by its own prefab + centroid (see
    ///     <c>DistrictResolver</c>) — districts have no SyncId scheme of their own.
    /// </summary>
    public class ServiceDistrictCommand : CommandBase
    {
        public ulong BuildingSyncId { get; set; }
        public string BuildingPrefabName { get; set; }
        public float BuildingX { get; set; }
        public float BuildingY { get; set; }
        public float BuildingZ { get; set; }

        public string[] DistrictPrefabNames { get; set; }
        public float[] DistrictCenterXs { get; set; }
        public float[] DistrictCenterZs { get; set; }
    }
}
