using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using CS2M.API.Commands;

namespace CS2M.Commands
{
    /// <summary>
    ///     v52: passive session recorder ("wire-tap"). When the CS2M_WIRETAP=1 environment variable
    ///     is set, EVERY command that crosses the wire — in either direction — is appended as one
    ///     JSONL line to a recording file in the game's LocalLow folder. It is purely observational:
    ///     it never mutates a command or any game state, and every IO path is guarded so a disk error
    ///     can only silence the tap, never crash the session.
    ///
    ///     Why: two live simulations interacting produce field bugs that solo testing misses. With the
    ///     tap on, each player's session is a deterministic recording — diff two players' files (or
    ///     read the host's around the timestamp of a bug) to see exactly where their command streams
    ///     diverged, who sent what, and in what order. Off by default = zero overhead in normal play
    ///     (Enabled is a false constant, so the call sites compile to a never-taken branch).
    ///
    ///     One tap point per direction, both funneling through MessagePack, so nothing escapes:
    ///       OUT — CommandInternal.Serialize (every send AND every host relay)
    ///       IN  — NetworkManager.ListenerOnNetworkReceiveEvent (every received packet, with peer id)
    /// </summary>
    public static class WireTap
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("CS2M_WIRETAP") == "1";

        private static readonly object Gate = new object();
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropCache =
            new ConcurrentDictionary<Type, PropertyInfo[]>();
        private static long _seq;
        private static StreamWriter _writer;
        private static bool _initTried;
        private static bool _dead;

        /// <summary>
        ///     Append one JSONL line describing a command. <paramref name="dir"/> is "OUT" or "IN";
        ///     <paramref name="peerId"/> is the LiteNetLib peer id for received packets (-1 when N/A).
        ///     Never throws: any failure permanently disables the tap for the rest of the session.
        /// </summary>
        public static void Record(string dir, CommandBase cmd, int peerId = -1)
        {
            if (!Enabled || _dead || cmd == null)
            {
                return;
            }

            try
            {
                long n = Interlocked.Increment(ref _seq);
                string line = Build(n, dir, cmd, peerId);
                lock (Gate)
                {
                    if (!_initTried)
                    {
                        _initTried = true;
                        Open();
                    }

                    if (_writer == null)
                    {
                        _dead = true;
                        return;
                    }

                    _writer.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                _dead = true;
                try { CS2M.Log.Info($"[WireTap] disabled after error: {ex.Message}"); }
                catch { /* logging itself failed — give up silently */ }
            }
        }

        private static void Open()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE");
            string dir = string.IsNullOrEmpty(profile)
                ? Path.GetTempPath()
                : Path.Combine(profile, "AppData", "LocalLow", "Colossal Order", "Cities Skylines II");
            if (!Directory.Exists(dir))
            {
                dir = Path.GetTempPath();
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, $"CS2M_wiretap_{stamp}.jsonl");
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };

            string ver = typeof(WireTap).Assembly.GetName().Version?.ToString() ?? "?";
            _writer.WriteLine(
                $"{{\"seq\":0,\"t\":\"{Now()}\",\"dir\":\"META\",\"type\":\"WireTapStart\",\"version\":\"{ver}\"}}");
            CS2M.Log.Info($"[WireTap] recording every command to {path}");
        }

        private static string Build(long n, string dir, CommandBase cmd, int peerId)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"seq\":").Append(n)
              .Append(",\"t\":\"").Append(Now()).Append('"')
              .Append(",\"dir\":\"").Append(dir).Append('"')
              .Append(",\"type\":\"").Append(cmd.GetType().Name).Append('"')
              .Append(",\"sender\":").Append(cmd.SenderId);
            if (peerId >= 0)
            {
                sb.Append(",\"peer\":").Append(peerId);
            }

            // Best-effort dump of the command's own public data (coords, prefab, ids, flags). Reading
            // auto-property getters on these plain data classes is side-effect-free; each is still
            // guarded, and both the count and each value's length are capped so one command can never
            // blow up a line.
            PropertyInfo[] props = PropCache.GetOrAdd(cmd.GetType(), ReadableProps);
            int shown = 0;
            foreach (PropertyInfo p in props)
            {
                if (shown >= 16)
                {
                    sb.Append(",\"_more\":").Append(props.Length - shown);
                    break;
                }

                object v;
                try { v = p.GetValue(cmd, null); }
                catch { continue; }

                sb.Append(",\"").Append(p.Name).Append("\":").Append(JsonVal(v));
                shown++;
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static PropertyInfo[] ReadableProps(Type t)
        {
            var list = new List<PropertyInfo>();
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                if (p.Name == "SenderId")
                {
                    continue; // already emitted as "sender"
                }

                list.Add(p);
            }

            return list.ToArray();
        }

        private static string JsonVal(object v)
        {
            switch (v)
            {
                case null:
                    return "null";
                case bool b:
                    return b ? "true" : "false";
                case float f:
                    return f.ToString("0.###", CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("0.###", CultureInfo.InvariantCulture);
                case int _:
                case long _:
                case short _:
                case byte _:
                case sbyte _:
                case uint _:
                case ulong _:
                case ushort _:
                    return Convert.ToString(v, CultureInfo.InvariantCulture);
            }

            if (v is string str)
            {
                if (str.Length > 80)
                {
                    str = str.Substring(0, 80);
                }

                return Quote(str);
            }

            // v53: dump array/collection contents (capped) instead of "System.Int32[]" — makes the
            // wiretap-diff analyzer able to tell array-carrying commands apart (zone paints, routes).
            if (v is System.Collections.IEnumerable seq)
            {
                var arr = new StringBuilder("[");
                int n = 0;
                foreach (object item in seq)
                {
                    if (n >= 48)
                    {
                        arr.Append(",\"...\"");
                        break;
                    }

                    if (n > 0)
                    {
                        arr.Append(',');
                    }

                    arr.Append(JsonVal(item));
                    n++;
                }

                arr.Append(']');
                return arr.ToString();
            }

            string s = v.ToString();
            if (s.Length > 80)
            {
                s = s.Substring(0, 80);
            }

            return Quote(s);
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        private static string Now()
        {
            return DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }
}
