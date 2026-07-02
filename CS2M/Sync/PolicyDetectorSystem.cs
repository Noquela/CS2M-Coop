using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects local city-policy toggles by diffing the City entity's <c>Policy</c> buffer against a
    ///     baseline snapshot and broadcasting the changed policy (by prefab type+name). Any player can
    ///     change policies. Echo guard: the apply marks (name,active) and this consumes it when the
    ///     resulting buffer change is seen, so a remote-applied toggle isn't echoed back.
    /// </summary>
    public partial class PolicyDetectorSystem : GameSystemBase
    {
        private CitySystem _citySystem;
        private PrefabSystem _prefabSystem;
        private EntityQuery _modifyEvents;
        private readonly System.Collections.Generic.HashSet<Entity> _sentEvents =
            new System.Collections.Generic.HashSet<Entity>();
        private int _eventClear;
        private bool _baselineDone;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // v46: BUILDING/DISTRICT policies are detected from the UI's own Modify events (the city
            // path keeps the proven snapshot diff; events for the city target are skipped here).
            _modifyEvents = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Common.Event>(),
                    ComponentType.ReadOnly<Modify>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(), // events created by our own apply
                },
            });
            CS2M.Log.Info("[Policy] PolicyDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            PolicySync.Tick();

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<Policy>(city))
            {
                return;
            }

            DynamicBuffer<Policy> buf = EntityManager.GetBuffer<Policy>(city, true);

            if (!_baselineDone)
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    if (_prefabSystem.TryGetPrefab(buf[i].m_Policy, out PrefabBase pb0) && pb0 != null)
                    {
                        PolicySync.Snapshot[pb0.name] = (buf[i].m_Flags & PolicyFlags.Active) != 0;
                    }
                }

                _baselineDone = true;
                return;
            }

            for (int i = 0; i < buf.Length; i++)
            {
                Policy p = buf[i];
                if (!_prefabSystem.TryGetPrefab(p.m_Policy, out PrefabBase pb) || pb == null)
                {
                    continue;
                }

                bool active = (p.m_Flags & PolicyFlags.Active) != 0;
                string name = pb.name;
                if (PolicySync.Snapshot.TryGetValue(name, out bool prev) && prev == active)
                {
                    continue; // unchanged
                }

                PolicySync.Snapshot[name] = active;
                if (PolicySync.ConsumeEcho(name, active))
                {
                    continue; // echo of a toggle we applied
                }

                Command.SendToAll?.Invoke(new PolicyCommand
                {
                    PolicyType = pb.GetType().Name,
                    PolicyName = name,
                    Active = active,
                    Adjustment = p.m_Adjustment,
                });
                CS2M.Log.Info($"[Policy] DETECT+SEND name={name} active={active}");
            }

            DetectScopedPolicies(city);
        }

        /// <summary>v46: building/district policy toggles, read straight from the UI's Modify events
        /// (visible this frame; the game consumes them at Mod4 and cleans them up at frame end).</summary>
        private void DetectScopedPolicies(Entity city)
        {
            if (++_eventClear >= 120)
            {
                _eventClear = 0;
                _sentEvents.Clear();
            }

            if (_modifyEvents.IsEmptyIgnoreFilter)
            {
                return;
            }

            Unity.Collections.NativeArray<Entity> events =
                _modifyEvents.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity ev in events)
                {
                    if (!_sentEvents.Add(ev))
                    {
                        continue;
                    }

                    Modify m = EntityManager.GetComponentData<Modify>(ev);
                    if (m.m_Entity == city || m.m_Entity == Entity.Null || !EntityManager.Exists(m.m_Entity))
                    {
                        continue; // city handled by the snapshot diff above
                    }

                    if (!_prefabSystem.TryGetPrefab(m.m_Policy, out PrefabBase policyPrefab) || policyPrefab == null)
                    {
                        continue;
                    }

                    byte kind;
                    float tx = 0f, tz = 0f;
                    ulong syncId = 0;
                    if (EntityManager.HasComponent<Game.Areas.District>(m.m_Entity)
                        && EntityManager.HasComponent<Game.Areas.Geometry>(m.m_Entity))
                    {
                        kind = 2;
                        var c = EntityManager.GetComponentData<Game.Areas.Geometry>(m.m_Entity).m_CenterPosition;
                        tx = c.x;
                        tz = c.z;
                    }
                    else if (EntityManager.HasComponent<Game.Buildings.Building>(m.m_Entity)
                             && EntityManager.HasComponent<Game.Objects.Transform>(m.m_Entity))
                    {
                        kind = 1;
                        var p = EntityManager.GetComponentData<Game.Objects.Transform>(m.m_Entity).m_Position;
                        tx = p.x;
                        tz = p.z;
                        if (EntityManager.HasComponent<CS2M_SyncId>(m.m_Entity))
                        {
                            syncId = EntityManager.GetComponentData<CS2M_SyncId>(m.m_Entity).m_Id;
                        }
                    }
                    else
                    {
                        continue; // unknown target kind
                    }

                    bool active = (m.m_Flags & PolicyFlags.Active) != 0;
                    Command.SendToAll?.Invoke(new PolicyCommand
                    {
                        PolicyType = policyPrefab.GetType().Name,
                        PolicyName = policyPrefab.name,
                        Active = active,
                        Adjustment = m.m_Adjustment,
                        TargetKind = kind,
                        TargetSyncId = syncId,
                        TargetX = tx,
                        TargetZ = tz,
                    });
                    CS2M.Log.Info($"[Policy] DETECT+SEND scoped name={policyPrefab.name} kind={kind} active={active}");
                }
            }
            finally
            {
                events.Dispose();
            }
        }
    }
}
