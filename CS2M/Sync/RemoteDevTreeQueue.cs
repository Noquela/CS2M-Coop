using System.Collections.Generic;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote dev-tree purchases waiting for the main thread.</summary>
    public static class RemoteDevTreeQueue
    {
        private static readonly Queue<DevTreeCommand> Queue = new Queue<DevTreeCommand>();
        private static readonly object Lock = new object();

        public static void Enqueue(DevTreeCommand cmd)
        {
            lock (Lock) { Queue.Enqueue(cmd); }
        }

        public static bool TryDequeue(out DevTreeCommand cmd)
        {
            lock (Lock)
            {
                if (Queue.Count > 0)
                {
                    cmd = Queue.Dequeue();
                    return true;
                }

                cmd = null;
                return false;
            }
        }

        public static void Clear()
        {
            lock (Lock) { Queue.Clear(); }
        }
    }

    /// <summary>
    ///     Shared unlocked-node snapshot: the detector diffs against it; the apply refreshes it so a
    ///     remotely-applied purchase isn't echoed back.
    /// </summary>
    public static class DevTreeSync
    {
        public static readonly HashSet<string> Unlocked = new HashSet<string>();
        public static bool BaselineBuilt;

        public static void Clear()
        {
            Unlocked.Clear();
            BaselineBuilt = false;
        }
    }
}
