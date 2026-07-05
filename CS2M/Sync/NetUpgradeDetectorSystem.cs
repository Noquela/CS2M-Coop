using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects local net upgrades by diffing each edge's <c>Upgraded.m_Flags</c> (General/Left/Right)
    ///     against a per-edge snapshot and broadcasting the change addressed by endpoint positions. The
    ///     first full pass caches the baseline silently (already-upgraded roads in the shared save aren't
    ///     re-sent). Echo guard via <see cref="RemoteNetEcho"/> so a remote-applied upgrade isn't echoed.
    /// </summary>
    public partial class NetUpgradeDetectorSystem : GameSystemBase
    {
        private EntityQuery _upgradedEdges;
        private EntityQuery _upgradedNodes;
        private readonly Dictionary<Entity, uint3> _snap = new Dictionary<Entity, uint3>();
        private readonly Dictionary<Entity, uint3> _snapNodes = new Dictionary<Entity, uint3>();
        private bool _baselineDone;

        protected override void OnCreate()
        {
            base.OnCreate();
            _upgradedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Upgraded>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            // Junction upgrades (traffic lights / stop signs / roundabout / crosswalks) live as Upgraded
            // General flags on the NODE, which the edge query above can't see. Exclude Edge so this is
            // strictly nodes.
            _upgradedNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Upgraded>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[NetEdit] NetUpgradeDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            NativeArray<Entity> edges = _upgradedEdges.ToEntityArray(Allocator.Temp);
            NativeArray<Entity> nodes = _upgradedNodes.ToEntityArray(Allocator.Temp);
            try
            {
                if (!_baselineDone)
                {
                    foreach (Entity e in edges)
                    {
                        _snap[e] = Flags(e);
                    }

                    foreach (Entity n in nodes)
                    {
                        _snapNodes[n] = Flags(n);
                    }

                    _baselineDone = true;
                    return;
                }

                foreach (Entity e in edges)
                {
                    uint3 cur = Flags(e);
                    if (_snap.TryGetValue(e, out uint3 prev) && math.all(prev == cur))
                    {
                        continue;
                    }

                    _snap[e] = cur;

                    Edge ed = EntityManager.GetComponentData<Edge>(e);
                    if (!EntityManager.HasComponent<Node>(ed.m_Start) || !EntityManager.HasComponent<Node>(ed.m_End))
                    {
                        continue;
                    }

                    float3 s = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
                    float3 en = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;
                    if (RemoteNetEcho.IsRecent(RemoteNetEcho.SegHash(s, en, "upg")))
                    {
                        continue;
                    }

                    ulong upS = EntityManager.HasComponent<CS2M_NodeSyncId>(ed.m_Start)
                        ? EntityManager.GetComponentData<CS2M_NodeSyncId>(ed.m_Start).m_Id : 0UL;
                    ulong upE = EntityManager.HasComponent<CS2M_NodeSyncId>(ed.m_End)
                        ? EntityManager.GetComponentData<CS2M_NodeSyncId>(ed.m_End).m_Id : 0UL;

                    Command.SendToAll?.Invoke(new NetUpgradeCommand
                    {
                        StartX = s.x, StartY = s.y, StartZ = s.z,
                        EndX = en.x, EndY = en.y, EndZ = en.z,
                        General = cur.x, Left = cur.y, Right = cur.z,
                        StartNodeId = upS, EndNodeId = upE,
                    });
                    CS2M.Log.Info($"[NetEdit] DETECT+SEND upgrade edge={e.Index} g={cur.x} l={cur.y} r={cur.z}");
                }

                // Junction upgrades: diff the node's Upgraded flags and ship them addressed by node position.
                foreach (Entity n in nodes)
                {
                    uint3 cur = Flags(n);
                    if (_snapNodes.TryGetValue(n, out uint3 prev) && math.all(prev == cur))
                    {
                        continue;
                    }

                    _snapNodes[n] = cur;

                    float3 p = EntityManager.GetComponentData<Node>(n).m_Position;
                    if (RemoteNetEcho.IsRecent(RemoteNetEcho.SegHash(p, p, "upgNode")))
                    {
                        continue;
                    }

                    ulong nId = EntityManager.HasComponent<CS2M_NodeSyncId>(n)
                        ? EntityManager.GetComponentData<CS2M_NodeSyncId>(n).m_Id : 0UL;

                    Command.SendToAll?.Invoke(new NetUpgradeCommand
                    {
                        IsNode = true,
                        StartX = p.x, StartY = p.y, StartZ = p.z,
                        General = cur.x, Left = cur.y, Right = cur.z,
                        NodeId = nId,
                    });
                    CS2M.Log.Info($"[NetEdit] DETECT+SEND node-upgrade node={n.Index} g={cur.x} pos=({p.x:F0},{p.z:F0})");
                }
            }
            finally
            {
                edges.Dispose();
                nodes.Dispose();
            }
        }

        private uint3 Flags(Entity e)
        {
            Upgraded u = EntityManager.GetComponentData<Upgraded>(e);
            return new uint3((uint) u.m_Flags.m_General, (uint) u.m_Flags.m_Left, (uint) u.m_Flags.m_Right);
        }
    }
}
