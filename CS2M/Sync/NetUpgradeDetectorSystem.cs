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
        private readonly Dictionary<Entity, uint3> _snap = new Dictionary<Entity, uint3>();
        private bool _baselineDone;

        protected override void OnCreate()
        {
            base.OnCreate();
            _upgradedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Upgraded>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
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
            try
            {
                if (!_baselineDone)
                {
                    foreach (Entity e in edges)
                    {
                        _snap[e] = Flags(e);
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

                    Command.SendToAll?.Invoke(new NetUpgradeCommand
                    {
                        StartX = s.x, StartY = s.y, StartZ = s.z,
                        EndX = en.x, EndY = en.y, EndZ = en.z,
                        General = cur.x, Left = cur.y, Right = cur.z,
                    });
                    CS2M.Log.Info($"[NetEdit] DETECT+SEND upgrade edge={e.Index} g={cur.x} l={cur.y} r={cur.z}");
                }
            }
            finally
            {
                edges.Dispose();
            }
        }

        private uint3 Flags(Entity e)
        {
            Upgraded u = EntityManager.GetComponentData<Upgraded>(e);
            return new uint3((uint) u.m_Flags.m_General, (uint) u.m_Flags.m_Left, (uint) u.m_Flags.m_Right);
        }
    }
}
