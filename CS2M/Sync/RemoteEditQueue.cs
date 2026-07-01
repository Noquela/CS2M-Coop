using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe hand-off for remote delete/move edits, drained by <see cref="RemoteEditApplySystem"/>.</summary>
    public static class RemoteEditQueue
    {
        private static readonly ConcurrentQueue<DeleteCommand> Deletes = new ConcurrentQueue<DeleteCommand>();
        private static readonly ConcurrentQueue<MoveCommand> Moves = new ConcurrentQueue<MoveCommand>();

        public static void EnqueueDelete(DeleteCommand c) => Deletes.Enqueue(c);
        public static void EnqueueMove(MoveCommand c) => Moves.Enqueue(c);
        public static bool TryDelete(out DeleteCommand c) => Deletes.TryDequeue(out c);
        public static bool TryMove(out MoveCommand c) => Moves.TryDequeue(out c);

        public static void Clear()
        {
            while (Deletes.TryDequeue(out _)) { }
            while (Moves.TryDequeue(out _)) { }
        }
    }
}
