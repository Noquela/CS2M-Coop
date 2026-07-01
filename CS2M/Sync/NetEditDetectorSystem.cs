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
    ///     Detects bulldozed net segments (an <c>Edge</c> that just gained <c>Deleted</c>) and broadcasts
    ///     its two endpoint (node) world positions so the other PCs delete the matching local edge.
    ///     Echo guard: the apply system marks the segment hash, so a remotely-applied delete isn't
    ///     re-broadcast here.
    /// </summary>
    public partial class NetEditDetectorSystem : GameSystemBase
    {
        private EntityQuery _deletedEdges;
        private readonly HashSet<Entity> _sent = new HashSet<Entity>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _deletedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            RequireForUpdate(_deletedEdges);
            CS2M.Log.Info("[NetEdit] NetEditDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_clearCounter >= 120)
            {
                _clearCounter = 0;
                _sent.Clear();
            }

            NativeArray<Entity> edges = _deletedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in edges)
                {
                    if (!_sent.Add(e))
                    {
                        continue;
                    }

                    Edge ed = EntityManager.GetComponentData<Edge>(e);
                    if (!EntityManager.HasComponent<Node>(ed.m_Start) || !EntityManager.HasComponent<Node>(ed.m_End))
                    {
                        continue;
                    }

                    float3 s = EntityManager.GetComponentData<Node>(ed.m_Start).m_Position;
                    float3 en = EntityManager.GetComponentData<Node>(ed.m_End).m_Position;

                    if (RemoteNetEcho.IsRecent(RemoteNetEcho.SegHash(s, en, "del")))
                    {
                        continue; // echo of a delete we just applied
                    }

                    Command.SendToAll?.Invoke(new NetDeleteCommand
                    {
                        StartX = s.x, StartY = s.y, StartZ = s.z,
                        EndX = en.x, EndY = en.y, EndZ = en.z,
                    });
                    CS2M.Log.Info($"[NetEdit] DETECT+SEND delete start=({s.x:F0},{s.z:F0}) end=({en.x:F0},{en.z:F0}) edge={e.Index}");
                }
            }
            finally
            {
                edges.Dispose();
            }
        }
    }
}
