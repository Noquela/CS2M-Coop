using Colossal.Mathematics;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace CS2M.Sync
{
    /// <summary>
    ///     v55: LIVE TOOL PREVIEW. The LOCAL side reads the net tool's in-progress ghost
    ///     (<c>Temp</c>+<c>Curve</c>+<c>Edge</c> — only the local drag makes those) ~12 Hz and
    ///     broadcasts the curve; the REMOTE side draws every OTHER player's preview as a curve in that
    ///     player's cursor color, so you SEE a friend drawing a road before they place it. Runs in the
    ///     Rendering phase (works while paused) and is inert whenever nobody is dragging.
    /// </summary>
    public partial class ToolPreviewSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 5; // ~12 Hz at 60 fps

        private EntityQuery _preview;
        private OverlayRenderSystem _overlay;
        private int _frame;
        private bool _sentActive;

        protected override void OnCreate()
        {
            base.OnCreate();
            _preview = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<Edge>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            CS2M.Log.Info("[Preview] ToolPreviewSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            SendLocalPreview();
            DrawRemotePreviews();
        }

        private void SendLocalPreview()
        {
            if (++_frame < SendEveryNFrames)
            {
                return;
            }

            _frame = 0;

            bool active = false;
            Bezier4x3 bez = default;
            if (!_preview.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> arr = _preview.ToEntityArray(Allocator.Temp);
                try
                {
                    float best = -1f;
                    foreach (Entity e in arr)
                    {
                        Bezier4x3 c = EntityManager.GetComponentData<Curve>(e).m_Bezier;
                        float len = MathUtils.Length(c);
                        if (len > best)
                        {
                            best = len;
                            bez = c;
                            active = true;
                        }
                    }
                }
                finally
                {
                    arr.Dispose();
                }
            }

            // Send exactly one "hide" on active->inactive, then stay quiet until dragging again.
            if (!active && !_sentActive)
            {
                return;
            }

            _sentActive = active;
            string username = NetworkInterface.Instance.LocalPlayer.Username;
            if (string.IsNullOrEmpty(username))
            {
                username = "Player";
            }

            Command.SendToAll?.Invoke(new ToolPreviewCommand
            {
                Active = active,
                Username = username,
                Ax = bez.a.x, Ay = bez.a.y, Az = bez.a.z,
                Bx = bez.b.x, By = bez.b.y, Bz = bez.b.z,
                Cx = bez.c.x, Cy = bez.c.y, Cz = bez.c.z,
                Dx = bez.d.x, Dy = bez.d.y, Dz = bez.d.z,
            });
        }

        private void DrawRemotePreviews()
        {
            var previews = RemoteToolPreviews.Snapshot();
            if (previews.Count == 0)
            {
                return;
            }

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle deps);
            deps.Complete();

            System.DateTime now = System.DateTime.UtcNow;
            foreach (var kv in previews)
            {
                // Stale (peer stopped sending >3 s ago) or explicitly inactive previews don't draw.
                if (!kv.Value.Active || (now - kv.Value.LastUtc).TotalSeconds > 3.0)
                {
                    continue;
                }

                UnityEngine.Color col = CursorOverlay.ColorFor(kv.Key);
                col.a = 0.75f;
                buffer.DrawCurve(col, kv.Value.Curve, 5f);
            }
        }
    }
}
