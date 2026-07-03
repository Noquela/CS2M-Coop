using System.Reflection;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Simulation;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Latest host demand snapshot (thread-safe hand-off from the network handler).</summary>
    public static class DemandSync
    {
        private static readonly object Lock = new object();
        private static DemandSyncCommand _latest;

        public static void Set(DemandSyncCommand cmd)
        {
            lock (Lock) { _latest = cmd; }
        }

        public static DemandSyncCommand Take()
        {
            lock (Lock)
            {
                DemandSyncCommand c = _latest;
                _latest = null;
                return c;
            }
        }

        public static void Clear()
        {
            lock (Lock) { _latest = null; }
        }
    }

    /// <summary>
    ///     v51: host-authoritative RCI demand. HOST reads the three demand systems' public values
    ///     and broadcasts ~0.5 Hz. CLIENTS disable those systems (their sim diverges and growables
    ///     already follow the host) and mirror the numbers into the private m_Last* fields the UI
    ///     reads — via cached reflection, since the game only exposes read-only properties.
    /// </summary>
    public partial class DemandSyncSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 120; // ~0.5 Hz

        private ResidentialDemandSystem _res;
        private CommercialDemandSystem _com;
        private IndustrialDemandSystem _ind;
        private int _frame;
        private bool _suppressed;

        private static readonly FieldInfo ResHousehold = typeof(ResidentialDemandSystem)
            .GetField("m_LastHouseholdDemand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ResBuilding = typeof(ResidentialDemandSystem)
            .GetField("m_LastBuildingDemand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ComCompany = typeof(CommercialDemandSystem)
            .GetField("m_LastCompanyDemand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ComBuilding = typeof(CommercialDemandSystem)
            .GetField("m_LastBuildingDemand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo IndCompany = typeof(IndustrialDemandSystem)
            .GetField("m_LastIndustrialCompanyDemand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo IndBuilding = typeof(IndustrialDemandSystem)
            .GetField("m_LastIndustrialBuildingDemand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo StoCompany = typeof(IndustrialDemandSystem)
            .GetField("m_LastStorageCompanyDemand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo StoBuilding = typeof(IndustrialDemandSystem)
            .GetField("m_LastStorageBuildingDemand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OffCompany = typeof(IndustrialDemandSystem)
            .GetField("m_LastOfficeCompanyDemand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OffBuilding = typeof(IndustrialDemandSystem)
            .GetField("m_LastOfficeBuildingDemand", BindingFlags.Instance | BindingFlags.NonPublic);

        protected override void OnCreate()
        {
            base.OnCreate();
            _res = World.GetOrCreateSystemManaged<ResidentialDemandSystem>();
            _com = World.GetOrCreateSystemManaged<CommercialDemandSystem>();
            _ind = World.GetOrCreateSystemManaged<IndustrialDemandSystem>();
            CS2M.Log.Info($"[Demand] DemandSyncSystem created (reflection ok={ResHousehold != null && ResBuilding != null && ComCompany != null && IndCompany != null})");
        }

        protected override void OnUpdate()
        {
            LocalPlayer local = NetworkInterface.Instance.LocalPlayer;

            bool wantSuppress = local.PlayerStatus == PlayerStatus.PLAYING
                                && local.PlayerType == PlayerType.CLIENT;
            if (wantSuppress != _suppressed)
            {
                _suppressed = wantSuppress;
                _res.Enabled = !wantSuppress;
                _com.Enabled = !wantSuppress;
                _ind.Enabled = !wantSuppress;
                CS2M.Log.Info($"[Demand] local demand sim {(wantSuppress ? "SUPPRESSED (host owns the bars)" : "restored")}");
            }

            if (local.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (local.PlayerType == PlayerType.SERVER)
            {
                if (++_frame < SendEveryNFrames)
                {
                    return;
                }

                _frame = 0;
                int3 rb = _res.buildingDemand;
                Command.SendToAll?.Invoke(new DemandSyncCommand
                {
                    Household = _res.householdDemand,
                    ResLow = rb.x, ResMedium = rb.y, ResHigh = rb.z,
                    Commercial = _com.companyDemand,
                    CommercialBuilding = _com.buildingDemand,
                    Industrial = _ind.industrialCompanyDemand,
                    IndustrialBuilding = _ind.industrialBuildingDemand,
                    Storage = _ind.storageCompanyDemand,
                    StorageBuilding = _ind.storageBuildingDemand,
                    Office = _ind.officeCompanyDemand,
                    OfficeBuilding = _ind.officeBuildingDemand,
                });
                return;
            }

            // CLIENT: mirror the latest host snapshot into the fields the UI reads.
            DemandSyncCommand cmd = DemandSync.Take();
            if (cmd == null)
            {
                return;
            }

            try
            {
                ResHousehold?.SetValue(_res, cmd.Household);
                ResBuilding?.SetValue(_res, new int3(cmd.ResLow, cmd.ResMedium, cmd.ResHigh));
                ComCompany?.SetValue(_com, cmd.Commercial);
                ComBuilding?.SetValue(_com, cmd.CommercialBuilding);
                IndCompany?.SetValue(_ind, cmd.Industrial);
                IndBuilding?.SetValue(_ind, cmd.IndustrialBuilding);
                StoCompany?.SetValue(_ind, cmd.Storage);
                StoBuilding?.SetValue(_ind, cmd.StorageBuilding);
                OffCompany?.SetValue(_ind, cmd.Office);
                OffBuilding?.SetValue(_ind, cmd.OfficeBuilding);
                CS2M.Log.Verbose($"[Demand] mirrored host bars res={cmd.Household} com={cmd.Commercial} ind={cmd.Industrial}");
            }
            catch (System.Exception ex)
            {
                CS2M.Log.Info($"[Guard] demand mirror failed: {ex.Message}");
            }
        }
    }
}
