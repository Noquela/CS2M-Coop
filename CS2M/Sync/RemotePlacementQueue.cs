using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>
    ///     Thread-safe hand-off between the network receive thread (command handlers)
    ///     and the main-thread ECS apply system. Handlers only enqueue the raw command;
    ///     all game/ECS/prefab work happens on the main thread in
    ///     <see cref="RemotePlacementApplySystem"/> (PrefabSystem/EntityManager are not
    ///     thread-safe).
    /// </summary>
    public static class RemotePlacementQueue
    {
        private static readonly ConcurrentQueue<ObjectPlaceCommand> Objects =
            new ConcurrentQueue<ObjectPlaceCommand>();

        public static void EnqueueObject(ObjectPlaceCommand command)
        {
            Objects.Enqueue(command);
        }

        public static bool TryDequeueObject(out ObjectPlaceCommand command)
        {
            return Objects.TryDequeue(out command);
        }

        /// <summary>Drop anything pending — called when the multiplayer session ends.</summary>
        public static void Clear()
        {
            while (Objects.TryDequeue(out _))
            {
            }
        }
    }
}
