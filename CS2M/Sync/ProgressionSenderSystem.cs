using CS2M.API.Commands;
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
    ///     Host-only: broadcasts authoritative city XP ~once per second (and on change) so clients'
    ///     progression/unlocks stay in lockstep. Clients let their own MilestoneSystem advance from
    ///     the synced XP.
    /// </summary>
    public partial class ProgressionSenderSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 90; // ~1.5 s

        private CitySystem _citySystem;
        private int _lastXp = int.MinValue;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            CS2M.Log.Info("[Prog] ProgressionSenderSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (Command.CurrentRole != MultiplayerRole.Server)
            {
                return;
            }

            if (++_frame < SendEveryNFrames)
            {
                return;
            }

            _frame = 0;

            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<XP>(city))
            {
                return;
            }

            XP xp = EntityManager.GetComponentData<XP>(city);
            if (xp.m_XP == _lastXp)
            {
                return;
            }

            _lastXp = xp.m_XP;

            int milestone = EntityManager.HasComponent<MilestoneLevel>(city)
                ? EntityManager.GetComponentData<MilestoneLevel>(city).m_AchievedMilestone
                : 0;

            Command.SendToAll?.Invoke(new ProgressionSyncCommand
            {
                Xp = xp.m_XP,
                MaxPopulation = xp.m_MaximumPopulation,
                MaxIncome = xp.m_MaximumIncome,
                XpRewardRecord = (byte) xp.m_XPRewardRecord,
                AchievedMilestone = milestone,
            });
            CS2M.Log.Info($"[Prog] SEND xp={xp.m_XP} milestone={milestone}");
        }
    }
}
