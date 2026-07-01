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
    ///     Applies remote net-delete: finds the local edge whose two endpoint node positions match the
    ///     command (order-independent, ~3 m tolerance) and tags it <c>Deleted</c> — the game's own net
    ///     cleanup removes it and any orphaned nodes/lanes. Marks the segment hash first so our detector
    ///     doesn't echo the delete back.
    /// </summary>
    public partial class NetEditApplySystem : GameSystemBase
    {
        private EntityQuery _edges;

        protected override void OnCreate()
        {
            base.OnCreate();
            _edges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            CS2M.Log.Info("[NetEdit] NetEditApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteNetDeleteQueue.TryDequeue(out NetDeleteCommand cmd))
            {
                ApplyDelete(cmd);
            }

            while (RemoteNetUpgradeQueue.TryDequeue(out NetUpgradeCommand up))
            {
                ApplyUpgrade(up);
            }
        }

        private void ApplyUpgrade(NetUpgradeCommand cmd)
        {
            var start = new float3(cmd.StartX, cmd.StartY, cmd.StartZ);
            var end = new float3(cmd.EndX, cmd.EndY, cmd.EndZ);

            if (!FindEdge(start, end, out Entity edge))
            {
                CS2M.Log.Info($"[NetEdit] upgrade SKIP noMatch start=({start.x:F0},{start.z:F0}) end=({end.x:F0},{end.z:F0})");
                return;
            }

            Edge ed = EntityManager.GetComponentData<Edge>(edge);
            float3 s = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
            float3 en = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(s, en, "upg"));

            var up = new Upgraded
            {
                m_Flags = new CompositionFlags
                {
                    m_General = (CompositionFlags.General) cmd.General,
                    m_Left = (CompositionFlags.Side) cmd.Left,
                    m_Right = (CompositionFlags.Side) cmd.Right,
                },
            };

            if (EntityManager.HasComponent<Upgraded>(edge))
            {
                EntityManager.SetComponentData(edge, up);
            }
            else
            {
                EntityManager.AddComponentData(edge, up);
            }

            if (!EntityManager.HasComponent<Updated>(edge))
            {
                EntityManager.AddComponent<Updated>(edge);
            }

            if (!EntityManager.HasComponent<BatchesUpdated>(edge))
            {
                EntityManager.AddComponent<BatchesUpdated>(edge);
            }

            CS2M.Log.Info($"[NetEdit] APPLIED upgrade edge={edge.Index} g={cmd.General} l={cmd.Left} r={cmd.Right}");
        }

        private void ApplyDelete(NetDeleteCommand cmd)
        {
            var start = new float3(cmd.StartX, cmd.StartY, cmd.StartZ);
            var end = new float3(cmd.EndX, cmd.EndY, cmd.EndZ);

            if (!FindEdge(start, end, out Entity edge))
            {
                CS2M.Log.Info($"[NetEdit] delete SKIP noMatch start=({start.x:F0},{start.z:F0}) end=({end.x:F0},{end.z:F0})");
                return;
            }

            Edge ed = EntityManager.GetComponentData<Edge>(edge);
            float3 s = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
            float3 en = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;
            RemoteNetEcho.Mark(RemoteNetEcho.SegHash(s, en, "del"));

            if (!EntityManager.HasComponent<Deleted>(edge))
            {
                EntityManager.AddComponent<Deleted>(edge);
            }

            CS2M.Log.Info($"[NetEdit] APPLIED delete edge={edge.Index} start=({s.x:F0},{s.z:F0}) end=({en.x:F0},{en.z:F0})");
        }

        private bool FindEdge(float3 start, float3 end, out Entity edge)
        {
            edge = Entity.Null;
            NativeArray<Entity> arr = _edges.ToEntityArray(Allocator.Temp);
            try
            {
                float best = 18f; // sum of endpoint distances² — ~3 m per endpoint
                foreach (Entity e in arr)
                {
                    Edge ed = EntityManager.GetComponentData<Edge>(e);
                    if (!EntityManager.HasComponent<Node>(ed.m_Start) || !EntityManager.HasComponent<Node>(ed.m_End))
                    {
                        continue;
                    }

                    float3 s = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
                    float3 en = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;
                    float d1 = math.distancesq(s, start) + math.distancesq(en, end);
                    float d2 = math.distancesq(s, end) + math.distancesq(en, start);
                    float d = math.min(d1, d2);
                    if (d < best)
                    {
                        best = d;
                        edge = e;
                    }
                }
            }
            finally
            {
                arr.Dispose();
            }

            return edge != Entity.Null;
        }
    }
}
