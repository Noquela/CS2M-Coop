using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when a player paints a district area. Carries the District prefab (type+name) plus
    ///     the boundary polygon as parallel world-coordinate arrays (Xs/Ys/Zs, ordered, closing point =
    ///     first). The receiver rebuilds the Area+District entity from the prefab's baked archetype.
    ///     District NAME is UI-managed (not ECS) and is not synced yet (v2).
    /// </summary>
    public class DistrictCommand : CommandBase
    {
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public uint OptionMask { get; set; }
        public float[] Xs { get; set; }
        public float[] Ys { get; set; }
        public float[] Zs { get; set; }
    }
}
