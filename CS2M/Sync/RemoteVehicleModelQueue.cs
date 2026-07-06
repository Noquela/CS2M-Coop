using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue of remote vehicle-model selection edits, drained by
    /// <see cref="VehicleModelApplySystem"/>.</summary>
    public static class RemoteVehicleModelQueue
    {
        private static readonly ConcurrentQueue<VehicleModelCommand> Q = new ConcurrentQueue<VehicleModelCommand>();

        public static void Enqueue(VehicleModelCommand c) => Q.Enqueue(c);

        public static bool TryDequeue(out VehicleModelCommand c) => Q.TryDequeue(out c);

        public static void Clear()
        {
            while (Q.TryDequeue(out _))
            {
            }
        }
    }
}
