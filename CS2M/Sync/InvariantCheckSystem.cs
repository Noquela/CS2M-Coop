using System.Collections.Generic;
using CS2M.API.Networking;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     v51: structural health watchdog. Field sessions kept producing corruption we only heard
    ///     about as vague symptoms hours later ("roads look weird") — this scans the world every
    ///     ~15 s DURING PLAY and logs a [Invariant] VIOLATION line the moment something breaks:
    ///       1. duplicated edges (same prefab, same endpoints — the "road on top of road")
    ///       2. orphaned owned entities (owner dead/missing — "piece of building left behind")
    ///       3. attached objects whose parent edge is gone (stops floating after road deletion)
    ///     Zero violations logs one quiet [Invariant] OK line (verbose only). Cheap: O(n) dictionary
    ///     pass at 0.07 Hz.
    /// </summary>
    public partial class InvariantCheckSystem : GameSystemBase
    {
        private const int ScanEveryNFrames = 900; // ~15 s

        private EntityQuery _edges;
        private EntityQuery _owned;
        private EntityQuery _attached;
        private PrefabSystem _prefabSystem;
        private int _frame;
        private int _lastDup, _lastOrphan, _lastBadAttach;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _edges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });
            _owned = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Owner>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _attached = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Attached>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            CS2M.Log.Info("[Invariant] InvariantCheckSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_frame < ScanEveryNFrames)
            {
                return;
            }

            _frame = 0;

            try
            {
                Scan();
            }
            catch (System.Exception ex)
            {
                CS2M.Log.Info($"[Guard] invariant scan failed: {ex.Message}");
            }
        }

        private void Scan()
        {
            // 1. Duplicated edges: same prefab + same endpoint pair (0.5 m XZ buckets).
            int dups = 0;
            var buckets = new Dictionary<long, Entity>();
            NativeArray<Entity> edges = _edges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in edges)
                {
                    Curve c = EntityManager.GetComponentData<Curve>(e);
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    long k = Key(prefab.Index, c.m_Bezier.a, c.m_Bezier.d);
                    long k2 = Key(prefab.Index, c.m_Bezier.d, c.m_Bezier.a);
                    long key = math.min(k, k2);
                    if (buckets.TryGetValue(key, out Entity other))
                    {
                        dups++;
                        if (dups <= 5 && _prefabSystem.TryGetPrefab(prefab, out PrefabBase p) && p != null)
                        {
                            CS2M.Log.Info($"[Invariant] VIOLATION dup-edge prefab={p.name} " +
                                          $"a=({c.m_Bezier.a.x:F0},{c.m_Bezier.a.z:F0}) d=({c.m_Bezier.d.x:F0},{c.m_Bezier.d.z:F0}) " +
                                          $"entities={other.Index},{e.Index}");
                        }
                    }
                    else
                    {
                        buckets[key] = e;
                    }
                }
            }
            finally
            {
                edges.Dispose();
            }

            // 2. Orphans: Owner points at a dead/missing entity.
            int orphans = 0;
            NativeArray<Entity> owned = _owned.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in owned)
                {
                    Entity owner = EntityManager.GetComponentData<Owner>(e).m_Owner;
                    if (owner == Entity.Null || !EntityManager.Exists(owner)
                        || EntityManager.HasComponent<Deleted>(owner))
                    {
                        orphans++;
                    }
                }
            }
            finally
            {
                owned.Dispose();
            }

            // 3. Attached to a dead parent.
            int badAttach = 0;
            NativeArray<Entity> attached = _attached.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in attached)
                {
                    Entity parent = EntityManager.GetComponentData<Game.Objects.Attached>(e).m_Parent;
                    if (parent != Entity.Null
                        && (!EntityManager.Exists(parent) || EntityManager.HasComponent<Deleted>(parent)))
                    {
                        badAttach++;
                    }
                }
            }
            finally
            {
                attached.Dispose();
            }

            if (dups > 0 || orphans > 0 || badAttach > 0)
            {
                // Only shout when the numbers MOVE — a save can carry old damage; what matters is growth.
                if (dups != _lastDup || orphans != _lastOrphan || badAttach != _lastBadAttach)
                {
                    CS2M.Log.Info($"[Invariant] VIOLATION summary dupEdges={dups} orphans={orphans} deadAttach={badAttach} " +
                                  $"(was {_lastDup}/{_lastOrphan}/{_lastBadAttach})");
                }
            }
            else
            {
                CS2M.Log.Verbose("[Invariant] OK (no dup edges, no orphans, no dead attach)");
            }

            _lastDup = dups;
            _lastOrphan = orphans;
            _lastBadAttach = badAttach;
        }

        private static long Key(int prefabIndex, float3 a, float3 d)
        {
            int ax = (int) math.round(a.x * 2f);
            int az = (int) math.round(a.z * 2f);
            int dx = (int) math.round(d.x * 2f);
            int dz = (int) math.round(d.z * 2f);
            unchecked
            {
                long h = prefabIndex;
                h = h * 1000003 + ax;
                h = h * 1000003 + az;
                h = h * 1000003 + dx;
                h = h * 1000003 + dz;
                return h;
            }
        }
    }
}
