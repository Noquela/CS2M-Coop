using System.Collections.Concurrent;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>Thread-safe hand-off for remote zoning changes, drained by <see cref="ZonePaintApplySystem"/>.</summary>
    public static class RemoteZoneQueue
    {
        private static readonly ConcurrentQueue<ZonePaintCommand> Zones = new ConcurrentQueue<ZonePaintCommand>();

        public static void Enqueue(ZonePaintCommand c) => Zones.Enqueue(c);
        public static bool TryDequeue(out ZonePaintCommand c) => Zones.TryDequeue(out c);

        public static void Clear()
        {
            while (Zones.TryDequeue(out _)) { }
        }
    }
}
