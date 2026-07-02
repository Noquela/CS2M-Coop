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
                points.m_Points = System.Math.Max(0, points.m_Points - cost);
                _pointsQuery.SetSingleton(points);
            }

            CS2M.Log.Info($"[DevTree] APPLIED node={cmd.NodeName} cost={cost}");
        }
    }
}
