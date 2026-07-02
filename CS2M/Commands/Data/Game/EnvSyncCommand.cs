using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Host-authoritative environment scalars (~0.5 Hz): weather overrides + the shared clock.
    ///     Weather: the client overrides its ClimateSystem sample values so rain/snow/clouds look the
    ///     same on both PCs. Clock: ElapsedTimeFrames = frameIndex - TimeData.m_FirstFrame on the host;
    ///     the client realigns its TimeData so the in-game date/time and sun position match.
    /// </summary>
    public class EnvSyncCommand : CommandBase
    {
        public float Temperature { get; set; }
        public float Precipitation { get; set; }
        public float Cloudiness { get; set; }
        public uint ElapsedTimeFrames { get; set; }
    }
}
