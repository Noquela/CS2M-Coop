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

    /// <summary>Global toggle for the v65 flags-authority path (Cell.m_State re-assertion). Separate from
    /// <see cref="ZoneAuthority"/> so the flags probe can be killed independently of the geometry/cells
    /// heal if it misbehaves, but every consumer ANDs it with <see cref="ZoneAuthority.Enabled"/> too —
    /// flags-only healing with no geometry authority underneath makes no sense. ON by default since
    /// 2026-07-07. Set env <c>CS2M_ZONEFLAGS=0</c> to disable.</summary>
    public static class ZoneFlagAuthority
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_ZONEFLAGS") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>v66 toggle for the "host owns the grid" set-reconcile path (CS2M_ZONESET, ON by default).
    /// When ON, a group the host ships COMPLETE (see <see cref="ZoneBlockAuthorityCommand.GroupComplete"/>) is
    /// RECONCILED on the client — heal matches, CREATE missing (clone a sibling), DELETE phantoms — so the
    /// two machines converge on the same SET of blocks even when their BlockSystem derivations produce
    /// different block counts (the v65 delta-heal could only fix EXISTING blocks; it never added a missing
    /// one or removed a ghost, so a set-divergence never converged). Separate gate so the create/delete
    /// half can be killed independently and fall back to the v65 heal-only behaviour, but every consumer ANDs
    /// it with <see cref="ZoneAuthority.Enabled"/> too. Set env <c>CS2M_ZONESET=0</c> to disable.</summary>
    /// <summary>v66.4: OFF BY DEFAULT (env <c>CS2M_ZONESET=1</c> to opt in). The v66 set-reconcile that
    /// CREATES a clone block (<c>EntityManager.Instantiate</c>) and DELETES a phantom
    /// (<c>AddComponent&lt;Deleted&gt;</c>) is fundamentally incompatible with the game's spatial index:
    /// zone blocks live in a <c>NativeQuadTree</c> maintained ONLY by the vanilla BlockSystem/SearchSystem,
    /// and creating/destroying blocks outside that path desyncs the tree — the game's own Burst job then
    /// throws "Item not found (NativeQuadTree.Update)" and ABORTS the process (proven in the field: the
    /// crash log fires on the exact frame a [ZoneAuth] CREATE/DELETE runs; every v66.x crash traced here,
    /// v65 heal-only never crashed). With this OFF, TryApplyGroup falls back to the v65 per-block HEAL,
    /// which only mutates EXISTING blocks (SetComponentData Block/Cell) and is QuadTree-safe. A genuine
    /// SET divergence (host block COUNT ≠ client, from a non-deterministic junction) is then left to the
    /// radar rather than "fixed" by a crash — the real fix is deterministic junction geometry, not
    /// out-of-band block create/delete.</summary>
    public static class ZoneSetReconcile
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    // v66.7: OFF by default (env CS2M_ZONESET=1 to opt in). Creating/deleting/resizing
                    // zone blocks OUT-OF-BAND from the game's BlockSystem corrupts multiple Burst-consumed
                    // structures — proven by native dumps: NativeQuadTree "Item not found", and a null-deref
                    // geometry job on resized blocks. Each fix reveals another face. The stable, convergent
                    // path is instead: AtomicBatch makes the ROAD identical on both PCs, so the game derives
                    // the SAME block set locally on each side, and the safe same-size HEAL (zone indices +
                    // flags + BuildOrder + sub-metre position, never a resize/create/delete) reconciles the
                    // CONTENT. That converges without ever mutating block geometry ourselves.
                    _state = System.Environment.GetEnvironmentVariable("CS2M_ZONESET") == "1" ? 1 : 0;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>The STABLE subset of <see cref="Game.Zones.CellFlags"/> — the bits the cell-overlap
    /// contest actually decides and that must therefore agree cross-machine (Blocked/Shared/Roadside/
    /// Visible/Overridden/Redundant/RoadLeft/RoadRight/RoadBack). Explicitly EXCLUDES Occupied (0x20 —
    /// flips independently whenever a building spawns/despawns, a channel this authority doesn't own and
    /// would otherwise force a constant re-send), Selected (0x40 — per-machine UI-only) and Updating
    /// (0x100 — a transient mid-recompute marker). Shared by the detector (fold into the signature + ship
    /// in CellStatePool), the applier (Heal's flagsMatch check + cell write) and ZoneFlagAssertSystem's
    /// continuous re-assert, so all three mask the exact same bits.</summary>
    internal static class ZoneFlagMask
    {
        public const ushort Stable = (ushort) (CellFlags.Blocked | CellFlags.Shared | CellFlags.Roadside
            | CellFlags.Visible | CellFlags.Overridden | CellFlags.Redundant
            | CellFlags.RoadLeft | CellFlags.RoadRight | CellFlags.RoadBack);
    }

    /// <summary>v65: client-side desired-flags ledger, keyed by local Block entity. Populated by
    /// <see cref="ZoneBlockAuthorityApplySystem"/>.Heal whenever a command ships CellStatePool, and
    /// continuously re-applied by <see cref="ZoneFlagAssertSystem"/> — a full geometric Heal only writes
    /// flags ONCE (then triggers <c>Updated</c>, and the game's own recompute may re-diverge them again
    /// per-machine, per the hysteresis in ZoneBlockAuthorityCommand.CellStatePool's doc-comment), so the
    /// convergence has to be re-checked on a cadence independent of new network commands. Locked like
    /// <see cref="DeferredUpdated"/>'s queue — defensive, since every actual mutation happens on the main
    /// ECS thread today, but this mirrors the repo's existing pattern for cross-call shared state.</summary>
    internal static class ZoneFlagAssert
    {
        // Internal (not private): ZoneFlagAssertSystem, in this same file/assembly, locks around direct
        // Want access itself (snapshot keys, TryGetValue, Remove) rather than duplicating every dictionary
        // operation as a wrapper method here.
        internal static readonly object Lock = new object();
        public static readonly Dictionary<Entity, ushort[]> Want = new Dictionary<Entity, ushort[]>();

        public static void Set(Entity target, ushort[] wantFlags)
        {
            lock (Lock)
            {
                Want[target] = wantFlags;
            }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                Want.Clear();
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

        // v64: a split-born node ships id=0 (see ZoneBlockAuthoritySystem.Sweep), so TWO DIFFERENT
        // unidentified edges in the SAME command can otherwise collide on the same (0,0)/(0,validId) key
        // and get grouped — and healed — as if they were one edge. The host copies the same node position
        // pair into every block of one edge-group verbatim (no recompute per block), so exact float
        // equality safely disambiguates within a single command. Pre-v64 senders never shipped an id-less
        // edge at all (the old sweep skipped it), so this is a no-op for them (defaults to 0f/0f).
        public float StartX;
        public float StartZ;
        public float EndX;
        public float EndZ;

        public bool Equals(EdgeIdKey other) => Start == other.Start && End == other.End
            && StartX.Equals(other.StartX) && StartZ.Equals(other.StartZ)
            && EndX.Equals(other.EndX) && EndZ.Equals(other.EndZ);
        public override bool Equals(object obj) => obj is EdgeIdKey k && Equals(k);
        public override int GetHashCode() => unchecked((Start.GetHashCode() * 397) ^ End.GetHashCode());
    }

    /// <summary>v66: identifies one (owning edge, side) group — the granularity the "host owns the grid"
    /// authority ships and reconciles at. On the HOST the Edge is the local edge entity (stable within a
    /// session) and keys the per-group signature ledger; on the CLIENT it's the resolved local edge and keys
    /// the anti-oscillation churn counter. Manual Equals/GetHashCode like the repo's other game-facing keys
    /// (EdgeIdKey/Block/Cell) rather than a ValueTuple this net472 target may lack.</summary>
    internal struct ZoneGroupKey : System.IEquatable<ZoneGroupKey>
    {
        public Entity Edge;
        public sbyte Side;

        public bool Equals(ZoneGroupKey other) => Edge == other.Edge && Side == other.Side;
        public override bool Equals(object obj) => obj is ZoneGroupKey k && Equals(k);
        public override int GetHashCode() => unchecked((Edge.GetHashCode() * 397) ^ Side);
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
        private double _lastTooBigLogAt;

        private PrefabSystem _prefabSystem;
        private EntityQuery _ownedBlocks;
        private int _frameCounter;
        private int _sweepCount;

        // v66: per-GROUP signature (was per-block). A (edge, side) group is shipped WHOLE whenever its fold
        // signature — every member's ComputeSignature + the member count — changes since the last send, so
        // the client always receives the complete authoritative set to reconcile against, not a delta. A
        // per-block ledger can't express "a block was removed" (its entry just goes stale), which is exactly
        // the set-divergence this rewrite exists to converge.
        private readonly Dictionary<ZoneGroupKey, long> _lastSig = new Dictionary<ZoneGroupKey, long>();

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
            // v66: any edge with a block still settling this frame is DEFERRED WHOLE. Under the v65 delta
            // model a mid-settle block was simply skipped (heal it next sweep — harmless). Under the v66
            // COMPLETE-SET model, omitting one block from a "complete" group tells the client that block no
            // longer exists, so it would DELETE the local counterpart as a phantom, then re-create it next
            // sweep once the host block settles — a create/delete oscillation. Ship an edge only once every
            // one of its blocks has settled, so the set the client reconciles against is truly complete.
            var settlingEdges = new HashSet<Entity>();
            NativeArray<Entity> blocks = _ownedBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in blocks)
                {
                    Owner owner = EntityManager.GetComponentData<Owner>(e);
                    Entity edge = owner.m_Owner;
                    if (edge == Entity.Null || !EntityManager.Exists(edge) || !EntityManager.HasComponent<Edge>(edge))
                    {
                        continue; // not a road-owned block (yet) — out of scope for this probe
                    }

                    // v61 (fast cadence): a block still being touched by THIS frame's derivation cascade
                    // (road edit → block/cell settling runs over a few frames) ships half-baked state. v66:
                    // mark its WHOLE edge as settling and drop the block; the edge ships next sweep (~250 ms)
                    // as one settled, complete set. At the old 4 s cadence this never mattered.
                    if (EntityManager.HasComponent<Updated>(e) || EntityManager.HasComponent<Created>(e))
                    {
                        settlingEdges.Add(edge);
                        continue;
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
            var outCellStatePool = new List<ushort>();
            var outBuildOrders = new List<uint>();
            // v64: owning edge's node XZ positions, one entry per emitted block (fallback identity for
            // split-born nodes — see ZoneBlockAuthorityCommand.EdgeStartXs doc-comment).
            var outEdgeStartXs = new List<float>();
            var outEdgeStartZs = new List<float>();
            var outEdgeEndXs = new List<float>();
            var outEdgeEndZs = new List<float>();
            // v66: per-block flag, true for every block emitted (the new model only ever ships COMPLETE
            // groups). The client reads any-true-in-group as "reconcile this whole set" (create/delete),
            // absent/false as the v65 heal-only path.
            var outGroupComplete = new List<bool>();
            var zonePool = new List<string>();
            var zonePoolIndex = new Dictionary<string, int>();
            int sent = 0;

            foreach (KeyValuePair<Entity, List<Entity>> kv in byEdge)
            {
                Entity edgeEntity = kv.Key;

                // v66: never ship a partially-settled edge as a complete set (would drive phantom deletes on
                // the client — see settlingEdges above). It ships whole on a later sweep once settled.
                if (settlingEdges.Contains(edgeEntity))
                {
                    continue;
                }

                Edge edgeData = EntityManager.GetComponentData<Edge>(edgeEntity);

                // v64 (split-junction drift, 2-sim statediff 07/07): this used to `continue` here whenever
                // either end lacked CS2M_NodeSyncId ("only covers roads already identity-synced"). But a
                // node born from a road SPLIT never gets one, so that skip meant the sweep never covered
                // the exact edges whose zone blocks diverge the most — the opposite of what this feature
                // is for. Ship the id when present (0 = "no identity yet"; CS2M_SyncIdSystem.Allocate never
                // returns 0, so 0 is a safe sentinel) and the node POSITIONS unconditionally, so the client
                // can still resolve + heal the edge by position (ZoneBlockAuthorityApplySystem.ResolveEdge).
                // Only bail if the node itself isn't there to read a position from.
                if (!EntityManager.HasComponent<Node>(edgeData.m_Start) || !EntityManager.HasComponent<Node>(edgeData.m_End))
                {
                    continue;
                }

                ulong startId = EntityManager.HasComponent<CS2M_NodeSyncId>(edgeData.m_Start)
                    ? EntityManager.GetComponentData<CS2M_NodeSyncId>(edgeData.m_Start).m_Id
                    : 0UL;
                ulong endId = EntityManager.HasComponent<CS2M_NodeSyncId>(edgeData.m_End)
                    ? EntityManager.GetComponentData<CS2M_NodeSyncId>(edgeData.m_End).m_Id
                    : 0UL;

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

                // v66: each (edge, side) group is emitted WHOLE-or-not-at-all (never sliced by the cap), so
                // no cap short-circuit between the two sides — EmitGroup itself defers a group that doesn't
                // fit the remaining budget to the next sweep. A small group can still fill in after a big one.
                sent = EmitGroup(plusSide, edgeEntity, (sbyte) 1, startId, endId, startNode.m_Position, endNode.m_Position, sent,
                    outStartIds, outEndIds, outSides, outOrdinals, outPosX, outPosY, outPosZ, outDirX, outDirZ,
                    outSizeX, outSizeY, outCellsOffset, outCellsCount, outCellZonePool, outCellStatePool, outBuildOrders,
                    outEdgeStartXs, outEdgeStartZs, outEdgeEndXs, outEdgeEndZs, outGroupComplete, zonePool, zonePoolIndex);

                sent = EmitGroup(minusSide, edgeEntity, (sbyte) -1, startId, endId, startNode.m_Position, endNode.m_Position, sent,
                    outStartIds, outEndIds, outSides, outOrdinals, outPosX, outPosY, outPosZ, outDirX, outDirZ,
                    outSizeX, outSizeY, outCellsOffset, outCellsCount, outCellZonePool, outCellStatePool, outBuildOrders,
                    outEdgeStartXs, outEdgeStartZs, outEdgeEndXs, outEdgeEndZs, outGroupComplete, zonePool, zonePoolIndex);
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
                CellStatePool = outCellStatePool.ToArray(),
                BuildOrders = outBuildOrders.ToArray(),
                EdgeStartXs = outEdgeStartXs.ToArray(),
                EdgeStartZs = outEdgeStartZs.ToArray(),
                EdgeEndXs = outEdgeEndXs.ToArray(),
                EdgeEndZs = outEdgeEndZs.ToArray(),
                GroupComplete = outGroupComplete.ToArray(),
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

        /// <summary>v66: emits one (edge, side) group WHOLE-or-not-at-all. Folds every settled member's
        /// <see cref="ComputeSignature"/> plus the member count into a single GROUP signature; if it's
        /// unchanged since the last send the whole group is skipped, otherwise EVERY member is emitted in
        /// ordinal order (0..N-1) with <c>GroupComplete=true</c>, so the client always reconciles against the
        /// complete authoritative set. Never slices a group across the <see cref="MaxBlocksPerCommand"/> cap:
        /// a group that doesn't fit the remaining budget is left INTACT for the next sweep (its signature is
        /// not recorded, so it re-qualifies). A single group larger than the whole cap can never fit — it is
        /// logged (throttled) and skipped, the one convergence corner this model concedes (a road side with
        /// &gt;256 zone blocks is not physically reachable).</summary>
        private int EmitGroup(List<ZoneBlockMeta> ordered, Entity edgeEntity, sbyte side, ulong startId, ulong endId,
            float3 startPos, float3 endPos, int sent,
            List<ulong> outStartIds, List<ulong> outEndIds, List<sbyte> outSides, List<int> outOrdinals,
            List<float> outPosX, List<float> outPosY, List<float> outPosZ, List<float> outDirX, List<float> outDirZ,
            List<int> outSizeX, List<int> outSizeY, List<int> outCellsOffset, List<int> outCellsCount,
            List<int> outCellZonePool, List<ushort> outCellStatePool, List<uint> outBuildOrders,
            List<float> outEdgeStartXs, List<float> outEdgeStartZs, List<float> outEdgeEndXs, List<float> outEdgeEndZs,
            List<bool> outGroupComplete, List<string> zonePool, Dictionary<string, int> zonePoolIndex)
        {
            // Settled members with a Cell buffer, in the T-sorted order the caller already produced. Both the
            // signature fold and the emission iterate this SAME list, so ordinals are stable and consistent.
            var members = new List<ZoneBlockMeta>(ordered.Count);
            foreach (ZoneBlockMeta m in ordered)
            {
                if (EntityManager.HasBuffer<Cell>(m.Entity))
                {
                    members.Add(m);
                }
            }

            var key = new ZoneGroupKey { Edge = edgeEntity, Side = side };

            // Fold the group signature: every member's per-block signature (v63 buildOrder + v65 flags folded
            // in by ComputeSignature) plus the count, so an ADD/REMOVE (count change) or any single member's
            // geometry/cells/flags/order change re-qualifies the whole group for a send.
            long groupSig;
            unchecked
            {
                groupSig = 1469598103934665603L;
                foreach (ZoneBlockMeta m in members)
                {
                    uint bo = EntityManager.HasComponent<Game.Zones.BuildOrder>(m.Entity)
                        ? EntityManager.GetComponentData<Game.Zones.BuildOrder>(m.Entity).m_Order
                        : 0u;
                    DynamicBuffer<Cell> c = EntityManager.GetBuffer<Cell>(m.Entity, true);
                    groupSig = groupSig * 16777619 + ComputeSignature(m.Block, c, bo);
                }

                groupSig = groupSig * 16777619 + members.Count;
            }

            if (_lastSig.TryGetValue(key, out long prevSig) && prevSig == groupSig)
            {
                return sent; // whole group unchanged since last send — skip it entirely
            }

            if (members.Count == 0)
            {
                // Group went empty (all blocks removed on this side). An empty set carries no member to ship,
                // so the client can't learn its phantoms are gone this way (documented limitation — the real
                // cure for a side losing all zoning is the road-placement/BlockSystem cascade deleting the
                // blocks on both machines). Don't record the signature; nothing was sent.
                return sent;
            }

            if (members.Count > MaxBlocksPerCommand)
            {
                double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
                if (now - _lastTooBigLogAt >= 1.0)
                {
                    _lastTooBigLogAt = now;
                    CS2M.Log.Warn($"[ZoneAuth] GROUP-TOOBIG edge={edgeEntity.Index} side={side} " +
                                  $"blocks={members.Count} > cap {MaxBlocksPerCommand} — cannot ship whole, skipping");
                }

                return sent; // never sliceable under the cap — concede (see method doc)
            }

            if (sent + members.Count > MaxBlocksPerCommand)
            {
                return sent; // doesn't fit the remaining budget — defer WHOLE to next sweep (sig not recorded)
            }

            for (int ordinal = 0; ordinal < members.Count; ordinal++)
            {
                ZoneBlockMeta meta = members[ordinal];

                // v63 (flags issue): the game's overlap-contest tiebreak (CellOverlapJobs.cs:582) picks the
                // block with the higher Game.Zones.BuildOrder.m_Order — a per-machine local counter
                // (GenerateEdgesSystem.cs:1556-1558) that two synced machines can still disagree on. Ship
                // it so the client can adopt the host's order and tiebreak the same way.
                uint buildOrder = EntityManager.HasComponent<Game.Zones.BuildOrder>(meta.Entity)
                    ? EntityManager.GetComponentData<Game.Zones.BuildOrder>(meta.Entity).m_Order
                    : 0u;

                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(meta.Entity, true);

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
                    // v65: masked flags (see ZoneFlagMask.Stable) alongside the zone index, same offset.
                    outCellStatePool.Add((ushort) ((ushort) cells[i].m_State & ZoneFlagMask.Stable));
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
                outBuildOrders.Add(buildOrder);

                // v64: shipped for EVERY block, even when EdgeStartIds/EdgeEndIds already resolved above —
                // the client's edge resolution + block re-pairing fallback can need it regardless of whether
                // identity resolution succeeded (ZoneBlockAuthorityApplySystem.ResolveEdge).
                outEdgeStartXs.Add(startPos.x);
                outEdgeStartZs.Add(startPos.z);
                outEdgeEndXs.Add(endPos.x);
                outEdgeEndZs.Add(endPos.z);

                // v66: every emitted block belongs to a COMPLETE group by construction.
                outGroupComplete.Add(true);
            }

            _lastSig[key] = groupSig;
            sent += members.Count;
            return sent;
        }

        /// <summary>Cheap dirty-tracking hash: size + quantized position (0.1 m) + every cell's zone index
        /// + masked flags (v65 — the STABLE overlap-contest bits, ZoneFlagMask.Stable; a flags-only change
        /// with geometry/cells otherwise unchanged, e.g. this block finally settling after a neighbor's
        /// recompute, must still re-trigger a send or the client never learns the new equilibrium) +
        /// BuildOrder (v63 — a change in tiebreak order alone, with geometry/cells unchanged, must still
        /// re-trigger a send or the client never learns the new order). Not cryptographic — only needs to
        /// change whenever the shipped state changes.</summary>
        private static long ComputeSignature(Block b, DynamicBuffer<Cell> cells, uint buildOrder)
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
                    h = h * 16777619 + ((ushort) cells[i].m_State & ZoneFlagMask.Stable);
                }

                h = h * 16777619 + buildOrder;

                return h;
            }
        }
    }

    /// <summary>
    ///     ZoneBlockAuthority APPLIER (client-only, gated CS2M_ZONEAUTH, ON by default since 2026-07-07). Resolves the owning edge by
    ///     identity — falling back to node POSITION (v64) when a split-born endpoint never got a
    ///     CS2M_NodeSyncId — finds the matching local block by (side, ordinal), tolerating drift by
    ///     re-pairing to the nearest unclaimed same-side block when the ordinal pick lands too far from the
    ///     shipped position, and overwrites its Block+Cell data with the host's authoritative values so both
    ///     machines converge on the same block shape.
    /// </summary>
    public partial class ZoneBlockAuthorityApplySystem : GameSystemBase
    {
        private const int MaxRetryAttempts = 20;

        // v64: how far apart a split-junction node's position may drift between host and client before we
        // give up matching an edge/block by position (observed live: ~1-2 m; 4 m/8 m leave headroom without
        // risking a cross-street mismatch — see ZoneBlockAuthorityCommand.EdgeStartXs doc-comment).
        private const float EdgeMatchTolerance = 4f;
        private const float BlockMatchTolerance = 8f;

        // v66: greedy set-reconcile match radius. Wider than the v65 heal tolerance (8 m) because the host's
        // block and its true local counterpart can sit further apart when the SETS diverge (the derivation
        // laid the cells out differently), yet still be the same block conceptually. Anything past this is
        // treated as "no local counterpart" → CREATE, and unmatched locals within the group → DELETE.
        private const float ReconcileMatchTolerance = 16f;

        // v66: a group whose reconcile keeps making structural changes (create/delete) this many sweeps in a
        // row is fighting the local BlockSystem re-derivation rather than converging — stop touching its set
        // and fall back to heal-only, per the module-wide "better to diverge than fight" law.
        private const int MaxStructuralReconciles = 10;

        private struct PendingGroup
        {
            public ZoneBlockAuthorityCommand Cmd;
            public ulong StartId;
            public ulong EndId;
            public List<int> Indices;
            public int Attempts;
        }

        private PrefabSystem _prefabSystem;
        private EntityQuery _liveEdges;
        private readonly Queue<PendingGroup> _retryQueue = new Queue<PendingGroup>();
        private readonly Dictionary<Entity, int> _healCount = new Dictionary<Entity, int>();

        // v66: consecutive structural (create/delete>0) reconciles per (edge, side) group, and the set of
        // groups that crossed MaxStructuralReconciles and have been demoted to heal-only.
        private readonly Dictionary<ZoneGroupKey, int> _structChurn = new Dictionary<ZoneGroupKey, int>();
        private readonly HashSet<ZoneGroupKey> _healOnlyGroups = new HashSet<ZoneGroupKey>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // v64: fallback edge lookup (ResolveEdge/FindEdgeByPosition) for split-born nodes that never
            // got a CS2M_NodeSyncId — same shape as NetBatchApplySystem's _liveEdges/FindEdgeByPosition.
            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
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

            // [Guard] (lei 9 — v66.2 FIELD FIX): the v66 set-reconcile does STRUCTURAL changes on the
            // client (Instantiate a clone block, AddComponent<Deleted> a phantom). Any exception that
            // escaped here killed SystemBase.Update → ModificationSystem → the whole PROCESS — exactly
            // the "sincou a primeira rua, crashou na segunda" client crash Bruno hit (second road split
            // an edge, its zone group reconciled with a create/delete, something threw, no [Guard] to
            // catch it). Every other apply in the mod already wraps its work like this. Log the full
            // exception (this is ALSO how we finally capture the native/managed cause) and survive; the
            // next sweep re-reconciles the group.
            try
            {
                // Drain at most one command per frame from the network queue.
                if (RemoteZoneBlockQueue.TryDequeue(out ZoneBlockAuthorityCommand cmd))
                {
                    SplitAndApply(cmd);
                }

                // Plus at most one retry slot per frame (unresolved edges from earlier commands).
                RetryOne();
            }
            catch (System.Exception ex)
            {
                CS2M.Log.Info($"[Guard] zone reconcile failed (survived): {ex}");
            }
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
                var key = new EdgeIdKey
                {
                    Start = cmd.EdgeStartIds[i],
                    End = cmd.EdgeEndIds[i],
                    StartX = ArrayAt(cmd.EdgeStartXs, i),
                    StartZ = ArrayAt(cmd.EdgeStartZs, i),
                    EndX = ArrayAt(cmd.EdgeEndXs, i),
                    EndZ = ArrayAt(cmd.EdgeEndZs, i),
                };
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
            if (indices.Count == 0
                || !ResolveEdge(cmd, startId, endId, indices[0], out Entity edge, out float3 startPos, out float3 endPos))
            {
                if (attempt >= MaxRetryAttempts)
                {
                    CS2M.Log.Info($"[ZoneAuth] DROP edge unresolved start={startId} end={endId} blocks={indices.Count} after {attempt} attempts");
                    return;
                }

                _retryQueue.Enqueue(new PendingGroup { Cmd = cmd, StartId = startId, EndId = endId, Indices = indices, Attempts = attempt });
                return;
            }

            if (!EntityManager.HasBuffer<SubBlock>(edge))
            {
                return; // road exists but has no generated zone blocks yet — nothing to heal this pass
            }

            // v66.2 FIELD FIX: never RECONCILE (create/delete blocks) on an edge the game's BlockSystem is
            // still deriving THIS frame — the edge just arrived from a road batch (the "second road" split)
            // and its SubBlock set is mid-flight. Instantiating/Deleting zone blocks into that half-built
            // set is the structural conflict behind the client crash. The host already defers a settling
            // group on its side (EmitGroup settlingEdges); mirror it here. Re-queue and let the edge settle
            // (Updated/Created gone) before we touch its grid. Heal-only (v65) path is position-tolerant and
            // safe, so this defer only guards the structural reconcile.
            if (ZoneSetReconcile.Enabled && GroupIsComplete(cmd, indices)
                && (EntityManager.HasComponent<Updated>(edge) || EntityManager.HasComponent<Created>(edge)))
            {
                if (attempt < MaxRetryAttempts)
                {
                    _retryQueue.Enqueue(new PendingGroup { Cmd = cmd, StartId = startId, EndId = endId, Indices = indices, Attempts = attempt + 1 });
                }

                return;
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
                    Side = ZoneBlockGeometry.Side(startPos, endPos, blk.m_Direction),
                    T = ZoneBlockGeometry.T(startPos, endPos, blk.m_Position),
                });
            }

            // v66: if the host shipped this group COMPLETE, RECONCILE the whole set (heal matches, create
            // missing, delete phantoms) instead of the v65 per-block ordinal heal — the only path that
            // converges a SET divergence. Gated on CS2M_ZONESET; off → fall through to the v65 behaviour.
            if (ZoneSetReconcile.Enabled && GroupIsComplete(cmd, indices))
            {
                ReconcileEdge(cmd, edge, startPos, endPos, indices, candidates);
                return;
            }

            // v64: guards against one command double-claiming the same local block through two different
            // wire indices — the position-tolerant re-pairing in ApplyOne can otherwise pick the same
            // nearest-unclaimed block for two entries when the local block COUNT diverged from the host's.
            var claimed = new HashSet<Entity>();
            foreach (int idx in indices)
            {
                ApplyOne(cmd, idx, candidates, claimed);
            }
        }

        /// <summary>v66: true if ANY block in this edge-group carries <c>GroupComplete=true</c> — the host
        /// ships a group whole or not at all, so any true means the whole (edge, side) set is present and the
        /// client should reconcile the SET. Absent/short array = v65 (or older) sender → heal-only.</summary>
        private static bool GroupIsComplete(ZoneBlockAuthorityCommand cmd, List<int> indices)
        {
            if (cmd.GroupComplete == null)
            {
                return false;
            }

            foreach (int idx in indices)
            {
                if (idx < cmd.GroupComplete.Length && cmd.GroupComplete[idx])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     v66 "host owns the grid" SET reconcile. The command's blocks for this edge are grouped by SIDE
        ///     (the host's authority granularity — the OTHER side may be absent because it was unchanged and
        ///     must NOT be treated as emptied), and for each side present in the command:
        ///       1. greedy-match each shipped block (in T order) to the nearest UNCLAIMED local same-side block
        ///          within <see cref="ReconcileMatchTolerance"/> and HEAL it (struct-move guard OFF — the set
        ///          is authoritative);
        ///       2. shipped blocks with no local match → CREATE by cloning a sibling (never a hand-built
        ///          archetype — project law; the game auto-links the clone into the edge's SubBlock buffer via
        ///          <c>Created</c>+<c>Owner</c>, decomp BlockReferencesSystem.cs:34-41);
        ///       3. local same-side blocks left UNCLAIMED → DELETE as phantoms.
        ///     Convergence is by construction every sweep. Anti-oscillation: a side that keeps making
        ///     structural changes MaxStructuralReconciles sweeps running is demoted to heal-only.
        /// </summary>
        private void ReconcileEdge(ZoneBlockAuthorityCommand cmd, Entity edge, float3 startPos, float3 endPos,
            List<int> indices, List<ZoneBlockMeta> candidates)
        {
            // Which sides does this command actually carry? Only those may reconcile (delete phantoms); a side
            // the host didn't ship this time is simply unchanged, not emptied.
            var sides = new HashSet<sbyte>();
            foreach (int idx in indices)
            {
                sides.Add(cmd.Sides[idx]);
            }

            foreach (sbyte side in sides)
            {
                ReconcileSide(cmd, edge, side, startPos, endPos, indices, candidates);
            }
        }

        private void ReconcileSide(ZoneBlockAuthorityCommand cmd, Entity edge, sbyte side, float3 startPos, float3 endPos,
            List<int> indices, List<ZoneBlockMeta> candidates)
        {
            var key = new ZoneGroupKey { Edge = edge, Side = side };
            bool healOnly = _healOnlyGroups.Contains(key);

            // Shipped blocks of this side, ordered by t (same axis the host ordered on) — greedy nearest match
            // walks them in this order so equal-distance ties resolve deterministically along the road.
            var wanted = new List<int>();
            foreach (int idx in indices)
            {
                if (cmd.Sides[idx] == side)
                {
                    wanted.Add(idx);
                }
            }

            wanted.Sort((a, b) =>
            {
                float ta = ZoneBlockGeometry.T(startPos, endPos, new float3(cmd.PosX[a], cmd.PosY[a], cmd.PosZ[a]));
                float tb = ZoneBlockGeometry.T(startPos, endPos, new float3(cmd.PosX[b], cmd.PosY[b], cmd.PosZ[b]));
                return ta.CompareTo(tb);
            });

            // Local same-side candidates.
            var locals = new List<ZoneBlockMeta>();
            foreach (ZoneBlockMeta c in candidates)
            {
                if (c.Side == side)
                {
                    locals.Add(c);
                }
            }

            var claimed = new HashSet<Entity>();
            Entity siblingSameSide = Entity.Null; // a healed/kept same-side block, preferred clone source
            int created = 0;
            int deleted = 0;
            int missingNoSibling = 0;

            foreach (int idx in wanted)
            {
                var wantPos = new float3(cmd.PosX[idx], cmd.PosY[idx], cmd.PosZ[idx]);

                // Nearest unclaimed local same-side block.
                Entity match = Entity.Null;
                float bestDist = ReconcileMatchTolerance;
                foreach (ZoneBlockMeta c in locals)
                {
                    if (claimed.Contains(c.Entity))
                    {
                        continue;
                    }

                    float dist = math.distance(c.Block.m_Position, wantPos);
                    if (dist <= bestDist)
                    {
                        bestDist = dist;
                        match = c.Entity;
                    }
                }

                if (match != Entity.Null)
                {
                    claimed.Add(match);
                    if (siblingSameSide == Entity.Null)
                    {
                        siblingSameSide = match;
                    }

                    // allowStructMove=true: inside a complete set we DO move a matched block onto the host's
                    // coordinates even past 4 m — the guard's premise (a partial command over diverged
                    // structure) doesn't hold here; the set as a whole is authoritative.
                    Heal(cmd, idx, match, allowStructMove: true);
                    continue;
                }

                // No local counterpart → CREATE (heal-only groups skip this — they only fix what exists).
                if (healOnly)
                {
                    continue;
                }

                Entity sibling = siblingSameSide != Entity.Null ? siblingSameSide
                    : (candidates.Count > 0 ? candidates[0].Entity : Entity.Null);
                if (sibling == Entity.Null)
                {
                    missingNoSibling++;
                    continue;
                }

                if (CreateBlock(cmd, idx, edge, startPos, endPos, sibling))
                {
                    created++;
                }
            }

            // Phantoms: local same-side blocks the shipped set never claimed. Heal-only groups leave them.
            if (!healOnly)
            {
                foreach (ZoneBlockMeta c in locals)
                {
                    if (!claimed.Contains(c.Entity))
                    {
                        DeletePhantom(c.Entity);
                        deleted++;
                    }
                }
            }

            if (missingNoSibling > 0)
            {
                // The edge has zero local blocks to clone from — can't materialise the host's set at all.
                CS2M.Log.Info($"[ZoneAuth] CANT-CREATE edge={edge.Index} side={side} missing={missingNoSibling} " +
                              "(no local sibling block to clone) — group deferred to a later sweep");
            }

            // Anti-oscillation bookkeeping (only when this group still creates/deletes).
            if (healOnly)
            {
                return;
            }

            if (created > 0 || deleted > 0)
            {
                int churn = _structChurn.TryGetValue(key, out int ch) ? ch + 1 : 1;
                _structChurn[key] = churn;
                if (churn > MaxStructuralReconciles)
                {
                    _healOnlyGroups.Add(key);
                    _structChurn.Remove(key);
                    CS2M.Log.Warn($"[ZoneAuth] GROUP-OSCILLATE edge={edge.Index} side={side} " +
                                  $"structural-reconciles={churn} — demoting to heal-only (local BlockSystem keeps re-deriving)");
                }
                else
                {
                    CS2M.Log.Info($"[ZoneAuth] RECONCILE edge={edge.Index} side={side} " +
                                  $"created={created} deleted={deleted} matched={claimed.Count}");
                }
            }
            else
            {
                _structChurn.Remove(key); // settled this sweep — reset the streak
            }
        }

        /// <summary>v66: create a block the host has but this machine lacks, by CLONING a local sibling zone
        /// block (<see cref="EntityManager.Instantiate(Entity)"/>) so it inherits the exact ZoneBlock
        /// archetype/prefab — never a hand-assembled archetype (project law). Then overwrite Block geometry,
        /// Owner (the resolved edge), BuildOrder and cells with the host's values, stamp <c>Created</c> so the
        /// game links it into the edge's SubBlock buffer (decomp BlockReferencesSystem.cs:34-41 adds a block
        /// with Created+Owner) and <c>Updated</c> so CellCheckSystem lights its cells, and mirror Heal's
        /// post-write bookkeeping (DeferredUpdated + ZoneEcho + snapshot/flag ledgers). Returns true if a
        /// block was created.</summary>
        private bool CreateBlock(ZoneBlockAuthorityCommand cmd, int idx, Entity edge, float3 startPos, float3 endPos, Entity sibling)
        {
            if (!EntityManager.Exists(sibling))
            {
                return false;
            }

            int sizeX = cmd.SizeX[idx];
            int sizeY = cmd.SizeY[idx];
            int offset = cmd.CellsOffset[idx];
            int count = cmd.CellsCount[idx];
            var pos = new float3(cmd.PosX[idx], cmd.PosY[idx], cmd.PosZ[idx]);

            // Resolve wanted zones/flags up front (local index space).
            var wantZoneIndex = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                wantZoneIndex[i] = ZoneSync.Index(cmd.ZonePool[cmd.CellZonePool[offset + i]]);
            }

            bool haveFlags = cmd.CellStatePool != null && offset + count <= cmd.CellStatePool.Length;
            ushort[] wantFlags = null;
            if (haveFlags)
            {
                wantFlags = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    wantFlags[i] = (ushort) (cmd.CellStatePool[offset + i] & ZoneFlagMask.Stable);
                }
            }

            // Clone the sibling — inherits PrefabRef, the ZoneBlock archetype, Cell buffer, CurvePosition,
            // ValidArea, BuildOrder, Owner, etc. We then overwrite the authoritative pieces.
            Entity clone = EntityManager.Instantiate(sibling);

            EntityManager.SetComponentData(clone, new Block
            {
                m_Position = pos,
                m_Direction = new float2(cmd.DirX[idx], cmd.DirZ[idx]),
                m_Size = new int2(sizeX, sizeY),
            });

            // Owner = the resolved edge (the sibling may be from ANOTHER edge in the empty-side fallback, so
            // set it explicitly rather than trusting the inherited value). Instantiate copied an Owner, so it
            // exists — SetComponentData is safe.
            EntityManager.SetComponentData(clone, new Owner { m_Owner = edge });

            // Authoritative BuildOrder (v63 tiebreak). Guarded — older senders omit it.
            if (cmd.BuildOrders != null && idx < cmd.BuildOrders.Length)
            {
                var bo = new Game.Zones.BuildOrder { m_Order = cmd.BuildOrders[idx] };
                if (EntityManager.HasComponent<Game.Zones.BuildOrder>(clone))
                {
                    EntityManager.SetComponentData(clone, bo);
                }
                else
                {
                    EntityManager.AddComponentData(clone, bo);
                }
            }

            // CurvePosition APPROXIMATION. The game's CurvePosition.m_CurvePosition (decomp CurvePosition.cs)
            // is a float2 = the block's [t_start, t_end] PARAMETRIC span along the owning edge's bezier in
            // [0,1] (BlockSystem.cs:957-959 sets it from lerp of the curve fractions, inverted for the left
            // side). The host doesn't ship it, so we approximate: project the block CENTER onto the shipped
            // straight start->end chord to a single scalar t and store it as a degenerate zero-width span
            // (both components = t). This ignores the block's real footprint width along the curve and the
            // bezier's curvature, so it is only APPROXIMATE — acceptable because (a) CurvePosition mainly
            // orders zone-spawn building placement along the road, not cell validity, and (b) the local
            // BlockSystem re-derivation, whenever the owning edge next gets Updated, overwrites it with the
            // exact value anyway (this clone is a best-effort placeholder until then).
            if (EntityManager.HasComponent<CurvePosition>(clone))
            {
                float2 axis = endPos.xz - startPos.xz;
                float denom = math.dot(axis, axis);
                float t = denom > 1e-6f
                    ? math.clamp(math.dot(pos.xz - startPos.xz, axis) / denom, 0f, 1f)
                    : 0f;
                EntityManager.SetComponentData(clone, new CurvePosition { m_CurvePosition = new float2(t, t) });
            }

            // Created → BlockReferencesSystem adds this block to the edge's SubBlock buffer next frame (query
            // Block+Owner Any[Created,Deleted]; decomp BlockReferencesSystem.cs:34-41). Without it the clone
            // would be invisible to the next sweep's candidate collection and get re-created every sweep — a
            // duplicate leak. CleanUpSystem strips Created afterwards, so it's a one-shot registration.
            if (!EntityManager.HasComponent<Created>(clone))
            {
                EntityManager.AddComponent<Created>(clone);
            }

            if (!EntityManager.HasComponent<Updated>(clone))
            {
                EntityManager.AddComponent<Updated>(clone);
            }

            // v66.5 CRASH FIX: re-stamp CREATED (not just Updated) next frame — SearchSystem@Mod5 runs
            // BEFORE this applier@Mod5, so it never sees the clone's Created this frame; a plain Updated
            // re-stamp then drives it into SearchSystem's Update branch on a block never Added → Burst
            // "Item not found (NativeQuadTree.Update)" → process crash. DeferredCreated re-stamps Created
            // so SearchSystem ADDs it next frame instead. See DeferredCreated's doc for the full chain.
            DeferredCreated.Enqueue(clone);

            // Authoritative cells — fresh buffer AFTER the structural AddComponents above (handle safety, same
            // lesson as Heal). Height left for CellCheckSystem to recompute; flags seeded with the host's.
            DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(clone);
            cells.ResizeUninitialized(sizeX * sizeY);
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = new Cell
                {
                    m_State = i < count && haveFlags ? (CellFlags) wantFlags[i] : default,
                    m_Zone = new ZoneType { m_Index = i < count ? wantZoneIndex[i] : (ushort) 0 },
                    m_Height = short.MaxValue,
                };
            }

            // Post-write bookkeeping, exactly as Heal does.
            ZoneEcho.Mark(clone);
            ZoneSync.Snapshot[clone] = wantZoneIndex;
            if (haveFlags)
            {
                ZoneFlagAssert.Set(clone, wantFlags);
            }

            CS2M.Log.Info($"[ZoneAuth] CREATE block=({cmd.PosX[idx]:F0},{cmd.PosZ[idx]:F0}) {sizeX}x{sizeY} " +
                          $"cells={count} edge={edge.Index} (cloned {sibling.Index})");
            return true;
        }

        /// <summary>v66: delete a local phantom zone block the host's authoritative set has no slot for.
        /// Zone blocks do NOT pass through the mod's delete detectors — DeleteDetectorSystem's queries all
        /// require Static/Object/Building (and None[Owner]) or a Route or a CS2M_SyncId'd extension
        /// (DeleteDetectorSystem.cs:61-150); a road-owned zone block is none of those — so adding Deleted here
        /// cannot bounce a DeleteCommand back to the host (no echo). The game's own BlockReferencesSystem
        /// removes it from the edge's SubBlock buffer (Deleted+Owner; decomp BlockReferencesSystem.cs:43-51)
        /// and CleanUpSystem destroys it.</summary>
        private void DeletePhantom(Entity block)
        {
            if (!EntityManager.Exists(block) || EntityManager.HasComponent<Deleted>(block))
            {
                return;
            }

            EntityManager.AddComponent<Deleted>(block);
            ZoneEcho.Mark(block);
            ZoneSync.Snapshot.Remove(block);
            _healCount.Remove(block);

            lock (ZoneFlagAssert.Lock)
            {
                ZoneFlagAssert.Want.Remove(block);
            }

            CS2M.Log.Info($"[ZoneAuth] DELETE phantom block={block.Index}:{block.Version}");
        }

        /// <summary>Resolves the owning edge and the CANONICAL (startPos, endPos) axis — the same axis
        /// order the host used to compute Side/T — for one group of block entries. Identity first
        /// (<see cref="FindEdgeById"/>, using the local node entities so a synced-but-drifted node's
        /// CURRENT position is used); falls back to POSITION (v64) when either id is 0 or identity
        /// resolution otherwise fails — the case a split-born node with no CS2M_NodeSyncId hits every time
        /// (see ZoneBlockAuthoritySystem.Sweep). A flipped position match still returns the axis in the
        /// host's (start, end) order — never the local edge's own m_Start/m_End — because a flipped axis
        /// inverts BOTH side and t and would silently heal the wrong blocks.</summary>
        private bool ResolveEdge(ZoneBlockAuthorityCommand cmd, ulong startId, ulong endId, int firstIdx,
            out Entity edge, out float3 startPos, out float3 endPos)
        {
            if (FindEdgeById(startId, endId, out edge))
            {
                CS2M_NodeSyncIds.TryResolve(EntityManager, startId, out Entity startEntity);
                CS2M_NodeSyncIds.TryResolve(EntityManager, endId, out Entity endEntity);
                startPos = EntityManager.GetComponentData<Node>(startEntity).m_Position;
                endPos = EntityManager.GetComponentData<Node>(endEntity).m_Position;
                return true;
            }

            edge = Entity.Null;
            startPos = default;
            endPos = default;

            if (!TryGetShippedEdgePositions(cmd, firstIdx, out float3 shipStart, out float3 shipEnd))
            {
                return false; // pre-v64 sender (or malformed command) — nothing to fall back to
            }

            return FindEdgeByPosition(shipStart, shipEnd, out edge, out startPos, out endPos);
        }

        /// <summary>v64 fallback edge lookup for a node that never got a CS2M_NodeSyncId (split-born —
        /// see ZoneBlockAuthoritySystem.Sweep). Scans every LIVE edge (no Temp/Deleted) and matches its two
        /// endpoint NODE positions against the shipped ones, in EITHER orientation, within
        /// <see cref="EdgeMatchTolerance"/>. Exactly one candidate wins; 0 or 2+ refuses rather than guess
        /// — mirrors NetBatchApplySystem.FindEdgeByPosition. On a match, returns (startPos, endPos) in the
        /// SAME order as the shipped (shipStart, shipEnd), using the LOCAL node positions (so a synced but
        /// drifted node still contributes its own current position to the axis, not the host's).</summary>
        private bool FindEdgeByPosition(float3 shipStart, float3 shipEnd, out Entity edge, out float3 startPos, out float3 endPos)
        {
            edge = Entity.Null;
            startPos = default;
            endPos = default;

            int matches = 0;
            Entity found = Entity.Null;
            bool foundFlipped = false;

            NativeArray<Entity> arr = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in arr)
                {
                    Edge ed = EntityManager.GetComponentData<Edge>(e);
                    if (!EntityManager.Exists(ed.m_Start) || !EntityManager.Exists(ed.m_End)
                        || !EntityManager.HasComponent<Node>(ed.m_Start) || !EntityManager.HasComponent<Node>(ed.m_End))
                    {
                        continue;
                    }

                    float3 sPos = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
                    float3 ePos = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;

                    bool straight = math.distance(sPos.xz, shipStart.xz) <= EdgeMatchTolerance
                        && math.distance(ePos.xz, shipEnd.xz) <= EdgeMatchTolerance;
                    bool flipped = math.distance(sPos.xz, shipEnd.xz) <= EdgeMatchTolerance
                        && math.distance(ePos.xz, shipStart.xz) <= EdgeMatchTolerance;
                    if (straight || flipped)
                    {
                        matches++;
                        found = e;
                        foundFlipped = flipped && !straight;
                        if (matches > 1)
                        {
                            break; // already ambiguous — no need to keep scanning
                        }
                    }
                }
            }
            finally
            {
                arr.Dispose();
            }

            if (matches == 0)
            {
                return false; // not found (yet) — caller retries like any other unresolved edge
            }

            if (matches > 1)
            {
                CS2M.Log.Info($"[ZoneAuth] AMBIG edge matches={matches} " +
                              $"start=({shipStart.x:F0},{shipStart.z:F0}) end=({shipEnd.x:F0},{shipEnd.z:F0}) — refusing to guess");
                return false;
            }

            Edge foundEdge = EntityManager.GetComponentData<Edge>(found);
            startPos = EntityManager.GetComponentData<Node>(foundFlipped ? foundEdge.m_End : foundEdge.m_Start).m_Position;
            endPos = EntityManager.GetComponentData<Node>(foundFlipped ? foundEdge.m_Start : foundEdge.m_End).m_Position;
            edge = found;
            return true;
        }

        /// <summary>Safe indexed read of a nullable/possibly-short parallel array — 0f when absent (older
        /// sender, or index out of range).</summary>
        private static float ArrayAt(float[] arr, int i) => arr != null && i < arr.Length ? arr[i] : 0f;

        private static bool TryGetShippedEdgePositions(ZoneBlockAuthorityCommand cmd, int idx, out float3 start, out float3 end)
        {
            start = default;
            end = default;
            if (cmd.EdgeStartXs == null || cmd.EdgeStartZs == null || cmd.EdgeEndXs == null || cmd.EdgeEndZs == null
                || idx >= cmd.EdgeStartXs.Length || idx >= cmd.EdgeStartZs.Length
                || idx >= cmd.EdgeEndXs.Length || idx >= cmd.EdgeEndZs.Length)
            {
                return false;
            }

            start = new float3(cmd.EdgeStartXs[idx], 0f, cmd.EdgeStartZs[idx]);
            end = new float3(cmd.EdgeEndXs[idx], 0f, cmd.EdgeEndZs[idx]);
            return true;
        }

        private void ApplyOne(ZoneBlockAuthorityCommand cmd, int idx, List<ZoneBlockMeta> candidates, HashSet<Entity> claimed)
        {
            sbyte wantSide = cmd.Sides[idx];
            int wantOrdinal = cmd.Ordinals[idx];
            var wantPos = new float3(cmd.PosX[idx], cmd.PosY[idx], cmd.PosZ[idx]);

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
                ZoneBlockMeta ordinalPick = sameSide[wantOrdinal];
                // v64: the ordinal alone is no longer trustworthy — a split-junction position drift of a
                // couple metres is enough to shift block ordinals when the local/host block COUNTS also
                // differ (host 238 vs client 240 in the live repro). Validate the pick's actual position
                // before trusting it, same tolerance as the position re-pairing fallback below.
                if (!claimed.Contains(ordinalPick.Entity) && math.distance(ordinalPick.Block.m_Position, wantPos) <= BlockMatchTolerance)
                {
                    target = ordinalPick.Entity;
                }
            }

            if (target == Entity.Null)
            {
                // Fallback: nearest UNCLAIMED same-side block by actual position, still capped at
                // BlockMatchTolerance so a diverged count never silently heals an unrelated block (v64 —
                // supersedes the old nearest-t fallback, which trusted ordinal-space distance instead of
                // real-world distance and had no double-claim guard).
                float bestDist = BlockMatchTolerance;
                foreach (ZoneBlockMeta c in sameSide)
                {
                    if (claimed.Contains(c.Entity))
                    {
                        continue;
                    }

                    float dist = math.distance(c.Block.m_Position, wantPos);
                    if (dist <= bestDist)
                    {
                        bestDist = dist;
                        target = c.Entity;
                    }
                }
            }

            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[ZoneAuth] MISS side={wantSide} ordinal={wantOrdinal} at=({cmd.PosX[idx]:F0},{cmd.PosZ[idx]:F0})");
                return;
            }

            claimed.Add(target);
            Heal(cmd, idx, target, allowStructMove: false);
        }

        /// <summary><paramref name="allowStructMove"/> false (v65 legacy heal): keep the STRUCT-DIV guard that
        /// refuses to move a block more than 4 m — a partial command over a diverged local structure would
        /// smear the block over neighbours. true (v66 complete-set reconcile): the guard is dropped because
        /// the whole (edge, side) SET is authoritative here and we've already decided, by greedy proximity,
        /// that this local block IS the host's block; moving it onto the host coords is the correct heal.</summary>
        private void Heal(ZoneBlockAuthorityCommand cmd, int idx, Entity target, bool allowStructMove)
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

            // v65: resolve the wanted (masked) cell flags the same way. Guard against null/short
            // CellStatePool (peer running an older build without the flags authority) — that peer never
            // shipped flags, so there's nothing to compare/adopt and flagsMatch trivially holds.
            bool haveFlags = cmd.CellStatePool != null && offset + count <= cmd.CellStatePool.Length;
            ushort[] wantFlags = null;
            if (haveFlags)
            {
                wantFlags = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    wantFlags[i] = (ushort) (cmd.CellStatePool[offset + i] & ZoneFlagMask.Stable);
                }
            }

            Block localBlock = EntityManager.GetComponentData<Block>(target);
            bool sizeMatches = localBlock.m_Size.x == sizeX && localBlock.m_Size.y == sizeY;
            bool cellsMatch = false;
            bool flagsMatch = !haveFlags;
            if (sizeMatches && EntityManager.HasBuffer<Cell>(target))
            {
                DynamicBuffer<Cell> localCells = EntityManager.GetBuffer<Cell>(target, true);
                if (localCells.Length == count)
                {
                    cellsMatch = true;
                    bool flagsOk = true;
                    for (int i = 0; i < count; i++)
                    {
                        if (localCells[i].m_Zone.m_Index != wantZoneIndex[i])
                        {
                            cellsMatch = false;
                        }

                        if (haveFlags && ((ushort) localCells[i].m_State & ZoneFlagMask.Stable) != wantFlags[i])
                        {
                            flagsOk = false;
                        }
                    }

                    if (haveFlags)
                    {
                        flagsMatch = flagsOk;
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

            // v65.1 STRUCT-DIV GUARD (field, 07/07 night): when the owning edge itself split at a
            // MATERIALLY different point on this machine (observed 48 m in the live repro), the local
            // derivation near it is a different structure, not a drifted copy. Healing such a block —
            // moving it metres onto the host's coordinates — stacks it over this machine's own
            // neighboring blocks: two Visible grids overlap on screen (worse than clean divergence).
            // Leave structurally-diverged blocks alone; the radar keeps reporting the region and the
            // real cure is the road-placement path converging (AtomicBatch), not cosmetic smearing.
            // v66: the guard is legacy-path only (allowStructMove=false). In a complete-set reconcile the set
            // is authoritative, so a matched block IS moved onto the host's coordinates (see Heal's summary).
            float structDelta = math.distance(localBlock.m_Position.xz,
                new float2(cmd.PosX[idx], cmd.PosZ[idx]));
            if (!allowStructMove && structDelta > 4f)
            {
                CS2M.Log.Info($"[ZoneAuth] SKIP structdiv block=({cmd.PosX[idx]:F0},{cmd.PosZ[idx]:F0}) " +
                              $"delta={structDelta:F1}m (edge split diverged — not healing over local structure)");
                return;
            }

            // v63 (flags issue): BuildOrder is the FINAL cell-overlap tiebreak (decomp CellOverlapJobs.cs:582),
            // so a divergent order alone (geometry/cells otherwise converged) still needs a heal or the two
            // machines keep picking different winners at every overlap. Older senders omit BuildOrders
            // entirely (null/short array) — treat that as "nothing to adopt" so old-vs-new peers stay idempotent.
            bool orderMatches = cmd.BuildOrders == null || idx >= cmd.BuildOrders.Length
                || (EntityManager.HasComponent<Game.Zones.BuildOrder>(target)
                    && EntityManager.GetComponentData<Game.Zones.BuildOrder>(target).m_Order == cmd.BuildOrders[idx]);

            if (sizeMatches && cellsMatch && posMatches && orderMatches && flagsMatch)
            {
                return; // already converged — silent skip (idempotent)
            }

            // Issue #4: a cell painted THIS frame (game tool writes at Mod4; we run at Mod5) hasn't
            // been seen by ZoneDetectorSystem (ModificationEnd) yet — the shared snapshot still holds
            // the pre-paint state. Overwriting now would revert the paint on screen AND absorb it as
            // baseline, so it would never ship. Defer instead: the paint round-trips to the host and
            // the next authority sweep (≤250 ms) re-broadcasts this block with the edit merged in.
            if (ZoneSync.Snapshot.TryGetValue(target, out ushort[] snap) && EntityManager.HasBuffer<Cell>(target))
            {
                DynamicBuffer<Cell> curCells = EntityManager.GetBuffer<Cell>(target, true);
                if (snap.Length == curCells.Length)
                {
                    for (int i = 0; i < snap.Length; i++)
                    {
                        if (curCells[i].m_Zone.m_Index != snap[i])
                        {
                            CS2M.Log.Info($"[ZoneAuth] DEFER heal block=({cmd.PosX[idx]:F0},{cmd.PosZ[idx]:F0}) " +
                                          "— local edit pending detection");
                            return;
                        }
                    }
                }
            }

            // v66.6 CRASH FIX (proven from a native dump: c0000005 null-deref in game Burst job
            // c9b7ca65, client crashed on a frame full of "HEAL block NxM->N'xM'" size changes).
            // RESIZING a zone block (m_Size + ResizeUninitialized of the Cell buffer) OUT-OF-BAND from
            // the game's BlockSystem corrupts the state a Burst geometry job consumes → whole-process
            // abort (same class as the node-teleport crash). A different local block SIZE is a symptom
            // of a divergent junction (the road derived a different block layout here), NOT something we
            // can force by rewriting geometry — only the game may resize a block. So: if the size does
            // not match, DO NOT heal this block (no resize, no position/size rewrite). We still heal
            // same-size blocks (zone indices + flags + BuildOrder + sub-metre position), which is the
            // safe, non-structural cure. Divergent-size blocks are left to the radar; the real fix is
            // deterministic junction geometry so blocks derive identically in the first place.
            if (!sizeMatches)
            {
                CS2M.Log.Info($"[ZoneAuth] SKIP resize block=({cmd.PosX[idx]:F0},{cmd.PosZ[idx]:F0}) " +
                              $"local={localBlock.m_Size.x}x{localBlock.m_Size.y} host={sizeX}x{sizeY} " +
                              "— refusing to resize a zone block out-of-band (would corrupt Burst geometry → crash)");
                return;
            }

            int oldW = localBlock.m_Size.x;
            int oldH = localBlock.m_Size.y;

            // 1) Authoritative Block geometry (no structural change — safe before AddComponent below).
            // Size is guaranteed == local here (guard above), so this is a position/direction nudge only,
            // never a resize.
            EntityManager.SetComponentData(target, new Block
            {
                m_Position = new float3(cmd.PosX[idx], cmd.PosY[idx], cmd.PosZ[idx]),
                m_Direction = new float2(cmd.DirX[idx], cmd.DirZ[idx]),
                m_Size = new int2(sizeX, sizeY),
            });

            // 1b) Authoritative BuildOrder (v63): adopt the host's cell-overlap tiebreak value (decomp
            // CellOverlapJobs.cs:582 — higher m_Order wins the contest; the per-machine counter that
            // produces it is GenerateEdgesSystem.cs:1556-1558) so the local recompute breaks ties the same
            // way the host does. Guarded — older senders omit BuildOrders, skip silently.
            if (cmd.BuildOrders != null && idx < cmd.BuildOrders.Length)
            {
                var buildOrder = new Game.Zones.BuildOrder { m_Order = cmd.BuildOrders[idx] };
                if (EntityManager.HasComponent<Game.Zones.BuildOrder>(target))
                {
                    EntityManager.SetComponentData(target, buildOrder);
                }
                else
                {
                    EntityManager.AddComponentData(target, buildOrder);
                }
            }

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
            // Height is left for the local CellCheckSystem to recompute on the Updated pass that follows
            // — the same pipeline a locally-derived block goes through. Flags (v65) are SEEDED with the
            // host's masked state up front instead of left at default/0: CellOverlapJobs' contest has
            // hysteresis (an already-resolved/Visible cell wins, see ZoneBlockAuthorityCommand.
            // CellStatePool's doc-comment) — writing the correct answer BEFORE the Updated-triggered
            // recompute runs gives the local contest a chance to latch onto it instead of re-deriving a
            // different equilibrium from scratch. This is a best-effort nudge, not a guarantee, which is
            // why ZoneFlagAssertSystem below keeps re-checking on its own cadence.
            DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(target);
            cells.ResizeUninitialized(sizeX * sizeY);
            for (int i = 0; i < cells.Length; i++)
            {
                // Beyond `count` (only possible if a malformed command ships count != sizeX*sizeY) the
                // resized buffer would otherwise hold UNINITIALIZED memory — always write every cell.
                cells[i] = new Cell
                {
                    m_State = i < count && haveFlags ? (CellFlags) wantFlags[i] : default,
                    m_Zone = new ZoneType { m_Index = i < count ? wantZoneIndex[i] : (ushort) 0 },
                    m_Height = short.MaxValue,
                };
            }

            // 4) Anti-echo: mark + refresh the shared zoning snapshot so ZoneDetectorSystem's own diff
            // doesn't see this heal as a local edit and bounce it back.
            ZoneEcho.Mark(target);
            ZoneSync.Snapshot[target] = wantZoneIndex;

            // v65: seed/refresh the continuous flags ledger so ZoneFlagAssertSystem keeps re-asserting
            // this block's flags on its own cadence even if the local recompute above re-diverges them
            // without any new command ever arriving (ZoneFlagAssert's doc-comment explains why a one-shot
            // write isn't enough). Guarded — nothing to watch when the peer never shipped flags.
            if (haveFlags)
            {
                ZoneFlagAssert.Set(target, wantFlags);
            }

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

    /// <summary>
    ///     v65 flags-authority ENFORCER (client-only, gated CS2M_ZONEFLAGS &amp;&amp; CS2M_ZONEAUTH). Every
    ///     <see cref="AssertEveryNFrames"/> frames, walks <see cref="ZoneFlagAssert.Want"/> (the desired
    ///     masked flags left behind by <see cref="ZoneBlockAuthorityApplySystem"/>.Heal) and re-writes ONLY
    ///     the <see cref="ZoneFlagMask.Stable"/> bits of any cell that drifted, preserving every locally-
    ///     owned bit (Occupied/Selected/Updating). Deliberately does NOT stamp <c>Updated</c> — that would
    ///     re-trigger CellCheckSystem's own recompute, which is the exact per-machine hysteresis this
    ///     system exists to override, turning a fix into an infinite fight. <c>BatchesUpdated</c> alone
    ///     (the same "refresh the render batch without re-deriving" component NetPlaceApplySystem.
    ///     MarkUpdated already uses) is enough to make the correction visible on screen.
    /// </summary>
    public partial class ZoneFlagAssertSystem : GameSystemBase
    {
        // Same cadence as the detector sweep (~250 ms @60fps) — flags only settle when SOME block/
        // neighbor gets Updated (decomp CellCheckSystem.cs:186), so there is no benefit to checking more
        // often than that, and every frame would mean a full Want-dictionary walk for nothing.
        private const int AssertEveryNFrames = 15;

        // v65: a block that needs correcting more than this many times is fighting some OTHER local
        // recompute (e.g. a neighbor still churning) rather than converging — per the module-wide
        // "better to diverge than to fight" lesson, stop re-asserting it and let it be.
        private const int MaxReassertsPerBlock = 20;

        private int _frameCounter;
        private double _lastLogAt;
        private int _assertedSinceLog;
        private readonly Dictionary<Entity, int> _reassertCount = new Dictionary<Entity, int>();

        protected override void OnCreate()
        {
            base.OnCreate();
            CS2M.Log.Info("[ZoneAuth] ZoneFlagAssertSystem created");
        }

        protected override void OnUpdate()
        {
            if (!ZoneFlagAuthority.Enabled || !ZoneAuthority.Enabled)
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

            if (++_frameCounter < AssertEveryNFrames)
            {
                return;
            }

            _frameCounter = 0;
            AssertAll();
        }

        private void AssertAll()
        {
            // Snapshot the keys up front — the loop body may remove entries (stale/deleted/oscillating)
            // from the SAME dictionary, which a live enumeration would throw on.
            Entity[] targets;
            lock (ZoneFlagAssert.Lock)
            {
                targets = new Entity[ZoneFlagAssert.Want.Count];
                ZoneFlagAssert.Want.Keys.CopyTo(targets, 0);
            }

            foreach (Entity target in targets)
            {
                AssertOne(target);
            }

            if (_assertedSinceLog <= 0)
            {
                return;
            }

            // Same 1/s throttle style as ZoneBlockAuthoritySystem.Sweep's SEND log — a big drift burst
            // (e.g. right after join) would otherwise spam one line per block per 250 ms.
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now - _lastLogAt < 1.0)
            {
                return;
            }

            _lastLogAt = now;
            CS2M.Log.Info($"[ZoneAuth] FLAG-ASSERT n={_assertedSinceLog} blocks");
            _assertedSinceLog = 0;
        }

        private static void RemoveWant(Entity target)
        {
            lock (ZoneFlagAssert.Lock)
            {
                ZoneFlagAssert.Want.Remove(target);
            }
        }

        private void AssertOne(Entity target)
        {
            if (!EntityManager.Exists(target) || EntityManager.HasComponent<Deleted>(target))
            {
                RemoveWant(target);
                _reassertCount.Remove(target);
                return;
            }

            ushort[] want;
            lock (ZoneFlagAssert.Lock)
            {
                if (!ZoneFlagAssert.Want.TryGetValue(target, out want))
                {
                    return; // removed by a concurrent path (e.g. Heal reseeding) between snapshot and now
                }
            }

            if (!EntityManager.HasBuffer<Cell>(target))
            {
                return;
            }

            DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(target);

            // The block's own geometry heal reseeds Want (ZoneBlockAuthorityApplySystem.Heal) whenever it
            // runs — if the cell count no longer matches, this entry is stale (superseded by a newer heal
            // that hasn't reseeded yet, or the block was resized locally); drop it rather than write
            // out-of-alignment data. A future Heal (or the next authority sweep) will reseed correctly.
            if (cells.Length != want.Length)
            {
                RemoveWant(target);
                _reassertCount.Remove(target);
                return;
            }

            bool anyMismatch = false;
            for (int i = 0; i < cells.Length; i++)
            {
                Cell c = cells[i];
                ushort local = (ushort) c.m_State;
                if ((local & ZoneFlagMask.Stable) == want[i])
                {
                    continue;
                }

                anyMismatch = true;
                // Preserve every bit OUTSIDE the mask (Occupied/Selected/Updating are locally owned) and
                // adopt the host's value for every bit INSIDE it.
                ushort merged = (ushort) ((local & ~ZoneFlagMask.Stable) | want[i]);
                c.m_State = (CellFlags) merged;
                cells[i] = c;
            }

            if (!anyMismatch)
            {
                return;
            }

            int count = _reassertCount.TryGetValue(target, out int c0) ? c0 + 1 : 1;
            _reassertCount[target] = count;

            if (count > MaxReassertsPerBlock)
            {
                CS2M.Log.Warn($"[ZoneAuth] FLAG-OSCILLATE block={target.Index}:{target.Version} " +
                              $"reasserts={count} — desistindo (jogo local mantém equilibrio proprio)");
                RemoveWant(target);
                _reassertCount.Remove(target);
                return;
            }

            // Refresh the render batch WITHOUT stamping Updated — see the class doc-comment for why
            // Updated here would re-trigger the very recompute this system is correcting.
            if (!EntityManager.HasComponent<BatchesUpdated>(target))
            {
                EntityManager.AddComponent<BatchesUpdated>(target);
            }

            _assertedSinceLog++;
        }
    }
}
