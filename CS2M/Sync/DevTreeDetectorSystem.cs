using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects development-tree node purchases by diffing the set of unlocked node prefabs
    ///     (a purchased node's enableable <c>Locked</c> component gets disabled by the game's
    ///     Unlock event). First sight builds a silent baseline (saves come with nodes already
    ///     bought). Echo guard: the apply system adds the node to the shared snapshot first.
    /// </summary>
    public partial class DevTreeDetectorSystem : GameSystemBase
    {
        private const int ScanEveryNFrames = 30;

        private PrefabSystem _prefabSystem;
        private EntityQuery _nodes;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _nodes = GetEntityQuery(ComponentType.ReadOnly<DevTreeNodeData>());
            CS2M.Log.Info("[DevTree] DevTreeDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_frame < ScanEveryNFrames)
            {
                return;
            }

            _frame = 0;

            bool baseline = !DevTreeSync.BaselineBuilt;
            NativeArray<Entity> nodes = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity node in nodes)
                {
                    // Still locked (enabled Locked component) -> not purchased.
                    if (EntityManager.HasComponent<Locked>(node) && EntityManager.IsComponentEnabled<Locked>(node))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(node, out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    if (!DevTreeSync.Unlocked.Add(prefab.name) || baseline)
                    {
                        continue; // known (or baseline pass — cache silently)
                    }

                    Command.SendToAll?.Invoke(new DevTreeCommand { NodeName = prefab.name });
                    CS2M.Log.Info($"[DevTree] DETECT+SEND node={prefab.name}");
                }
            }
            finally
            {
                nodes.Dispose();
            }

            DevTreeSync.BaselineBuilt = true;
        }
    }
}
