using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Host broadcasts the city's authoritative cash so clients snap to it (cancels the small
    ///     per-PC economy drift). Sent ~once/second and on change.
    /// </summary>
    public class MoneySyncCommand : CommandBase
    {
        public int Cash { get; set; }
    }
}
