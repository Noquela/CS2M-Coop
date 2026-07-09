using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>Global toggle for allowing a temporary negative DevTreePoints balance. ON by default
    /// since 2026-07-07 — validated on a 2-sim (see <see cref="Enabled"/>). Env <c>CS2M_DEVTREEFIX=0</c>
    /// disables it.
    ///     Gap: <see cref="DevTreeApplySystem.ApplyOne"/> floors the local balance at
    ///     <c>Max(0, points - cost)</c> after mirroring a remote purchase. <c>DevTreePoints</c> is
    ///     itself derived deterministically from milestones reached (decomp
    ///     <c>Game.City.DevTreeSystem.AppendPointsJob</c>: <c>points += milestone.m_DevTreePoints</c>),
    ///     and the city XP that drives milestones only reaches a client via
    ///     <see cref="ProgressionSenderSystem"/> on a ~1.5 s cadence. If the remote purchase's mirrored
    ///     cost lands here BEFORE the local mirrored XP has crossed the milestone that grants the points
    ///     the sender already had, flooring at 0 doesn't just delay the deduction — it permanently
    ///     erases the difference, because nothing ever resyncs <c>DevTreePoints</c> on its own (unlike
    ///     money, which <c>MoneySyncSenderSystem</c> resyncs host-&gt;client every ~1 s).
    ///     Two ways to close this were weighed:
    ///     (a) let the balance go temporarily negative (implemented here): once the mirrored XP crosses
    ///         the milestone, the game's own <c>AppendPointsJob</c> unconditionally adds the milestone's
    ///         points to whatever value is already there (no clamp on its side either), so a negative
    ///         balance self-corrects the moment the milestone fires locally — no new command, no new
    ///         host-authoritative channel, minimal surface.
    ///     (b) add a periodic host-&gt;client <c>DevTreePoints</c> resync (money's pattern). Safer against
    ///         a permanently-wrong DISPLAYED number sooner, but it is a second, independent source of
    ///         truth for the same value the milestone job already computes deterministically on both
    ///         sides, and doing a delta-Add (like money) or a raw overwrite (like the old tax full-array
    ///         apply, GAP A above) opens a new race with the local AppendPointsJob write which itself
    ///         isn't gated.
    ///     (a) was implemented as the safer, smaller change; (b) is reported, not built, given the ask
    ///         to avoid needless extra machinery when the simpler fix is deterministic.</summary>
    public static class DevTreeFix
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    // ON por padrão desde 2026-07-07 — validado em 2-sim (saldo -2 transitório sem
                    // floor, auto-corrige quando o milestone local soma). CS2M_DEVTREEFIX=0 desliga.
                    _state = System.Environment.GetEnvironmentVariable("CS2M_DEVTREEFIX") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     Mirrors a remote dev-tree purchase: raises the same <c>Unlock</c>+<c>Event</c> entity the
    ///     game's <c>DevTreeSystem.Purchase</c> raises (the UnlockSystem then disables the node's
    ///     Locked state and applies its effects) and deducts the node cost from the local
    ///     DevTreePoints — both PCs earn identical points from the synced XP, so mirroring the
    ///     deduction keeps the balances aligned. Skips the cost/requirement checks: the sender's
    ///     game already validated the purchase.
    /// </summary>
    public partial class DevTreeApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _pointsQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _pointsQuery = GetEntityQuery(ComponentType.ReadWrite<DevTreePoints>());
            CS2M.Log.Info("[DevTree] DevTreeApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteDevTreeQueue.TryDequeue(out DevTreeCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] apply failed in DevTreeApplySystem: {ex.Message}"); }
            }
        }

        private void ApplyOne(DevTreeCommand cmd)
        {
            var prefabId = new PrefabID("DevTreeNodePrefab", cmd.NodeName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null
                || !_prefabSystem.TryGetEntity(prefab, out Entity node))
            {
                CS2M.Log.Info($"[DevTree] RESOLVE-FAIL node={cmd.NodeName}");
                return;
            }

            // Echo guard BEFORE the unlock materializes: the detector's next scan sees the node
            // unlocked but already present in the snapshot.
            DevTreeSync.Unlocked.Add(cmd.NodeName);

            if (EntityManager.HasComponent<Locked>(node) && !EntityManager.IsComponentEnabled<Locked>(node))
            {
                CS2M.Log.Info($"[DevTree] SKIP already-unlocked node={cmd.NodeName}");
                return;
            }

            // Same event DevTreeSystem.Purchase raises (via EndFrameBarrier there; direct here).
            Entity e = EntityManager.CreateEntity();
            EntityManager.AddComponentData(e, new Unlock(node));
            EntityManager.AddComponent<Event>(e);

            // Mirror the point deduction so both sides' balances stay aligned.
            int cost = EntityManager.HasComponent<DevTreeNodeData>(node)
                ? EntityManager.GetComponentData<DevTreeNodeData>(node).m_Cost
                : 0;
            if (cost > 0 && !_pointsQuery.IsEmptyIgnoreFilter)
            {
                DevTreePoints points = _pointsQuery.GetSingleton<DevTreePoints>();
                // CS2M_DEVTREEFIX: let the balance go negative instead of flooring at 0 when our
                // mirrored XP hasn't yet crossed the milestone the sender already had — the game's own
                // AppendPointsJob adds new milestone points unconditionally, so a negative balance
                // self-corrects the instant that milestone fires locally. Flooring here (legacy)
                // erases the difference for good instead of just delaying it.
                points.m_Points = DevTreeFix.Enabled
                    ? points.m_Points - cost
                    : System.Math.Max(0, points.m_Points - cost);
                _pointsQuery.SetSingleton(points);
            }

            CS2M.Log.Info($"[DevTree] APPLIED node={cmd.NodeName} cost={cost}");
        }
    }
}
