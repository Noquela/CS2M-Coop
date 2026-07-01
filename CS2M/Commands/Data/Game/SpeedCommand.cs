using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Host-authoritative simulation speed (0 = paused, 1/2/3 = speeds). Broadcast ~1 Hz and on
    ///     change so every PC advances the same number of ticks — closing the biggest avoidable source of
    ///     drift between the two independent simulations. (Same host-authoritative pattern as money/XP.)
    /// </summary>
    public class SpeedCommand : CommandBase
    {
        public float Speed { get; set; }
    }
}
