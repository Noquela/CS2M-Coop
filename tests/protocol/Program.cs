using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MessagePack;
using MessagePack.Attributeless;
using MessagePack.Attributeless.Implementation;

// CS2M protocol harness: validates that every sync command round-trips through the exact same
// attributeless-MessagePack polymorphic setup the mod uses (BetterGraphOf over CommandBase). This is
// the part we CAN verify without the game — it proves the wire format serializes/deserializes and that
// the polymorphic union resolves back to the right concrete type (incl. the zoning string[]/int[]).

namespace CS2MTests
{
    // ---- Command shapes: byte-for-byte copies of the mod's POCOs (properties, primitives only) ----
    public abstract class CommandBase { public int SenderId { get; set; } }

    public class ObjectPlaceCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public uint Hash0 { get; set; }
        public uint Hash1 { get; set; }
        public uint Hash2 { get; set; }
        public uint Hash3 { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float RotW { get; set; }
        public int RandomSeed { get; set; }
        public float Elevation { get; set; }
        public byte ElevationFlags { get; set; }
    }

    public class NetPlaceCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public uint Hash0 { get; set; }
        public uint Hash1 { get; set; }
        public uint Hash2 { get; set; }
        public uint Hash3 { get; set; }
        public float Ax { get; set; }
        public float Ay { get; set; }
        public float Az { get; set; }
        public float Bx { get; set; }
        public float By { get; set; }
        public float Bz { get; set; }
        public float Cx { get; set; }
        public float Cy { get; set; }
        public float Cz { get; set; }
        public float Dx { get; set; }
        public float Dy { get; set; }
        public float Dz { get; set; }
        public float StartElevX { get; set; }
        public float StartElevY { get; set; }
        public float EndElevX { get; set; }
        public float EndElevY { get; set; }
        public int RandomSeed { get; set; }
    }

    public class MoveCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float RotW { get; set; }
    }

    public class DeleteCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public byte TargetKind { get; set; }
        public int Number { get; set; }
    }

    public class DevTreeCommand : CommandBase { public string NodeName { get; set; } }

    public class TilePurchaseCommand : CommandBase { public float[] Xs { get; set; } public float[] Zs { get; set; } public int Cost { get; set; } }

    // --- v60 auto-heal commands ---
    public class HealRequestCommand : CommandBase
    {
        public string Domain { get; set; }
        public float[] TerrainHeights { get; set; }
    }

    public class WaterHealCommand : CommandBase
    {
        public float[] PosX { get; set; }
        public float[] PosY { get; set; }
        public float[] PosZ { get; set; }
        public float[] Radius { get; set; }
        public float[] Height { get; set; }
        public float[] Multiplier { get; set; }
        public float[] Polluted { get; set; }
        public int[] ConstantDepth { get; set; }
    }

    public class TerrainPatchCommand : CommandBase
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public ushort[] Data { get; set; }
    }

    // --- v50 commands ---
    public class MapPingCommand : CommandBase
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public string Username { get; set; }
    }

    public class PlayerStatsCommand : CommandBase
    {
        public int[] Ids { get; set; }
        public string[] Names { get; set; }
        public int[] Pings { get; set; }
    }

    public class DemandSyncCommand : CommandBase
    {
        public int Household { get; set; }
        public int ResLow { get; set; }
        public int ResMedium { get; set; }
        public int ResHigh { get; set; }
        public int Commercial { get; set; }
        public int CommercialBuilding { get; set; }
        public int Industrial { get; set; }
        public int IndustrialBuilding { get; set; }
        public int Storage { get; set; }
        public int StorageBuilding { get; set; }
        public int Office { get; set; }
        public int OfficeBuilding { get; set; }
    }

    public class FireSyncCommand : CommandBase
    {
        public byte Kind { get; set; }
        public ulong TargetSyncId { get; set; }
        public string PrefabName { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float Intensity { get; set; }
    }

    public class LoanCommand : CommandBase { public int Amount { get; set; } }

    public class RenameCommand : CommandBase
    {
        public byte TargetKind { get; set; }
        public int Number { get; set; }
        public ulong TargetSyncId { get; set; }
        public string TargetPrefabName { get; set; }
        public float TargetX { get; set; }
        public float TargetZ { get; set; }
        public string Name { get; set; }
    }

    public class AreaEditCommand : CommandBase
    {
        public ulong OwnerSyncId { get; set; }
        public string OwnerPrefabName { get; set; }
        public float OwnerX { get; set; }
        public float OwnerY { get; set; }
        public float OwnerZ { get; set; }
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public float[] Xs { get; set; }
        public float[] Ys { get; set; }
        public float[] Zs { get; set; }
        public float[] Els { get; set; }
        public bool Delete { get; set; }
        public float CenterX { get; set; }
        public float CenterZ { get; set; }
    }

    public class RouteCreateCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public bool Replace { get; set; }
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public bool Complete { get; set; }
        public byte ColorR { get; set; }
        public byte ColorG { get; set; }
        public byte ColorB { get; set; }
        public byte ColorA { get; set; }
        public int Number { get; set; }
        public float[] WpX { get; set; }
        public float[] WpY { get; set; }
        public float[] WpZ { get; set; }
        public byte[] WpHasConn { get; set; }
        public ulong[] WpConnId { get; set; }
        public float[] WpConnX { get; set; }
        public float[] WpConnZ { get; set; }
    }

    public class RouteColorCommand : CommandBase
    {
        public ulong SyncId { get; set; }
        public string PrefabName { get; set; }
        public int Number { get; set; }
        public byte ColorR { get; set; }
        public byte ColorG { get; set; }
        public byte ColorB { get; set; }
        public byte ColorA { get; set; }
    }

    public class EnvSyncCommand : CommandBase
    {
        public float Temperature { get; set; }
        public float Precipitation { get; set; }
        public float Cloudiness { get; set; }
        public uint ElapsedTimeFrames { get; set; }
    }

    public class StateHashCommand : CommandBase
    {
        public int Edges { get; set; }
        public int SyncedObjects { get; set; }
        public int Districts { get; set; }
        public int WaterSources { get; set; }
        public long EdgeHash { get; set; }
        public int Nodes { get; set; }
        public long NodeHash { get; set; }
        public int Buildings { get; set; }
        public long BuildingHash { get; set; }
        public int ZoneBlocks { get; set; }
        public long ZoneHash { get; set; }
        public long AreaHash { get; set; }
        public int Money { get; set; }
    }

    public class MoneySyncCommand : CommandBase { public int Cash { get; set; } }

    public class ProgressionSyncCommand : CommandBase
    {
        public int Xp { get; set; }
        public int MaxPopulation { get; set; }
        public int MaxIncome { get; set; }
        public byte XpRewardRecord { get; set; }
        public int AchievedMilestone { get; set; }
    }

    public class ZonePaintCommand : CommandBase
    {
        public float BlockX { get; set; }
        public float BlockZ { get; set; }
        public float DirX { get; set; }
        public float DirZ { get; set; }
        public int SizeX { get; set; }
        public int SizeY { get; set; }
        public int[] CellIndices { get; set; }
        public string[] ZoneNames { get; set; }
    }

    public class JoinNoticeCommand : CommandBase
    {
        public string Username { get; set; }
        public bool Joining { get; set; }
    }

    public class PlayerCursorCommand : CommandBase
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public bool Valid { get; set; }
        public string Username { get; set; }
    }

    public class ToolPreviewCommand : CommandBase
    {
        public bool Active { get; set; }
        public string Username { get; set; }
        public float Ax { get; set; }
        public float Ay { get; set; }
        public float Az { get; set; }
        public float Bx { get; set; }
        public float By { get; set; }
        public float Bz { get; set; }
        public float Cx { get; set; }
        public float Cy { get; set; }
        public float Cz { get; set; }
        public float Dx { get; set; }
        public float Dy { get; set; }
        public float Dz { get; set; }
    }

    // ---- New sync commands added by the fork (byte-for-byte copies of the mod's POCOs) ----
    public class TaxSyncCommand : CommandBase { public int[] Rates { get; set; } }

    public class BudgetCommand : CommandBase
    {
        public string ServiceType { get; set; }
        public string ServiceName { get; set; }
        public int Percentage { get; set; }
    }

    public class PolicyCommand : CommandBase
    {
        public string PolicyType { get; set; }
        public string PolicyName { get; set; }
        public bool Active { get; set; }
        public float Adjustment { get; set; }
        public byte TargetKind { get; set; }
        public ulong TargetSyncId { get; set; }
        public float TargetX { get; set; }
        public float TargetZ { get; set; }
        public string TargetName { get; set; }
    }

    public class DistrictCommand : CommandBase
    {
        public string PrefabType { get; set; }
        public string PrefabName { get; set; }
        public uint OptionMask { get; set; }
        public float[] Xs { get; set; }
        public float[] Ys { get; set; }
        public float[] Zs { get; set; }
    }

    public class WaterCommand : CommandBase
    {
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float Radius { get; set; }
        public float Height { get; set; }
        public float Multiplier { get; set; }
        public float Polluted { get; set; }
        public int ConstantDepth { get; set; }
        public bool Delete { get; set; }
    }

    public class TerrainCommand : CommandBase
    {
        public int Type { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float Size { get; set; }
        public float Strength { get; set; }
    }

    public class SpeedCommand : CommandBase { public float Speed { get; set; } }

    public class ResyncCommand : CommandBase { }

    public class NetDeleteCommand : CommandBase
    {
        public float StartX { get; set; }
        public float StartY { get; set; }
        public float StartZ { get; set; }
        public float EndX { get; set; }
        public float EndY { get; set; }
        public float EndZ { get; set; }
    }

    public class NetUpgradeCommand : CommandBase
    {
        public float StartX { get; set; }
        public float StartY { get; set; }
        public float StartZ { get; set; }
        public float EndX { get; set; }
        public float EndY { get; set; }
        public float EndZ { get; set; }
        public uint General { get; set; }
        public uint Left { get; set; }
        public uint Right { get; set; }
    }

    // AtomicBatch (byte-for-byte copy of the mod's NetBatchCommand — the whole result of one net-tool
    // apply as flat parallel primitive arrays; the widest command on the wire).
    public class NetBatchCommand : CommandBase
    {
        public ulong[] NodeIds { get; set; }
        public float[] NodePosX { get; set; }
        public float[] NodePosY { get; set; }
        public float[] NodePosZ { get; set; }
        public float[] NodeRotX { get; set; }
        public float[] NodeRotY { get; set; }
        public float[] NodeRotZ { get; set; }
        public float[] NodeRotW { get; set; }
        public string[] NodePrefabTypes { get; set; }
        public string[] NodePrefabNames { get; set; }
        public bool[] NodeHasStandalone { get; set; }
        public bool[] NodeHasElevation { get; set; }
        public float[] NodeElevX { get; set; }
        public float[] NodeElevY { get; set; }
        public int[] NodeSeeds { get; set; }
        public ulong[] EdgeStartNodeIds { get; set; }
        public ulong[] EdgeEndNodeIds { get; set; }
        public float[] EdgeAX { get; set; }
        public float[] EdgeAY { get; set; }
        public float[] EdgeAZ { get; set; }
        public float[] EdgeBX { get; set; }
        public float[] EdgeBY { get; set; }
        public float[] EdgeBZ { get; set; }
        public float[] EdgeCX { get; set; }
        public float[] EdgeCY { get; set; }
        public float[] EdgeCZ { get; set; }
        public float[] EdgeDX { get; set; }
        public float[] EdgeDY { get; set; }
        public float[] EdgeDZ { get; set; }
        public string[] EdgePrefabTypes { get; set; }
        public string[] EdgePrefabNames { get; set; }
        public bool[] EdgeHasUpgraded { get; set; }
        public uint[] EdgeUpgradedG { get; set; }
        public uint[] EdgeUpgradedL { get; set; }
        public uint[] EdgeUpgradedR { get; set; }
        public bool[] EdgeHasElevation { get; set; }
        public float[] EdgeElevX { get; set; }
        public float[] EdgeElevY { get; set; }
        public int[] EdgeSeeds { get; set; }
        public uint[] EdgeBuildOrderStart { get; set; }
        public uint[] EdgeBuildOrderEnd { get; set; }
        public ulong[] DelStartNodeIds { get; set; }
        public ulong[] DelEndNodeIds { get; set; }
        public float[] DelStartX { get; set; }
        public float[] DelStartZ { get; set; }
        public float[] DelEndX { get; set; }
        public float[] DelEndZ { get; set; }
        public ulong[] BoundaryNodeIds { get; set; }
        public float[] BoundaryPosX { get; set; }
        public float[] BoundaryPosY { get; set; }
        public float[] BoundaryPosZ { get; set; }
    }

    public static class Program
    {
        // Copy of the mod's MessagePackExtensions.BetterGraphOf (same walker → same wire format).
        private static MessagePackSerializerOptionsBuilder BetterGraphOf(
            this MessagePackSerializerOptionsBuilder builder, Type self, params Assembly[] assemblies)
        {
            var result = new List<Type>();
            void Add(Type type)
            {
                if (result.Contains(type)) return;
                if (type.IsConstructedGenericType)
                    foreach (Type t in type.GenericTypeArguments) Add(t);
                if (type.IsArray) { Add(type.GetElementType()); return; }
                if (!assemblies.Contains(type.Assembly)) return;
                result.Add(type);
                IEnumerable<Type> children = type.GetProperties().Where(p => !p.IsIndexed())
                    .Select(p => p.PropertyType).Distinct().Where(x => !x.IsEnum);
                IEnumerable<Type> derivations = type.GetSubTypes(assemblies);
                foreach (Type t in children.Concat(derivations)) Add(t);
            }
            Add(self);
            foreach (Type t in result)
            {
                if (t.IsAbstract) builder.AllSubTypesOf(t, assemblies);
                else builder.AutoKeyed(t);
            }
            return builder;
        }

        private static int _pass;
        private static int _fail;

        public static int Main()
        {
            Console.WriteLine("=== CS2M protocol round-trip harness (2 players over the wire) ===\n");

            MessagePackSerializerOptionsBuilder builder = MessagePackSerializerOptions.Standard.Configure();
            builder.BetterGraphOf(typeof(CommandBase), typeof(CommandBase).Assembly);
            MessagePackSerializerOptions opts = builder.Build();

            RoundTrip(opts, new ObjectPlaceCommand { SenderId = 0, SyncId = 123456789012UL, PrefabType = "BuildingPrefab", PrefabName = "FireHouse01", PosX = -300.9f, PosY = 479.1f, PosZ = 284.8f, RotW = 1f, RandomSeed = 23203, Elevation = 0f }, c => c.PrefabName == "FireHouse01" && c.SyncId == 123456789012UL && Math.Abs(c.PosX - (-300.9f)) < 0.01f);
            RoundTrip(opts, new NetPlaceCommand { SyncId = 7UL, PrefabType = "NetPrefab", PrefabName = "Small Road", Ax = 12.9f, Dz = 478.4f, RandomSeed = 4309 }, c => c.PrefabName == "Small Road" && Math.Abs(c.Dz - 478.4f) < 0.01f);
            RoundTrip(opts, new MoveCommand { SyncId = 42UL, PosX = 1f, PosY = 2f, PosZ = 3f, RotW = 1f }, c => c.SyncId == 42UL && c.PosZ == 3f);
            RoundTrip(opts, new DeleteCommand { SyncId = 99UL }, c => c.SyncId == 99UL);
            RoundTrip(opts, new DeleteCommand { SyncId = 0UL, PrefabType = "BuildingPrefab", PrefabName = "WaterTower03", PosX = -152.4f, PosY = 478f, PosZ = -61f }, c => c.SyncId == 0UL && c.PrefabName == "WaterTower03" && Math.Abs(c.PosX - (-152.4f)) < 0.01f);
            RoundTrip(opts, new DevTreeCommand { NodeName = "Healthcare Node 3" }, c => c.NodeName == "Healthcare Node 3");
            RoundTrip(opts, new EnvSyncCommand { Temperature = 21.5f, Precipitation = 0.7f, Cloudiness = 0.4f, ElapsedTimeFrames = 987654u }, c => Math.Abs(c.Temperature - 21.5f) < 0.01f && c.ElapsedTimeFrames == 987654u);
            RoundTrip(opts, new StateHashCommand { Edges = 1085, SyncedObjects = 42, Districts = 3, WaterSources = 5, EdgeHash = 8877665544332211L, Nodes = 512, NodeHash = -123456789012345L, Buildings = 730, BuildingHash = 999888777666L, ZoneBlocks = 640, ZoneHash = -5L, AreaHash = 42424242L, Money = 1750000 }, c => c.Edges == 1085 && c.EdgeHash == 8877665544332211L && c.NodeHash == -123456789012345L && c.Buildings == 730 && c.ZoneHash == -5L && c.AreaHash == 42424242L && c.Money == 1750000);
            RoundTrip(opts, new MoneySyncCommand { Cash = 1234567 }, c => c.Cash == 1234567);
            RoundTrip(opts, new ProgressionSyncCommand { Xp = 5000, MaxPopulation = 12000, AchievedMilestone = 7, XpRewardRecord = 3 }, c => c.Xp == 5000 && c.AchievedMilestone == 7 && c.XpRewardRecord == 3);
            RoundTrip(opts, new ZonePaintCommand { BlockX = 100f, BlockZ = 200f, SizeX = 6, SizeY = 4, CellIndices = new[] { 0, 5, 11, 23 }, ZoneNames = new[] { "EU Residential Low", "", "EU Commercial", "EU Residential Low" } }, c => c.CellIndices.Length == 4 && c.CellIndices[2] == 11 && c.ZoneNames[1] == "" && c.ZoneNames[2] == "EU Commercial");
            RoundTrip(opts, new JoinNoticeCommand { Username = "Bruno", Joining = true }, c => c.Username == "Bruno" && c.Joining);
            RoundTrip(opts, new PlayerCursorCommand { X = 1.5f, Z = 2.5f, Valid = true, Username = "amigo" }, c => c.Username == "amigo" && c.Valid && c.X == 1.5f);
            RoundTrip(opts, new ToolPreviewCommand { Active = true, Username = "amigo", Ax = 200f, Az = 200f, Dx = 296f, Dz = 200f }, c => c.Active && c.Username == "amigo" && c.Ax == 200f && c.Dx == 296f);

            // --- New fork commands ---
            RoundTrip(opts, new TaxSyncCommand { Rates = new[] { 10, 3, -2, 5, 0, 7 } }, c => c.Rates.Length == 6 && c.Rates[1] == 3 && c.Rates[2] == -2);
            RoundTrip(opts, new BudgetCommand { ServiceType = "ServicePrefab", ServiceName = "Roads", Percentage = 90 }, c => c.ServiceName == "Roads" && c.Percentage == 90);
            RoundTrip(opts, new PolicyCommand { PolicyType = "PolicyPrefab", PolicyName = "Taxi Starting Fee", Active = true, Adjustment = 27.5f }, c => c.PolicyName == "Taxi Starting Fee" && c.Active && Math.Abs(c.Adjustment - 27.5f) < 0.01f);
            RoundTrip(opts, new DistrictCommand { PrefabType = "DistrictPrefab", PrefabName = "District Area", OptionMask = 5u, Xs = new[] { -60f, 60f, 60f, -60f, -60f }, Ys = new[] { 477f, 477f, 477f, 477f, 477f }, Zs = new[] { -60f, -60f, 60f, 60f, -60f } }, c => c.PrefabName == "District Area" && c.OptionMask == 5u && c.Xs.Length == 5 && c.Zs[2] == 60f);
            RoundTrip(opts, new WaterCommand { PosX = -307f, PosY = 5f, PosZ = -3124f, Radius = 20f, Height = 5f, Multiplier = 1f, Polluted = 0f, ConstantDepth = 0 }, c => Math.Abs(c.PosX - (-307f)) < 0.01f && c.Radius == 20f && !c.Delete);
            RoundTrip(opts, new WaterCommand { PosX = 10f, PosZ = 20f, Delete = true }, c => c.Delete && c.PosZ == 20f);
            RoundTrip(opts, new TerrainCommand { Type = 0, PosX = -187f, PosY = 477f, PosZ = -3004f, Size = 40f, Strength = 100000f }, c => c.Type == 0 && c.Size == 40f && c.Strength == 100000f);
            RoundTrip(opts, new SpeedCommand { Speed = 3f }, c => c.Speed == 3f);
            RoundTrip(opts, new ResyncCommand(), c => true);
            RoundTrip(opts, new NetDeleteCommand { StartX = 3644f, StartZ = 7096f, EndX = 3835f, EndZ = 7100f }, c => Math.Abs(c.StartX - 3644f) < 0.01f && Math.Abs(c.EndZ - 7100f) < 0.01f);
            RoundTrip(opts, new NetUpgradeCommand { StartX = 1f, EndX = 2f, General = 0u, Left = 0x1000u, Right = 0u }, c => c.Left == 0x1000u && c.General == 0u);
            RoundTrip(opts, new TilePurchaseCommand { Xs = new[] { 100f, 200f }, Zs = new[] { -100f, -200f }, Cost = 44000 }, c => c.Xs.Length == 2 && c.Zs[1] == -200f && c.Cost == 44000);

            // --- v60 auto-heal commands (ushort[] payload is the novel wire type — prove it) ---
            RoundTrip(opts, new HealRequestCommand { Domain = "terrain", TerrainHeights = new[] { 477.5f, 480f, 0f } }, c => c.Domain == "terrain" && c.TerrainHeights.Length == 3 && Math.Abs(c.TerrainHeights[0] - 477.5f) < 0.01f);
            RoundTrip(opts, new HealRequestCommand { Domain = "water" }, c => c.Domain == "water" && c.TerrainHeights == null);
            RoundTrip(opts, new WaterHealCommand { PosX = new[] { 1f, 2f }, PosY = new[] { 5f, 6f }, PosZ = new[] { 9f, 10f }, Radius = new[] { 20f, 30f }, Height = new[] { 5f, 6f }, Multiplier = new[] { 1f, 0.5f }, Polluted = new[] { 0f, 0.1f }, ConstantDepth = new[] { 0, 1 } }, c => c.PosX.Length == 2 && c.Radius[1] == 30f && c.ConstantDepth[1] == 1);
            RoundTrip(opts, new TerrainPatchCommand { X = 2048, Y = 1024, W = 3, H = 2, Data = new ushort[] { 0, 1, 32767, 65535, 42, 7 } }, c => c.X == 2048 && c.W == 3 && c.Data.Length == 6 && c.Data[2] == 32767 && c.Data[3] == 65535);

            // --- v50 commands ---
            RoundTrip(opts, new MapPingCommand { X = -321.5f, Y = 480f, Z = 1204.25f, Username = "Bruno" }, c => c.Username == "Bruno" && Math.Abs(c.Z - 1204.25f) < 0.01f);
            RoundTrip(opts, new PlayerStatsCommand { Ids = new[] { 0, 1, 2 }, Names = new[] { "Host", "Amigo1", "Amigo2" }, Pings = new[] { 0, 45, 120 } }, c => c.Ids.Length == 3 && c.Names[2] == "Amigo2" && c.Pings[1] == 45);
            RoundTrip(opts, new FireSyncCommand { Kind = 2, TargetSyncId = 0UL, PrefabName = "Residential High 03", PosX = 15.5f, PosY = 478f, PosZ = -92.75f, Intensity = 0.85f }, c => c.Kind == 2 && c.PrefabName == "Residential High 03" && Math.Abs(c.Intensity - 0.85f) < 0.001f);
            RoundTrip(opts, new DemandSyncCommand { Household = 55, ResLow = 40, ResMedium = 20, ResHigh = 10, Commercial = 30, CommercialBuilding = 25, Industrial = 60, IndustrialBuilding = 45, Storage = 5, StorageBuilding = 3, Office = 15, OfficeBuilding = 12 }, c => c.Household == 55 && c.ResHigh == 10 && c.OfficeBuilding == 12);
            RoundTrip(opts, new LoanCommand { Amount = 250000 }, c => c.Amount == 250000);
            RoundTrip(opts, new RenameCommand { TargetKind = 1, Number = 3, TargetPrefabName = "Bus Line", Name = "Linha Centro" }, c => c.TargetKind == 1 && c.Name == "Linha Centro");
            RoundTrip(opts, new AreaEditCommand { OwnerPrefabName = "GrainFarm01", PrefabName = "Grain Field", Xs = new[] { 1f, 2f, 3f }, Ys = new[] { 0f, 0f, 0f }, Zs = new[] { 4f, 5f, 6f }, Els = new[] { -3.4e38f, 0f, 0f }, Delete = false, CenterX = 2f, CenterZ = 5f }, c => c.PrefabName == "Grain Field" && c.Xs.Length == 3 && c.CenterZ == 5f);
            RoundTrip(opts, new RouteCreateCommand { SyncId = 42UL, PrefabType = "TransportLinePrefab", PrefabName = "Bus Line", Complete = true, ColorR = 10, ColorG = 20, ColorB = 30, ColorA = 255, Number = 7, WpX = new[] { 1f, 2f, 3f }, WpY = new[] { 0f, 0f, 0f }, WpZ = new[] { 9f, 8f, 7f }, WpHasConn = new byte[] { 1, 0, 1 }, WpConnId = new ulong[] { 5UL, 0UL, 0UL }, WpConnX = new[] { 1f, 0f, 3f }, WpConnZ = new[] { 9f, 0f, 7f } }, c => c.Complete && c.Number == 7 && c.WpX.Length == 3 && c.WpHasConn[2] == 1 && c.WpConnId[0] == 5UL);
            RoundTrip(opts, new RouteColorCommand { SyncId = 42UL, PrefabName = "Bus Line", Number = 7, ColorR = 200, ColorG = 100, ColorB = 50, ColorA = 255 }, c => c.Number == 7 && c.ColorR == 200);
            RoundTrip(opts, new DeleteCommand { SyncId = 0UL, TargetKind = 1, PrefabName = "Bus Line", Number = 7 }, c => c.TargetKind == 1 && c.Number == 7);
            RoundTrip(opts, new PolicyCommand { PolicyType = "PolicyPrefab", PolicyName = "Recycling", Active = true, Adjustment = 0f, TargetKind = 1, TargetSyncId = 99UL, TargetX = 10f, TargetZ = 20f, TargetName = "" }, c => c.TargetKind == 1 && c.TargetSyncId == 99UL);

            // --- AtomicBatch (the widest wire shape: ~50 parallel arrays incl. ulong/bool/string) ---
            RoundTrip(opts, new NetBatchCommand
            {
                NodeIds = new[] { 11UL, 12UL },
                NodePosX = new[] { 100f, 200f }, NodePosY = new[] { 478f, 478f }, NodePosZ = new[] { -50f, -60f },
                NodeRotX = new[] { 0f, 0f }, NodeRotY = new[] { 0f, 0f }, NodeRotZ = new[] { 0f, 0f }, NodeRotW = new[] { 1f, 1f },
                NodePrefabTypes = new[] { "RoadPrefab", "RoadPrefab" }, NodePrefabNames = new[] { "Small Road", "Small Road" },
                NodeHasStandalone = new[] { false, true },
                NodeHasElevation = new[] { true, false }, NodeElevX = new[] { 2.5f, 0f }, NodeElevY = new[] { 2.5f, 0f },
                NodeSeeds = new[] { 41, 42 },
                EdgeStartNodeIds = new[] { 11UL }, EdgeEndNodeIds = new[] { 12UL },
                EdgeAX = new[] { 100f }, EdgeAY = new[] { 478f }, EdgeAZ = new[] { -50f },
                EdgeBX = new[] { 133f }, EdgeBY = new[] { 478f }, EdgeBZ = new[] { -53f },
                EdgeCX = new[] { 166f }, EdgeCY = new[] { 478f }, EdgeCZ = new[] { -57f },
                EdgeDX = new[] { 200f }, EdgeDY = new[] { 478f }, EdgeDZ = new[] { -60f },
                EdgePrefabTypes = new[] { "RoadPrefab" }, EdgePrefabNames = new[] { "Small Road" },
                EdgeHasUpgraded = new[] { true }, EdgeUpgradedG = new[] { 4u }, EdgeUpgradedL = new[] { 0x1000u }, EdgeUpgradedR = new[] { 0u },
                EdgeHasElevation = new[] { false }, EdgeElevX = new[] { 0f }, EdgeElevY = new[] { 0f },
                EdgeSeeds = new[] { 77 },
                EdgeBuildOrderStart = new[] { 160u }, EdgeBuildOrderEnd = new[] { 175u },
                DelStartNodeIds = new[] { 5UL }, DelEndNodeIds = new[] { 6UL },
                DelStartX = new[] { 90f }, DelStartZ = new[] { -40f }, DelEndX = new[] { 95f }, DelEndZ = new[] { -45f },
                BoundaryNodeIds = new[] { 9UL }, BoundaryPosX = new[] { 88f }, BoundaryPosY = new[] { 478f }, BoundaryPosZ = new[] { -33f },
            }, c => c.NodeIds.Length == 2 && c.NodeIds[1] == 12UL && c.NodeHasStandalone[1] && !c.NodeHasStandalone[0]
                    && c.EdgeStartNodeIds[0] == 11UL && Math.Abs(c.EdgeDX[0] - 200f) < 0.01f
                    && c.EdgeHasUpgraded[0] && c.EdgeUpgradedL[0] == 0x1000u
                    && c.EdgeBuildOrderEnd[0] == 175u && c.DelStartNodeIds[0] == 5UL
                    && c.BoundaryNodeIds[0] == 9UL && Math.Abs(c.BoundaryPosZ[0] - (-33f)) < 0.01f);

            // Empty-arrays shape (a pure-delete or node-only batch; also what the selftest fabricates).
            RoundTrip(opts, new NetBatchCommand
            {
                NodeIds = new ulong[0], NodePosX = new float[0], NodePosY = new float[0], NodePosZ = new float[0],
                NodeRotX = new float[0], NodeRotY = new float[0], NodeRotZ = new float[0], NodeRotW = new float[0],
                NodePrefabTypes = new string[0], NodePrefabNames = new string[0],
                NodeHasStandalone = new bool[0], NodeHasElevation = new bool[0], NodeElevX = new float[0], NodeElevY = new float[0],
                NodeSeeds = new int[0],
                EdgeStartNodeIds = new ulong[0], EdgeEndNodeIds = new ulong[0],
                EdgeAX = new float[0], EdgeAY = new float[0], EdgeAZ = new float[0],
                EdgeBX = new float[0], EdgeBY = new float[0], EdgeBZ = new float[0],
                EdgeCX = new float[0], EdgeCY = new float[0], EdgeCZ = new float[0],
                EdgeDX = new float[0], EdgeDY = new float[0], EdgeDZ = new float[0],
                EdgePrefabTypes = new string[0], EdgePrefabNames = new string[0],
                EdgeHasUpgraded = new bool[0], EdgeUpgradedG = new uint[0], EdgeUpgradedL = new uint[0], EdgeUpgradedR = new uint[0],
                EdgeHasElevation = new bool[0], EdgeElevX = new float[0], EdgeElevY = new float[0],
                EdgeSeeds = new int[0], EdgeBuildOrderStart = new uint[0], EdgeBuildOrderEnd = new uint[0],
                DelStartNodeIds = new[] { 3UL }, DelEndNodeIds = new[] { 4UL },
                DelStartX = new[] { 1f }, DelStartZ = new[] { 2f }, DelEndX = new[] { 3f }, DelEndZ = new[] { 4f },
                BoundaryNodeIds = new ulong[0], BoundaryPosX = new float[0], BoundaryPosY = new float[0], BoundaryPosZ = new float[0],
            }, c => c.NodeIds.Length == 0 && c.EdgeStartNodeIds.Length == 0 && c.DelStartNodeIds.Length == 1
                    && c.DelEndNodeIds[0] == 4UL && c.BoundaryNodeIds.Length == 0);

            Console.WriteLine($"\n=== {_pass} passed, {_fail} failed ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void RoundTrip<T>(MessagePackSerializerOptions opts, T cmd, Func<T, bool> check)
            where T : CommandBase
        {
            string name = typeof(T).Name;
            try
            {
                byte[] bytes = MessagePackSerializer.Serialize<CommandBase>(cmd, opts);
                CommandBase back = MessagePackSerializer.Deserialize<CommandBase>(bytes, opts);
                bool typeOk = back is T;
                bool fieldsOk = typeOk && check((T) back);
                if (typeOk && fieldsOk)
                {
                    _pass++;
                    Console.WriteLine($"  PASS  {name,-24} {bytes.Length,4} bytes  -> {back.GetType().Name}");
                }
                else
                {
                    _fail++;
                    Console.WriteLine($"  FAIL  {name,-24} typeOk={typeOk} fieldsOk={fieldsOk} (got {back.GetType().Name})");
                }
            }
            catch (Exception e)
            {
                _fail++;
                Console.WriteLine($"  FAIL  {name,-24} EXCEPTION {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
