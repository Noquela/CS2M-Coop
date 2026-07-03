using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue for remote work-area edits.</summary>
    public static class RemoteAreaQueue
    {
        private static readonly Queue<AreaEditCommand> Queue = new Queue<AreaEditCommand>();
        private static readonly object Lock = new object();

        public static void Enqueue(AreaEditCommand cmd)
        {
            lock (Lock) { Queue.Enqueue(cmd); }
        }

        public static bool TryDequeue(out AreaEditCommand cmd)
        {
            lock (Lock)
            {
                if (Queue.Count > 0)
                {
                    cmd = Queue.Dequeue();
                    return true;
                }

                cmd = null;
                return false;
            }
        }

        public static void Clear()
        {
            lock (Lock) { Queue.Clear(); }
        }
    }

    /// <summary>
    ///     Detects edits to building-owned areas (repainting a farm field etc.): an Applied Area with
    ///     an Owner that is a building. Districts have their own sync; remote-written areas carry
    ///     <c>CS2M_RemotePlaced</c> (echo guard).
    /// </summary>
    public partial class AreaEditDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _appliedAreas;
        private EntityQuery _appliedStandalone;
        private EntityQuery _deletedAreas;
        private readonly HashSet<Entity> _recentlySent = new HashSet<Entity>();
        private int _clearCounter;
        private int _scanCounter;
        private EntityQuery _workAreas;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // v51 FIELD FIX: editing an existing work area only marks it Updated — never Applied —
            // so the Applied-based query below NEVER saw real edits ("my farm field doesn't show up
            // until /resync"). Poll owned areas at ~1 Hz and diff their polygon hash instead.
            _workAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.District>(),
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                },
            });
            _appliedAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.District>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            // v46: standalone areas — surfaces/pavement painted with the area tool, no owner.
            _appliedStandalone = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Game.Areas.District>(), // districts have their own sync
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            // v46: bulldozed areas (surfaces, work areas AND districts) sync by prefab + center.
            _deletedAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            RequireAnyForUpdate(_appliedAreas, _appliedStandalone, _deletedAreas);
            CS2M.Log.Info("[Area] AreaEditDetectorSystem created");
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
                _recentlySent.Clear();
            }

            NativeArray<Entity> areas = _appliedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_recentlySent.Add(area))
                    {
                        continue;
                    }

                    Entity owner = EntityManager.GetComponentData<Owner>(area).m_Owner;
                    if (!EntityManager.HasComponent<Game.Buildings.Building>(owner)
                        || !EntityManager.HasComponent<Game.Objects.Transform>(owner)
                        || !EntityManager.HasComponent<PrefabRef>(owner))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase prefab) || prefab == null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab,
                            out PrefabBase ownerPrefab) || ownerPrefab == null)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    var xs = new float[nodes.Length];
                    var ys = new float[nodes.Length];
                    var zs = new float[nodes.Length];
                    var els = new float[nodes.Length];
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        xs[i] = nodes[i].m_Position.x;
                        ys[i] = nodes[i].m_Position.y;
                        zs[i] = nodes[i].m_Position.z;
                        els[i] = nodes[i].m_Elevation;
                    }

                    var ownerTf = EntityManager.GetComponentData<Game.Objects.Transform>(owner);
                    Command.SendToAll?.Invoke(new AreaEditCommand
                    {
                        OwnerSyncId = EntityManager.HasComponent<CS2M_SyncId>(owner)
                            ? EntityManager.GetComponentData<CS2M_SyncId>(owner).m_Id
                            : 0,
                        OwnerPrefabName = ownerPrefab.name,
                        OwnerX = ownerTf.m_Position.x,
                        OwnerY = ownerTf.m_Position.y,
                        OwnerZ = ownerTf.m_Position.z,
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        Xs = xs, Ys = ys, Zs = zs, Els = els,
                    });
                    CS2M.Log.Info($"[Area] DETECT+SEND name={prefab.name} owner={ownerPrefab.name} nodes={nodes.Length}");
                }
            }
            finally
            {
                areas.Dispose();
            }

            DetectStandalone();
            DetectDeleted();

            if (++_scanCounter >= 60)
            {
                _scanCounter = 0;
                ScanWorkAreaEdits();
            }
        }

        /// <summary>v51: ~1 Hz polygon-hash diff over owned areas — the only reliable signal for a
        /// player RESHAPING a work area (vanilla marks the entity Updated, never Applied). First
        /// sight is a silent baseline; the apply system updates the shared hash so a remotely
        /// applied rewrite is never bounced back.</summary>
        private void ScanWorkAreaEdits()
        {
            NativeArray<Entity> areas = _workAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    if (nodes.Length == 0)
                    {
                        continue;
                    }

                    int hash = WorkAreaHash.Compute(nodes);
                    if (!WorkAreaHash.TryGet(area, out int known))
                    {
                        WorkAreaHash.Set(area, hash); // baseline (building placement, world load…)
                        continue;
                    }

                    if (known == hash)
                    {
                        continue;
                    }

                    WorkAreaHash.Set(area, hash);

                    Entity owner = EntityManager.GetComponentData<Owner>(area).m_Owner;
                    if (!EntityManager.HasComponent<Game.Buildings.Building>(owner)
                        || !EntityManager.HasComponent<Game.Objects.Transform>(owner)
                        || !EntityManager.HasComponent<PrefabRef>(owner))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase prefab) || prefab == null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab,
                            out PrefabBase ownerPrefab) || ownerPrefab == null)
                    {
                        continue;
                    }

                    var xs = new float[nodes.Length];
                    var ys = new float[nodes.Length];
                    var zs = new float[nodes.Length];
                    var els = new float[nodes.Length];
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        xs[i] = nodes[i].m_Position.x;
                        ys[i] = nodes[i].m_Position.y;
                        zs[i] = nodes[i].m_Position.z;
                        els[i] = nodes[i].m_Elevation;
                    }

                    var ownerTf = EntityManager.GetComponentData<Game.Objects.Transform>(owner);
                    Command.SendToAll?.Invoke(new AreaEditCommand
                    {
                        OwnerSyncId = EntityManager.HasComponent<CS2M_SyncId>(owner)
                            ? EntityManager.GetComponentData<CS2M_SyncId>(owner).m_Id
                            : 0,
                        OwnerPrefabName = ownerPrefab.name,
                        OwnerX = ownerTf.m_Position.x,
                        OwnerY = ownerTf.m_Position.y,
                        OwnerZ = ownerTf.m_Position.z,
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        Xs = xs, Ys = ys, Zs = zs, Els = els,
                    });
                    CS2M.Log.Info($"[Area] DETECT+SEND edit name={prefab.name} owner={ownerPrefab.name} nodes={nodes.Length} (polygon diff)");
                }
            }
            finally
            {
                areas.Dispose();
            }
        }

        private void DetectStandalone()
        {
            if (_appliedStandalone.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> areas = _appliedStandalone.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_recentlySent.Add(area))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    var xs = new float[nodes.Length];
                    var ys = new float[nodes.Length];
                    var zs = new float[nodes.Length];
                    var els = new float[nodes.Length];
                    float cx = 0f, cz = 0f;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        xs[i] = nodes[i].m_Position.x;
                        ys[i] = nodes[i].m_Position.y;
                        zs[i] = nodes[i].m_Position.z;
                        els[i] = nodes[i].m_Elevation;
                        cx += xs[i];
                        cz += zs[i];
                    }

                    Command.SendToAll?.Invoke(new AreaEditCommand
                    {
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        Xs = xs, Ys = ys, Zs = zs, Els = els,
                        CenterX = cx / nodes.Length,
                        CenterZ = cz / nodes.Length,
                    });
                    CS2M.Log.Info($"[Area] DETECT+SEND standalone name={prefab.name} nodes={nodes.Length}");
                }
            }
            finally
            {
                areas.Dispose();
            }
        }

        private void DetectDeleted()
        {
            if (_deletedAreas.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> areas = _deletedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_recentlySent.Add(area))
                    {
                        continue;
                    }

                    // v50.2 FIELD FIX: an owned area whose owner is dying is CASCADE, not a player
                    // action — the building delete already syncs and cascades the same sub-areas on
                    // every PC. Re-sending them (734 in one session, from the host's sim demolishing
                    // abandoned buildings) deleted walking areas and FARM FIELDS under living
                    // buildings on the other PCs. An owned area deleted while its owner LIVES is a
                    // real edit (clearing a work area) and still syncs.
                    if (EntityManager.HasComponent<Owner>(area))
                    {
                        Entity areaOwner = EntityManager.GetComponentData<Owner>(area).m_Owner;
                        if (!EntityManager.Exists(areaOwner)
                            || EntityManager.HasComponent<Deleted>(areaOwner))
                        {
                            continue;
                        }
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    if (nodes.Length == 0)
                    {
                        continue;
                    }

                    float cx = 0f, cz = 0f;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        cx += nodes[i].m_Position.x;
                        cz += nodes[i].m_Position.z;
                    }

                    Command.SendToAll?.Invoke(new AreaEditCommand
                    {
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        Delete = true,
                        CenterX = cx / nodes.Length,
                        CenterZ = cz / nodes.Length,
                    });
                    CS2M.Log.Info($"[Area] DETECT+SEND delete name={prefab.name} center=({cx / nodes.Length:F0},{cz / nodes.Length:F0})");
                }
            }
            finally
            {
                areas.Dispose();
            }
        }
    }

    /// <summary>
    ///     Applies a remote work-area edit: resolves the owner building, rewrites the matching owned
    ///     area's polygon (or creates the area via a Permanent definition when missing). Runs before
    ///     Modification1 so the area triangulation/visual systems consume Updated the same frame.
    /// </summary>
    /// <summary>Shared polygon-hash snapshot per owned area — the edit-diff scanner's memory,
    /// updated by the apply system so remote rewrites never echo back.</summary>
    public static class WorkAreaHash
    {
        private static readonly System.Collections.Generic.Dictionary<Entity, int> Hashes =
            new System.Collections.Generic.Dictionary<Entity, int>();
        private static readonly object Lock = new object();

        public static int Compute(DynamicBuffer<Game.Areas.Node> nodes)
        {
            unchecked
            {
                int h = (int) 2166136261 ^ nodes.Length;
                for (int i = 0; i < nodes.Length; i++)
                {
                    h = (h * 16777619) ^ (int) math.round(nodes[i].m_Position.x * 10f);
                    h = (h * 16777619) ^ (int) math.round(nodes[i].m_Position.z * 10f);
                }

                return h;
            }
        }

        public static void Set(Entity e, int hash)
        {
            lock (Lock) { Hashes[e] = hash; }
        }

        public static bool TryGet(Entity e, out int hash)
        {
            lock (Lock) { return Hashes.TryGetValue(e, out hash); }
        }

        public static void Clear()
        {
            lock (Lock) { Hashes.Clear(); }
        }
    }

    public partial class AreaEditApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _ownedAreas;
        private EntityQuery _buildings;
        private readonly List<Entity> _pendingDefinitions = new List<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _ownedAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.District>(),
                },
            });
            _buildings = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[Area] AreaEditApplySystem created");
        }

        protected override void OnUpdate()
        {
            for (int i = 0; i < _pendingDefinitions.Count; i++)
            {
                if (EntityManager.Exists(_pendingDefinitions[i]))
                {
                    EntityManager.DestroyEntity(_pendingDefinitions[i]);
                }
            }

            _pendingDefinitions.Clear();

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteAreaQueue.TryDequeue(out AreaEditCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area apply failed: {ex.Message}"); }
            }
        }

        private void ApplyOne(AreaEditCommand cmd)
        {
            if (cmd.Delete)
            {
                ApplyDelete(cmd);
                return;
            }

            if (cmd.Xs == null || cmd.Zs == null || cmd.Xs.Length < 3)
            {
                return;
            }

            // v46: standalone area (surface/pavement — no owner shipped).
            if (cmd.OwnerSyncId == 0 && string.IsNullOrEmpty(cmd.OwnerPrefabName))
            {
                ApplyStandalone(cmd);
                return;
            }

            Entity owner = ResolveOwner(cmd);
            if (owner == Entity.Null)
            {
                CS2M.Log.Info($"[Area] SKIP noOwner owner={cmd.OwnerPrefabName} at=({cmd.OwnerX:F0},{cmd.OwnerZ:F0})");
                return;
            }

            // Existing owned area of the same prefab → rewrite its polygon in place.
            Entity target = Entity.Null;
            NativeArray<Entity> areas = _ownedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (EntityManager.GetComponentData<Owner>(area).m_Owner != owner)
                    {
                        continue;
                    }

                    if (_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase p) && p != null && p.name == cmd.PrefabName)
                    {
                        target = area;
                        break;
                    }
                }
            }
            finally
            {
                areas.Dispose();
            }

            if (target != Entity.Null)
            {
                if (!EntityManager.HasComponent<CS2M_RemotePlaced>(target))
                {
                    EntityManager.AddComponent<CS2M_RemotePlaced>(target); // echo guard
                }

                if (!EntityManager.HasComponent<Updated>(target))
                {
                    EntityManager.AddComponent<Updated>(target);
                }

                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(target);
                nodes.ResizeUninitialized(cmd.Xs.Length);
                for (int i = 0; i < cmd.Xs.Length; i++)
                {
                    float el = cmd.Els != null && i < cmd.Els.Length ? cmd.Els[i] : float.MinValue;
                    nodes[i] = new Game.Areas.Node(new float3(cmd.Xs[i], cmd.Ys[i], cmd.Zs[i]), el);
                }

                // v51: update the shared polygon hash so the edit-diff scanner treats this remotely
                // applied shape as already-known (no bounce-back).
                WorkAreaHash.Set(target, WorkAreaHash.Compute(nodes));

                CS2M.Log.Info($"[Area] APPLIED rewrite name={cmd.PrefabName} nodes={cmd.Xs.Length} entity={target.Index}");
                return;
            }

            // No such area yet → create it via the vanilla Permanent-definition path.
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase areaPrefab) || areaPrefab == null
                || !_prefabSystem.TryGetEntity(areaPrefab, out Entity areaPrefabEntity))
            {
                CS2M.Log.Info($"[Area] RESOLVE-FAIL name={cmd.PrefabName}");
                return;
            }

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = areaPrefabEntity,
                m_Owner = owner,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponent<Updated>(def);
            DynamicBuffer<Game.Areas.Node> defNodes = EntityManager.AddBuffer<Game.Areas.Node>(def);
            defNodes.ResizeUninitialized(cmd.Xs.Length);
            for (int i = 0; i < cmd.Xs.Length; i++)
            {
                float el = cmd.Els != null && i < cmd.Els.Length ? cmd.Els[i] : float.MinValue;
                defNodes[i] = new Game.Areas.Node(new float3(cmd.Xs[i], cmd.Ys[i], cmd.Zs[i]), el);
            }

            _pendingDefinitions.Add(def);
            CS2M.Log.Info($"[Area] APPLIED-DEF create name={cmd.PrefabName} nodes={cmd.Xs.Length}");
        }

        /// <summary>Finds any non-tile area of the same prefab whose polygon center is nearest the
        /// shipped center (districts included — a bulldozed district syncs through here too).</summary>
        private Entity FindAreaByCenter(string prefabName, float cx, float cz, float maxDistSq)
        {
            EntityQuery all = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                },
            });

            Entity best = Entity.Null;
            float bestD = maxDistSq;
            NativeArray<Entity> areas = all.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase p) || p == null || p.name != prefabName)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    if (nodes.Length == 0)
                    {
                        continue;
                    }

                    float ax = 0f, az = 0f;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        ax += nodes[i].m_Position.x;
                        az += nodes[i].m_Position.z;
                    }

                    ax /= nodes.Length;
                    az /= nodes.Length;
                    float dx = ax - cx;
                    float dz = az - cz;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = area;
                    }
                }
            }
            finally
            {
                areas.Dispose();
            }

            return best;
        }

        private void ApplyDelete(AreaEditCommand cmd)
        {
            Entity target = FindAreaByCenter(cmd.PrefabName, cmd.CenterX, cmd.CenterZ, 100f);
            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[Area] SKIP delete noMatch name={cmd.PrefabName} at=({cmd.CenterX:F0},{cmd.CenterZ:F0})");
                return;
            }

            if (!EntityManager.HasComponent<CS2M_RemotePlaced>(target))
            {
                EntityManager.AddComponent<CS2M_RemotePlaced>(target); // echo guard
            }

            EntityManager.AddComponent<Deleted>(target);
            CS2M.Log.Info($"[Area] APPLIED delete name={cmd.PrefabName} entity={target.Index}");
        }

        private void ApplyStandalone(AreaEditCommand cmd)
        {
            Entity target = FindAreaByCenter(cmd.PrefabName, cmd.CenterX, cmd.CenterZ, 25f);
            if (target != Entity.Null)
            {
                if (!EntityManager.HasComponent<CS2M_RemotePlaced>(target))
                {
                    EntityManager.AddComponent<CS2M_RemotePlaced>(target);
                }

                if (!EntityManager.HasComponent<Updated>(target))
                {
                    EntityManager.AddComponent<Updated>(target);
                }

                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(target);
                nodes.ResizeUninitialized(cmd.Xs.Length);
                for (int i = 0; i < cmd.Xs.Length; i++)
                {
                    float el = cmd.Els != null && i < cmd.Els.Length ? cmd.Els[i] : float.MinValue;
                    nodes[i] = new Game.Areas.Node(new float3(cmd.Xs[i], cmd.Ys[i], cmd.Zs[i]), el);
                }

                CS2M.Log.Info($"[Area] APPLIED standalone rewrite name={cmd.PrefabName} nodes={cmd.Xs.Length}");
                return;
            }

            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase areaPrefab) || areaPrefab == null
                || !_prefabSystem.TryGetEntity(areaPrefab, out Entity areaPrefabEntity))
            {
                CS2M.Log.Info($"[Area] RESOLVE-FAIL standalone name={cmd.PrefabName}");
                return;
            }

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = areaPrefabEntity,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponent<Updated>(def);
            DynamicBuffer<Game.Areas.Node> defNodes = EntityManager.AddBuffer<Game.Areas.Node>(def);
            defNodes.ResizeUninitialized(cmd.Xs.Length);
            for (int i = 0; i < cmd.Xs.Length; i++)
            {
                float el = cmd.Els != null && i < cmd.Els.Length ? cmd.Els[i] : float.MinValue;
                defNodes[i] = new Game.Areas.Node(new float3(cmd.Xs[i], cmd.Ys[i], cmd.Zs[i]), el);
            }

            _pendingDefinitions.Add(def);
            CS2M.Log.Info($"[Area] APPLIED-DEF standalone create name={cmd.PrefabName} nodes={cmd.Xs.Length}");
        }

        private Entity ResolveOwner(AreaEditCommand cmd)
        {
            if (cmd.OwnerSyncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(cmd.OwnerSyncId, out Entity byId)
                && EntityManager.Exists(byId) && !EntityManager.HasComponent<Deleted>(byId))
            {
                return byId;
            }

            Entity best = Entity.Null;
            float bestD = 9f;
            NativeArray<Entity> ents = _buildings.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    var p = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - cmd.OwnerX;
                    float dz = p.z - cmd.OwnerZ;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = cand;
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
}
