using System.Reflection;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Global toggle for the v56 input-replay path. OFF by default (env CS2M_REPLAY=1) so the
    /// proven reconstruct path (NetDetector→NetPlaceApply) stays in charge until replay is validated.</summary>
    public static class NetToolReplay
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_REPLAY") == "1" ? 1 : 0;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     INPUT-REPLAY sender (v56, M2): when the local player APPLIES the net tool, capture its raw input
    ///     — the ControlPoint list + prefab/mode/seed/config — and broadcast a <see cref="NetToolReplayCommand"/>
    ///     so every other PC replays it through the game's own CreateDefinitionsJob (identical topology by
    ///     construction). Gated on <see cref="NetToolReplay.Enabled"/>; when on, NetDetectorSystem suppresses
    ///     its Applied-edge send so we don't double-sync. applyMode + GetControlPoints are public; m_Mode,
    ///     m_Prefab and m_RandomSeed are read by reflection.
    /// </summary>
    public partial class NetToolCaptureSystem : GameSystemBase
    {
        private ToolSystem _toolSystem;
        private PrefabSystem _prefabSystem;
        private Game.City.CityConfigurationSystem _cityConfig;
        private bool _appliedLastFrame;

        private static FieldInfo _modeField, _prefabField, _seedField, _rsSeedField;

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _cityConfig = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
            CS2M.Log.Info($"[Replay] NetToolCaptureSystem created (enabled={NetToolReplay.Enabled})");
        }

        protected override void OnUpdate()
        {
            if (!NetToolReplay.Enabled
                || NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (!(_toolSystem.activeTool is NetToolSystem netTool))
            {
                _appliedLastFrame = false;
                return;
            }

            // Edge-trigger on the Apply frame so we ship exactly once per placement.
            bool applying = netTool.applyMode == ApplyMode.Apply;
            if (applying && !_appliedLastFrame)
            {
                try { Capture(netTool); }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] replay capture failed: {ex.Message}"); }
            }

            _appliedLastFrame = applying;
        }

        private void Capture(NetToolSystem netTool)
        {
            NativeList<ControlPoint> pts = netTool.GetControlPoints(out JobHandle deps);
            deps.Complete();
            int n = pts.Length;
            if (n < 2)
            {
                return;
            }

            // Prefab (reflection: private m_Prefab is a NetPrefab).
            var prefab = GetField(ref _prefabField, netTool, "m_Prefab") as PrefabBase;
            if (prefab == null)
            {
                return;
            }

            int mode = (int) (NetToolSystem.Mode) GetField(ref _modeField, netTool, "m_Mode");
            int seed = ReadSeed(netTool);

            var cmd = new NetToolReplayCommand
            {
                PrefabType = prefab.GetType().Name,
                PrefabName = prefab.name,
                Mode = mode,
                RandomSeed = seed,
                EditorMode = _toolSystem.actionMode.IsEditor(),
                LeftHandTraffic = _cityConfig.leftHandTraffic,
                RemoveUpgrade = false,
                ParallelOffset = 0f,
                ParallelCount = 0,
                PosX = new float[n], PosY = new float[n], PosZ = new float[n],
                HitX = new float[n], HitY = new float[n], HitZ = new float[n],
                DirX = new float[n], DirZ = new float[n],
                HitDirX = new float[n], HitDirY = new float[n], HitDirZ = new float[n],
                RotX = new float[n], RotY = new float[n], RotZ = new float[n], RotW = new float[n],
                SnapPriX = new float[n], SnapPriY = new float[n],
                ElemIdxX = new int[n], ElemIdxY = new int[n],
                CurvePos = new float[n], Elev = new float[n],
                SnapPosX = new float[n], SnapPosZ = new float[n],
                SnapKind = new int[n], SnapNodeId = new ulong[n],
            };

            for (int i = 0; i < n; i++)
            {
                ControlPoint cp = pts[i];
                cmd.PosX[i] = cp.m_Position.x; cmd.PosY[i] = cp.m_Position.y; cmd.PosZ[i] = cp.m_Position.z;
                cmd.HitX[i] = cp.m_HitPosition.x; cmd.HitY[i] = cp.m_HitPosition.y; cmd.HitZ[i] = cp.m_HitPosition.z;
                cmd.DirX[i] = cp.m_Direction.x; cmd.DirZ[i] = cp.m_Direction.y;
                cmd.HitDirX[i] = cp.m_HitDirection.x; cmd.HitDirY[i] = cp.m_HitDirection.y; cmd.HitDirZ[i] = cp.m_HitDirection.z;
                cmd.RotX[i] = cp.m_Rotation.value.x; cmd.RotY[i] = cp.m_Rotation.value.y;
                cmd.RotZ[i] = cp.m_Rotation.value.z; cmd.RotW[i] = cp.m_Rotation.value.w;
                cmd.SnapPriX[i] = cp.m_SnapPriority.x; cmd.SnapPriY[i] = cp.m_SnapPriority.y;
                cmd.ElemIdxX[i] = cp.m_ElementIndex.x; cmd.ElemIdxY[i] = cp.m_ElementIndex.y;
                cmd.CurvePos[i] = cp.m_CurvePosition; cmd.Elev[i] = cp.m_Elevation;
                WriteSnap(cmd, i, cp.m_OriginalEntity);
            }

            Command.SendToAll?.Invoke(cmd);
            CS2M.Log.Info($"[Replay] CAPTURE+SEND name={prefab.name} mode={mode} points={n}");
        }

        /// <summary>Translate the machine-local m_OriginalEntity to a cross-machine snap descriptor:
        /// node → stable id + position; edge → position; else none.</summary>
        private void WriteSnap(NetToolReplayCommand cmd, int i, Entity e)
        {
            cmd.SnapKind[i] = 0;
            cmd.SnapNodeId[i] = 0;
            cmd.SnapPosX[i] = 0f;
            cmd.SnapPosZ[i] = 0f;
            if (e == Entity.Null || !EntityManager.Exists(e))
            {
                return;
            }

            if (EntityManager.HasComponent<Node>(e))
            {
                float3 p = EntityManager.GetComponentData<Node>(e).m_Position;
                cmd.SnapKind[i] = 1;
                cmd.SnapNodeId[i] = CS2M_NodeSyncIds.Ensure(EntityManager, e);
                cmd.SnapPosX[i] = p.x; cmd.SnapPosZ[i] = p.z;
            }
            else if (EntityManager.HasComponent<Curve>(e))
            {
                float3 p = EntityManager.GetComponentData<Curve>(e).m_Bezier.a;
                cmd.SnapKind[i] = 2;
                cmd.SnapPosX[i] = p.x; cmd.SnapPosZ[i] = p.z;
            }
        }

        private int ReadSeed(NetToolSystem netTool)
        {
            object rs = GetField(ref _seedField, netTool, "m_RandomSeed");
            if (rs == null)
            {
                return 0;
            }

            if (_rsSeedField == null)
            {
                _rsSeedField = typeof(Game.Common.RandomSeed).GetField("m_Seed",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            return (int) (uint) _rsSeedField.GetValue(rs);
        }

        private static object GetField(ref FieldInfo cache, object target, string name)
        {
            if (cache == null)
            {
                cache = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            }

            return cache?.GetValue(target);
        }
    }
}
