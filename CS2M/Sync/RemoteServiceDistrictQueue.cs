using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote service-district assignment edits, drained by
    /// <see cref="ServiceDistrictApplySystem"/>.</summary>
    public static class RemoteServiceDistrictQueue
    {
        private static readonly ConcurrentQueue<ServiceDistrictCommand> Q = new ConcurrentQueue<ServiceDistrictCommand>();

        public static void Enqueue(ServiceDistrictCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out ServiceDistrictCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
