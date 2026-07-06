using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Per-route content-hash snapshot of the <c>Game.Routes.VehicleModel</c> buffer — the diff
    ///     signal AND the echo guard, mirroring <see cref="FeeSync"/>/<see cref="RouteSync"/>.Snapshot:
    ///     the apply recomputes and stores the hash it just wrote, so the detector's next scan sees
    ///     "unchanged" and stays quiet. Unlike <see cref="ServiceDistrictSync"/> this fold is SEQUENTIAL
    ///     (not XOR) — each buffer slot pairs a primary+secondary prefab at a specific index, so order
    ///     matters. The hash only needs to be locally stable frame-to-frame (it is never shipped), so it
    ///     is taken straight off the local <c>Entity</c> index/version — no prefab-name resolution needed
    ///     until an actual change is detected and <c>BuildCommand</c> runs.
    /// </summary>
    public static class VehicleModelSync
    {
        public static readonly Dictionary<Entity, ulong> Snapshot = new Dictionary<Entity, ulong>();

        public static void Clear()
        {
            Snapshot.Clear();
        }

        public static bool TryComputeHash(EntityManager em, Entity route, out ulong hash)
        {
            hash = 0;
            if (!em.HasBuffer<VehicleModel>(route))
            {
                return false;
            }

            DynamicBuffer<VehicleModel> buf = em.GetBuffer<VehicleModel>(route, true);
            unchecked
            {
                ulong h = 14695981039346656037UL;
                void Mix(int v)
                {
                    h = (h ^ (uint) v) * 1099511628211UL;
                }

                Mix(buf.Length);
                for (int i = 0; i < buf.Length; i++)
                {
                    Mix(buf[i].m_PrimaryPrefab.Index);
                    Mix(buf[i].m_PrimaryPrefab.Version);
                    Mix(buf[i].m_SecondaryPrefab.Index);
                    Mix(buf[i].m_SecondaryPrefab.Version);
                }

                hash = h;
            }

            return true;
        }
    }
}
