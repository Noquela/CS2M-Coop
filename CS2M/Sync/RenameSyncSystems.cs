using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>Queue + per-entity snapshot of custom names (echo guard).</summary>
    public static class RenameSync
    {
        private static readonly Queue<RenameCommand> Queue = new Queue<RenameCommand>();
        private static readonly object Lock = new object();

        public static readonly Dictionary<Entity, string> Snapshot = new Dictionary<Entity, string>();
        public static bool BaselineBuilt;

        public static void Enqueue(RenameCommand cmd)
        {
            lock (Lock) { Queue.Enqueue(cmd); }
        }

        public static bool TryDequeue(out RenameCommand cmd)
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
            Snapshot.Clear();
            BaselineBuilt = false;
        }
    }

    /// <summary>
    ///     Detects renamed buildings/districts by diffing entities carrying <c>CustomName</c> against
    ///     a snapshot (~every 2 s; renames are rare). First sight builds a silent baseline.
    /// </summary>
    public partial class RenameDetectorSystem : GameSystemBase
    {
        private const int ScanEveryNFrames = 120;

        private NameSystem _nameSystem;
        private PrefabSystem _prefabSystem;
        private EntityQuery _named;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _nameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _named = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CustomName>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            CS2M.Log.Info("[Rename] RenameDetectorSystem created");
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

            bool baseline = !RenameSync.BaselineBuilt;
            NativeArray<Entity> ents = _named.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_nameSystem.TryGetCustomName(e, out string name))
                    {
                        continue;
                    }

                    if (RenameSync.Snapshot.TryGetValue(e, out string prev) && prev == name)
                    {
                        continue;
                    }

                    RenameSync.Snapshot[e] = name;
                    if (baseline)
                    {
                        continue;
                    }

                    SendRename(e, name);
                }
            }
            finally
            {
                ents.Dispose();
            }

            RenameSync.BaselineBuilt = true;
        }

        private void SendRename(Entity e, string name)
        {
            byte kind;
            ulong syncId = 0;
            string prefabName = null;
            float tx = 0f, tz = 0f;

            if (EntityManager.HasComponent<Game.Areas.District>(e)
                && EntityManager.HasComponent<Game.Areas.Geometry>(e))
            {
                kind = 2;
                var c = EntityManager.GetComponentData<Game.Areas.Geometry>(e).m_CenterPosition;
                tx = c.x;
                tz = c.z;
            }
            else if (EntityManager.HasComponent<Game.Buildings.Building>(e)
                     && EntityManager.HasComponent<Game.Objects.Transform>(e)
                     && EntityManager.HasComponent<PrefabRef>(e))
            {
                kind = 1;
                var p = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                tx = p.x;
                tz = p.z;
                if (EntityManager.HasComponent<CS2M_SyncId>(e))
                {
                    syncId = EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id;
                }

                if (_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                        out PrefabBase pb) && pb != null)
                {
                    prefabName = pb.name;
                }
            }
            else
            {
                return; // streets/lines/citizens: out of scope here
            }

            Command.SendToAll?.Invoke(new RenameCommand
            {
                TargetKind = kind,
                TargetSyncId = syncId,
                TargetPrefabName = prefabName,
                TargetX = tx,
                TargetZ = tz,
                Name = name,
            });
            CS2M.Log.Info($"[Rename] DETECT+SEND kind={kind} name=\"{name}\"");
        }
    }

    /// <summary>Applies a remote rename via the game's own <c>NameSystem.SetCustomName</c>.</summary>
    public partial class RenameApplySystem : GameSystemBase
    {
        private NameSystem _nameSystem;
        private PrefabSystem _prefabSystem;
        private EntityQuery _buildings;
        private EntityQuery _districts;

        protected override void OnCreate()
        {
            base.OnCreate();
            _nameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _buildings = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            _districts = GetEntityQuery(
                ComponentType.ReadOnly<Game.Areas.District>(),
                ComponentType.ReadOnly<Game.Areas.Geometry>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            CS2M.Log.Info("[Rename] RenameApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RenameSync.TryDequeue(out RenameCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] rename apply failed: {ex.Message}"); }
            }
        }

        private void ApplyOne(RenameCommand cmd)
        {
            Entity target = Resolve(cmd);
            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[Rename] SKIP noTarget kind={cmd.TargetKind} at=({cmd.TargetX:F0},{cmd.TargetZ:F0})");
                return;
            }

            RenameSync.Snapshot[target] = cmd.Name; // echo guard before the detector's next scan
            _nameSystem.SetCustomName(target, cmd.Name);
            CS2M.Log.Info($"[Rename] APPLIED kind={cmd.TargetKind} name=\"{cmd.Name}\" entity={target.Index}");
        }

        private Entity Resolve(RenameCommand cmd)
        {
            if (cmd.TargetKind == 1)
            {
                if (cmd.TargetSyncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(cmd.TargetSyncId, out Entity byId)
                    && EntityManager.Exists(byId) && !EntityManager.HasComponent<Deleted>(byId))
                {
                    return byId;
                }

                return Nearest(_buildings, cmd.TargetX, cmd.TargetZ, 9f, useGeometry: false);
            }

            return Nearest(_districts, cmd.TargetX, cmd.TargetZ, 2500f, useGeometry: true);
        }

        private Entity Nearest(EntityQuery query, float x, float z, float maxDistSq, bool useGeometry)
        {
            Entity best = Entity.Null;
            float bestD = maxDistSq;
            NativeArray<Entity> ents = query.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    Unity.Mathematics.float3 p = useGeometry
                        ? EntityManager.GetComponentData<Game.Areas.Geometry>(cand).m_CenterPosition
                        : EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - x;
                    float dz = p.z - z;
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
