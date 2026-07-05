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
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            _built = true;
            CS2M.Log.Info($"[Zone] registry built: {IndexToName.Count} zone types");
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
