using System.Collections.Generic;
using Colossal.Mathematics;
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
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Materializes nets placed by remote players.
    ///
    ///     v3 approach (vanilla definition injection): the direct-archetype edge (v2) turned out to be a
    ///     hollow shell — this system ran at Modification5, AFTER every net consumer (References@Mod2B,
    ///     CompositionSelect@Mod3, Geometry/Lane/Block@Mod4), and the game strips Created/Updated at the
    ///     end of the same frame, so no downstream system ever saw the edge: no composition, no mesh, no
    ///     zoning blocks (the "[Zone] SKIP noBlock" from the first 2-PC test).
    ///
    ///     The fix copies what the game itself does for programmatic, non-tool construction
    ///     (Game.Simulation.BuildingConstructionSystem.CreateNets / ZoneSpawnSystem): create a
    ///     CreationDefinition with <c>CreationFlags.Permanent</c> + NetCourse + Updated, registered
    ///     BEFORE Modification1. GenerateNodesSystem(@Mod1)/GenerateEdgesSystem(@Mod2) then build the
    ///     REAL net — terrain-adjusted curve, node merge/reuse, BuildOrder, ConnectedNode — and the whole
    ///     pipeline (composition, geometry, lanes, zone blocks, search tree, mesh) completes in the same
    ///     frame. With Permanent, no Temp is added (GenerateEdge skips it), so nothing needs the tool flow.
    /// </summary>
    public partial class NetPlaceApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _liveNodes;
        private readonly List<Entity> _pendingDefinitions = new List<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _liveNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Node>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(), // standalone road nodes only, not building sub-nets
                },
            });
            CS2M.Log.Info("[Net] NetPlaceApplySystem created (definition-injection mode)");
        }

        protected override void OnUpdate()
        {
            // Definitions injected last frame were consumed by GenerateNodes/Edges already — clean up.
            // (ToolClearSystem may have destroyed them first; the Exists guard makes this idempotent.)
            for (int i = 0; i < _pendingDefinitions.Count; i++)
            {
                if (EntityManager.Exists(_pendingDefinitions[i]))
                {
                    EntityManager.DestroyEntity(_pendingDefinitions[i]);
                }
            }

            _pendingDefinitions.Clear();

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

            if (!EntityManager.HasComponent<NetData>(netPrefab))
            {
                CS2M.Log.Info($"[Net] APPLY-FAIL prefab {cmd.PrefabName} has no NetData (not a net?)");
                return;
            }

            var bezier = new Bezier4x3(
                new float3(cmd.Ax, cmd.Ay, cmd.Az),
                new float3(cmd.Bx, cmd.By, cmd.Bz),
                new float3(cmd.Cx, cmd.Cy, cmd.Cz),
                new float3(cmd.Dx, cmd.Dy, cmd.Dz));

            // Connect to the existing net where the endpoints already have nodes (cross-PC snapping).
            Entity startNode = FindExistingNode(bezier.a);
            Entity endNode = FindExistingNode(bezier.d);

            // Idempotency: if an edge with this prefab already links these two nodes (duplicate or
            // re-sent packet, echo miss), don't build the same segment twice.
            if (startNode != Entity.Null && endNode != Entity.Null && EdgeExists(startNode, endNode, netPrefab))
            {
                CS2M.Log.Info($"[Net] SKIP duplicate name={cmd.PrefabName} " +
                              $"start=({bezier.a.x:F1},{bezier.a.z:F1}) end=({bezier.d.x:F1},{bezier.d.z:F1})");
                return;
            }

            // Mark the echo hash BEFORE the edge exists so our detector skips it when it appears.
            // SegHash is XZ-only by design: GenerateEdge re-snaps Y to the local terrain.
            int segHash = RemoteNetEcho.SegHash(bezier.a, bezier.d, cmd.PrefabName);
            RemoteNetEcho.Mark(segHash);

            NetCourse course = default;
            course.m_Curve = bezier;
            course.m_Length = MathUtils.Length(bezier);
            course.m_FixedIndex = -1;

            course.m_StartPosition.m_Position = bezier.a;
            course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(bezier));
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_Elevation = new float2(cmd.StartElevX, cmd.StartElevY);
            course.m_StartPosition.m_ParentMesh = -1;
            course.m_StartPosition.m_Flags = CoursePosFlags.IsFirst;
            course.m_StartPosition.m_Entity = startNode;

            course.m_EndPosition.m_Position = bezier.d;
            course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(bezier));
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_Elevation = new float2(cmd.EndElevX, cmd.EndElevY);
            course.m_EndPosition.m_ParentMesh = -1;
            course.m_EndPosition.m_Flags = CoursePosFlags.IsLast;
            course.m_EndPosition.m_Entity = endNode;

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = netPrefab,
                m_RandomSeed = cmd.RandomSeed,
                m_Flags = CreationFlags.Permanent, // no Temp: GenerateEdge builds the REAL segment
            });
            EntityManager.AddComponentData(def, course);
            EntityManager.AddComponent<Updated>(def); // required by GenerateEdgesSystem's definition query
            _pendingDefinitions.Add(def);

            CS2M.Log.Info(
                $"[Net] APPLIED-DEF name={cmd.PrefabName} len={course.m_Length:F1} segHash={segHash} " +
                $"startNode={(startNode != Entity.Null ? startNode.Index : 0)} " +
                $"endNode={(endNode != Entity.Null ? endNode.Index : 0)} " +
                $"start=({bezier.a.x:F1},{bezier.a.y:F1},{bezier.a.z:F1}) " +
                $"end=({bezier.d.x:F1},{bezier.d.y:F1},{bezier.d.z:F1})");
        }

        /// <summary>Nearest live standalone node within 0.5 m (XZ) of <paramref name="pos"/>, or Null.</summary>
        private Entity FindExistingNode(float3 pos)
        {
            NativeArray<Entity> nodes = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestSq = 0.25f;
                foreach (Entity n in nodes)
                {
                    float3 p = EntityManager.GetComponentData<Node>(n).m_Position;
                    float dx = p.x - pos.x;
                    float dz = p.z - pos.z;
                    float d = dx * dx + dz * dz;
                    if (d < bestSq)
                    {
                        bestSq = d;
                        best = n;
                    }
                }

                return best;
            }
            finally
            {
                nodes.Dispose();
            }
        }

        private bool EdgeExists(Entity startNode, Entity endNode, Entity netPrefab)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(startNode))
            {
                return false;
            }

            DynamicBuffer<ConnectedEdge> connected = EntityManager.GetBuffer<ConnectedEdge>(startNode);
            for (int i = 0; i < connected.Length; i++)
            {
                Entity e = connected[i].m_Edge;
                if (!EntityManager.Exists(e) || !EntityManager.HasComponent<Edge>(e))
                {
                    continue;
                }

                Edge edge = EntityManager.GetComponentData<Edge>(e);
                bool linksSameNodes = (edge.m_Start == startNode && edge.m_End == endNode)
                                      || (edge.m_Start == endNode && edge.m_End == startNode);
                if (!linksSameNodes)
                {
                    continue;
                }

                if (EntityManager.HasComponent<PrefabRef>(e)
                    && EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == netPrefab)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
