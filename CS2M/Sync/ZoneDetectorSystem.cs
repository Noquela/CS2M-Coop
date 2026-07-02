using System.Collections.Generic;
using CS2M.API.Commands;
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
    ///     Detects zoning changes by diffing each Updated <c>Block</c>'s cell zones against a snapshot
    ///     and broadcasting the changed cells (as ZonePrefab names). First sight of a block caches its
    ///     baseline silently (handles road-driven block create/recreate). Echo guard: the apply system
    ///     updates the same snapshot, so a remote-applied change produces no diff here.
    /// </summary>
    public partial class ZoneDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _updatedBlocks;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _updatedBlocks = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Block>(),
                    ComponentType.ReadOnly<Cell>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            RequireForUpdate(_updatedBlocks);
            CS2M.Log.Info("[Zone] ZoneDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            ZoneEcho.Tick();

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);

            NativeArray<Entity> blocks = _updatedBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in blocks)
                {
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(e, true);
                    int n = cells.Length;
                    var cur = new ushort[n];
                    for (int i = 0; i < n; i++)
                    {
                        cur[i] = cells[i].m_Zone.m_Index;
                    }

                    if (!ZoneSync.Snapshot.TryGetValue(e, out ushort[] prev) || prev.Length != n)
                    {
                        ZoneSync.Snapshot[e] = cur; // first sight: cache baseline, don't send
                        continue;
                    }

                    // Echo guard: this block was just remote-applied. The game recomputes its cells
                    // over the next frames (CellCheckSystem overlap sharing), so absorb the REAL state
                    // into the snapshot instead of diffing — otherwise we ping-pong the paint back.
                    if (ZoneEcho.IsMarked(e))
                    {
                        ZoneSync.Snapshot[e] = cur;
                        continue;
                    }

                    List<int> changedIdx = null;
                    List<string> changedZone = null;
                    for (int i = 0; i < n; i++)
                    {
                        if (cur[i] == prev[i])
                        {
                            continue;
                        }

                        if (changedIdx == null)
                        {
                            changedIdx = new List<int>();
                            changedZone = new List<string>();
                        }

                        changedIdx.Add(i);
                        changedZone.Add(ZoneSync.Name(cur[i]));
                    }

                    ZoneSync.Snapshot[e] = cur;
                    if (changedIdx == null)
                    {
                        continue;
                    }

                    Block b = EntityManager.GetComponentData<Block>(e);
                    Command.SendToAll?.Invoke(new ZonePaintCommand
                    {
                        BlockX = b.m_Position.x,
                        BlockZ = b.m_Position.z,
                        DirX = b.m_Direction.x,
                        DirZ = b.m_Direction.y,
                        SizeX = b.m_Size.x,
                        SizeY = b.m_Size.y,
                        CellIndices = changedIdx.ToArray(),
                        ZoneNames = changedZone.ToArray(),
                    });
                    CS2M.Log.Info($"[Zone] DETECT+SEND block=({b.m_Position.x:F0},{b.m_Position.z:F0}) cells={changedIdx.Count}");
                }
            }
            finally
            {
                blocks.Dispose();
            }
        }
    }
}
