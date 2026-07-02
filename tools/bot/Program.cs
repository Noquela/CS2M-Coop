extern alias cs2m;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Linq;
using CS2M.API.Commands;
using cs2m::CS2M.Commands.Data.Game;
using cs2m::CS2M.Commands.Data.Internal;
using LiteNetLib;
using MessagePack;
using MessagePack.Attributeless;
using MessagePack.Attributeless.Implementation;
using MessagePack.Formatters;
using MessagePack.Resolvers;

// cs2m-bot: a headless CS2M *client* that speaks the mod's real network protocol (LiteNetLib +
// attributeless MessagePack built over the REAL mod assemblies, so type ids are byte-identical).
// It connects to a game instance hosting on this machine, passes the preconditions handshake,
// downloads (and discards) the world transfer, then plays the part of a remote friend: places
// objects/roads, paints zones, deletes — while counting the host-authoritative traffic it receives
// (money/speed/progression). This validates the full 2-machine wire path without a second game.
namespace CS2MBot
{
    /// <summary>Colossal.Version formatter with the same wire bytes as the mod's ColossalFormatter
    /// (a MessagePack bin holding [1B versionVersion][8B version LE][4B versionExtra LE]) but
    /// without Unity native allocations, so it runs in a plain console.</summary>
    public sealed class VersionFormatter : IMessagePackFormatter<Colossal.Version>
    {
        private static readonly FieldInfo FVv = typeof(Colossal.Version)
            .GetField("m_VersionVersion", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FV = typeof(Colossal.Version)
            .GetField("m_Version", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FVx = typeof(Colossal.Version)
            .GetField("m_VersionExtra", BindingFlags.NonPublic | BindingFlags.Instance);

        public void Serialize(ref MessagePackWriter writer, Colossal.Version value, MessagePackSerializerOptions options)
        {
            object boxed = value;
            byte vv = (byte)FVv.GetValue(boxed);
            long v = (long)FV.GetValue(boxed);
            int vx = (int)FVx.GetValue(boxed);
            byte[] bytes = new byte[13];
            bytes[0] = vv;
            for (int i = 0; i < 8; i++) { bytes[1 + i] = (byte)(v >> (8 * i)); }
            for (int i = 0; i < 4; i++) { bytes[9 + i] = (byte)(vx >> (8 * i)); }
            writer.Write(bytes);
        }

        public Colossal.Version Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            byte[] b = reader.ReadBytes().GetValueOrDefault().ToArray();
            object boxed = default(Colossal.Version);
            if (b.Length >= 13)
            {
                long v = 0; int vx = 0;
                for (int i = 7; i >= 0; i--) { v = (v << 8) | b[1 + i]; }
                for (int i = 3; i >= 0; i--) { vx = (vx << 8) | b[9 + i]; }
                FVv.SetValue(boxed, b[0]);
                FV.SetValue(boxed, v);
                FVx.SetValue(boxed, vx);
            }

            return (Colossal.Version)boxed;
        }
    }

    public sealed class BotResolver : IFormatterResolver
    {
        public static readonly BotResolver Instance = new BotResolver();
        private static readonly VersionFormatter Version = new VersionFormatter();

        public IMessagePackFormatter<T> GetFormatter<T>()
        {
            return typeof(T) == typeof(Colossal.Version) ? (IMessagePackFormatter<T>)(object)Version : null;
        }
    }

    /// <summary>Bootstrap kept free of Colossal-typed statics: the AssemblyResolve hook must be
    /// registered BEFORE any type that references Colossal.Core gets initialized.</summary>
    public static class Boot
    {
        public static int Main(string[] args)
        {
            string managed = @"C:\JogosCrackeados\Cities.Skylines.II.v1.5.3f1\game\Cities2_Data\Managed";
            string modDir = @"C:\Users\Bruno\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2M";
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { modDir, managed })
                {
                    string p = Path.Combine(dir, name);
                    // UnsafeLoadFrom: the game files carry the mark-of-the-web, which plain
                    // LoadFrom refuses (loadFromRemoteSources).
                    if (File.Exists(p)) { return Assembly.UnsafeLoadFrom(p); }
                }

                return null;
            };

            return Jump(args);
        }

        // Separate non-inlined frame: Program's type init (which references Colossal.Version)
        // must only happen AFTER the resolve handler above is registered. If this call lived in
        // Main, the JIT would resolve Program while compiling Main — before the handler exists.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int Jump(string[] args)
        {
            return Program.Entry(args);
        }
    }

    public static class Program
    {
        private static MessagePackSerializerOptions _model;
        private static NetManager _net;
        private static NetPeer _server;

        private static string _username = "BotFriend";
        private static string _ip = "127.0.0.1";
        private static int _port = 1111;

        private static volatile bool _preconditionsOk;
        private static volatile bool _worldDone;
        private static PreconditionsErrorCommand _lastError;
        private static long _worldBytes;
        private static int _recvMoney, _recvSpeed, _recvProg, _recvChat, _recvCursor, _recvOther;

        // Calibration echoed by the server on a version/dlc/mod mismatch (first attempt sends blanks).
        private static Version _modVersion = new Version(0, 0, 0, 0);
        private static Colossal.Version _gameVersion;
        private static List<string> _mods = new List<string>();
        private static List<int> _dlcs = new List<int>();

        public static int Entry(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--ip") { _ip = args[i + 1]; }
                if (args[i] == "--port") { _port = int.Parse(args[i + 1]); }
                if (args[i] == "--user") { _username = args[i + 1]; }
            }

            return Run();
        }

        private static int Run()
        {
            BuildModel();

            // Try to read the real versions from the mod/game assemblies (fallback: calibration).
            try { _modVersion = cs2m::CS2M.Util.VersionUtil.GetModVersion(); Log($"mod version={_modVersion}"); }
            catch (Exception ex) { Log($"mod version unavailable ({ex.GetType().Name}) — will calibrate"); }
            try { _gameVersion = cs2m::CS2M.Util.VersionUtil.GetGameVersion(); Log($"game version read OK"); }
            catch (Exception ex) { Log($"game version unavailable ({ex.GetType().Name}) — will calibrate"); }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Log($"=== connect attempt {attempt} ===");
                if (Connect())
                {
                    return RunSession();
                }

                if (_lastError != null)
                {
                    Calibrate(_lastError);
                    _lastError = null;
                    continue;
                }

                Log("connection failed with no server feedback");
                return 2;
            }

            Log("FAILED: preconditions never accepted");
            return 2;
        }

        private static void BuildModel()
        {
            IFormatterResolver resolver = CompositeResolver.Create(BotResolver.Instance, StandardResolver.Instance);
            MessagePackSerializerOptionsBuilder options =
                MessagePackSerializerOptions.Standard.WithResolver(resolver).Configure();
            // Same walk over the SAME assemblies the game uses -> identical polymorphic type ids.
            BetterGraphOf(options, typeof(CommandBase),
                typeof(CommandBase).Assembly, typeof(cs2m::CS2M.Mod).Assembly);
            _model = options.Build();
            Log("wire model built over real mod assemblies");
        }

        /// <summary>Verbatim copy of the mod's CS2M.Util.MessagePackExtensions.BetterGraphOf (that
        /// one is merged/internalized inside CS2M.dll) — same algorithm + same input assemblies =
        /// identical type-id assignment.</summary>
        private static void BetterGraphOf(MessagePackSerializerOptionsBuilder builder, Type self,
            params Assembly[] assemblies)
        {
            var result = new List<Type>();
            Add(self);

            void Add(Type type)
            {
                if (result.Contains(type)) { return; }

                if (type.IsConstructedGenericType)
                {
                    foreach (Type t in type.GenericTypeArguments) { Add(t); }
                }

                if (type.IsArray)
                {
                    Add(type.GetElementType());
                    return;
                }

                if (!assemblies.Contains(type.Assembly)) { return; }

                result.Add(type);
                IEnumerable<Type> children =
                    type.GetProperties()
                        .Where(p => !p.IsIndexed())
                        .Select(p => p.PropertyType)
                        .Distinct()
                        .Where(x => !x.IsEnum);
                IEnumerable<Type> derivations = type.GetSubTypes(assemblies);

                foreach (Type t in children.Concat(derivations))
                {
                    Add(t);
                }
            }

            foreach (Type t in result)
            {
                if (t.IsAbstract) { builder.AllSubTypesOf(t, assemblies); }
                else { builder.AutoKeyed(t); }
            }
        }

        private static bool Connect()
        {
            _preconditionsOk = false;
            _worldDone = false;
            _worldBytes = 0;

            var listener = new EventBasedNetListener();
            _net = new NetManager(listener) { MtuDiscovery = true };
            listener.PeerConnectedEvent += peer =>
            {
                _server = peer;
                Log("connected; sending preconditions check");
                Send(new PreconditionsCheckCommand
                {
                    Username = _username,
                    Password = "",
                    ModVersion = _modVersion,
                    GameVersion = _gameVersion,
                    Mods = _mods,
                    DlcIds = _dlcs,
                });
            };
            listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                byte[] bytes = reader.GetRemainingBytes();
                try { OnCommand(MessagePackSerializer.Deserialize<CommandBase>(bytes, _model)); }
                catch (Exception ex) { Log($"DESERIALIZE-FAIL {bytes.Length}B: {ex.Message}"); }
            };
            listener.PeerDisconnectedEvent += (peer, info) => Log($"disconnected: {info.Reason}");

            _net.Start();
            _net.Connect(_ip, _port, "CSM");

            // Pump until preconditions verdict (+world) or timeout.
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                _net.PollEvents();
                Thread.Sleep(15);
                if (_lastError != null) { _net.Stop(); return false; }
                if (_preconditionsOk && _worldDone) { return true; }
            }

            Log($"timeout (preconditionsOk={_preconditionsOk} worldDone={_worldDone} worldBytes={_worldBytes})");
            _net.Stop();
            return false;
        }

        private static void OnCommand(CommandBase cmd)
        {
            switch (cmd)
            {
                case PreconditionsSuccessCommand _:
                    Log("preconditions ACCEPTED — announcing join (host should pause)");
                    _preconditionsOk = true;
                    Send(new JoinNoticeCommand { Username = _username, Joining = true });
                    break;
                case PreconditionsErrorCommand err:
                    Log($"preconditions ERROR flags={err.Errors}");
                    _lastError = err;
                    break;
                case WorldTransferCommand world:
                    _worldBytes += world.WorldSlice?.Length ?? 0;
                    if (world.RemainingBytes == 0)
                    {
                        Log($"world transfer complete ({_worldBytes / 1024} KB) — announcing joined");
                        Send(new JoinNoticeCommand { Username = _username, Joining = false });
                        _worldDone = true;
                    }

                    break;
                case MoneySyncCommand m: _recvMoney++; break;
                case SpeedCommand s: _recvSpeed++; break;
                case ProgressionSyncCommand p: _recvProg++; break;
                case ChatMessageCommand c:
                    _recvChat++;
                    Log($"chat<{c.Username}> {c.Message}");
                    break;
                case PlayerCursorCommand pc: _recvCursor++; break;
                default: _recvOther++; break;
            }
        }

        private static void Calibrate(PreconditionsErrorCommand err)
        {
            Log("calibrating from server echo (adopting the server's versions/mods/dlcs)");
            if (err.ModVersion != null) { _modVersion = err.ModVersion; }
            _gameVersion = err.GameVersion;
            if (err.Mods != null) { _mods = err.Mods; }
            if (err.DlcIds != null) { _dlcs = err.DlcIds; }
        }

        private static int RunSession()
        {
            Log("=== PLAYING (from the host's perspective) — running remote-friend script ===");
            Pump(3000);

            Send(new ChatMessageCommand { Username = _username, Message = "bot online — validation run" });
            Pump(1000);

            // 1. Place a tree (the same command a real friend's detector would emit).
            ulong syncBase = (ulong)new Random().Next(1 << 20) << 40;
            Send(new ObjectPlaceCommand
            {
                SyncId = syncBase | 1,
                PrefabType = "StaticObjectPrefab", PrefabName = "EU_AlderTree01",
                PosX = 150f, PosY = 480f, PosZ = 150f, RotW = 1f,
                RandomSeed = 4242,
            });
            Log("sent ObjectPlaceCommand EU_AlderTree01 (syncId ...|1)");
            Pump(1500);

            // 2. Place a straight Small Road segment.
            Send(new NetPlaceCommand
            {
                SyncId = syncBase | 2,
                PrefabType = "RoadPrefab", PrefabName = "Small Road",
                Ax = 200f, Ay = 480f, Az = 200f,
                Bx = 232f, By = 480f, Bz = 200f,
                Cx = 264f, Cy = 480f, Cz = 200f,
                Dx = 296f, Dy = 480f, Dz = 200f,
                RandomSeed = 777,
            });
            Log("sent NetPlaceCommand Small Road (200,200)->(296,200)");
            Pump(2500);

            // 3. Delete the tree again (delete path = the old crasher).
            Send(new DeleteCommand { SyncId = syncBase | 1 });
            Log("sent DeleteCommand for the tree");
            Pump(2500);

            Send(new ChatMessageCommand { Username = _username, Message = "bot done" });
            Pump(6000);

            Log("=== RECEIVED FROM HOST ===");
            Log($"money={_recvMoney} speed={_recvSpeed} prog={_recvProg} chat={_recvChat} cursor={_recvCursor} other={_recvOther}");
            bool hostAuthoritativeFlowing = _recvMoney >= 1 && _recvSpeed >= 1;
            Log(hostAuthoritativeFlowing
                ? "BOT-RESULT host-authoritative: PASS (money+speed flowing over the real wire)"
                : "BOT-RESULT host-authoritative: FAIL (missing money/speed from host)");

            _net.Stop();
            return hostAuthoritativeFlowing ? 0 : 1;
        }

        private static void Pump(int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                _net.PollEvents();
                Thread.Sleep(15);
            }
        }

        private static void Send(CommandBase cmd)
        {
            _server.Send(MessagePackSerializer.Serialize(cmd, _model), DeliveryMethod.ReliableOrdered);
        }

        private static void Log(string msg)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [bot] {msg}");
        }
    }
}
