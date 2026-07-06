using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    /// Kills cross-machine <see cref="Game.Zones.BuildOrder"/> ties AT THE SOURCE.
    ///
    /// ROOT CAUSE (decomp-proven): <c>BlockSystem</c>'s block-derivation job computes
    /// <c>BuildOrder.m_Order</c> as <c>max(own edge order, nearest-neighbor edge order)</c>
    /// (decomp/Game/Game/Zones/BlockSystem.cs:190 `buildOrder2 = math.max(buildOrder.m_Start,
    /// buildOrder.m_End)`; :285-288 `buildOrder = math.max(buildOrder, math.max(componentData
    /// .m_Start, componentData.m_End))`; written out at :403-404 `component.m_Order = buildOrder`).
    /// Two ADJACENT zone blocks can legitimately fold to the exact same neighbor edge and end up
    /// with the SAME m_Order — measured live: two neighboring blocks both o7327. When two blocks
    /// with a tied m_Order contest the same overlapping cells, <c>CellCheckHelpers.BlockOverlap
    /// .CompareTo</c> (decomp/Game/Game/Zones/CellCheckHelpers.cs:38-43) breaks the tie, in order,
    /// by (1) m_Group, (2) m_Priority (== BuildOrder.m_Order), and ONLY if both are still tied,
    /// (3) `m_Block.Index - other.m_Block.Index` — Entity.Index, which is per-machine (allocation
    /// order differs between host and client) and never synced. Net effect: the SAME genuine tie
    /// resolves in opposite directions on the two machines -> a cell is Visible on one PC and
    /// Blocked on the other, with nothing to Harmony-patch (the whole contest is a single Burst
    /// job graph inside CellCheckSystem.OnUpdate).
    ///
    /// FIX: re-stamp `m_Order = (orderBase &lt;&lt; 8) | (posHash &amp; 0xFF)` for every zone block
    /// CellCheckSystem is about to re-contest THIS frame, where posHash is a deterministic FNV-1a
    /// hash of the block's OWN position (quantized 0.5 m — same recipe as StateHashSystems.cs's
    /// `Pt()`), identical on both machines for the same block. The 8-bit left-shift preserves the
    /// PRIMARY order (a genuinely newer road still always wins, since a higher orderBase always
    /// beats a lower one regardless of the appended byte); the appended byte only ever decides
    /// what used to be a coin-flip. `Entity.Index - Entity.Index` in CompareTo is exercised
    /// exactly as before, but the two operands (m_Priority, which is m_Order) it was originally
    /// meant to be a last resort FOR are no longer tied for two DIFFERENT blocks whose positions
    /// differ (a hash collision on the low byte is still possible in principle, but then it's a
    /// true residual tie decided by Entity.Index same as any other still-tied byte — no worse than
    /// today, and vastly less frequent since it now needs both the 24-bit orderBase AND the 8-bit
    /// posHash to collide).
    ///
    /// PHASE (decomp-proven, not guessed): BlockSystem is registered at
    /// `SystemUpdatePhase.Modification4` (decomp/Game/Game/Common/SystemOrder.cs:156) and writes
    /// the raw m_Order there via its own EntityCommandBuffer against `ModificationBarrier4`
    /// (BlockSystem.OnCreate: `m_ModificationBarrier = World.GetOrCreateSystemManaged
    /// &lt;ModificationBarrier4&gt;()`). CellCheckSystem is registered at
    /// `SystemUpdatePhase.Modification5` (SystemOrder.cs:215) and its OnUpdate schedules the WHOLE
    /// overlap-contest job chain (CollectUpdatedBlocks -> CellBlockJobs.BlockCellsJob ->
    /// CellCheckHelpers.FindOverlappingBlocksJob -> GroupOverlappingBlocksJob -> ...) in that same
    /// phase, reading BuildOrder.m_Order fresh via ComponentLookup each time
    /// (CellCheckHelpers.cs:227,239,256,281,311,330 — `value.m_Priority = buildOrderData.m_Order`).
    /// `SystemUpdatePhase` (decomp/Game/Game/SystemUpdatePhase.cs) enumerates phases in strict
    /// order and lists `Modification4B` BETWEEN `Modification4` and `Modification5` — a phase
    /// boundary, not a job-graph internal to either system. `Game.UpdateSystem.Update(phase)`
    /// (decomp/Game/Game/UpdateSystem.cs) runs phases sequentially and drains each phase's
    /// modification barrier before the next phase starts, so by the time Modification4B runs,
    /// BlockSystem's ECB writes from Modification4 are already applied to the real components, and
    /// CellCheckSystem@Modification5 has not yet read anything this frame. THIS IS NOT the
    /// "same-phase job chain" worst case the spec anticipated needing a 1-frame-lag workaround for
    /// — Modification4B gives an exact, race-free seam. Registered via
    /// `updateSystem.UpdateAt&lt;ZoneOrderTiebreakSystem&gt;(SystemUpdatePhase.Modification4B)` in
    /// Mod.cs (single-type overload only — see the repo's own double-register warning next to the
    /// other UpdateBefore/UpdateAt calls there).
    ///
    /// IDEMPOTENCY STRATEGY: a block that's still `Updated` on a later frame (e.g. re-tagged by an
    /// unrelated neighbor change, or by ZoneBlockAuthorityApplySystem's DeferredUpdated re-stamp)
    /// must NOT get re-shifted on top of its own previous stamp — `(stamped &lt;&lt; 8) &lt;&lt; 8`
    /// would blow the order out in a handful of frames. But BlockSystem re-deriving the SAME block
    /// legitimately overwrites m_Order back to a fresh RAW value (it has no idea about our stamp
    /// format), and that fresh raw value must be treated as a new base, not un-shifted. Distinguish
    /// the two by remembering, per entity, the exact stamped value we last wrote
    /// (<see cref="_stamped"/>): if the component's CURRENT value still equals what we last wrote,
    /// nothing external touched it since -> the value is "stamped", recover orderBase via `&gt;&gt;
    /// 8` (this also lets a position-only change, e.g. a ZoneBlockAuthority heal that never touches
    /// BuildOrder, refresh posHash without disturbing orderBase). If the current value differs from
    /// what we last wrote (including the very first time, when there's no entry at all), BlockSystem
    /// (or nothing) wrote it since -> treat the CURRENT value as a fresh raw orderBase. This makes
    /// "raw vs already-stamped" a comparison against OUR OWN last-write record instead of a guess
    /// from the value's magnitude alone (which can't distinguish a genuinely large raw order from a
    /// small stamped one).
    /// </summary>
    public partial class ZoneOrderTiebreakSystem : GameSystemBase
    {
        private const int FullSweepEveryNFrames = 120;
        private const int CleanupEverySweeps = 5;

        private EntityQuery _updatedBlocks;
        private EntityQuery _allBlocks;
        private readonly Dictionary<Entity, uint> _stamped = new Dictionary<Entity, uint>();
        private int _frameCounter;
        private int _sweepCounter;
        private bool _overflowWarned;

        protected override void OnCreate()
        {
            base.OnCreate();
            _updatedBlocks = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<Block>(),
                    ComponentType.ReadWrite<BuildOrder>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            _allBlocks = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<Block>(),
                    ComponentType.ReadWrite<BuildOrder>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[ZoneAuth] ZoneOrderTiebreakSystem created");
        }

        protected override void OnUpdate()
        {
            if (!ZoneAuthority.Enabled)
            {
                return;
            }

            // Per-frame: whatever just (re)derived. Cheap — Updated blocks only.
            NativeArray<Entity> blocks = _updatedBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in blocks)
                {
                    Stamp(e, requeueContest: false); // it's already Updated — contest re-runs anyway
                }
            }
            finally
            {
                blocks.Dispose();
            }

            // v56.2 FIELD FIX (round-5 evidence: host o1666056 vs client o7413 on old-save blocks):
            // stamping ONLY Updated blocks leaves a MIXED world — any stamped value (base<<8) beats
            // any raw value, so a stamped block near an unstamped one flips contests it should lose,
            // and the two machines stamp DIFFERENT subsets (whatever happened to be Updated on each
            // side). The invariant must be ALL-OR-NOTHING: a periodic full sweep stamps every block
            // whose current value isn't our own last write, and re-queues the newly-stamped ones for
            // a next-frame contest re-run (DeferredUpdated) so historical overlap outcomes converge
            // under the now-uniform ordering — one avalanche on the first sweep, idempotent after.
            if (++_frameCounter >= FullSweepEveryNFrames)
            {
                _frameCounter = 0;
                NativeArray<Entity> all = _allBlocks.ToEntityArray(Allocator.Temp);
                int restamped = 0;
                try
                {
                    foreach (Entity e in all)
                    {
                        if (Stamp(e, requeueContest: true))
                        {
                            restamped++;
                        }
                    }
                }
                finally
                {
                    all.Dispose();
                }

                if (restamped > 0)
                {
                    CS2M.Log.Info($"[ZoneOrderTiebreak] full sweep restamped={restamped}");
                }

                if (++_sweepCounter >= CleanupEverySweeps)
                {
                    _sweepCounter = 0;
                    CleanupStaleEntries();
                }
            }
        }

        /// <summary>Re-stamps one block's BuildOrder.m_Order if needed; returns true when the value
        /// actually changed. With <paramref name="requeueContest"/>, a changed block is queued into
        /// <see cref="DeferredUpdated"/> so next frame's CellCheck re-contests its overlaps under the
        /// new uniform ordering (used by the full sweep — per-frame Updated blocks re-contest anyway).
        /// See the class doc for the raw-vs-stamped detection strategy.</summary>
        private bool Stamp(Entity e, bool requeueContest)
        {
            BuildOrder buildOrder = EntityManager.GetComponentData<BuildOrder>(e);
            uint current = buildOrder.m_Order;

            // v56.3 REDESIGN (round-6 evidence: same save blocks with host base 6895 vs client base
            // 7679, offsets NON-uniform across regions — +784 here, +4248 there): the raw m_Order
            // base is a PER-MACHINE reconstruction. The client re-derives blocks during the join
            // load with its own process-local GenerateEdges counter, so bases never agree and can't
            // be made to — preserving them (base<<8|hash) just preserved the disagreement. The only
            // ordering both machines can compute identically is one derived from SHARED content:
            // the block's own position. So under the gate the ENTIRE m_Order becomes PosHash32.
            // Cost: vanilla's "newest road wins the overlap" becomes "stable-arbitrary wins" — a
            // gameplay-visible but CONSISTENT choice, and consistency is the whole point of co-op.
            Block block = EntityManager.GetComponentData<Block>(e);
            uint stamped = PosHash32(block.m_Position);

            bool changed = stamped != current;
            if (changed)
            {
                buildOrder.m_Order = stamped;
                EntityManager.SetComponentData(e, buildOrder);
                if (requeueContest)
                {
                    DeferredUpdated.Enqueue(e);
                }
            }

            _stamped[e] = stamped;
            return changed;
        }

        /// <summary>Deterministic FNV-1a hash of the block's own position quantized to 0.5 m —
        /// identical on both machines for the same block (same recipe as StateHashSystems.cs's
        /// `Pt()`). Full 32 bits: under the gate this IS the block's contest order.</summary>
        private static uint PosHash32(float3 pos)
        {
            long x = (int) math.round(pos.x * 2f);
            long z = (int) math.round(pos.z * 2f);
            unchecked
            {
                long h = 1469598103934665603L;
                h = (h ^ (x & 0xffffffffL)) * 1099511628211L;
                h = (h ^ (z & 0xffffffffL)) * 1099511628211L;
                return (uint) (h & 0xFFFFFFFFL);
            }
        }

        private void CleanupStaleEntries()
        {
            List<Entity> stale = null;
            foreach (Entity e in _stamped.Keys)
            {
                if (!EntityManager.Exists(e))
                {
                    (stale ?? (stale = new List<Entity>())).Add(e);
                }
            }

            if (stale == null)
            {
                return;
            }

            foreach (Entity e in stale)
            {
                _stamped.Remove(e);
            }
        }
    }
}
