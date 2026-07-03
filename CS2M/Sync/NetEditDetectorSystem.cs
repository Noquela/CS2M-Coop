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
        private EntityQuery _appliedEdges;
        private readonly HashSet<Entity> _sent = new HashSet<Entity>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _deletedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Deleted>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    // v50.2 FIELD FIX: building sub-nets (Owner = the building) cascade with their
                    // building on BOTH PCs. The host's sim demolishing abandoned buildings re-sent
                    // every sub-net as a standalone "road delete" (297 in one session), and the
                    // endpoint-addressed applies tore up REAL roads and pipes on the other PCs.
                    ComponentType.ReadOnly<Owner>(),
                },
            });
            _appliedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<Applied>(),
                },
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

            // Same-frame applied pieces: if a deleted edge is covered by shorter Applied pieces, the
            // deletion came from the game SPLITTING it under a new crossing road — a derived event.
            // The other PC splits its own copy when the causal road arrives; syncing this delete would
            // remove a segment the remote game still needs (seen as "SKIP noMatch" noise in v38).
            var appliedCurves = new List<Curve>();
            if (!_appliedEdges.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> applied = _appliedEdges.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity a in applied)
                    {
                        appliedCurves.Add(EntityManager.GetComponentData<Curve>(a));
                    }
                }
                finally
                {
                    applied.Dispose();
                }
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

                    if (appliedCurves.Count > 0 && EntityManager.HasComponent<Curve>(e))
                    {
                        Curve original = EntityManager.GetComponentData<Curve>(e);
                        bool splitDeletion = false;
                        foreach (Curve piece in appliedCurves)
                        {
                            if (NetSplitUtil.IsSplitPiece(piece.m_Bezier, piece.m_Length,
                                    original.m_Bezier, original.m_Length))
                            {
                                splitDeletion = true;
                                break;
                            }
                        }

                        if (splitDeletion)
                        {
                            CS2M.Log.Info($"[NetEdit] SKIP delete reason=split edge={e.Index} (derived, remote splits on its own)");
                            continue;
                        }
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
