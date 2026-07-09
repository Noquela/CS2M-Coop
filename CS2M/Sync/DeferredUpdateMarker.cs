using System.Collections.Concurrent;
using Game;
using Game.Common;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Thread-safe hand-off for entities that need <c>Updated</c> stamped at the START of the NEXT
    ///     frame instead of right now. Root cause (confirmed 06/07 with matching client/host zone-block
    ///     content but <c>Cell.m_State==0</c> in bulk on the client): zone appliers
    ///     (<see cref="ZonePaintApplySystem"/>, <see cref="ZoneBlockAuthorityApplySystem"/>.Heal) run at
    ///     <c>SystemUpdatePhase.Modification5</c> — AFTER every consumer that reacts to <c>Updated</c>
    ///     (BlockSystem/CellCheckSystem etc., which run Mod1-4) has already executed this frame. Then
    ///     <c>Game.Common.CleanUpSystem</c> (decomp/Game/Game/Common/CleanUpSystem.cs:41-56, wired at
    ///     <c>SystemUpdatePhase.Cleanup</c> via SystemOrder.cs:54) strips <c>Updated</c> (among other
    ///     flow tags) off every entity in its per-frame batch before the NEXT frame's Mod1-4 ever get a
    ///     chance to see it. The tag is added and erased inside the same frame without being observed —
    ///     exactly the "Modification5 is a dead zone" lesson already paid for in Mod.cs for
    ///     net/edit/placement (see the v38/v41 comments there). Those systems could just move earlier
    ///     because their own definitions are the thing consumed at Mod1-4. Zone appliers can't: they
    ///     target blocks that must ALREADY exist (derived locally from already-synced roads) and retry
    ///     across frames until the block shows up, so the system itself has to stay late. Instead, this
    ///     queue lets them keep their immediate (and harmless) direct <c>AddComponent&lt;Updated&gt;</c>
    ///     for the current frame AND additionally enqueue here — <see cref="DeferredUpdatedSystem"/>
    ///     drains the queue at the very start of the NEXT frame (<c>UpdateBefore</c>
    ///     <c>Modification1</c>), so the re-stamped <c>Updated</c> is guaranteed to live through that
    ///     whole frame's Mod1-4 before Cleanup reaps it again.
    /// </summary>
    public static class DeferredUpdated
    {
        private static readonly ConcurrentQueue<Entity> Pending = new ConcurrentQueue<Entity>();

        public static void Enqueue(Entity e) => Pending.Enqueue(e);

        public static void Clear()
        {
            while (Pending.TryDequeue(out _)) { }
        }

        internal static bool TryDequeue(out Entity e) => Pending.TryDequeue(out e);
    }

    /// <summary>
    ///     Drains <see cref="DeferredUpdated"/> at the start of every frame, BEFORE
    ///     <c>SystemUpdatePhase.Modification1</c>, and re-stamps <c>Updated</c> on each entity so it
    ///     survives the whole frame for Mod1-4 consumers. Registered with the single-type
    ///     <c>UpdateBefore&lt;DeferredUpdatedSystem&gt;(phase)</c> overload in Mod.cs — NEVER the
    ///     two-type <c>UpdateBefore&lt;A,B&gt;</c> overload, which double-registers the anchor system
    ///     (it would then run twice/frame and, since this system holds no cross-frame state of its own
    ///     beyond the shared queue, the second pass would just find an empty queue — but the pattern is
    ///     banned repo-wide regardless per the v50 lesson in Mod.cs, so it is not used here either).
    /// </summary>
    public partial class DeferredUpdatedSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            while (DeferredUpdated.TryDequeue(out Entity e))
            {
                if (!EntityManager.Exists(e))
                {
                    continue;
                }

                if (!EntityManager.HasComponent<Updated>(e))
                {
                    EntityManager.AddComponent<Updated>(e);
                }
            }
        }
    }

    /// <summary>
    ///     v66.5 CRASH FIX — the companion to <see cref="DeferredUpdated"/> for BLOCKS WE CREATE
    ///     (ZoneBlockAuthorityApplySystem.CreateBlock's clone). Root cause, proven from the decomp phase
    ///     order: Game.Zones.SearchSystem runs at Modification5 (SystemOrder.cs:199) and maintains the
    ///     block NativeQuadTree — it ADDs a block only while the block carries <c>Created</c>, otherwise
    ///     it <c>Update</c>s it, and Update THROWS "Item not found" (a Burst job → aborts the whole
    ///     process) if the block was never in the tree. Our applier is also at Modification5 but runs
    ///     AFTER SearchSystem, so a clone we Instantiate this frame is never seen with <c>Created</c>;
    ///     Cleanup then strips <c>Created</c>, and a plain <c>DeferredUpdated</c> re-stamp next frame gives
    ///     the clone <c>Updated</c> WITHOUT <c>Created</c> → SearchSystem takes the Update branch on a
    ///     block it never Added → crash (the "receiver of the road crashes" bug). Fix: re-stamp
    ///     <c>Created</c> (+<c>Updated</c>) at the START of the next frame, so when SearchSystem runs at
    ///     Modification5 that frame it sees <c>Created</c> and ADDs the clone to the tree. One-shot: the
    ///     block is enqueued once at creation and drained once; from then on it lives in the tree and
    ///     normal <c>DeferredUpdated</c>/Update is safe.
    /// </summary>
    public static class DeferredCreated
    {
        private static readonly ConcurrentQueue<Entity> Pending = new ConcurrentQueue<Entity>();

        public static void Enqueue(Entity e) => Pending.Enqueue(e);

        public static void Clear()
        {
            while (Pending.TryDequeue(out _)) { }
        }

        internal static bool TryDequeue(out Entity e) => Pending.TryDequeue(out e);
    }

    /// <summary>Drains <see cref="DeferredCreated"/> at the start of every frame (UpdateBefore
    /// Modification1), re-stamping <c>Created</c>+<c>Updated</c> so Game.Zones.SearchSystem ADDs the
    /// clone to the block quadtree this frame instead of faulting on an Update of an unknown item.</summary>
    public partial class DeferredCreatedSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            while (DeferredCreated.TryDequeue(out Entity e))
            {
                if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e))
                {
                    continue;
                }

                if (!EntityManager.HasComponent<Created>(e))
                {
                    EntityManager.AddComponent<Created>(e);
                }

                if (!EntityManager.HasComponent<Updated>(e))
                {
                    EntityManager.AddComponent<Updated>(e);
                }
            }
        }
    }
}
