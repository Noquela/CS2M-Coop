using System.Collections.Generic;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Echo guard for zoning, keyed by Block entity (like <see cref="RemoteNetEcho"/> but for
    ///     blocks). After a remote zone paint is applied, the game itself recomputes cells on the next
    ///     frame (CellCheckSystem/CellOverlapJobs rewrite shared cells between overlapping blocks), so a
    ///     plain snapshot refresh still diffs and re-sends — the ping-pong seen in the first 2-PC
    ///     session. While a block is marked, the detector re-snapshots the REAL post-apply state instead
    ///     of sending; when the TTL expires, genuine local edits diff against absorbed reality again.
    /// </summary>
    public static class ZoneEcho
    {
        private const int TtlFrames = 4; // apply frame + the game's cell recompute on following frames

        private static readonly Dictionary<Entity, int> Ttl = new Dictionary<Entity, int>();

        public static void Mark(Entity block)
        {
            Ttl[block] = TtlFrames;
        }

        public static bool IsMarked(Entity block)
        {
            return Ttl.ContainsKey(block);
        }

        /// <summary>Decrement all TTLs once per frame; call from the detector's OnUpdate.</summary>
        public static void Tick()
        {
            if (Ttl.Count == 0)
            {
                return;
            }

            var keys = new List<Entity>(Ttl.Keys);
            foreach (Entity k in keys)
            {
                int v = Ttl[k] - 1;
                if (v <= 0)
                {
                    Ttl.Remove(k);
                }
                else
                {
                    Ttl[k] = v;
                }
            }
        }

        public static void Clear()
        {
            Ttl.Clear();
        }
    }
}
