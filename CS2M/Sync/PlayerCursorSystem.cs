using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using CS2M.UI;
using Game;
using Game.Common;
using Game.Rendering;
using Game.Tools;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     First real gameplay-sync feature: shares each player's mouse cursor.
    ///     Reads the local player's cursor world position (from the tool raycast) and
    ///     broadcasts it a few times per second; every frame it draws the other
    ///     players' cursors as colored circles (overlay) plus a name label (UI),
    ///     interpolating between updates so movement looks smooth.
    ///     Runs in the Rendering phase, so it also works while the simulation is paused.
    /// </summary>
    public partial class PlayerCursorSystem : GameSystemBase
    {
        // ~20 Hz at 60 fps. Interpolation smooths this into continuous motion.
        private const int SendEveryNFrames = 3;

        // How fast the drawn cursor eases toward the latest received position.
        private const float Smoothing = 0.3f;

        // Diagnostic summary roughly every 6 s.
        private const int LogEveryNFrames = 360;

        // Hex colors matching CursorOverlay.Palette (used for the UI name labels).
        private static readonly string[] PaletteHex =
        {
            "#3399FF", "#FF7333", "#4DD959", "#E64DCC", "#FFD933",
        };

        private ToolRaycastSystem _raycast;
        private OverlayRenderSystem _overlay;
        private UISystem _uiSystem;
        private int _frame;
        private int _logFrame;

        // Per-player interpolated (drawn) position — main-thread only.
        private readonly Dictionary<int, float3> _rendered = new Dictionary<int, float3>();
        private readonly StringBuilder _json = new StringBuilder(256);
        private bool _hadLabels;

        // Last local raycast state, kept only for the diagnostic log line.
        private bool _lastValid;
        private float3 _lastPos;
        private bool _sentValid; // last state we actually sent (send one "hide" on valid→invalid)
        private int _lastLabelCount;

        // v50: last valid local cursor world position — /ping pings this spot.
        public static float3 LastLocalCursorPos;
        public static bool LastLocalCursorValid;

        protected override void OnCreate()
        {
            base.OnCreate();
            _raycast = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _uiSystem = World.GetOrCreateSystemManaged<UISystem>();
            CS2M.Log.Info("[Cursor] PlayerCursorSystem created");
        }

        protected override void OnUpdate()
        {
            // Only active during a running multiplayer session.
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            SendLocalCursor();
            int drawn = DrawRemoteCursorsAndLabels();
            DrawMapPings();

            if (++_logFrame >= LogEveryNFrames)
            {
                _logFrame = 0;
                int remote = RemotePlayerCursors.Snapshot().Count;
                CS2M.Log.Info(
                    $"[Cursor] raycastValid={_lastValid} pos=({_lastPos.x:F0},{_lastPos.y:F0},{_lastPos.z:F0}) " +
                    $"remoteCursors={remote} drawn={drawn} labels={_lastLabelCount}");
            }
        }

        private void SendLocalCursor()
        {
            if (++_frame < SendEveryNFrames)
            {
                return;
            }

            _frame = 0;

            bool valid = false;
            float3 pos = default;
            try
            {
                if (_raycast.GetRaycastResult(out RaycastResult result))
                {
                    pos = result.m_Hit.m_HitPosition;
                    valid = !math.any(math.isnan(pos));
                }
            }
            catch
            {
                // Raycast not available this frame; report as invalid.
            }

            _lastValid = valid;
            _lastPos = pos;
            if (valid)
            {
                LastLocalCursorPos = pos;
                LastLocalCursorValid = true;
            }

            // Don't spam 20 Hz invalid packets while the mouse sits on the UI: send exactly one
            // "hide" packet on the valid→invalid transition, then stay quiet until valid again.
            if (!valid && !_sentValid)
            {
                return;
            }

            _sentValid = valid;

            string username = NetworkInterface.Instance.LocalPlayer.Username;
            if (string.IsNullOrEmpty(username))
            {
                username = "Player";
            }

            Command.SendToAll?.Invoke(new PlayerCursorCommand
            {
                X = pos.x,
                Y = pos.y,
                Z = pos.z,
                Valid = valid,
                Username = username,
            });
        }

        private int DrawRemoteCursorsAndLabels()
        {
            var cursors = RemotePlayerCursors.Snapshot();
            if (cursors.Count == 0)
            {
                _rendered.Clear();
                PushLabels("[]");
                return 0;
            }

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            _json.Clear();
            _json.Append('[');
            bool firstLabel = true;

            // NOTE: we deliberately do NOT skip by player id. CS2M never assigns
            // PlayerId, so every SenderId is 0; a self-skip would wrongly drop the only
            // remote cursor. A player never receives its own cursor packet anyway.
            int drawn = 0;
            int labelCount = 0;
            System.DateTime nowUtc = System.DateTime.UtcNow;
            foreach (var kv in cursors)
            {
                // Hidden (remote mouse on UI) or stale (peer frozen/silent >3 s) cursors don't draw.
                if (!kv.Value.Visible || (nowUtc - kv.Value.LastValidUtc).TotalSeconds > 3.0)
                {
                    _rendered.Remove(kv.Key);
                    continue;
                }

                float3 target = kv.Value.Target;
                float3 current = _rendered.TryGetValue(kv.Key, out float3 prev)
                    ? math.lerp(prev, target, Smoothing)
                    : target;
                _rendered[kv.Key] = current;

                string hex = PaletteHex[((kv.Key % PaletteHex.Length) + PaletteHex.Length) % PaletteHex.Length];
                CursorOverlay.DrawCursor(buffer, kv.Key, current);
                drawn++;

                // Add a screen-space name label if the point is on screen.
                if (CursorProjection.TryProject(current, out float nx, out float ny))
                {
                    if (!firstLabel)
                    {
                        _json.Append(',');
                    }

                    firstLabel = false;
                    labelCount++;
                    _json.Append("{\"x\":").Append(nx.ToString("F4", CultureInfo.InvariantCulture))
                        .Append(",\"y\":").Append(ny.ToString("F4", CultureInfo.InvariantCulture))
                        .Append(",\"n\":\"").Append(JsonEscape(kv.Value.Username))
                        .Append("\",\"c\":\"").Append(hex).Append("\"}");
                }
            }

            _json.Append(']');
            _lastLabelCount = labelCount;
            PushLabels(_json.ToString());

            CleanupRendered(cursors);
            return drawn;
        }

        /// <summary>v50: pulsing "look here!" markers — expanding rings + a steady center dot,
        /// colored per pinging player, fading out over the ping's lifetime.</summary>
        private void DrawMapPings()
        {
            List<MapPingSync.Ping> pings = MapPingSync.Snapshot();
            if (pings.Count == 0)
            {
                return;
            }

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            System.DateTime now = System.DateTime.UtcNow;
            foreach (MapPingSync.Ping ping in pings)
            {
                float remaining = (float) (ping.ExpiresUtc - now).TotalSeconds;
                if (remaining <= 0f)
                {
                    continue;
                }

                float age = MapPingSync.LifetimeSeconds - remaining;
                float fade = math.saturate(remaining / MapPingSync.LifetimeSeconds) * 0.9f + 0.1f;
                UnityEngine.Color color = CursorOverlay.ColorFor(ping.PlayerId);
                color.a = fade;

                // Steady dot + two rings expanding on a 1-second cycle, phase-shifted.
                buffer.DrawCircle(color, ping.Position, 10f);
                float cycle = age - (float) math.floor(age);
                var faint = new UnityEngine.Color(color.r, color.g, color.b, fade * (1f - cycle) * 0.8f);
                buffer.DrawCircle(faint, ping.Position, 20f + cycle * 60f);
                float cycle2 = cycle + 0.5f > 1f ? cycle - 0.5f : cycle + 0.5f;
                var faint2 = new UnityEngine.Color(color.r, color.g, color.b, fade * (1f - cycle2) * 0.8f);
                buffer.DrawCircle(faint2, ping.Position, 20f + cycle2 * 60f);
            }
        }

        private void PushLabels(string json)
        {
            // Avoid spamming the UI with "[]" every frame once it's already empty.
            bool hasLabels = json.Length > 2;
            if (!hasLabels && !_hadLabels)
            {
                return;
            }

            _hadLabels = hasLabels;
            _uiSystem?.SetCursorLabels(json);
        }

        private void CleanupRendered(List<KeyValuePair<int, RemotePlayerCursors.CursorState>> cursors)
        {
            if (_rendered.Count <= cursors.Count)
            {
                return;
            }

            var present = new HashSet<int>();
            foreach (var kv in cursors)
            {
                present.Add(kv.Key);
            }

            var stale = new List<int>();
            foreach (int id in _rendered.Keys)
            {
                if (!present.Contains(id))
                {
                    stale.Add(id);
                }
            }

            foreach (int id in stale)
            {
                _rendered.Remove(id);
            }
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
