using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Sent by a joining player: <c>Joining=true</c> when it starts loading in (everyone already
    ///     in-game pauses + sees a notice), <c>Joining=false</c> when it finishes (everyone resumes).
    /// </summary>
    public class JoinNoticeCommand : CommandBase
    {
        public string Username { get; set; }
        public bool Joining { get; set; }
    }
}
