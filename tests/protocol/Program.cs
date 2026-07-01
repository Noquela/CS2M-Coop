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

    public class DeleteCommand : CommandBase { public ulong SyncId { get; set; } }

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
            RoundTrip(opts, new MoneySyncCommand { Cash = 1234567 }, c => c.Cash == 1234567);
            RoundTrip(opts, new ProgressionSyncCommand { Xp = 5000, MaxPopulation = 12000, AchievedMilestone = 7, XpRewardRecord = 3 }, c => c.Xp == 5000 && c.AchievedMilestone == 7 && c.XpRewardRecord == 3);
            RoundTrip(opts, new ZonePaintCommand { BlockX = 100f, BlockZ = 200f, SizeX = 6, SizeY = 4, CellIndices = new[] { 0, 5, 11, 23 }, ZoneNames = new[] { "EU Residential Low", "", "EU Commercial", "EU Residential Low" } }, c => c.CellIndices.Length == 4 && c.CellIndices[2] == 11 && c.ZoneNames[1] == "" && c.ZoneNames[2] == "EU Commercial");
            RoundTrip(opts, new JoinNoticeCommand { Username = "Bruno", Joining = true }, c => c.Username == "Bruno" && c.Joining);
            RoundTrip(opts, new PlayerCursorCommand { X = 1.5f, Z = 2.5f, Valid = true, Username = "amigo" }, c => c.Username == "amigo" && c.Valid && c.X == 1.5f);

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
