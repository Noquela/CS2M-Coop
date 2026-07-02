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

        // v39: zones painted along a just-synced road can arrive before (or slightly offset from)
        // the receiver's freshly generated blocks — the first 2-PC sessions dropped them with
        // "SKIP noBlock". Instead of discarding, park the command and retry for a while.
        private const int RetryTtlFrames = 900; // ~15 s at 60 fps
        private const int RetryEveryNFrames = 30; // ~2 attempts/s

        private struct PendingZone
        {
            public ZonePaintCommand Cmd;
            public int FramesLeft;
        }

        private readonly System.Collections.Generic.List<PendingZone> _pending =
            new System.Collections.Generic.List<PendingZone>();

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
                _pending.Clear();
                return;
            }

            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);

            while (RemoteZoneQueue.TryDequeue(out ZonePaintCommand cmd))
            {
                try { ApplyOne(cmd, true); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] zone apply failed: {ex.Message}"); }
            }

            RetryPending();
        }

        private void RetryPending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingZone p = _pending[i];
                p.FramesLeft--;

                if (p.FramesLeft % RetryEveryNFrames == 0 && ApplyOne(p.Cmd, false))
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                if (p.FramesLeft <= 0)
                {
                    CS2M.Log.Info($"[Zone] DROP noBlock at=({p.Cmd.BlockX:F0},{p.Cmd.BlockZ:F0}) after retries " +
                                  "(block never appeared — /resync reconciles)");
                    _pending.RemoveAt(i);
                    continue;
                }

                _pending[i] = p;
            }
        }

        /// <summary>Returns true when handled (applied or invalid); false only when retryable (no block yet).</summary>
        private bool ApplyOne(ZonePaintCommand cmd, bool firstTry)
        {
            if (cmd.CellIndices == null || cmd.ZoneNames == null)
            {
                return true;
            }

            // Retries use a wider match (rebuilt road geometry can shift block centers a few meters);
            // the SizeX/SizeY equality filter keeps a wrong-block match unlikely.
            Entity target = FindBlock(cmd, firstTry ? MatchEpsilonSq : 16f);
            if (target == Entity.Null)
            {
                if (firstTry)
                {
                    CS2M.Log.Info($"[Zone] RETRY noBlock at=({cmd.BlockX:F0},{cmd.BlockZ:F0}) " +
                                  $"(block not generated yet — retrying ~{RetryTtlFrames / 60}s)");
                    _pending.Add(new PendingZone { Cmd = cmd, FramesLeft = RetryTtlFrames });
                }

                return false;
            }

            // STRUCTURAL CHANGE FIRST: AddComponent moves the entity to another chunk and invalidates
            // any DynamicBuffer handle taken before it. The old order (GetBuffer → write → AddComponent
            // → re-read the STALE handle for the snapshot) silently read another block's inline cells,
            // corrupting the echo snapshot — the zone ping-pong from the first 2-PC session.
            if (!EntityManager.HasComponent<Updated>(target))
            {
                EntityManager.AddComponent<Updated>(target);
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

            // Refresh snapshot (handle still valid: no structural change since GetBuffer) and mark the
            // echo TTL so the detector absorbs the game's own cell recompute instead of re-sending.
            int n = cells.Length;
            var cur = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                cur[i] = cells[i].m_Zone.m_Index;
            }

            ZoneSync.Snapshot[target] = cur;
            ZoneEcho.Mark(target);

            CS2M.Log.Info($"[Zone] APPLIED block=({cmd.BlockX:F0},{cmd.BlockZ:F0}) cells={applied} entity={target.Index}" +
                          (firstTry ? "" : " (after retry)"));
            return true;
        }

        private Entity FindBlock(ZonePaintCommand cmd, float epsilonSq)
        {
            NativeArray<Entity> blocks = _allBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestD = epsilonSq;
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
