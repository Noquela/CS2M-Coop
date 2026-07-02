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
    /// <summary>Latest-wins mailbox + shared snapshot (echo guard) for the city loan.</summary>
    public static class LoanSync
    {
        private static readonly object Lock = new object();
        private static LoanCommand _latest;

        /// <summary>Last known loan amount (baseline/applied); int.MinValue = not initialized.</summary>
        public static int Snapshot = int.MinValue;

        public static void Set(LoanCommand cmd)
        {
            lock (Lock) { _latest = cmd; }
        }

        public static bool TryTake(out LoanCommand cmd)
        {
            lock (Lock)
            {
                cmd = _latest;
                _latest = null;
                return cmd != null;
            }
        }

        public static void Clear()
        {
            lock (Lock) { _latest = null; }
            Snapshot = int.MinValue;
        }
    }

    /// <summary>Detects loan changes (take/repay) by diffing <c>Loan.m_Amount</c> on the City.</summary>
    public partial class LoanDetectorSystem : GameSystemBase
    {
        private const int ScanEveryNFrames = 30;

        private CitySystem _citySystem;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            CS2M.Log.Info("[Loan] LoanDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_frame < ScanEveryNFrames)
            {
                return;
            }

            _frame = 0;

            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<Loan>(city))
            {
                return;
            }

            int amount = EntityManager.GetComponentData<Loan>(city).m_Amount;
            if (LoanSync.Snapshot == int.MinValue)
            {
                LoanSync.Snapshot = amount; // baseline from the save, don't send
                return;
            }

            if (amount == LoanSync.Snapshot)
            {
                return;
            }

            LoanSync.Snapshot = amount;
            Command.SendToAll?.Invoke(new LoanCommand { Amount = amount });
            CS2M.Log.Info($"[Loan] DETECT+SEND amount={amount}");
        }
    }

    /// <summary>
    ///     Mirrors a remote loan change: on the HOST also applies the money delta (mirror of the
    ///     vanilla LoanActionJob) so the authoritative balance includes the loan cash exactly once;
    ///     clients only mirror the Loan component (their balance comes from the host's money sync).
    /// </summary>
    public partial class LoanApplySystem : GameSystemBase
    {
        private CitySystem _citySystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            CS2M.Log.Info("[Loan] LoanApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (!LoanSync.TryTake(out LoanCommand cmd))
            {
                return;
            }

            try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] loan apply failed: {ex.Message}"); }
        }

        private void ApplyOne(LoanCommand cmd)
        {
            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<Loan>(city))
            {
                return;
            }

            Loan loan = EntityManager.GetComponentData<Loan>(city);
            int delta = cmd.Amount - loan.m_Amount;
            if (delta == 0)
            {
                return;
            }

            LoanSync.Snapshot = cmd.Amount; // echo guard BEFORE the detector's next scan

            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER
                && EntityManager.HasComponent<PlayerMoney>(city))
            {
                PlayerMoney pm = EntityManager.GetComponentData<PlayerMoney>(city);
                if (!pm.m_Unlimited)
                {
                    pm.Add(delta); // vanilla LoanActionJob mirror
                    EntityManager.SetComponentData(city, pm);
                }
            }

            loan.m_Amount = cmd.Amount;
            loan.m_LastModified = World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex;
            EntityManager.SetComponentData(city, loan);

            CS2M.Log.Info($"[Loan] APPLIED amount={cmd.Amount} delta={delta}");
        }
    }
}
