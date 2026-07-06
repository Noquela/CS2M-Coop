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
        private const int CleanupEveryNFrames = 600;

        private EntityQuery _updatedBlocks;
        private readonly Dictionary<Entity, uint> _stamped = new Dictionary<Entity, uint>();
        private int _frameCounter;
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
            CS2M.Log.Info("[ZoneAuth] ZoneOrderTiebreakSystem created");
        }

        protected override void OnUpdate()
        {
            if (!ZoneAuthority.Enabled)
            {
                return;
            }

            NativeArray<Entity> blocks = _updatedBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in blocks)
                {
                    Stamp(e);
                }
            }
            finally
            {
                blocks.Dispose();
            }

            // Periodic sweep so _stamped doesn't grow unbounded across a long session as blocks
            // get deleted (Entity.Index gets recycled with a new Version, so a stale entry would
            // just sit there unused forever rather than corrupt anything — this is pure hygiene).
            if (++_frameCounter >= CleanupEveryNFrames)
            {
                _frameCounter = 0;
                CleanupStaleEntries();
            }
        }

        /// <summary>Re-stamps one block's BuildOrder.m_Order if needed. See the class doc for the
        /// full raw-vs-stamped detection strategy.</summary>
        private void Stamp(Entity e)
        {
            BuildOrder buildOrder = EntityManager.GetComponentData<BuildOrder>(e);
            uint current = buildOrder.m_Order;

            uint orderBase;
            if (_stamped.TryGetValue(e, out uint prevStamped) && prevStamped == current)
            {
                // Untouched since our own last stamp -- recover the base and keep it; only
                // posHash below might change (e.g. Block.m_Position nudged without a re-derive).
                orderBase = current >> 8;
            }
            else
            {
                // Never stamped, or BlockSystem re-derived this block THIS frame and overwrote our
                // previous stamp with its own fresh max(edge, neighbor) value -- either way, the
                // CURRENT component value is a raw base, not something to un-shift.
                orderBase = current;
            }

            if (orderBase > 0xFFFFFF)
            {
                if (!_overflowWarned)
                {
                    _overflowWarned = true;
                    CS2M.Log.Warn(
                        $"[ZoneOrderTiebreak] orderBase {orderBase} exceeds the 24-bit tie-break budget " +
                        "-- capping to 0xFFFFFF (primary order precision unaffected in practice; only " +
                        "the reserved tie-break byte budget is capped)");
                }

                orderBase = 0xFFFFFF;
            }

            Block block = EntityManager.GetComponentData<Block>(e);
            byte posHash = PosHash(block.m_Position);
            uint stamped = (orderBase << 8) | posHash;

            if (stamped != current)
            {
                buildOrder.m_Order = stamped;
                EntityManager.SetComponentData(e, buildOrder);
            }

            _stamped[e] = stamped;
        }

        /// <summary>Deterministic FNV-1a hash of the block's own position quantized to 0.5 m, low
        /// byte only -- identical on both machines for the same block (same recipe as
        /// StateHashSystems.cs's `Pt()`, kept local here since that class isn't gated the same way
        /// and this needs only 8 bits, not a full fingerprint).</summary>
        private static byte PosHash(float3 pos)
        {
            long x = (int) math.round(pos.x * 2f);
            long z = (int) math.round(pos.z * 2f);
            unchecked
            {
                long h = 1469598103934665603L;
                h = (h ^ (x & 0xffffffffL)) * 1099511628211L;
                h = (h ^ (z & 0xffffffffL)) * 1099511628211L;
                return (byte) (h & 0xFF);
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
