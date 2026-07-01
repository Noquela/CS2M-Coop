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
        private bool _baselineDone;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
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
        }
    }
}
