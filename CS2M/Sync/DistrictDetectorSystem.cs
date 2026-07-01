using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects a freshly-painted district (an <c>Area</c>+<c>District</c> that just gained
    ///     <c>Applied</c>) and broadcasts its boundary polygon (the <c>Node</c> buffer) + prefab + option
    ///     mask. Echo guard: districts recreated from a remote command carry <c>CS2M_RemotePlaced</c>,
    ///     which this query excludes.
    /// </summary>
    public partial class DistrictDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _appliedDistricts;
        private readonly HashSet<Entity> _sent = new HashSet<Entity>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _appliedDistricts = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<District>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });
            RequireForUpdate(_appliedDistricts);
            CS2M.Log.Info("[District] DistrictDetectorSystem created");
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

            NativeArray<Entity> ents = _appliedDistricts.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_sent.Add(e))
                    {
                        continue;
                    }

                    if (!EntityManager.HasBuffer<Node>(e) || !EntityManager.HasComponent<PrefabRef>(e))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(e, true);
                    int n = nodes.Length;
                    if (n < 3)
                    {
                        continue;
                    }

                    var xs = new float[n];
                    var ys = new float[n];
                    var zs = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        xs[i] = nodes[i].m_Position.x;
                        ys[i] = nodes[i].m_Position.y;
                        zs[i] = nodes[i].m_Position.z;
                    }

                    uint mask = EntityManager.HasComponent<District>(e)
                        ? EntityManager.GetComponentData<District>(e).m_OptionMask : 0u;

                    Command.SendToAll?.Invoke(new DistrictCommand
                    {
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        OptionMask = mask,
                        Xs = xs, Ys = ys, Zs = zs,
                    });
                    CS2M.Log.Info($"[District] DETECT+SEND name={prefab.name} points={n}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }
    }
}
