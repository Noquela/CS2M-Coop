using System.Collections.Generic;
using Game.Areas;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Per-building content-hash snapshot of the <c>Game.Areas.ServiceDistrict</c> buffer — the diff
    ///     signal AND the echo guard, mirroring <see cref="FeeSync"/>/<see cref="DistrictReshapeSync"/>:
    ///     the apply recomputes and stores the hash it just wrote, so the detector's next ~1 Hz scan sees
    ///     "unchanged" and stays quiet. Districts have no SyncId of their own, so each entry is folded via
    ///     <see cref="DistrictResolver.TryDescribe"/> (prefab + centroid) XOR'd together — the buffer is a
    ///     SET of served districts, not an ordered sequence, so entry order must not affect the hash.
    /// </summary>
    public static class ServiceDistrictSync
    {
        public static readonly Dictionary<Entity, int> Snapshot = new Dictionary<Entity, int>();

        public static void Clear()
        {
            Snapshot.Clear();
        }

        public static bool TryComputeHash(EntityManager em, PrefabSystem prefabSystem, Entity building, out int hash)
        {
            hash = 0;
            if (!em.HasBuffer<ServiceDistrict>(building))
            {
                return false;
            }

            DynamicBuffer<ServiceDistrict> buf = em.GetBuffer<ServiceDistrict>(building, true);
            unchecked
            {
                int h = (int) 2166136261 ^ buf.Length;
                for (int i = 0; i < buf.Length; i++)
                {
                    if (!DistrictResolver.TryDescribe(em, prefabSystem, buf[i].m_District,
                            out string prefabName, out float cx, out float cz))
                    {
                        continue; // unresolvable entry (mid-teardown) — ignored the same way BuildCommand skips it
                    }

                    int eh = prefabName.GetHashCode();
                    eh = (eh * 16777619) ^ (int) math.round(cx * 10f);
                    eh = (eh * 16777619) ^ (int) math.round(cz * 10f);
                    h ^= eh; // XOR: a SET of districts — order must not matter
                }

                hash = h;
            }

            return true;
        }
    }
}
