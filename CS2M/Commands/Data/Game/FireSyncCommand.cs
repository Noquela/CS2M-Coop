using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     v50: host-authoritative fires. The host's simulation is the only source of truth for
    ///     ignition, extinguishing and fire collapse; clients suppress their own fire simulation
    ///     (FireHazard/FireSimulation/FireRescueDispatch disabled) and just mirror these events.
    ///     Target resolves by SyncId when the burning object is session-synced, else prefab+position.
    /// </summary>
    public class FireSyncCommand : CommandBase
    {
        /// <summary>0 = fire started, 1 = fire ended (extinguished/burned out), 2 = destroyed (collapse).</summary>
        public byte Kind { get; set; }

        public ulong TargetSyncId { get; set; }
        public string PrefabName { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float Intensity { get; set; }
    }
}
