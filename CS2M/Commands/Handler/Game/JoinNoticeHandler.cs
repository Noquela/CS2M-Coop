using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    public class JoinNoticeHandler : CommandHandler<JoinNoticeCommand>
    {
        public JoinNoticeHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(JoinNoticeCommand command)
        {
            CS2M.Log.Info($"[Join] RECV {command.Username} joining={command.Joining}");
            RemoteJoinState.Update(command.Username, command.Joining);

            try
            {
                string msg = command.Joining
                    ? $"{command.Username} is joining — game paused…"
                    : $"{command.Username} joined!";
                CS2M.API.Chat.Instance?.PrintGameMessage(msg);
            }
            catch
            {
                // Chat not ready yet; the pause still works.
            }
        }
    }
}
