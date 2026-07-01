using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.City;
using Game.Simulation;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Clients: set the city's XP to the host's authoritative value; the client's own
    ///     <c>MilestoneSystem</c> then advances the milestone level and applies unlocks/rewards. We do
    ///     NOT inject milestone events (that would double-grant dev-tree points).
    /// </summary>
    public partial class ProgressionApplySystem : GameSystemBase
    {
        private CitySystem _citySystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            CS2M.Log.Info("[Prog] ProgressionApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (!RemoteProgressionQueue.TryTake(out ProgressionSyncCommand cmd))
            {
                return;
            }

            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<XP>(city))
            {
                return;
            }

            XP xp = EntityManager.GetComponentData<XP>(city);
            xp.m_XP = cmd.Xp;
            xp.m_MaximumPopulation = cmd.MaxPopulation;
            xp.m_MaximumIncome = cmd.MaxIncome;
            xp.m_XPRewardRecord = (XPRewardFlags) cmd.XpRewardRecord;
            EntityManager.SetComponentData(city, xp);

            CS2M.Log.Info($"[Prog] APPLIED xp={cmd.Xp} milestone(host)={cmd.AchievedMilestone}");
        }
    }
}
