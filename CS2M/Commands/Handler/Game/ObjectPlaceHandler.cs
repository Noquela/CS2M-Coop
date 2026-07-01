using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>
    ///     Receives a remote object placement and hands it to the main-thread apply
    ///     system via <see cref="RemotePlacementQueue"/>. Does no ECS/prefab work here
    ///     because handlers may run off the main thread.
    /// </summary>
    public class ObjectPlaceHandler : CommandHandler<ObjectPlaceCommand>
    {
        public ObjectPlaceHandler()
        {
            // Apply immediately on receive; not part of a save-transaction batch.
            TransactionCmd = false;
        }

        protected override void Handle(ObjectPlaceCommand command)
        {
            CS2M.Log.Info(
                $"[Place] RECV ObjectPlaceCommand from sender={command.SenderId} " +
                $"type={command.PrefabType} name={command.PrefabName} " +
                $"pos=({command.PosX:F1},{command.PosY:F1},{command.PosZ:F1}) seed={command.RandomSeed}");

            RemotePlacementQueue.EnqueueObject(command);
        }
    }
}
