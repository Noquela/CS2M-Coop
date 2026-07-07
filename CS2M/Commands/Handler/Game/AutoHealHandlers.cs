using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>Client → host request for a domain slice. Host-consumed; never relayed.</summary>
    public class HealRequestHandler : CommandHandler<HealRequestCommand>
    {
        public HealRequestHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false;
        }

        protected override void Handle(HealRequestCommand command)
        {
            AutoHealQueues.EnqueueRequest(command);
        }
    }

    /// <summary>Host-originated authoritative water list.</summary>
    public class WaterHealHandler : CommandHandler<WaterHealCommand>
    {
        public WaterHealHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false; // host-originated broadcast
        }

        protected override void Handle(WaterHealCommand command)
        {
            AutoHealQueues.EnqueueWater(command);
        }
    }

    /// <summary>Host-originated exact heightmap patch.</summary>
    public class TerrainPatchHandler : CommandHandler<TerrainPatchCommand>
    {
        public TerrainPatchHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false; // host-originated broadcast
        }

        protected override void Handle(TerrainPatchCommand command)
        {
            AutoHealQueues.EnqueueTerrainPatch(command);
        }
    }
}
