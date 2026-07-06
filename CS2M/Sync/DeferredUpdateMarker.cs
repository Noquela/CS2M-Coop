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
}
