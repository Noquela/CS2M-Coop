using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Applies remote zoning changes. Finds the local Block matching the sent world
    ///     position/size (blocks are deterministic from synced roads), rewrites the named cells to the
    ///     locally-resolved zone index, marks the block <c>Updated</c> to re-render/re-simulate, and
    ///     refreshes the shared snapshot so the change isn't echoed back.
    /// </summary>
    public partial class ZonePaintApplySystem : GameSystemBase
    {
        private const float MatchEpsilonSq = 4f; // 2 m

        private PrefabSystem _prefabSystem;
        private EntityQuery _allBlocks;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _allBlocks = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Block>(),
                    ComponentType.ReadOnly<Cell>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[Zone] ZonePaintApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);

            while (RemoteZoneQueue.TryDequeue(out ZonePaintCommand cmd))
            {
                ApplyOne(cmd);
            }
        }

        private void ApplyOne(ZonePaintCommand cmd)
        {
            if (cmd.CellIndices == null || cmd.ZoneNames == null)
            {
                return;
            }

            Entity target = FindBlock(cmd);
            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[Zone] SKIP noBlock at=({cmd.BlockX:F0},{cmd.BlockZ:F0}) (road not synced yet?)");
                return;
            }

            DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(target);
            int applied = 0;
            int count = System.Math.Min(cmd.CellIndices.Length, cmd.ZoneNames.Length);
            for (int k = 0; k < count; k++)
            {
                int idx = cmd.CellIndices[k];
                if (idx < 0 || idx >= cells.Length)
                {
                    continue;
                }

                Cell c = cells[idx];
                c.m_Zone = new ZoneType { m_Index = ZoneSync.Index(cmd.ZoneNames[k]) };
                cells[idx] = c;
                applied++;
            }

            if (!EntityManager.HasComponent<Updated>(target))
            {
                EntityManager.AddComponent<Updated>(target);
            }

            // Refresh snapshot so our detector doesn't echo the change we just made.
            int n = cells.Length;
            var cur = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                cur[i] = cells[i].m_Zone.m_Index;
            }

            ZoneSync.Snapshot[target] = cur;

            CS2M.Log.Info($"[Zone] APPLIED block=({cmd.BlockX:F0},{cmd.BlockZ:F0}) cells={applied} entity={target.Index}");
        }

        private Entity FindBlock(ZonePaintCommand cmd)
        {
            NativeArray<Entity> blocks = _allBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestD = MatchEpsilonSq;
                foreach (Entity e in blocks)
                {
                    Block b = EntityManager.GetComponentData<Block>(e);
                    if (b.m_Size.x != cmd.SizeX || b.m_Size.y != cmd.SizeY)
                    {
                        continue;
                    }

                    float dx = b.m_Position.x - cmd.BlockX;
                    float dz = b.m_Position.z - cmd.BlockZ;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = e;
                    }
                }

                return best;
            }
            finally
            {
                blocks.Dispose();
            }
        }
    }
}
