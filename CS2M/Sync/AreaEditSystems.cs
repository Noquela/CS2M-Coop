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
        private readonly HashSet<Entity> _recentlySent = new HashSet<Entity>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
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
            RequireForUpdate(_appliedAreas);
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
        }
    }

    /// <summary>
    ///     Applies a remote work-area edit: resolves the owner building, rewrites the matching owned
    ///     area's polygon (or creates the area via a Permanent definition when missing). Runs before
    ///     Modification1 so the area triangulation/visual systems consume Updated the same frame.
    /// </summary>
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
            if (cmd.Xs == null || cmd.Zs == null || cmd.Xs.Length < 3)
            {
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
