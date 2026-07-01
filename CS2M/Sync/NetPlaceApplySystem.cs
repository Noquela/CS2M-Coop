using Colossal.Mathematics;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Materializes nets placed by remote players. Rebuilds the exact <c>Bezier4x3</c> from the
    ///     command, wraps it in a <c>NetCourse</c> + <c>CreationDefinition(Permanent|SubElevation)</c>,
    ///     and injects it into <c>ModificationBarrier1</c>'s command buffer — the standing
    ///     <c>GenerateNodesSystem</c>/<c>GenerateEdgesSystem</c> build the real nodes/edges/lanes from
    ///     it (works for roads, rails, pipes, power, fences — one pipeline). Coincident endpoints
    ///     auto-merge, so simple connected networks stitch up without cross-PC node references (v1).
    /// </summary>
    public partial class NetPlaceApplySystem : GameSystemBase
    {
        private ModificationBarrier1 _barrier;
        private PrefabSystem _prefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _barrier = World.GetOrCreateSystemManaged<ModificationBarrier1>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            CS2M.Log.Info("[Net] NetPlaceApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteNetQueue.TryDequeue(out NetPlaceCommand cmd))
            {
                ApplyOne(cmd);
            }
        }

        private void ApplyOne(NetPlaceCommand cmd)
        {
            var hash = new Colossal.Hash128(new uint4(cmd.Hash0, cmd.Hash1, cmd.Hash2, cmd.Hash3));
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, hash);

            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info($"[Net] RESOLVE-FAIL type={cmd.PrefabType} name={cmd.PrefabName}");
                return;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out Entity netPrefab))
            {
                CS2M.Log.Info($"[Net] RESOLVE-FAIL no prefab entity name={cmd.PrefabName}");
                return;
            }

            var bezier = new Bezier4x3(
                new float3(cmd.Ax, cmd.Ay, cmd.Az),
                new float3(cmd.Bx, cmd.By, cmd.Bz),
                new float3(cmd.Cx, cmd.Cy, cmd.Cz),
                new float3(cmd.Dx, cmd.Dy, cmd.Dz));

            // Mark the echo hash BEFORE the edge exists so our detector skips it when it appears.
            int segHash = RemoteNetEcho.SegHash(bezier.a, bezier.d, cmd.PrefabName);
            RemoteNetEcho.Mark(segHash);

            var startElev = new float2(cmd.StartElevX, cmd.StartElevY);
            var endElev = new float2(cmd.EndElevX, cmd.EndElevY);

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_StartPosition = MakeCoursePos(bezier.a, MathUtils.Tangent(bezier, 0f), startElev, 0f,
                CoursePosFlags.IsFirst);
            course.m_EndPosition = MakeCoursePos(bezier.d, MathUtils.Tangent(bezier, 1f), endElev, 1f,
                CoursePosFlags.IsLast);
            course.m_Elevation = (startElev + endElev) * 0.5f;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;

            CreationDefinition cd = default;
            cd.m_Prefab = netPrefab;
            cd.m_RandomSeed = cmd.RandomSeed;
            cd.m_Flags = CreationFlags.Permanent | CreationFlags.SubElevation;

            EntityCommandBuffer ecb = _barrier.CreateCommandBuffer();
            Entity def = ecb.CreateEntity();
            ecb.AddComponent(def, cd);
            ecb.AddComponent(def, course);
            ecb.AddComponent<Updated>(def);
            ecb.AddComponent<CS2M_RemotePlaced>(def);

            CS2M.Log.Info(
                $"[Net] INJECT name={cmd.PrefabName} prefabEntity={netPrefab.Index} len={course.m_Length:F1} " +
                $"segHash={segHash} seed={cmd.RandomSeed} start=({bezier.a.x:F1},{bezier.a.y:F1},{bezier.a.z:F1}) " +
                $"end=({bezier.d.x:F1},{bezier.d.y:F1},{bezier.d.z:F1})");
        }

        private static CoursePos MakeCoursePos(float3 pos, float3 tangent, float2 elevation, float delta,
            CoursePosFlags endFlag)
        {
            CoursePos p = default;
            p.m_Entity = Entity.Null;      // v1: no cross-PC snapping; coincident ends auto-merge
            p.m_Position = pos;
            p.m_Rotation = NetUtils.GetNodeRotation(tangent);
            p.m_Elevation = elevation;
            p.m_CourseDelta = delta;
            p.m_ParentMesh = -1;
            p.m_Flags = endFlag | CoursePosFlags.IsLeft | CoursePosFlags.IsRight;
            return p;
        }
    }
}
