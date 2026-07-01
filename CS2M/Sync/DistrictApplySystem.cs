using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Materializes a district painted by a remote player. Direct-archetype approach (same as objects
    ///     and nets): resolve the District prefab, create the entity from <c>AreaData.m_Archetype</c>, set
    ///     <c>Area(Complete)</c> + <c>District(optionMask)</c> + <c>PrefabRef</c> and rebuild the
    ///     <c>Node</c> boundary buffer; the game's area systems triangulate + render it. District names
    ///     are UI-managed, not synced here.
    /// </summary>
    public partial class DistrictApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            CS2M.Log.Info("[District] DistrictApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteDistrictQueue.TryDequeue(out DistrictCommand cmd))
            {
                ApplyOne(cmd);
            }
        }

        private void ApplyOne(DistrictCommand cmd)
        {
            if (cmd.Xs == null || cmd.Ys == null || cmd.Zs == null || cmd.Xs.Length < 3)
            {
                CS2M.Log.Info("[District] APPLY-FAIL too few boundary points");
                return;
            }

            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info($"[District] RESOLVE-FAIL type={cmd.PrefabType} name={cmd.PrefabName}");
                return;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out Entity districtPrefab)
                || !EntityManager.HasComponent<AreaData>(districtPrefab))
            {
                CS2M.Log.Info($"[District] RESOLVE-FAIL no AreaData for name={cmd.PrefabName}");
                return;
            }

            AreaData ad = EntityManager.GetComponentData<AreaData>(districtPrefab);
            if (!ad.m_Archetype.Valid)
            {
                CS2M.Log.Info($"[District] APPLY-FAIL archetype invalid name={cmd.PrefabName}");
                return;
            }

            Entity area = EntityManager.CreateEntity();
            EntityManager.SetArchetype(area, ad.m_Archetype);
            SetOrAdd(area, new Area { m_Flags = AreaFlags.Complete });
            SetOrAdd(area, new District { m_OptionMask = cmd.OptionMask });
            SetOrAdd(area, new PrefabRef(districtPrefab));

            DynamicBuffer<Node> nodes = EntityManager.HasBuffer<Node>(area)
                ? EntityManager.GetBuffer<Node>(area)
                : EntityManager.AddBuffer<Node>(area);
            nodes.Clear();
            int n = math.min(cmd.Xs.Length, math.min(cmd.Ys.Length, cmd.Zs.Length));
            for (int i = 0; i < n; i++)
            {
                nodes.Add(new Node { m_Position = new float3(cmd.Xs[i], cmd.Ys[i], cmd.Zs[i]), m_Elevation = 0f });
            }

            EntityManager.AddComponent<CS2M_RemotePlaced>(area);
            if (!EntityManager.HasComponent<Created>(area)) { EntityManager.AddComponent<Created>(area); }
            if (!EntityManager.HasComponent<Updated>(area)) { EntityManager.AddComponent<Updated>(area); }

            CS2M.Log.Info($"[District] APPLIED name={cmd.PrefabName} entity={area.Index} points={n}");
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
