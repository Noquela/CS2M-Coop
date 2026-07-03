using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Areas;
using Game.Common;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue + shared owned-tile snapshot (echo guard).</summary>
    public static class TileSync
    {
        private static readonly Queue<TilePurchaseCommand> Queue = new Queue<TilePurchaseCommand>();
        private static readonly object Lock = new object();

        /// <summary>Tile entities already known as owned (baseline + applied) — detector skips them.</summary>
        public static readonly HashSet<Entity> KnownOwned = new HashSet<Entity>();
        public static bool BaselineBuilt;

        public static void Enqueue(TilePurchaseCommand cmd)
        {
            lock (Lock) { Queue.Enqueue(cmd); }
        }

        public static bool TryDequeue(out TilePurchaseCommand cmd)
        {
            lock (Lock)
            {
                if (Queue.Count > 0)
                {
                    cmd = Queue.Dequeue();
                    return true;
                }

                cmd = null;
                return false;
            }
        }

        public static void Clear()
        {
            lock (Lock) { Queue.Clear(); }
            KnownOwned.Clear();
            BaselineBuilt = false;
        }
    }

    /// <summary>
    ///     Detects newly purchased map tiles (a MapTile losing its <c>Native</c> component) by diffing
    ///     the owned set every ~2 s, and broadcasts their center positions. First sight builds a silent
    ///     baseline (saves come with tiles already owned).
    /// </summary>
    public partial class TileDetectorSystem : GameSystemBase
    {
        private const int ScanEveryNFrames = 120;

        private EntityQuery _ownedTiles;
        private int _frame;
        private Game.Simulation.MapTilePurchaseSystem _purchaseSystem;
        private int _lastSelectionCost;

        protected override void OnCreate()
        {
            base.OnCreate();
            _purchaseSystem = World.GetOrCreateSystemManaged<Game.Simulation.MapTilePurchaseSystem>();
            _ownedTiles = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<MapTile>(),
                    ComponentType.ReadOnly<Geometry>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Native>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[Tile] TileDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // v50: sample the live selection's price every frame — the purchase clears the
            // selection, so by the time the ~2 s diff sees the new tiles this holds what was paid.
            int selCost = _purchaseSystem.cost;
            if (selCost > 0)
            {
                _lastSelectionCost = selCost;
            }

            if (++_frame < ScanEveryNFrames)
            {
                return;
            }

            _frame = 0;

            bool baseline = !TileSync.BaselineBuilt;
            List<float> xs = null, zs = null;
            NativeArray<Entity> tiles = _ownedTiles.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity tile in tiles)
                {
                    if (!TileSync.KnownOwned.Add(tile) || baseline)
                    {
                        continue;
                    }

                    var center = EntityManager.GetComponentData<Geometry>(tile).m_CenterPosition;
                    if (xs == null)
                    {
                        xs = new List<float>();
                        zs = new List<float>();
                    }

                    xs.Add(center.x);
                    zs.Add(center.z);
                }
            }
            finally
            {
                tiles.Dispose();
            }

            TileSync.BaselineBuilt = true;
            if (xs == null)
            {
                return;
            }

            Command.SendToAll?.Invoke(new TilePurchaseCommand
            {
                Xs = xs.ToArray(),
                Zs = zs.ToArray(),
                Cost = _lastSelectionCost,
            });
            CS2M.Log.Info($"[Tile] DETECT+SEND purchased tiles={xs.Count} cost={_lastSelectionCost}");
            _lastSelectionCost = 0;
        }
    }

    /// <summary>
    ///     Unlocks the matching tiles the same way <c>MapTilePurchaseSystem.UnlockTile</c> does:
    ///     remove <c>Native</c> + add <c>Updated</c>. Matched by area center (fixed per-map grid).
    /// </summary>
    public partial class TileApplySystem : GameSystemBase
    {
        private EntityQuery _lockedTiles;

        protected override void OnCreate()
        {
            base.OnCreate();
            _lockedTiles = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<MapTile>(),
                    ComponentType.ReadOnly<Native>(),
                    ComponentType.ReadOnly<Geometry>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[Tile] TileApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (TileSync.TryDequeue(out TilePurchaseCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] tile apply failed: {ex.Message}"); }
            }
        }

        private void ApplyOne(TilePurchaseCommand cmd)
        {
            if (cmd.Xs == null || cmd.Zs == null)
            {
                return;
            }

            int applied = 0;
            NativeArray<Entity> tiles = _lockedTiles.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < cmd.Xs.Length && i < cmd.Zs.Length; i++)
                {
                    Entity best = Entity.Null;
                    float bestD = 100f; // 10 m — tile centers are hundreds of meters apart
                    foreach (Entity tile in tiles)
                    {
                        if (TileSync.KnownOwned.Contains(tile))
                        {
                            continue; // already unlocked by an earlier entry this batch
                        }

                        var c = EntityManager.GetComponentData<Geometry>(tile).m_CenterPosition;
                        float dx = c.x - cmd.Xs[i];
                        float dz = c.z - cmd.Zs[i];
                        float d = dx * dx + dz * dz;
                        if (d < bestD)
                        {
                            bestD = d;
                            best = tile;
                        }
                    }

                    if (best == Entity.Null)
                    {
                        CS2M.Log.Info($"[Tile] SKIP noMatch at=({cmd.Xs[i]:F0},{cmd.Zs[i]:F0})");
                        continue;
                    }

                    // Echo guard BEFORE unlocking: the detector's next scan sees it as known.
                    TileSync.KnownOwned.Add(best);
                    if (EntityManager.HasComponent<Native>(best))
                    {
                        EntityManager.RemoveComponent<Native>(best);
                        EntityManager.AddComponent<Updated>(best);
                        applied++;
                    }
                }
            }
            finally
            {
                tiles.Dispose();
            }

            // v50: host-authoritative economy — debit what the buyer paid; the ~1 Hz money sync
            // then propagates the corrected balance to everyone (same pattern as construction).
            if (applied > 0 && cmd.Cost > 0
                && NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                Entity city = World.GetOrCreateSystemManaged<Game.Simulation.CitySystem>().City;
                if (city != Entity.Null && EntityManager.HasComponent<Game.City.PlayerMoney>(city))
                {
                    Game.City.PlayerMoney pm =
                        EntityManager.GetComponentData<Game.City.PlayerMoney>(city);
                    if (!pm.m_Unlimited)
                    {
                        pm.Subtract(cmd.Cost);
                        EntityManager.SetComponentData(city, pm);
                        CS2M.Log.Info($"[Tile] CHARGED cost={cmd.Cost} cash={pm.money}");
                    }
                }
            }

            CS2M.Log.Info($"[Tile] APPLIED purchased tiles={applied}/{cmd.Xs.Length}");
        }
    }
}
