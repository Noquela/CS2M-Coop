using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using CS2M.UI;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Input;
using Game.Rendering;
using Game.Simulation;
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
        private CameraUpdateSystem _cameraUpdateSystem;
        private TerrainSystem _terrainSystem;
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

        // v71: the raycast drops out for a frame or two while the mouse moves fast or no tool is
        // active, which used to hide the remote cursor instantly → it flickered/disappeared. Debounce:
        // hold the last VALID position and keep sending it for a tolerance window; only send the "hide"
        // packet after the raycast has been invalid for this many consecutive sends (~0.5 s at 20 Hz).
        // v73: ToolRaycastSystem.GetRaycastResult only has data while a tool is actively requesting a
        // raycast — the DefaultTool (mouse idling, no tool active) never asks for one, so the primary
        // path was invalid ~100% of idle time. SendLocalCursor now falls back to our own terrain
        // raycast (same recipe as Game.Debug.TerrainRaycastDebugSystem) whenever the primary misses,
        // so `valid` is true almost always; this debounce is now a second line of defense for the
        // rare frame where even the fallback raycast misses (e.g. mouse over UI or camera not ready).
        private int _invalidSends;
        private const int HideAfterInvalidSends = 10;

        // v50: last valid local cursor world position — /ping pings this spot.
        public static float3 LastLocalCursorPos;
        public static bool LastLocalCursorValid;

        protected override void OnCreate()
        {
            base.OnCreate();
            _raycast = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _uiSystem = World.GetOrCreateSystemManaged<UISystem>();
            _cameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
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
                else if (InputManager.instance.controlOverWorld
                         && _cameraUpdateSystem.TryGetViewer(out Viewer viewer))
                {
                    // v73 fallback: the DefaultTool (mouse idling, no tool active) never requests a
                    // raycast, so the primary path above is invalid almost all of idle time. Do our
                    // own terrain raycast — same recipe as the vanilla debug system — so the remote
                    // cursor stays visible while the player is just looking around.
                    Line3.Segment seg = ToolRaycastSystem.CalculateRaycastLine(viewer.camera);
                    TerrainHeightData heightData = _terrainSystem.GetHeightData();
                    if (TerrainUtils.Raycast(ref heightData, seg, true, out float t, out float3 normal, out Bounds3 hitBounds))
                    {
                        pos = MathUtils.Position(seg, t);
                        valid = !math.any(math.isnan(pos));
                    }
                }
            }
            catch
            {
                // Raycast not available this frame; report as invalid.
            }

            _lastValid = valid;
            _lastPos = pos;

            // v71 debounce: keep the cursor alive at its last good position through brief raycast
            // drop-outs (fast mouse / no active tool) instead of hiding it every stutter.
            float3 sendPos;
            bool sendValid;
            if (valid)
            {
                _invalidSends = 0;
                LastLocalCursorPos = pos;
                LastLocalCursorValid = true;
                sendPos = pos;
                sendValid = true;
            }
            else
            {
                _invalidSends++;
                if (_invalidSends < HideAfterInvalidSends && _sentValid && LastLocalCursorValid)
                {
                    // Within the tolerance window: resend the last VALID spot so the remote cursor
                    // holds steady instead of blinking out.
                    sendPos = LastLocalCursorPos;
                    sendValid = true;
                }
                else
                {
                    // Truly off the map/on UI for a while: send exactly one "hide", then stay quiet.
                    if (!_sentValid)
                    {
                        return;
                    }

                    sendPos = default;
                    sendValid = false;
                }
            }

            _sentValid = sendValid;

            string username = NetworkInterface.Instance.LocalPlayer.Username;
            if (string.IsNullOrEmpty(username))
            {
                username = "Player";
            }

            Command.SendToAll?.Invoke(new PlayerCursorCommand
            {
                X = sendPos.x,
                Y = sendPos.y,
                Z = sendPos.z,
                Valid = sendValid,
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
