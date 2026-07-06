using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects a freshly-painted district (an <c>Area</c>+<c>District</c> that just gained
    ///     <c>Applied</c>) and broadcasts its boundary polygon (the <c>Node</c> buffer) + prefab + option
    ///     mask. Echo guard: districts recreated from a remote command carry <c>CS2M_RemotePlaced</c>,
    ///     which this query excludes.
    /// </summary>
    /// <summary>v55: per-district (polygon hash + centroid) snapshot for RESHAPE detection. The centroid
    /// is the resolve key both PCs share from the last synced state, so a reshape can be addressed without
    /// a SyncId. The apply refreshes it (echo guard) so a remotely-applied rewrite isn't bounced back.</summary>
    public static class DistrictReshapeSync
    {
        public struct Snap
        {
            public int Hash;
            public float Cx;
            public float Cz;
        }

        public static readonly Dictionary<Entity, Snap> Snapshot = new Dictionary<Entity, Snap>();

        public static void Clear()
        {
            Snapshot.Clear();
        }
    }

    /// <summary>v56: districts have no SyncId scheme of their own (areas are polygon-boundary entities
    /// recreated per-PC), so every consumer that needs to name one on the wire shares this pair:
    /// <see cref="TryDescribe"/> turns a LOCAL district entity into (prefabName, centroid) to ship, and
    /// <see cref="FindByCenter"/> takes that back to the nearest LOCAL entity with a matching prefab —
    /// the same 40 m² tolerance <c>DistrictApplySystem</c>'s reshape resolution already validated
    /// on-screen. Extracted so <c>ServiceDistrictDetectorSystem</c>/<c>ServiceDistrictApplySystem</c>
    /// (translating a building's served-district list) don't duplicate the centroid-resolve logic.</summary>
    public static class DistrictResolver
    {
        public static bool TryDescribe(EntityManager em, PrefabSystem prefabSystem, Entity district,
            out string prefabName, out float centerX, out float centerZ)
        {
            prefabName = null;
            centerX = 0f;
            centerZ = 0f;

            if (district == Entity.Null || !em.Exists(district) || !em.HasBuffer<Node>(district)
                || !em.HasComponent<PrefabRef>(district))
            {
                return false;
            }

            DynamicBuffer<Node> nodes = em.GetBuffer<Node>(district, true);
            if (nodes.Length == 0)
            {
                return false;
            }

            if (!prefabSystem.TryGetPrefab(em.GetComponentData<PrefabRef>(district).m_Prefab,
                    out PrefabBase prefab) || prefab == null)
            {
                return false;
            }

            float cx = 0f, cz = 0f;
            for (int i = 0; i < nodes.Length; i++)
            {
                cx += nodes[i].m_Position.x;
                cz += nodes[i].m_Position.z;
            }

            prefabName = prefab.name;
            centerX = cx / nodes.Length;
            centerZ = cz / nodes.Length;
            return true;
        }

        /// <summary>Nearest district (same prefab) whose centroid is within ~40 m of (x,z) — the
        /// centroid both PCs share from the last synced state. <paramref name="districts"/> should
        /// already exclude Temp/Deleted (caller-owned query, mirrors <c>RouteResolver.Resolve</c>).</summary>
        public static Entity FindByCenter(EntityManager em, EntityQuery districts, PrefabSystem prefabSystem,
            float x, float z, string prefabName)
        {
            Entity best = Entity.Null;
            float bestD = 1600f; // 40 m²
            NativeArray<Entity> ents = districts.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!string.IsNullOrEmpty(prefabName))
                    {
                        if (!prefabSystem.TryGetPrefab(em.GetComponentData<PrefabRef>(e).m_Prefab,
                                out PrefabBase pb) || pb == null || pb.name != prefabName)
                        {
                            continue;
                        }
                    }

                    if (!em.HasBuffer<Node>(e))
                    {
                        continue;
                    }

                    DynamicBuffer<Node> nb = em.GetBuffer<Node>(e, true);
                    if (nb.Length == 0)
                    {
                        continue;
                    }

                    float cx = 0f, cz = 0f;
                    for (int i = 0; i < nb.Length; i++)
                    {
                        cx += nb[i].m_Position.x;
                        cz += nb[i].m_Position.z;
                    }

                    cx /= nb.Length;
                    cz /= nb.Length;

                    float dx = cx - x, dz = cz - z;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = e;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return best;
        }
    }

    public partial class DistrictDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _appliedDistricts;
        private EntityQuery _allDistricts;
        private readonly HashSet<Entity> _sent = new HashSet<Entity>();
        private int _clearCounter;
        private int _reshapeFrame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _appliedDistricts = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<District>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });
            // Reshape scan runs over ALL live districts (a reshape marks the area Updated, never Applied,
            // and it must work on remote-created districts too — the polygon hash is the echo guard here,
            // not CS2M_RemotePlaced). No RequireForUpdate so the ~1 Hz scanner always runs.
            _allDistricts = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<District>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            CS2M.Log.Info("[District] DistrictDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_clearCounter >= 120)
            {
                _clearCounter = 0;
                _sent.Clear();
            }

            NativeArray<Entity> ents = _appliedDistricts.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_sent.Add(e))
                    {
                        continue;
                    }

                    if (!EntityManager.HasBuffer<Node>(e) || !EntityManager.HasComponent<PrefabRef>(e))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(e, true);
                    int n = nodes.Length;
                    if (n < 3)
                    {
                        continue;
                    }

                    var xs = new float[n];
                    var ys = new float[n];
                    var zs = new float[n];
                    float cx = 0f, cz = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        xs[i] = nodes[i].m_Position.x;
                        ys[i] = nodes[i].m_Position.y;
                        zs[i] = nodes[i].m_Position.z;
                        cx += xs[i];
                        cz += zs[i];
                    }

                    cx /= n;
                    cz /= n;

                    uint mask = EntityManager.HasComponent<District>(e)
                        ? EntityManager.GetComponentData<District>(e).m_OptionMask : 0u;

                    Command.SendToAll?.Invoke(new DistrictCommand
                    {
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        OptionMask = mask,
                        Xs = xs, Ys = ys, Zs = zs,
                        CenterX = cx, CenterZ = cz,
                    });
                    // Baseline the reshape snapshot for our freshly-painted district so the scanner adopts
                    // it silently (both PCs now hold the same polygon + centroid).
                    DistrictReshapeSync.Snapshot[e] = new DistrictReshapeSync.Snap
                    {
                        Hash = WorkAreaHash.Compute(nodes), Cx = cx, Cz = cz,
                    };
                    CS2M.Log.Info($"[District] DETECT+SEND name={prefab.name} points={n}");
                }
            }
            finally
            {
                ents.Dispose();
            }

            if (++_reshapeFrame >= 60)
            {
                _reshapeFrame = 0;
                ScanDistrictReshapes();
            }
        }

        /// <summary>~1 Hz polygon-hash diff over every live district — the only signal for a RESHAPE
        /// (vanilla marks the area Updated, never Applied). First sight is a silent baseline; the apply
        /// refreshes the snapshot so a remotely-applied rewrite is never bounced back.</summary>
        private void ScanDistrictReshapes()
        {
            NativeArray<Entity> ents = _allDistricts.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(e, true);
                    int n = nodes.Length;
                    if (n < 3)
                    {
                        continue;
                    }

                    int hash = WorkAreaHash.Compute(nodes);
                    float cx = 0f, cz = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        cx += nodes[i].m_Position.x;
                        cz += nodes[i].m_Position.z;
                    }

                    cx /= n;
                    cz /= n;

                    if (!DistrictReshapeSync.Snapshot.TryGetValue(e, out DistrictReshapeSync.Snap prev))
                    {
                        DistrictReshapeSync.Snapshot[e] = new DistrictReshapeSync.Snap { Hash = hash, Cx = cx, Cz = cz };
                        continue; // baseline (save load / just painted / just applied)
                    }

                    if (prev.Hash == hash)
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    var xs = new float[n];
                    var ys = new float[n];
                    var zs = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        xs[i] = nodes[i].m_Position.x;
                        ys[i] = nodes[i].m_Position.y;
                        zs[i] = nodes[i].m_Position.z;
                    }

                    uint mask = EntityManager.GetComponentData<District>(e).m_OptionMask;

                    // Address by the PREVIOUS centroid — the spot the remotes currently have this district
                    // (their copy hasn't been reshaped yet). After they rewrite to the same polygon, their
                    // centroid matches ours again, so the next reshape's old-centre still resolves.
                    Command.SendToAll?.Invoke(new DistrictCommand
                    {
                        Replace = true,
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        OptionMask = mask,
                        Xs = xs, Ys = ys, Zs = zs,
                        CenterX = prev.Cx, CenterZ = prev.Cz,
                    });
                    DistrictReshapeSync.Snapshot[e] = new DistrictReshapeSync.Snap { Hash = hash, Cx = cx, Cz = cz };
                    CS2M.Log.Info($"[District] DETECT+SEND reshape name={prefab.name} points={n} oldC=({prev.Cx:F0},{prev.Cz:F0})");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }
    }
}
