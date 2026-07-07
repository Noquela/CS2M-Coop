using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>Global toggle for prefab-filtered building-policy resolve. ON by default since
    /// 2026-07-07 — validated live in 2-sim + selftest 88 PASS/0 FAIL with every gated fix enabled
    /// together (no regression/echo/crash).
    ///     Gap: <see cref="PolicyApplySystem.ResolveTarget"/>'s <c>TargetKind == 1</c> (building) branch
    ///     falls back to <c>FindNearest</c> by proximity ALONE (9 m² ~ 3 m radius) whenever the target
    ///     has no live <c>CS2M_SyncId</c> (e.g. a building placed before sync-id tracking, or loaded
    ///     from a save) — unlike kinds 3 (transport line) and 4 (extension) in this same file, which
    ///     both already filter candidates by prefab name. In a dense block, the nearest building within
    ///     3 m of the policy target is not necessarily the SAME building, so the policy lands on the
    ///     wrong one.
    ///     Gated ON: the detector also sends the building's prefab name
    ///     (<see cref="Commands.Data.Game.PolicyCommand.TargetName"/>, otherwise unused for kind 1) and
    ///     the apply filters candidates by matching prefab name before falling back to plain proximity
    ///     (empty/no-match name -&gt; identical to the legacy behavior, so an older peer or a
    ///     gate-off sender never breaks resolution). Set env <c>CS2M_POLICYFIX=0</c> to disable.</summary>
    public static class PolicyFix
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_POLICYFIX") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     Applies a remote city-policy change by raising a Modify event: a new entity with
    ///     <c>Event</c> + <c>Modify(City, policyPrefab, active, adjustment)</c> — the same components the
    ///     game's UI writes (the UI's own SetCityPolicy can't be reused here: it creates the event in the
    ///     EndFrameBarrier's command buffer, which isn't allowed from the Modification5 phase). The
    ///     game's <c>PolicyModifiedSystem</c> consumes the event and updates the Policy buffer + effects.
    /// </summary>
    public partial class PolicyApplySystem : GameSystemBase
    {
        private CitySystem _citySystem;
        private PrefabSystem _prefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            CS2M.Log.Info("[Policy] PolicyApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemotePolicyQueue.TryDequeue(out PolicyCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] apply failed in PolicyApplySystem: {ex.Message}"); }
            }
        }

        private void ApplyOne(PolicyCommand cmd)
        {
            Entity target = ResolveTarget(cmd);
            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[Policy] SKIP noTarget kind={cmd.TargetKind} name={cmd.PolicyName} " +
                              $"at=({cmd.TargetX:F0},{cmd.TargetZ:F0})");
                return;
            }

            var prefabId = new PrefabID(cmd.PolicyType, cmd.PolicyName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info($"[Policy] RESOLVE-FAIL type={cmd.PolicyType} name={cmd.PolicyName}");
                return;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out Entity policyEntity))
            {
                CS2M.Log.Info($"[Policy] RESOLVE-FAIL no entity name={cmd.PolicyName}");
                return;
            }

            if (cmd.TargetKind == 0)
            {
                PolicySync.MarkApplied(cmd.PolicyName, cmd.Active);
            }

            Entity e = EntityManager.CreateEntity();
            EntityManager.AddComponent<Event>(e);
            EntityManager.AddComponentData(e, new Modify(target, policyEntity, cmd.Active, cmd.Adjustment));
            // Echo guard: the scoped-policy detector reads Modify events — never re-send ours.
            EntityManager.AddComponent<CS2M_RemotePlaced>(e);

            CS2M.Log.Info($"[Policy] APPLIED kind={cmd.TargetKind} name={cmd.PolicyName} active={cmd.Active} adj={cmd.Adjustment}");
        }

        private Entity ResolveTarget(PolicyCommand cmd)
        {
            if (cmd.TargetKind == 0)
            {
                return _citySystem.City;
            }

            if (cmd.TargetKind == 3)
            {
                // v49: transport line — SyncId first, then prefab name + RouteNumber (in TargetX).
                return RouteResolver.Resolve(EntityManager, GetEntityQuery(
                        ComponentType.ReadOnly<Game.Routes.Route>(),
                        ComponentType.ReadOnly<Game.Routes.RouteNumber>(),
                        ComponentType.ReadOnly<PrefabRef>(),
                        ComponentType.Exclude<Game.Tools.Temp>(),
                        ComponentType.Exclude<Deleted>()),
                    _prefabSystem, cmd.TargetSyncId, cmd.TargetName, (int)cmd.TargetX);
            }

            if (cmd.TargetKind == 1)
            {
                if (cmd.TargetSyncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(cmd.TargetSyncId, out Entity byId)
                    && EntityManager.Exists(byId) && !EntityManager.HasComponent<Deleted>(byId))
                {
                    return byId;
                }

                // CS2M_POLICYFIX: try prefab+proximity first (same idea as kinds 3/4 below) before
                // falling back to proximity-alone, so a dense block of same-type buildings doesn't
                // pick the wrong neighbor.
                if (PolicyFix.Enabled && !string.IsNullOrEmpty(cmd.TargetName))
                {
                    Entity byPrefab = FindNearestBuildingByPrefab(cmd.TargetName, cmd.TargetX, cmd.TargetZ, 9f);
                    if (byPrefab != Entity.Null)
                    {
                        return byPrefab;
                    }
                }

                return FindNearest(GetEntityQuery(
                        ComponentType.ReadOnly<Game.Buildings.Building>(),
                        ComponentType.ReadOnly<Game.Objects.Transform>(),
                        ComponentType.Exclude<Game.Tools.Temp>(),
                        ComponentType.Exclude<Deleted>()),
                    cmd.TargetX, cmd.TargetZ, 9f, useGeometry: false);
            }

            if (cmd.TargetKind == 4)
            {
                // v55: service-building extension — nearest Extension with the matching prefab name near
                // the sent world position (extensions carry no shared SyncId, so name+position addresses it).
                return FindNearestExtension(cmd.TargetName, cmd.TargetX, cmd.TargetZ, 9f);
            }

            // districts: match by area center
            return FindNearest(GetEntityQuery(
                    ComponentType.ReadOnly<Game.Areas.District>(),
                    ComponentType.ReadOnly<Game.Areas.Geometry>(),
                    ComponentType.Exclude<Game.Tools.Temp>(),
                    ComponentType.Exclude<Deleted>()),
                cmd.TargetX, cmd.TargetZ, 2500f, useGeometry: true);
        }

        /// <summary>Nearest live Extension sub-object whose prefab name matches, within maxDistSq of (x,z).</summary>
        private Entity FindNearestExtension(string prefabName, float x, float z, float maxDistSq)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return Entity.Null;
            }

            EntityQuery query = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Extension>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Game.Tools.Temp>(),
                ComponentType.Exclude<Deleted>());

            Entity best = Entity.Null;
            float bestD = maxDistSq;
            Unity.Collections.NativeArray<Entity> ents =
                query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(cand).m_Prefab,
                            out PrefabBase pb) || pb == null || pb.name != prefabName)
                    {
                        continue;
                    }

                    Unity.Mathematics.float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - x;
                    float dz = p.z - z;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = cand;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return best;
        }

        /// <summary>CS2M_POLICYFIX: nearest live BUILDING whose prefab name matches, within maxDistSq of
        /// (x,z). Mirrors <see cref="FindNearestExtension"/> for the building (kind 1) target.</summary>
        private Entity FindNearestBuildingByPrefab(string prefabName, float x, float z, float maxDistSq)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return Entity.Null;
            }

            EntityQuery query = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Game.Tools.Temp>(),
                ComponentType.Exclude<Deleted>());

            Entity best = Entity.Null;
            float bestD = maxDistSq;
            Unity.Collections.NativeArray<Entity> ents =
                query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(cand).m_Prefab,
                            out PrefabBase pb) || pb == null || pb.name != prefabName)
                    {
                        continue;
                    }

                    Unity.Mathematics.float3 p = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - x;
                    float dz = p.z - z;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = cand;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return best;
        }

        private Entity FindNearest(EntityQuery query, float x, float z, float maxDistSq, bool useGeometry)
        {
            Entity best = Entity.Null;
            float bestD = maxDistSq;
            Unity.Collections.NativeArray<Entity> ents =
                query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    Unity.Mathematics.float3 p = useGeometry
                        ? EntityManager.GetComponentData<Game.Areas.Geometry>(cand).m_CenterPosition
                        : EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - x;
                    float dz = p.z - z;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = cand;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return best;
        }
    }
}
