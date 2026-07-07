using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Global toggle for the ZoneBlockAuthority path. ON by default since 2026-07-07 —
    /// validated live in 2-sim + selftest 88 PASS/0 FAIL with every gated fix enabled together (no
    /// regression/echo/crash). Every consumer (<see cref="ZoneBlockAuthoritySystem"/>,
    /// <see cref="ZoneBlockAuthorityApplySystem"/>, <see cref="ZoneOrderTiebreakSystem"/>) additionally
    /// checks <c>PlayerStatus.PLAYING</c> before acting, so single-player is unaffected regardless of
    /// this gate. Set env <c>CS2M_ZONEAUTH=0</c> to disable.</summary>
    public static class ZoneAuthority
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_ZONEAUTH") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>Edge identity key (the CS2M_NodeSyncId of its two endpoints). Manual Equals/GetHashCode,
    /// like the repo's own game-facing structs (Block/Cell/SubBlock), instead of pulling in a ValueTuple
    /// dependency that this net472 target may not have available.</summary>
    internal struct EdgeIdKey : System.IEquatable<EdgeIdKey>
    {
        public ulong Start;
        public ulong End;

        public bool Equals(EdgeIdKey other) => Start == other.Start && End == other.End;
        public override bool Equals(object obj) => obj is EdgeIdKey k && Equals(k);
        public override int GetHashCode() => unchecked((Start.GetHashCode() * 397) ^ End.GetHashCode());
    }

    /// <summary>A zone Block plus its (side, t) relative to the owning edge's start->end axis. Shared shape
    /// between the detector (sender, real blocks) and the applier (receiver, candidate blocks).</summary>
    internal struct ZoneBlockMeta
    {
        public Entity Entity;
        public Block Block;
        public sbyte Side;
        public float T;
    }

    /// <summary>Side/ordinal math shared byte-for-byte between the detector (sender) and the applier
    /// (receiver) — both sides must address a given block the SAME way regardless of how far their own
    /// local derivation diverged, or the heal lands on the wrong block.</summary>
    internal static class ZoneBlockGeometry
    {
        /// <summary>+1/-1: which side of the start-&gt;end axis the block's direction faces.</summary>
        public static sbyte Side(float3 startPos, float3 endPos, float2 blockDirection)
        {
            float2 axis = endPos.xz - startPos.xz;
            float2 perp = new float2(axis.y, -axis.x);
            return (sbyte) (math.dot(blockDirection, perp) >= 0f ? 1 : -1);
        }

        /// <summary>Unnormalized projection of the block center onto the start-&gt;end axis — only used to
        /// ORDER blocks on the same side of the same edge, never compared across different edges.</summary>
        public static float T(float3 startPos, float3 endPos, float3 blockPosition)
        {
            float2 axis = endPos.xz - startPos.xz;
            return math.dot(blockPosition.xz - startPos.xz, axis);
        }
    }

    /// <summary>
    ///     ZoneBlockAuthority DETECTOR (host-only, gated CS2M_ZONEAUTH, ON by default since 2026-07-07). Periodically sweeps every
    ///     road-owned zone Block and, for the ones whose geometry/cells changed since the last send, ships
    ///     the host's authoritative Block position/direction/size + cell zones so the client can heal its
    ///     own (possibly differently-derived) block to match. Root cause + rationale: docs/zoneauth-spec.md.
    /// </summary>
    public partial class ZoneBlockAuthoritySystem : GameSystemBase
    {
        // v61: 240 (~4 s) → 15 (~250 ms @60fps). The sweep is already DELTA (per-block signature —
        // steady state ships NOTHING), so cadence only bounds (a) the drift window a player can SEE
        // and (b) how fast a big dirty burst drains through the 256-block cap (2.5 s instead of 40 s
        // for a ~2.5k-block city). Sweep cost is O(cells) hashing (~90k ints) — well under 1 ms, fine
        // at 4 Hz. The SEND log is throttled below so active painting doesn't spam CS2M.log.
        private const int SweepEveryNFrames = 15;
        private const int MaxBlocksPerCommand = 256;
        private double _lastSendLogAt;

        private PrefabSystem _prefabSystem;
        private EntityQuery _ownedBlocks;
        private int _frameCounter;
        private int _sweepCount;
        private readonly Dictionary<Entity, long> _lastSig = new Dictionary<Entity, long>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _ownedBlocks = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Block>(),
                    ComponentType.ReadOnly<Cell>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[ZoneAuth] ZoneBlockAuthoritySystem created");
        }

        protected override void OnUpdate()
        {
            if (!ZoneAuthority.Enabled)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);

            if (++_frameCounter < SweepEveryNFrames)
            {
                return;
            }

            _frameCounter = 0;
            Sweep();
        }

        private void Sweep()
        {
            _sweepCount++;

            // Group blocks by their owning edge — the ordinal depends on the WHOLE per-edge/per-side group,
            // so every block of an edge must be gathered before any of them can be emitted (spec).
            var byEdge = new Dictionary<Entity, List<Entity>>();
            NativeArray<Entity> blocks = _ownedBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in blocks)
                {
                    // v61 (fast cadence): a block still being touched by THIS frame's derivation cascade
                    // (road edit → block/cell settling runs over a few frames) ships half-baked state and
                    // forces a re-send next sweep. Skip it — the tags are gone by the next sweep (~250 ms)
                    // and the settled block ships once. At the old 4 s cadence this never mattered.
                    if (EntityManager.HasComponent<Updated>(e) || EntityManager.HasComponent<Created>(e))
                    {
                        continue;
                    }

                    Owner owner = EntityManager.GetComponentData<Owner>(e);
                    Entity edge = owner.m_Owner;
                    if (edge == Entity.Null || !EntityManager.Exists(edge) || !EntityManager.HasComponent<Edge>(edge))
                    {
                        continue; // not a road-owned block (yet) — out of scope for this probe
                    }

                    if (!byEdge.TryGetValue(edge, out List<Entity> list))
                    {
                        list = new List<Entity>();
                        byEdge[edge] = list;
                    }

                    list.Add(e);
                }
            }
            finally
            {
                blocks.Dispose();
            }

            var outStartIds = new List<ulong>();
            var outEndIds = new List<ulong>();
            var outSides = new List<sbyte>();
            var outOrdinals = new List<int>();
            var outPosX = new List<float>();
            var outPosY = new List<float>();
            var outPosZ = new List<float>();
            var outDirX = new List<float>();
            var outDirZ = new List<float>();
            var outSizeX = new List<int>();
            var outSizeY = new List<int>();
            var outCellsOffset = new List<int>();
            var outCellsCount = new List<int>();
            var outCellZonePool = new List<int>();
            var zonePool = new List<string>();
            var zonePoolIndex = new Dictionary<string, int>();
            int sent = 0;

            foreach (KeyValuePair<Entity, List<Entity>> kv in byEdge)
            {
                if (sent >= MaxBlocksPerCommand)
                {
                    break;
                }

                Entity edgeEntity = kv.Key;
                Edge edgeData = EntityManager.GetComponentData<Edge>(edgeEntity);
                if (!EntityManager.HasComponent<CS2M_NodeSyncId>(edgeData.m_Start)
                    || !EntityManager.HasComponent<CS2M_NodeSyncId>(edgeData.m_End))
                {
                    continue; // only covers roads already identity-synced (CS2M_NodeSyncId); the rest is
                              // future scope (spec).
                }

                ulong startId = EntityManager.GetComponentData<CS2M_NodeSyncId>(edgeData.m_Start).m_Id;
                ulong endId = EntityManager.GetComponentData<CS2M_NodeSyncId>(edgeData.m_End).m_Id;
                if (startId == 0 || endId == 0)
                {
                    continue;
                }

                Node startNode = EntityManager.GetComponentData<Node>(edgeData.m_Start);
                Node endNode = EntityManager.GetComponentData<Node>(edgeData.m_End);

                var plusSide = new List<ZoneBlockMeta>();
                var minusSide = new List<ZoneBlockMeta>();
                foreach (Entity b in kv.Value)
                {
                    Block blk = EntityManager.GetComponentData<Block>(b);
                    var meta = new ZoneBlockMeta
                    {
                        Entity = b,
                        Block = blk,
                        Side = ZoneBlockGeometry.Side(startNode.m_Position, endNode.m_Position, blk.m_Direction),
                        T = ZoneBlockGeometry.T(startNode.m_Position, endNode.m_Position, blk.m_Position),
                    };

                    (meta.Side >= 0 ? plusSide : minusSide).Add(meta);
                }

                plusSide.Sort((a, b) => a.T.CompareTo(b.T));
                minusSide.Sort((a, b) => a.T.CompareTo(b.T));

                sent = EmitGroup(plusSide, startId, endId, sent, outStartIds, outEndIds, outSides, outOrdinals,
                    outPosX, outPosY, outPosZ, outDirX, outDirZ, outSizeX, outSizeY, outCellsOffset, outCellsCount,
                    outCellZonePool, zonePool, zonePoolIndex);
                if (sent >= MaxBlocksPerCommand)
                {
                    continue;
                }

                sent = EmitGroup(minusSide, startId, endId, sent, outStartIds, outEndIds, outSides, outOrdinals,
                    outPosX, outPosY, outPosZ, outDirX, outDirZ, outSizeX, outSizeY, outCellsOffset, outCellsCount,
                    outCellZonePool, zonePool, zonePoolIndex);
            }

            if (sent == 0)
            {
                return;
            }

            var cmd = new ZoneBlockAuthorityCommand
            {
                EdgeStartIds = outStartIds.ToArray(),
                EdgeEndIds = outEndIds.ToArray(),
                Sides = outSides.ToArray(),
                Ordinals = outOrdinals.ToArray(),
                PosX = outPosX.ToArray(),
                PosY = outPosY.ToArray(),
                PosZ = outPosZ.ToArray(),
                DirX = outDirX.ToArray(),
                DirZ = outDirZ.ToArray(),
                SizeX = outSizeX.ToArray(),
                SizeY = outSizeY.ToArray(),
                CellsOffset = outCellsOffset.ToArray(),
                CellsCount = outCellsCount.ToArray(),
                ZonePool = zonePool.ToArray(),
                CellZonePool = outCellZonePool.ToArray(),
            };

            Command.SendToAll?.Invoke(cmd);

            // At 4 Hz an active zone-paint drag would log every sweep — cap at ~1 line/s (the count
            // still reflects everything shipped since dirty blocks accumulate into the next send).
            double logNow = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (logNow - _lastSendLogAt >= 1.0)
            {
                _lastSendLogAt = logNow;
                CS2M.Log.Info($"[ZoneAuth] SEND blocks={sent} (sweep={_sweepCount})");
            }
        }

        /// <summary>Emits every DIRTY block of one (edge, side) group, in ordinal order, capped at
        /// <see cref="MaxBlocksPerCommand"/>. Ordinals are assigned over the WHOLE ordered group even for
        /// blocks that don't end up shipped, so a block's ordinal never shifts just because a sibling was
        /// unchanged.</summary>
        private int EmitGroup(List<ZoneBlockMeta> ordered, ulong startId, ulong endId, int sent,
            List<ulong> outStartIds, List<ulong> outEndIds, List<sbyte> outSides, List<int> outOrdinals,
            List<float> outPosX, List<float> outPosY, List<float> outPosZ, List<float> outDirX, List<float> outDirZ,
            List<int> outSizeX, List<int> outSizeY, List<int> outCellsOffset, List<int> outCellsCount,
            List<int> outCellZonePool, List<string> zonePool, Dictionary<string, int> zonePoolIndex)
        {
            for (int ordinal = 0; ordinal < ordered.Count; ordinal++)
            {
                if (sent >= MaxBlocksPerCommand)
                {
                    break;
                }

                ZoneBlockMeta meta = ordered[ordinal];
                if (!EntityManager.HasBuffer<Cell>(meta.Entity))
                {
                    continue;
                }

                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(meta.Entity, true);
                long sig = ComputeSignature(meta.Block, cells);
                if (_lastSig.TryGetValue(meta.Entity, out long prevSig) && prevSig == sig)
                {
                    continue; // unchanged since last send
                }

                int offset = outCellZonePool.Count;
                int n = cells.Length;
                for (int i = 0; i < n; i++)
                {
                    string zn = ZoneSync.Name(cells[i].m_Zone.m_Index);
                    if (!zonePoolIndex.TryGetValue(zn, out int zi))
                    {
                        zi = zonePool.Count;
                        zonePool.Add(zn);
                        zonePoolIndex[zn] = zi;
                    }

                    outCellZonePool.Add(zi);
                }

                outStartIds.Add(startId);
                outEndIds.Add(endId);
                outSides.Add(meta.Side);
                outOrdinals.Add(ordinal);
                outPosX.Add(meta.Block.m_Position.x);
                outPosY.Add(meta.Block.m_Position.y);
                outPosZ.Add(meta.Block.m_Position.z);
                outDirX.Add(meta.Block.m_Direction.x);
                outDirZ.Add(meta.Block.m_Direction.y);
                outSizeX.Add(meta.Block.m_Size.x);
                outSizeY.Add(meta.Block.m_Size.y);
                outCellsOffset.Add(offset);
                outCellsCount.Add(n);

                _lastSig[meta.Entity] = sig;
                sent++;
            }

            return sent;
        }

        /// <summary>Cheap dirty-tracking hash: size + quantized position (0.1 m) + every cell's zone index.
        /// Not cryptographic — only needs to change whenever the shipped state changes.</summary>
        private static long ComputeSignature(Block b, DynamicBuffer<Cell> cells)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                h = h * 16777619 + b.m_Size.x;
                h = h * 16777619 + b.m_Size.y;
                h = h * 16777619 + (long) math.round(b.m_Position.x * 10f);
                h = h * 16777619 + (long) math.round(b.m_Position.y * 10f);
                h = h * 16777619 + (long) math.round(b.m_Position.z * 10f);
                for (int i = 0; i < cells.Length; i++)
                {
                    h = h * 16777619 + cells[i].m_Zone.m_Index;
                }

                return h;
            }
        }
    }

    /// <summary>
    ///     ZoneBlockAuthority APPLIER (client-only, gated CS2M_ZONEAUTH, ON by default since 2026-07-07). Resolves the owning edge by
    ///     identity, finds the matching local block by (side, ordinal) — falling back to nearest-t on the
    ///     same side when the local block count itself diverged — and overwrites its Block+Cell data with
    ///     the host's authoritative values so both machines converge on the same block shape.
    /// </summary>
    public partial class ZoneBlockAuthorityApplySystem : GameSystemBase
    {
        private const int MaxRetryAttempts = 20;

        private struct PendingGroup
        {
            public ZoneBlockAuthorityCommand Cmd;
            public ulong StartId;
            public ulong EndId;
            public List<int> Indices;
            public int Attempts;
        }

        private PrefabSystem _prefabSystem;
        private readonly Queue<PendingGroup> _retryQueue = new Queue<PendingGroup>();
        private readonly Dictionary<Entity, int> _healCount = new Dictionary<Entity, int>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            CS2M.Log.Info("[ZoneAuth] ZoneBlockAuthorityApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (!ZoneAuthority.Enabled)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                return;
            }

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            ZoneSync.EnsureBuilt(EntityManager, _prefabSystem);

            // Drain at most one command per frame from the network queue.
            if (RemoteZoneBlockQueue.TryDequeue(out ZoneBlockAuthorityCommand cmd))
            {
                SplitAndApply(cmd);
            }

            // Plus at most one retry slot per frame (unresolved edges from earlier commands).
            RetryOne();
        }

        /// <summary>Splits a command's flat block entries by owning edge identity and attempts each
        /// edge-group once.</summary>
        private void SplitAndApply(ZoneBlockAuthorityCommand cmd)
        {
            if (cmd.EdgeStartIds == null)
            {
                return;
            }

            var groups = new Dictionary<EdgeIdKey, List<int>>();
            for (int i = 0; i < cmd.EdgeStartIds.Length; i++)
            {
                var key = new EdgeIdKey { Start = cmd.EdgeStartIds[i], End = cmd.EdgeEndIds[i] };
                if (!groups.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>();
                    groups[key] = list;
                }

                list.Add(i);
            }

            foreach (KeyValuePair<EdgeIdKey, List<int>> kv in groups)
            {
                TryApplyGroup(cmd, kv.Key.Start, kv.Key.End, kv.Value, 1);
            }
        }

        private void RetryOne()
        {
            if (_retryQueue.Count == 0)
            {
                return;
            }

            PendingGroup p = _retryQueue.Dequeue();
            TryApplyGroup(p.Cmd, p.StartId, p.EndId, p.Indices, p.Attempts + 1);
        }

        /// <summary>Resolves the edge; on success heals every block in <paramref name="indices"/>, on
        /// failure re-queues (up to <see cref="MaxRetryAttempts"/>) then DROPs with a log.</summary>
        private void TryApplyGroup(ZoneBlockAuthorityCommand cmd, ulong startId, ulong endId, List<int> indices, int attempt)
        {
            if (!FindEdgeById(startId, endId, out Entity edge))
            {
                if (attempt >= MaxRetryAttempts)
                {
                    CS2M.Log.Info($"[ZoneAuth] DROP edge unresolved start={startId} end={endId} blocks={indices.Count} after {attempt} attempts");
                    return;
                }

                _retryQueue.Enqueue(new PendingGroup { Cmd = cmd, StartId = startId, EndId = endId, Indices = indices, Attempts = attempt });
                return;
            }

            // Canonical axis orientation: compute side/t relative to the SHIPPED (startId, endId) node
            // order, never the local edge's own m_Start/m_End — FindEdgeById accepts a flipped edge, and
            // a flipped axis inverts BOTH side and t, silently healing the wrong blocks. Both resolves
            // already succeeded inside FindEdgeById.
            CS2M_NodeSyncIds.TryResolve(EntityManager, startId, out Entity startEntity);
            CS2M_NodeSyncIds.TryResolve(EntityManager, endId, out Entity endEntity);
            Node startNode = EntityManager.GetComponentData<Node>(startEntity);
            Node endNode = EntityManager.GetComponentData<Node>(endEntity);

            if (!EntityManager.HasBuffer<SubBlock>(edge))
            {
                return; // road exists but has no generated zone blocks yet — nothing to heal this pass
            }

            DynamicBuffer<SubBlock> subBlocks = EntityManager.GetBuffer<SubBlock>(edge, true);
            var candidates = new List<ZoneBlockMeta>();
            for (int i = 0; i < subBlocks.Length; i++)
            {
                Entity be = subBlocks[i].m_SubBlock;
                if (be == Entity.Null || !EntityManager.Exists(be) || !EntityManager.HasComponent<Block>(be)
                    || EntityManager.HasComponent<Deleted>(be))
                {
                    continue;
                }

                Block blk = EntityManager.GetComponentData<Block>(be);
                candidates.Add(new ZoneBlockMeta
                {
                    Entity = be,
                    Block = blk,
                    Side = ZoneBlockGeometry.Side(startNode.m_Position, endNode.m_Position, blk.m_Direction),
                    T = ZoneBlockGeometry.T(startNode.m_Position, endNode.m_Position, blk.m_Position),
                });
            }

            foreach (int idx in indices)
            {
                ApplyOne(cmd, idx, candidates, startNode.m_Position, endNode.m_Position);
            }
        }

        private void ApplyOne(ZoneBlockAuthorityCommand cmd, int idx, List<ZoneBlockMeta> candidates, float3 startPos, float3 endPos)
        {
            sbyte wantSide = cmd.Sides[idx];
            int wantOrdinal = cmd.Ordinals[idx];

            // Rebuild the (side, ordinal) numbering exactly like the detector: group by side, sort by t.
            var sameSide = new List<ZoneBlockMeta>();
            foreach (ZoneBlockMeta c in candidates)
            {
                if (c.Side == wantSide)
                {
                    sameSide.Add(c);
                }
            }

            sameSide.Sort((a, b) => a.T.CompareTo(b.T));

            Entity target = Entity.Null;
            if (wantOrdinal >= 0 && wantOrdinal < sameSide.Count)
            {
                target = sameSide[wantOrdinal].Entity;
            }
            else if (sameSide.Count > 0)
            {
                // Fallback: same side, closest t — covers a local block COUNT that diverged from the
                // host's (extra/missing block is the very divergence this feature exists to heal).
                float wantT = ZoneBlockGeometry.T(startPos, endPos, new float3(cmd.PosX[idx], cmd.PosY[idx], cmd.PosZ[idx]));
                float bestDiff = float.MaxValue;
                foreach (ZoneBlockMeta c in sameSide)
                {
                    float diff = math.abs(c.T - wantT);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        target = c.Entity;
                    }
                }
            }

            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[ZoneAuth] MISS side={wantSide} ordinal={wantOrdinal} at=({cmd.PosX[idx]:F0},{cmd.PosZ[idx]:F0})");
                return;
            }

            Heal(cmd, idx, target);
        }

        private void Heal(ZoneBlockAuthorityCommand cmd, int idx, Entity target)
        {
            int sizeX = cmd.SizeX[idx];
            int sizeY = cmd.SizeY[idx];
            int offset = cmd.CellsOffset[idx];
            int count = cmd.CellsCount[idx];

            // Resolve the wanted cell zone indices (local index space) up front, for the idempotency check.
            var wantZoneIndex = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                int poolIdx = cmd.CellZonePool[offset + i];
                string zoneName = cmd.ZonePool[poolIdx];
                wantZoneIndex[i] = ZoneSync.Index(zoneName);
            }

            Block localBlock = EntityManager.GetComponentData<Block>(target);
            bool sizeMatches = localBlock.m_Size.x == sizeX && localBlock.m_Size.y == sizeY;
            bool cellsMatch = false;
            if (sizeMatches && EntityManager.HasBuffer<Cell>(target))
            {
                DynamicBuffer<Cell> localCells = EntityManager.GetBuffer<Cell>(target, true);
                if (localCells.Length == count)
                {
                    cellsMatch = true;
                    for (int i = 0; i < count; i++)
                    {
                        if (localCells[i].m_Zone.m_Index != wantZoneIndex[i])
                        {
                            cellsMatch = false;
                            break;
                        }
                    }
                }
            }

            // v56 FIELD FIX (statediff com flags, 06/07 ~18h): posição TAMBÉM é conteúdo. Blocos com o
            // mesmo tamanho + as mesmas zonas mas centro deslocado ~1 m entre as máquinas (ruído
            // sub-métrico residual de geometria de rua) produzem contests de sobreposição diferentes →
            // células Unzoned Visible num PC e Blocked no outro (o padrão branco-vs-verde que o olho
            // pega e o radar não). O skip idempotente antigo ignorava posição e deixava exatamente esse
            // caso passar sem heal.
            // v57 (rodada ZoneOrderTiebreak): 0.25f -> 0.05f. Blocos sub-25cm ainda passavam o skip e
            // ainda assim flipavam o contest de célula de borda (CellCheckHelpers.FindOverlappingBlocksJob
            // usa a posição do bloco para decidir vizinhança/overlap — um resíduo de poucos cm já é
            // suficiente para virar o resultado do contest perto da borda de uma célula).
            bool posMatches = math.distance(localBlock.m_Position,
                new float3(cmd.PosX[idx], cmd.PosY[idx], cmd.PosZ[idx])) <= 0.05f;

            if (sizeMatches && cellsMatch && posMatches)
            {
                return; // already converged — silent skip (idempotent)
            }

            int oldW = localBlock.m_Size.x;
            int oldH = localBlock.m_Size.y;

            // 1) Authoritative Block geometry (no structural change — safe before AddComponent below).
            EntityManager.SetComponentData(target, new Block
            {
                m_Position = new float3(cmd.PosX[idx], cmd.PosY[idx], cmd.PosZ[idx]),
                m_Direction = new float2(cmd.DirX[idx], cmd.DirZ[idx]),
                m_Size = new int2(sizeX, sizeY),
            });

            // 2) Ask the game to re-simulate/re-render the block. STRUCTURAL CHANGE FIRST (moves the
            // entity to another chunk), same lesson as ZonePaintApplySystem.ApplyOne: any DynamicBuffer
            // handle taken before this AddComponent would be invalidated by it.
            if (!EntityManager.HasComponent<Updated>(target))
            {
                EntityManager.AddComponent<Updated>(target);
            }

            // Same Modification5 dead-zone as ZonePaintApplySystem.ApplyOne (DeferredUpdateMarker.cs
            // docs the full chain): Mod1-4 consumers already ran this frame and CleanUpSystem strips
            // Updated before the next frame's Mod1-4 get a look, so also re-stamp at the start of the
            // NEXT frame — otherwise a healed block's cells never get re-lit (m_State stays 0).
            DeferredUpdated.Enqueue(target);

            // 3) Authoritative cells (fresh buffer handle, taken AFTER the structural change above).
            // Flags/height are left for the local CellCheckSystem to recompute on the Updated pass that
            // follows — the same pipeline a locally-derived block goes through.
            DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(target);
            cells.ResizeUninitialized(sizeX * sizeY);
            for (int i = 0; i < cells.Length; i++)
            {
                // Beyond `count` (only possible if a malformed command ships count != sizeX*sizeY) the
                // resized buffer would otherwise hold UNINITIALIZED memory — always write every cell.
                cells[i] = new Cell
                {
                    m_State = default,
                    m_Zone = new ZoneType { m_Index = i < count ? wantZoneIndex[i] : (ushort) 0 },
                    m_Height = short.MaxValue,
                };
            }

            // 4) Anti-echo: mark + refresh the shared zoning snapshot so ZoneDetectorSystem's own diff
            // doesn't see this heal as a local edit and bounce it back.
            ZoneEcho.Mark(target);
            ZoneSync.Snapshot[target] = wantZoneIndex;

            CS2M.Log.Info($"[ZoneAuth] HEAL block=({cmd.PosX[idx]:F0},{cmd.PosZ[idx]:F0}) {oldW}x{oldH}->{sizeX}x{sizeY} cells={count}");

            // 5) Oscillation radar: the probe this whole feature exists to run. Repeated heals on the SAME
            // block mean the local derivation keeps re-diverging on top of the fix.
            int healCount = _healCount.TryGetValue(target, out int hc) ? hc + 1 : 1;
            _healCount[target] = healCount;
            if (healCount >= 5)
            {
                CS2M.Log.Warn($"[ZoneAuth] OSCILLA block=({cmd.PosX[idx]:F0},{cmd.PosZ[idx]:F0}) heals={healCount} — jogo local re-deriva por cima");
            }
        }

        /// <summary>Copy of NetEditApplySystem.FindEdgeById (NetEditApplySystem.cs:273-307): identity-first
        /// edge resolution via CS2M_NodeSyncId + ConnectedEdge walk.</summary>
        private bool FindEdgeById(ulong aId, ulong bId, out Entity edge)
        {
            edge = Entity.Null;
            if (aId == 0 || bId == 0)
            {
                return false;
            }

            if (!CS2M_NodeSyncIds.TryResolve(EntityManager, aId, out Entity a)
                || !CS2M_NodeSyncIds.TryResolve(EntityManager, bId, out Entity b)
                || a == b || !EntityManager.HasBuffer<ConnectedEdge>(a))
            {
                return false;
            }

            DynamicBuffer<ConnectedEdge> ce = EntityManager.GetBuffer<ConnectedEdge>(a, true);
            for (int i = 0; i < ce.Length; i++)
            {
                Entity e = ce[i].m_Edge;
                if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e)
                    || !EntityManager.HasComponent<Edge>(e))
                {
                    continue;
                }

                Edge ed = EntityManager.GetComponentData<Edge>(e);
                if ((ed.m_Start == a && ed.m_End == b) || (ed.m_Start == b && ed.m_End == a))
                {
                    edge = e;
                    return true;
                }
            }

            return false;
        }
    }
}
