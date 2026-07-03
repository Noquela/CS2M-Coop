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
            // Portable: prefer the same env vars the build uses (CSII_MANAGEDPATH / CSII_LOCALMODSPATH),
            // fall back to the local dev paths so it still runs here with no env set. Keeps the bot
            // free of machine-specific paths for a public/upstream contribution.
            string managed = Environment.GetEnvironmentVariable("CSII_MANAGEDPATH");
            if (string.IsNullOrEmpty(managed))
            {
                managed = @"C:\JogosCrackeados\Cities.Skylines.II.v1.5.3f1\game\Cities2_Data\Managed";
            }

            string modsRoot = Environment.GetEnvironmentVariable("CSII_LOCALMODSPATH");
            string modDir = !string.IsNullOrEmpty(modsRoot)
                ? Path.Combine(modsRoot, "CS2M")
                : @"C:\Users\Bruno\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2M";
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
        private static string _mode = "act"; // act = build things; listen = assert relayed traffic arrives
        private static int _latencyMs; // --latency N: simulate bad internet (LiteNetLib built-in)
        private static int _lossPct;   // --loss P: simulate packet loss %

        private static volatile bool _preconditionsOk;
        private static volatile bool _worldDone;
        private static volatile bool _hostGone; // set if the host peer drops — a crash during a scenario
        private static PreconditionsErrorCommand _lastError;
        private static long _worldBytes;
        private static int _recvMoney, _recvSpeed, _recvProg, _recvChat, _recvCursor, _recvOther;
        private static int _recvObjectPlace, _recvNetPlace, _recvDelete;
        private static readonly HashSet<int> _sendersSeen = new HashSet<int>();

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
                if (args[i] == "--mode") { _mode = args[i + 1]; }
                if (args[i] == "--latency") { _latencyMs = int.Parse(args[i + 1]); }
                if (args[i] == "--loss") { _lossPct = int.Parse(args[i + 1]); }
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
            if (_latencyMs > 0 || _lossPct > 0)
            {
                _net.SimulateLatency = _latencyMs > 0;
                _net.SimulationMinLatency = _latencyMs / 2;
                _net.SimulationMaxLatency = _latencyMs;
                _net.SimulatePacketLoss = _lossPct > 0;
                _net.SimulationPacketLossChance = _lossPct;
                Log($"network simulation: latency<= {_latencyMs}ms loss={_lossPct}%");
            }
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
            listener.PeerDisconnectedEvent += (peer, info) => { _hostGone = true; Log($"disconnected: {info.Reason}"); };

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
                case ObjectPlaceCommand op:
                    _recvObjectPlace++;
                    _sendersSeen.Add(op.SenderId);
                    Log($"RELAYED object place: {op.PrefabName} sender={op.SenderId}");
                    break;
                case NetPlaceCommand np:
                    _recvNetPlace++;
                    _sendersSeen.Add(np.SenderId);
                    Log($"RELAYED net place: {np.PrefabName} sender={np.SenderId}");
                    break;
                case DeleteCommand dc:
                    _recvDelete++;
                    Log($"RELAYED delete sender={dc.SenderId}");
                    break;
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
            if (_mode == "listen")
            {
                return RunListenSession();
            }

            if (_mode == "hunt")
            {
                return RunHuntSession();
            }

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

        /// <summary>3rd-player validation: sit connected and assert that another CLIENT's actions
        /// reach us via the host's relay (the star-topology hop that was missing before v43).</summary>
        private static int RunListenSession()
        {
            Log("=== LISTEN mode: waiting for relayed traffic from the OTHER client ===");
            Pump(45000);

            Log("=== RECEIVED ===");
            Log($"objectPlace={_recvObjectPlace} netPlace={_recvNetPlace} delete={_recvDelete} " +
                $"money={_recvMoney} speed={_recvSpeed} senders=[{string.Join(",", _sendersSeen)}]");
            bool relayed = _recvObjectPlace >= 1 && _recvNetPlace >= 1 && _recvDelete >= 1;
            bool identified = _sendersSeen.Count > 0 && !_sendersSeen.Contains(0);
            Log(relayed && identified
                ? "BOT-RESULT relay: PASS (other client's actions relayed with a non-zero sender id)"
                : $"BOT-RESULT relay: FAIL (relayed={relayed} identified={identified})");

            _net.Stop();
            return relayed && identified ? 0 : 1;
        }

        /// <summary>Bug HUNTER: drives the host through the exact operations that broke in real field
        /// sessions (T/X junctions, overlapping roads, place/delete storms) so the host's own radars
        /// (InvariantCheck / [Guard] / not crashing) surface the bug — no friends, no second game.
        /// The bot can't see the host's world, so the harness greps the host log for the verdict; the
        /// bot itself reports whether the host survived each scenario (a drop mid-scenario = a crash).</summary>
        private static int RunHuntSession()
        {
            Log("=== HUNT: martelando os cenarios que bugam em campo ===");
            Pump(3000);
            Send(new ChatMessageCommand { Username = _username, Message = "bot hunt online" });
            Pump(1000);

            ulong sb = (ulong)new Random().Next(1 << 20) << 40;
            int step = 0;

            bool Alive(string what)
            {
                if (_hostGone) { Log($"*** HOST CAIU durante '{what}' <-- BUG (crash) ***"); return false; }
                Log($"[ok] host vivo apos: {what}");
                return true;
            }

            // 1. rua reta (baseline — mesma do run de validacao que ja funcionava)
            Road(sb | (ulong)++step, 200, 200, 296, 200);
            Log("[1] rua reta A (200,200)->(296,200)"); Pump(2500);
            if (!Alive("rua reta")) { return Done(false); }

            // 2. juncao T: rua B termina no MEIO da A (o caso que so o receptor precisa dividir)
            Road(sb | (ulong)++step, 248, 168, 248, 200);
            Log("[2] juncao T: B (248,168)->(248,200) encosta no meio de A"); Pump(3000);
            if (!Alive("juncao T")) { return Done(false); }

            // 3. cruzamento X: rua C cruza a A no meio
            Road(sb | (ulong)++step, 224, 232, 272, 168);
            Log("[3] cruzamento X: C cruza A"); Pump(3000);
            if (!Alive("cruzamento X")) { return Done(false); }

            // 4. duplicata: repete a A exata (deve ser ignorada pelo CoveredByExistingEdge)
            Road(sb | (ulong)++step, 200, 200, 296, 200);
            Log("[4] duplicata: repete A (guard deve ignorar)"); Pump(3000);
            if (!Alive("duplicata")) { return Done(false); }

            // 5. rajada place/delete (estressa a fila de aplicacao e o cleanup)
            Log("[5] rajada: 8 ruas + delete de todas");
            var ids = new List<ulong>();
            for (int i = 0; i < 8; i++)
            {
                ulong id = sb | (ulong)++step; ids.Add(id);
                Road(id, 150 + i * 18, 260, 150 + i * 18, 300); Pump(300);
            }
            Pump(1500);
            foreach (ulong id in ids) { Send(new DeleteCommand { SyncId = id }); Pump(200); }
            Pump(3000);
            if (!Alive("rajada place/delete")) { return Done(false); }

            // 6. objeto: planta uma arvore (caminho de objeto + owner)
            Send(new ObjectPlaceCommand
            {
                SyncId = sb | (ulong)++step, PrefabType = "StaticObjectPrefab", PrefabName = "EU_AlderTree01",
                PosX = 160f, PosY = 480f, PosZ = 160f, RotW = 1f, RandomSeed = 99,
            });
            Log("[6] objeto: arvore"); Pump(2000);
            if (!Alive("objeto")) { return Done(false); }

            // 7. CONCORRENCIA: duas ruas identicas no mesmo instante, SEM pump entre elas — simula
            // dois players desenhando em cima um do outro ao mesmo tempo (chegam no mesmo batch).
            // O dedup por posicao (v54) tem que segurar a segunda mesmo com SyncIds diferentes.
            Log("[7] concorrencia: 2x a MESMA rua sem intervalo (dois players no mesmo ponto)");
            Road(sb | (ulong)++step, 500, 500, 560, 500);
            Road(sb | (ulong)++step, 500, 500, 560, 500);
            Pump(3500);
            if (!Alive("concorrencia (2x mesma rua)")) { return Done(false); }

            // 8. CONCORRENCIA cruzada: duas ruas que se cruzam, disparadas juntas (split simultaneo).
            Log("[8] concorrencia cruzada: 2 ruas que se cruzam, no mesmo instante");
            Road(sb | (ulong)++step, 520, 470, 520, 530);
            Road(sb | (ulong)++step, 490, 500, 550, 500);
            Pump(3500);
            if (!Alive("concorrencia cruzada")) { return Done(false); }

            // 9. juncao estrela: 4 ruas encontrando no MESMO ponto (grau 4+ num no).
            Log("[9] juncao estrela: 4 ruas num ponto (600,600)");
            Road(sb | (ulong)++step, 560, 600, 600, 600);
            Road(sb | (ulong)++step, 600, 600, 640, 600);
            Road(sb | (ulong)++step, 600, 560, 600, 600);
            Road(sb | (ulong)++step, 600, 600, 600, 640);
            Pump(4000);
            if (!Alive("juncao estrela (4 vias)")) { return Done(false); }

            // 10. RACE place/delete: coloca A, coloca B em cima, deleta A — tudo junto (mesmo batch).
            Log("[10] race: A + B sobreposta + delete de A no mesmo instante");
            ulong ra = sb | (ulong)++step;
            Road(ra, 700, 600, 760, 600);
            Pump(200);
            Road(sb | (ulong)++step, 700, 600, 760, 600);
            Send(new DeleteCommand { SyncId = ra });
            Pump(3500);
            if (!Alive("race place/delete sobreposto")) { return Done(false); }

            // 11. re-colocacao: place -> delete -> place a MESMA rua (id novo) — o guard nao pode
            // deixar sobra nem recusar a rua legitima depois do delete.
            Log("[11] re-colocacao: place -> delete -> place a mesma");
            ulong rc = sb | (ulong)++step;
            Road(rc, 700, 660, 760, 660);
            Pump(800);
            Send(new DeleteCommand { SyncId = rc });
            Pump(800);
            Road(sb | (ulong)++step, 700, 660, 760, 660);
            Pump(3500);
            if (!Alive("re-colocacao apos delete")) { return Done(false); }

            Send(new ChatMessageCommand { Username = _username, Message = "bot hunt done" });
            Log("aguardando o InvariantCheck do host varrer pos-cenarios (25s)...");
            Pump(25000);
            return Done(!_hostGone);
        }

        private static int Done(bool alive)
        {
            Log("=== HUNT FIM ===");
            Log($"host vivo={alive}  recv(money={_recvMoney} netPlace={_recvNetPlace} delete={_recvDelete} other={_recvOther})");
            // com so o bot conectado, o host NAO deve devolver os comandos do bot (nao ha outro cliente).
            if (_recvNetPlace > 0)
            {
                Log($"ATENCAO: recebi {_recvNetPlace} netPlace de volta — possivel eco/relay indevido (sem outro cliente nao devia)");
            }

            Log(alive
                ? "BOT-RESULT hunt: PASS (host sobreviveu a todos os cenarios) — ver host log por [Invariant]/[Guard]"
                : "BOT-RESULT hunt: FAIL (host caiu num cenario — bug reproduzido)");
            _net.Stop();
            return alive ? 0 : 1;
        }

        /// <summary>Send a straight Small Road A->D (b,c interpolated at 1/3, 2/3).</summary>
        private static void Road(ulong id, float ax, float az, float dx, float dz)
        {
            float bx = ax + (dx - ax) / 3f, bz = az + (dz - az) / 3f;
            float cx = ax + 2f * (dx - ax) / 3f, cz = az + 2f * (dz - az) / 3f;
            Send(new NetPlaceCommand
            {
                SyncId = id, PrefabType = "RoadPrefab", PrefabName = "Small Road",
                Ax = ax, Ay = 480f, Az = az, Bx = bx, By = 480f, Bz = bz,
                Cx = cx, Cy = 480f, Cz = cz, Dx = dx, Dy = 480f, Dz = dz,
                RandomSeed = (int)(id & 0xffff),
            });
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
