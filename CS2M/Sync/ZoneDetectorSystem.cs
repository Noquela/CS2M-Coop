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
    ///     and broadcasting the changed cells (as ZonePrefab names). A brand-new block is born fully
    ///     Unzoned, so its baseline on first sight is an all-zero array — NOT whatever the cells
    ///     currently look like. This matters because a block can already be painted the very first
    ///     time this system observes it (road-driven block create/recreate that preserves paint, or a
    ///     player painting a cell in the same frame/batch the block is born) — caching the painted
    ///     state as baseline would diff against itself forever and silently eat that edit (the
    ///     "single zone block not detected" bug: the 18*Unzoned-vs-18*NA_Residential_Low drift). Echo
    ///     guard: the apply system marks+updates the same snapshot for a remote-applied change (paint
    ///     OR a block created/healed by remote sync), so it produces no diff here — this guard is
    ///     checked BEFORE the first-sight zero-baseline logic, so a block born from a remote apply is
    ///     never mistaken for a fresh local edit and bounced back.
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

                    bool firstSight = !ZoneSync.Snapshot.TryGetValue(e, out ushort[] prev) || prev.Length != n;

                    // Echo guard FIRST (covers both the "known block updated" path and the "brand-new
                    // block" first-sight path): this block was just remote-applied (a zone paint, or a
                    // block CREATED/HEALED by remote sync — ZonePaintApplySystem/ZoneBlockAuthorityApplySystem
                    // both call ZoneEcho.Mark on the entity they wrote). The game recomputes cells over
                    // the next frames (CellCheckSystem overlap sharing), so absorb the REAL state into the
                    // snapshot instead of diffing — otherwise we ping-pong the remote paint straight back
                    // to its sender. A block born from a remote apply must NEVER fall into the first-sight
                    // branch below and be treated as a fresh local edit.
                    if (ZoneEcho.IsMarked(e))
                    {
                        ZoneSync.Snapshot[e] = cur;
                        continue;
                    }

                    if (firstSight)
                    {
                        // FIX: the correct baseline for a block we've never snapshotted is "fully Unzoned"
                        // (every cell index 0) — a block is born without zoning; any zone found on it is a
                        // player edit. The OLD code cached whatever the cells currently look like as the
                        // baseline, so a block that arrives ALREADY PAINTED the first time this system sees
                        // it (e.g. spawned from a road edit, or painted in the same batch the block itself
                        // was created) never diffed against anything and its paint was silently swallowed
                        // forever — exactly the "single zone block not detected" drift. Diffing against an
                        // all-zero baseline instead makes the already-painted cells show up as a diff on
                        // this very first pass, so they ship immediately. (The echo guard above already
                        // excluded blocks born from a remote apply, so this only fires for genuine local
                        // paint / local block regeneration.)
                        prev = new ushort[n];
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
