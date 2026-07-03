using CS2M.API;
using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;
using Unity.Mathematics;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>
    ///     v50: receives a "look here!" map ping — stores it for the overlay renderer and announces
    ///     it in chat. Relayed by the host (default), so all players see every ping.
    /// </summary>
    public class MapPingHandler : CommandHandler<MapPingCommand>
    {
        public MapPingHandler()
        {
            TransactionCmd = false;
        }

        protected override void Handle(MapPingCommand command)
        {
            string user = string.IsNullOrEmpty(command.Username) ? "Player" : command.Username;
            MapPingSync.Add(command.SenderId, new float3(command.X, command.Y, command.Z), user);
            Chat.Instance?.PrintGameMessage($"📍 {user} pinged the map");
            CS2M.Log.Info($"[Ping] RECEIVED from={user} pos=({command.X:F0},{command.Z:F0})");
        }
    }
}
