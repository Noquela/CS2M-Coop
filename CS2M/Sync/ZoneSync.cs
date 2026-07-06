using System.Collections.Generic;
using Game.Prefabs;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Shared state for zoning sync:
    ///     - a per-PC map between the local <c>ZoneType.m_Index</c> and the cross-PC-stable ZonePrefab
    ///       name (indices differ per machine; names match), so we send names on the wire; and
    ///     - a per-block snapshot of cell zone indices used both to diff for changes AND as the echo
    ///       guard (the apply system updates the snapshot, so the resulting Updated shows no diff).
    /// </summary>
    public static class ZoneSync
    {
        private static readonly Dictionary<ushort, string> IndexToName = new Dictionary<ushort, string>();
        private static readonly Dictionary<string, ushort> NameToIndex = new Dictionary<string, ushort>();
        private static readonly Dictionary<ushort, long> IndexToHash = new Dictionary<ushort, long>();
        private static bool _built;

        public static readonly Dictionary<Entity, ushort[]> Snapshot = new Dictionary<Entity, ushort[]>();

        public static void EnsureBuilt(EntityManager em, PrefabSystem prefabSystem)
        {
            if (_built)
            {
                return;
            }

            EntityQuery q = em.CreateEntityQuery(ComponentType.ReadOnly<ZoneData>());
            NativeArray<Entity> ents = q.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    ZoneData zd = em.GetComponentData<ZoneData>(e);
                    if (prefabSystem.TryGetPrefab(e, out PrefabBase pb) && pb != null)
                    {
                        IndexToName[zd.m_ZoneType.m_Index] = pb.name;
                        NameToIndex[pb.name] = zd.m_ZoneType.m_Index;
                        IndexToHash[zd.m_ZoneType.m_Index] = Fnv(pb.name);
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            _built = true;

            // Fingerprints do registro pra comparar host vs client no log: nameSet igual = mesmo
            // CONJUNTO de zonas; assignment igual = mesma ATRIBUIÇÃO índice→nome (se nameSet bate e
            // assignment não, os índices locais diferem — Cell.m_Zone.m_Index cru NÃO é comparável
            // e o save transferido no join chega com índices do host). Soma comutativa: ordem de
            // iteração do dicionário não afeta.
            long nameSet = 0, assignment = 0;
            foreach (KeyValuePair<string, ushort> kv in NameToIndex)
            {
                nameSet = unchecked(nameSet + Fnv(kv.Key));
                assignment = unchecked(assignment + Fnv(kv.Key + "=" + kv.Value));
            }

            CS2M.Log.Info($"[Zone] registry built: {IndexToName.Count} zone types " +
                          $"nameSet={nameSet:X16} assignment={assignment:X16}");
        }

        /// <summary>FNV-1a 64 bits sobre UTF8 — estável cross-machine/cross-process (string.GetHashCode
        /// não tem essa garantia). Base do radar de zonas e dos fingerprints do registro.</summary>
        public static long Fnv(string s)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s ?? "");
                for (int i = 0; i < bytes.Length; i++)
                {
                    h = (h ^ bytes[i]) * 1099511628211UL;
                }

                return (long) h;
            }
        }

        /// <summary>Hash cross-machine do NOME da zona atrás do índice LOCAL (0 para None/desconhecido).
        /// É isto que o radar folda — nunca o índice cru, que é por-máquina.</summary>
        public static long NameHash(ushort index)
        {
            return IndexToHash.TryGetValue(index, out long h) ? h : 0L;
        }

        /// <summary>Local zone index → ZonePrefab name ("" for None/index 0).</summary>
        public static string Name(ushort index)
        {
            return IndexToName.TryGetValue(index, out string n) ? n : "";
        }

        /// <summary>ZonePrefab name → local zone index (0/None for empty or unknown).</summary>
        public static ushort Index(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return 0;
            }

            return NameToIndex.TryGetValue(name, out ushort i) ? i : (ushort) 0;
        }

        /// <summary>Is this a name the local registry KNOWS? True for "Unzoned" (the None zone, index 0) and
        /// every real zone; false ONLY for a genuinely unresolvable name. Lets the apply distinguish a legit
        /// DEZONE ("Unzoned"/index 0) from an unknown zone it must NOT write (which would wrongly dezone).</summary>
        public static bool IsKnown(string name)
        {
            return string.IsNullOrEmpty(name) || NameToIndex.ContainsKey(name);
        }

        public static void Clear()
        {
            Snapshot.Clear();
        }
    }
}
