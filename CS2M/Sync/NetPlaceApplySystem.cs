using Colossal.Mathematics;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Materializes nets placed by remote players.
    ///
    ///     v2 approach ("Option B" — direct archetype instantiation, same as the object apply): the
    ///     definition path (CreationDefinition + NetCourse) never generated an edge when injected outside
    ///     the net tool's ToolOutputBarrier/Temp flow. So instead we build the real entities ourselves
    ///     from the net prefab's baked archetypes: <c>NetData.m_NodeArchetype</c> for the two endpoints
    ///     and <c>NetData.m_EdgeArchetype</c> for the segment. We set the same components a placed net
    ///     carries — <c>Node</c>(pos/rot), <c>Edge</c>(start/end), <c>Curve</c>(bezier), <c>PrefabRef</c>,
    ///     <c>PseudoRandomSeed</c> — plus <c>Created</c>/<c>Updated</c>, and the game's own net geometry/
    ///     lane/aggregate systems build the road/rail/pipe/power/fence from there. Fully synchronous, no
    ///     barrier timing, no Temp. Coincident endpoints still auto-merge via the game's node systems.
    /// </summary>
    public partial class NetPlaceApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            CS2M.Log.Info("[Net] NetPlaceApplySystem created (direct-archetype mode)");
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

            if (!EntityManager.HasComponent<NetData>(netPrefab))
            {
                CS2M.Log.Info($"[Net] APPLY-FAIL prefab {cmd.PrefabName} has no NetData (not a net?)");
                return;
            }

            NetData netData = EntityManager.GetComponentData<NetData>(netPrefab);
            if (!netData.m_NodeArchetype.Valid || !netData.m_EdgeArchetype.Valid)
            {
                CS2M.Log.Info($"[Net] APPLY-FAIL prefab {cmd.PrefabName} node/edge archetype invalid");
                return;
            }

            var bezier = new Bezier4x3(
                new float3(cmd.Ax, cmd.Ay, cmd.Az),
                new float3(cmd.Bx, cmd.By, cmd.Bz),
                new float3(cmd.Cx, cmd.Cy, cmd.Cz),
                new float3(cmd.Dx, cmd.Dy, cmd.Dz));
            float length = MathUtils.Length(bezier);

            // Mark the echo hash BEFORE the edge exists so our detector skips it when it appears.
            int segHash = RemoteNetEcho.SegHash(bezier.a, bezier.d, cmd.PrefabName);
            RemoteNetEcho.Mark(segHash);

            // Two endpoint nodes from the prefab's node archetype.
            Entity startNode = CreateNode(netData.m_NodeArchetype, netPrefab, bezier.a,
                MathUtils.Tangent(bezier, 0f), cmd.RandomSeed);
            Entity endNode = CreateNode(netData.m_NodeArchetype, netPrefab, bezier.d,
                MathUtils.Tangent(bezier, 1f), cmd.RandomSeed);

            // The edge itself from the prefab's edge archetype.
            Entity edge = EntityManager.CreateEntity();
            EntityManager.SetArchetype(edge, netData.m_EdgeArchetype);
            SetOrAdd(edge, new Edge { m_Start = startNode, m_End = endNode });
            SetOrAdd(edge, new Curve { m_Bezier = bezier, m_Length = length });
            SetOrAdd(edge, new PrefabRef(netPrefab));
            SetSeed(edge, cmd.RandomSeed);
            EntityManager.AddComponent<CS2M_RemotePlaced>(edge);
            EnsureCreatedUpdated(edge);

            CS2M.Log.Info(
                $"[Net] APPLIED name={cmd.PrefabName} edge={edge.Index} startNode={startNode.Index} " +
                $"endNode={endNode.Index} len={length:F1} segHash={segHash} " +
                $"start=({bezier.a.x:F1},{bezier.a.y:F1},{bezier.a.z:F1}) " +
                $"end=({bezier.d.x:F1},{bezier.d.y:F1},{bezier.d.z:F1})");
        }

        private Entity CreateNode(EntityArchetype archetype, Entity netPrefab, float3 pos, float3 tangent,
            int seed)
        {
            Entity node = EntityManager.CreateEntity();
            EntityManager.SetArchetype(node, archetype);
            SetOrAdd(node, new Node { m_Position = pos, m_Rotation = NetUtils.GetNodeRotation(tangent) });
            SetOrAdd(node, new PrefabRef(netPrefab));
            SetSeed(node, seed);
            EntityManager.AddComponent<CS2M_RemotePlaced>(node);
            EnsureCreatedUpdated(node);
            return node;
        }

        private void SetSeed(Entity e, int seed)
        {
            if (EntityManager.HasComponent<PseudoRandomSeed>(e))
            {
                EntityManager.SetComponentData(e, new PseudoRandomSeed((ushort) seed));
            }
            else
            {
                EntityManager.AddComponentData(e, new PseudoRandomSeed((ushort) seed));
            }
        }

        private void EnsureCreatedUpdated(Entity e)
        {
            if (!EntityManager.HasComponent<Created>(e))
            {
                EntityManager.AddComponent<Created>(e);
            }

            if (!EntityManager.HasComponent<Updated>(e))
            {
                EntityManager.AddComponent<Updated>(e);
            }
        }

        private void SetOrAdd<T>(Entity e, T data) where T : unmanaged, IComponentData
        {
            if (EntityManager.HasComponent<T>(e))
            {
                EntityManager.SetComponentData(e, data);
            }
            else
            {
                EntityManager.AddComponentData(e, data);
            }
        }
    }
}
