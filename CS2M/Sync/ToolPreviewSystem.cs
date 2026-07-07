using Colossal.Mathematics;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     v55: LIVE TOOL PREVIEW. The LOCAL side reads the net tool's in-progress ghost
    ///     (<c>Temp</c>+<c>Curve</c>+<c>Edge</c> — only the local drag makes those) ~12 Hz and
    ///     broadcasts the curve; the REMOTE side draws every OTHER player's preview as a curve in that
    ///     player's cursor color, so you SEE a friend drawing a road before they place it. Runs in the
    ///     Rendering phase (works while paused) and is inert whenever nobody is dragging.
    ///
    ///     v56: extended to OBJECT placement ghosts too — building/farm/service/prop, anything the
    ///     object tool is positioning (<c>Temp</c>+<c>PrefabRef</c>+<c>Transform</c>+<c>ObjectGeometry</c>,
    ///     which the road ghost never carries). Up to <see cref="MaxPreviewObjects"/> footprints ride
    ///     in the SAME packet as the road curve and are drawn as rotated rectangles on the ground in
    ///     the sender's color. The road path is untouched — this is purely additive.
    /// </summary>
    public partial class ToolPreviewSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 5; // ~12 Hz at 60 fps
        private const int MaxPreviewObjects = 8; // cap a brush/zone drag's Temp flood
        private const float FootprintLineWidth = 0.5f;

        private EntityQuery _preview;
        private EntityQuery _objPreview;
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
            // v56: object-placement ghost. Requires ObjectGeometry so we only pick up entities that
            // actually have a footprint to draw, and excludes Curve so a road ghost (already captured
            // by _preview above) is never double-counted here.
            _objPreview = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<Game.Objects.ObjectGeometry>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Curve>(),
                },
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

            bool curveActive = false;
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
                            curveActive = true;
                        }
                    }
                }
                finally
                {
                    arr.Dispose();
                }
            }

            float[] objPosX = null, objPosY = null, objPosZ = null;
            float[] objRotY = null, objSizeX = null, objSizeZ = null;
            int objCount = CaptureObjects(ref objPosX, ref objPosY, ref objPosZ, ref objRotY, ref objSizeX, ref objSizeZ);
            bool anyActive = curveActive || objCount > 0;

            // Send exactly one "hide" on active->inactive (either kind), then stay quiet until dragging
            // (road OR object) again.
            if (!anyActive && !_sentActive)
            {
                return;
            }

            _sentActive = anyActive;
            string username = NetworkInterface.Instance.LocalPlayer.Username;
            if (string.IsNullOrEmpty(username))
            {
                username = "Player";
            }

            Command.SendToAll?.Invoke(new ToolPreviewCommand
            {
                Active = curveActive,
                Username = username,
                Ax = bez.a.x, Ay = bez.a.y, Az = bez.a.z,
                Bx = bez.b.x, By = bez.b.y, Bz = bez.b.z,
                Cx = bez.c.x, Cy = bez.c.y, Cz = bez.c.z,
                Dx = bez.d.x, Dy = bez.d.y, Dz = bez.d.z,
                ObjPosX = objPosX, ObjPosY = objPosY, ObjPosZ = objPosZ,
                ObjRotY = objRotY, ObjSizeX = objSizeX, ObjSizeZ = objSizeZ,
            });
        }

        /// <summary>
        ///     v56: captures up to <see cref="MaxPreviewObjects"/> in-progress object ghosts (whatever
        ///     the object tool is positioning — building/farm/service/prop/tree). Takes the FIRST
        ///     matches in query iteration order rather than sorting (cheap — this runs every throttled
        ///     tick), so a big brush/zone drag is capped instead of flooding the packet. Footprint size
        ///     comes from the PREFAB's <c>ObjectGeometryData.m_Size</c> (the ghost entity itself only
        ///     carries the empty <c>ObjectGeometry</c> tag, not the dimensions). Returns the object
        ///     count actually written (0 when nothing is active).
        /// </summary>
        private int CaptureObjects(
            ref float[] posX, ref float[] posY, ref float[] posZ,
            ref float[] rotY, ref float[] sizeX, ref float[] sizeZ)
        {
            if (_objPreview.IsEmptyIgnoreFilter)
            {
                return 0;
            }

            NativeArray<Entity> arr = _objPreview.ToEntityArray(Allocator.Temp);
            try
            {
                int count = math.min(arr.Length, MaxPreviewObjects);
                posX = new float[count];
                posY = new float[count];
                posZ = new float[count];
                rotY = new float[count];
                sizeX = new float[count];
                sizeZ = new float[count];

                for (int i = 0; i < count; i++)
                {
                    Entity e = arr[i];
                    Game.Objects.Transform tf = EntityManager.GetComponentData<Game.Objects.Transform>(e);

                    // Footprint dimensions live on the PREFAB, not the ghost — fall back to a small
                    // placeholder square if the prefab is missing ObjectGeometryData for some reason.
                    float sx = 4f;
                    float sz = 4f;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    if (EntityManager.HasComponent<ObjectGeometryData>(prefab))
                    {
                        float3 size = EntityManager.GetComponentData<ObjectGeometryData>(prefab).m_Size;
                        sx = size.x;
                        sz = size.z;
                    }

                    float3 fwd = math.forward(tf.m_Rotation);

                    posX[i] = tf.m_Position.x;
                    posY[i] = tf.m_Position.y;
                    posZ[i] = tf.m_Position.z;
                    rotY[i] = math.atan2(fwd.x, fwd.z); // yaw only — footprints don't need pitch/roll
                    sizeX[i] = sx;
                    sizeZ[i] = sz;
                }

                return count;
            }
            finally
            {
                arr.Dispose();
            }
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
                // Stale (peer stopped sending >3 s ago) drops both the road curve and any object
                // footprints for that peer.
                if ((now - kv.Value.LastUtc).TotalSeconds > 3.0)
                {
                    continue;
                }

                UnityEngine.Color col = CursorOverlay.ColorFor(kv.Key);
                col.a = 0.75f;

                // Road preview — unchanged from v55.
                if (kv.Value.Active)
                {
                    buffer.DrawCurve(col, kv.Value.Curve, 5f);
                }

                // v56: object-placement footprints.
                DrawObjectFootprints(buffer, col, kv.Value);
            }
        }

        /// <summary>
        ///     v56: draws each remote object ghost's footprint as a rotated rectangle on the ground
        ///     (four straight edges, SizeX×SizeZ, yawed by RotY, sitting at Pos.y) in the sender's
        ///     cursor color. No-op when the peer has no object ghost active (empty/null arrays).
        /// </summary>
        private static void DrawObjectFootprints(OverlayRenderSystem.Buffer buffer, UnityEngine.Color col, RemoteToolPreviews.PreviewState s)
        {
            float[] px = s.ObjPosX;
            if (px == null || px.Length == 0)
            {
                return;
            }

            for (int i = 0; i < px.Length; i++)
            {
                float3 pos = new float3(px[i], s.ObjPosY[i], s.ObjPosZ[i]);
                float hx = s.ObjSizeX[i] * 0.5f;
                float hz = s.ObjSizeZ[i] * 0.5f;
                float sinY = math.sin(s.ObjRotY[i]);
                float cosY = math.cos(s.ObjRotY[i]);

                float3 Corner(float lx, float lz) => pos + new float3(
                    lx * cosY + lz * sinY,
                    0f,
                    -lx * sinY + lz * cosY);

                float3 c0 = Corner(-hx, -hz);
                float3 c1 = Corner(hx, -hz);
                float3 c2 = Corner(hx, hz);
                float3 c3 = Corner(-hx, hz);

                buffer.DrawLine(col, new Line3.Segment(c0, c1), FootprintLineWidth);
                buffer.DrawLine(col, new Line3.Segment(c1, c2), FootprintLineWidth);
                buffer.DrawLine(col, new Line3.Segment(c2, c3), FootprintLineWidth);
                buffer.DrawLine(col, new Line3.Segment(c3, c0), FootprintLineWidth);
            }
        }
    }
}
