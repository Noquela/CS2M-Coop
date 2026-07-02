using System.Collections.Generic;
using Colossal.Mathematics;
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
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects nets (roads/rails/pipes/power/fences) the local player just placed and broadcasts a
    ///     <see cref="NetPlaceCommand"/>. A freshly-committed segment gets a live <c>Edge</c>+<c>Curve</c>
    ///     with <c>Applied</c>. We ship the exact <c>Curve.m_Bezier</c> so the other PC rebuilds identical
    ///     geometry. Echo guard: skip any edge whose segment hash was just marked by our own apply system.
    /// </summary>
    public partial class NetDetectorSystem : GameSystemBase
    {
        private ToolSystem _toolSystem;
        private PrefabSystem _prefabSystem;
        private EntityQuery _appliedEdges;
        private EntityQuery _deletedEdges;
        private readonly HashSet<Entity> _recentlySent = new HashSet<Entity>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _appliedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                    // Building sub-nets (Owner = the building) are generated deterministically from
                    // the prefab on BOTH PCs by the placement apply — syncing them would duplicate.
                    ComponentType.ReadOnly<Owner>(),
                },
            });
            _deletedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            RequireForUpdate(_appliedEdges);
            CS2M.Log.Info("[Net] NetDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            RemoteNetEcho.Tick();

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_clearCounter >= 120)
            {
                _clearCounter = 0;
                _recentlySent.Clear();
            }

            string toolId = _toolSystem.activeTool != null ? _toolSystem.activeTool.toolID : null;

            // Same-frame deleted curves: an Applied edge whose endpoints lie on one of these is a
            // split piece (the game cut an existing road under a new crossing) — a DERIVED event.
            // The other PC performs the same split itself when the causal road arrives; re-syncing
            // the pieces duplicates segments (seen in the first v38 2-PC session).
            var deletedCurves = new List<Curve>();
            if (!_deletedEdges.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> del = _deletedEdges.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (Entity d in del)
                    {
                        deletedCurves.Add(EntityManager.GetComponentData<Curve>(d));
                    }
                }
                finally
                {
                    del.Dispose();
                }
            }

            NativeArray<Entity> edges = _appliedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in edges)
                {
                    if (!_recentlySent.Add(e))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    Curve curveData = EntityManager.GetComponentData<Curve>(e);
                    Bezier4x3 bezier = curveData.m_Bezier;
                    string name = prefab.name;

                    bool isSplitPiece = false;
                    foreach (Curve original in deletedCurves)
                    {
                        if (NetSplitUtil.IsSplitPiece(bezier, curveData.m_Length, original.m_Bezier, original.m_Length))
                        {
                            isSplitPiece = true;
                            break;
                        }
                    }

                    if (isSplitPiece)
                    {
                        CS2M.Log.Info($"[Net] SKIP reason=split name={name} edge={e.Index} (derived, remote splits on its own)");
                        continue;
                    }

                    int segHash = RemoteNetEcho.SegHash(bezier.a, bezier.d, name);
                    if (RemoteNetEcho.IsRecent(segHash))
                    {
                        CS2M.Log.Info($"[Net] SKIP reason=remoteEcho segHash={segHash} name={name}");
                        continue;
                    }

                    float2 startElev = default;
                    float2 endElev = default;
                    if (EntityManager.HasComponent<Game.Net.Elevation>(e))
                    {
                        float2 el = EntityManager.GetComponentData<Game.Net.Elevation>(e).m_Elevation;
                        startElev = el;
                        endElev = el;
                    }

                    int seed = 0;
                    if (EntityManager.HasComponent<PseudoRandomSeed>(e))
                    {
                        seed = EntityManager.GetComponentData<PseudoRandomSeed>(e).m_Seed;
                    }

                    var cmd = new NetPlaceCommand
                    {
                        SyncId = CS2M_SyncIdSystem.Allocate(),
                        PrefabType = prefab.GetType().Name,
                        PrefabName = name,
                        Hash0 = 0, Hash1 = 0, Hash2 = 0, Hash3 = 0,
                        Ax = bezier.a.x, Ay = bezier.a.y, Az = bezier.a.z,
                        Bx = bezier.b.x, By = bezier.b.y, Bz = bezier.b.z,
                        Cx = bezier.c.x, Cy = bezier.c.y, Cz = bezier.c.z,
                        Dx = bezier.d.x, Dy = bezier.d.y, Dz = bezier.d.z,
                        StartElevX = startElev.x, StartElevY = startElev.y,
                        EndElevX = endElev.x, EndElevY = endElev.y,
                        RandomSeed = seed,
                    };

                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Info(
                        $"[Net] DETECT+SEND name={name} start=({bezier.a.x:F1},{bezier.a.y:F1},{bezier.a.z:F1}) " +
                        $"end=({bezier.d.x:F1},{bezier.d.y:F1},{bezier.d.z:F1}) seed={seed} tool={toolId} edge={e.Index}");
                }
            }
            finally
            {
                edges.Dispose();
            }
        }
    }
}
