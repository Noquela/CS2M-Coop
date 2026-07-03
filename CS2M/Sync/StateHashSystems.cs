using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     v52: the world fingerprint both sides compute identically. Upgraded from bare counts to
    ///     position-based CONTENT hashes so "same count, different geometry" desyncs (roads in the
    ///     wrong place / overlapping / not connected; zones painted on one PC only; a building the
    ///     other PC never got) are caught, not just gross count drift. Only player-authored state is
    ///     fingerprinted — growables and emergent citizen/vehicle sim differ by design.
    ///
    ///     Positions are the hash inputs because world coordinates are identical across machines,
    ///     which sidesteps the fragile assumption that prefab entity indices match. Zone paint is
    ///     hashed by <c>Cell.m_Zone.m_Index</c> — the exact value the zone sync already ships over the
    ///     wire, so it is cross-machine stable by construction. Every accumulation uses commutative
    ///     addition, so entity iteration order never affects the result.
    /// </summary>
    internal struct HashBundle
    {
        public int Edges;
        public long EdgeHash;
        public int Nodes;
        public long NodeHash;
        public int Buildings;
        public long BuildingHash;
        public int ZoneBlocks;
        public long ZoneHash;
        public int Districts;
        public long AreaHash;
        public int WaterSources;
        public int SyncedObjects;
        public int Money;

        public StateHashCommand ToCommand()
        {
            return new StateHashCommand
            {
                Edges = Edges,
                EdgeHash = EdgeHash,
                Nodes = Nodes,
                NodeHash = NodeHash,
                Buildings = Buildings,
                BuildingHash = BuildingHash,
                ZoneBlocks = ZoneBlocks,
                ZoneHash = ZoneHash,
                Districts = Districts,
                AreaHash = AreaHash,
                WaterSources = WaterSources,
                SyncedObjects = SyncedObjects,
                Money = Money,
            };
        }

        public static HashBundle FromCommand(StateHashCommand c)
        {
            return new HashBundle
            {
                Edges = c.Edges,
                EdgeHash = c.EdgeHash,
                Nodes = c.Nodes,
                NodeHash = c.NodeHash,
                Buildings = c.Buildings,
                BuildingHash = c.BuildingHash,
                ZoneBlocks = c.ZoneBlocks,
                ZoneHash = c.ZoneHash,
                Districts = c.Districts,
                AreaHash = c.AreaHash,
                WaterSources = c.WaterSources,
                SyncedObjects = c.SyncedObjects,
                Money = c.Money,
            };
        }
    }

    /// <summary>Shared queries + fingerprint math so host and clients build the bundle identically.</summary>
    internal static class StateHash
    {
        // Global kill switch — on by default (it is the field bug catcher); CS2M_STATEHASH=0 disables.
        public static readonly bool Enabled =
            System.Environment.GetEnvironmentVariable("CS2M_STATEHASH") != "0";

        public static EntityQueryDesc EdgeDesc() => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
            None = new[]
            {
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Owner>(), // building sub-nets are derived, not compared
            },
        };

        public static EntityQueryDesc NodeDesc() => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Node>() },
            None = new[]
            {
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Owner>(),
            },
        };

        public static EntityQueryDesc BuildingDesc() => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(),
            },
            None = new[]
            {
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Owner>(), // upgrades/sub-buildings are derived
            },
        };

        public static EntityQueryDesc BlockDesc() => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Block>() },
            None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
        };

        public static EntityQueryDesc AreaDesc() => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Game.Areas.Area>(),
                ComponentType.ReadOnly<Game.Areas.Geometry>(),
            },
            None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
        };

        public static EntityQueryDesc DistrictDesc() => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Game.Areas.District>() },
            None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
        };

        public static EntityQueryDesc WaterDesc() => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Game.Simulation.WaterSourceData>() },
            None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
        };

        public static HashBundle Compute(EntityManager em, EntityQuery edges, EntityQuery nodes,
            EntityQuery buildings, EntityQuery blocks, EntityQuery areas, EntityQuery districts,
            EntityQuery water, Game.Simulation.CitySystem city)
        {
            var b = new HashBundle();
            b.EdgeHash = AccEdges(em, edges, out b.Edges);
            b.NodeHash = AccNodes(em, nodes, out b.Nodes);
            b.BuildingHash = AccBuildings(em, buildings, out b.Buildings);
            b.ZoneHash = AccBlocks(em, blocks, out b.ZoneBlocks);
            b.AreaHash = AccAreas(em, areas, out int _);
            b.Districts = districts.CalculateEntityCount();
            b.WaterSources = water.CalculateEntityCount();
            b.SyncedObjects = CS2M_SyncIdSystem.Map.Count;
            b.Money = ReadMoney(em, city);
            return b;
        }

        private static long AccEdges(EntityManager em, EntityQuery q, out int count)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            count = arr.Length;
            long acc = 0;
            try
            {
                foreach (Entity e in arr)
                {
                    Curve c = em.GetComponentData<Curve>(e);
                    acc = unchecked(acc + Seg(c.m_Bezier.a, c.m_Bezier.d));
                }
            }
            finally { arr.Dispose(); }

            return acc;
        }

        private static long AccNodes(EntityManager em, EntityQuery q, out int count)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            count = arr.Length;
            long acc = 0;
            try
            {
                foreach (Entity e in arr)
                {
                    acc = unchecked(acc + Pt(em.GetComponentData<Node>(e).m_Position));
                }
            }
            finally { arr.Dispose(); }

            return acc;
        }

        private static long AccBuildings(EntityManager em, EntityQuery q, out int count)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            count = arr.Length;
            long acc = 0;
            try
            {
                foreach (Entity e in arr)
                {
                    acc = unchecked(acc + Pt(em.GetComponentData<Game.Objects.Transform>(e).m_Position));
                }
            }
            finally { arr.Dispose(); }

            return acc;
        }

        private static long AccBlocks(EntityManager em, EntityQuery q, out int count)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            count = arr.Length;
            long acc = 0;
            try
            {
                foreach (Entity e in arr)
                {
                    Block b = em.GetComponentData<Block>(e);
                    long cells = 0;
                    if (em.HasBuffer<Cell>(e))
                    {
                        DynamicBuffer<Cell> buf = em.GetBuffer<Cell>(e, true);
                        for (int i = 0; i < buf.Length; i++)
                        {
                            // m_Zone.m_Index is the exact value the zone sync ships — hashing it makes
                            // paint divergence (zoned here, blank there) show up as a hash mismatch.
                            cells = unchecked(cells + Mix(i, buf[i].m_Zone.m_Index));
                        }
                    }

                    acc = unchecked(acc + Mix(Pt(b.m_Position), Mix(Mix(b.m_Size.x, b.m_Size.y), cells)));
                }
            }
            finally { arr.Dispose(); }

            return acc;
        }

        private static long AccAreas(EntityManager em, EntityQuery q, out int count)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            count = arr.Length;
            long acc = 0;
            try
            {
                foreach (Entity e in arr)
                {
                    acc = unchecked(acc + Pt(em.GetComponentData<Game.Areas.Geometry>(e).m_CenterPosition));
                }
            }
            finally { arr.Dispose(); }

            return acc;
        }

        public static int ReadMoney(EntityManager em, Game.Simulation.CitySystem city)
        {
            Entity c = city.City;
            if (c != Entity.Null && em.HasComponent<Game.City.PlayerMoney>(c))
            {
                Game.City.PlayerMoney pm = em.GetComponentData<Game.City.PlayerMoney>(c);
                return pm.m_Unlimited ? int.MinValue : pm.money;
            }

            return int.MinValue;
        }

        // Position rounded to 0.5 m, folded with FNV-1a. Identical inputs on both machines -> identical hash.
        private static long Pt(float3 p)
        {
            long x = (int) math.round(p.x * 2f);
            long z = (int) math.round(p.z * 2f);
            unchecked
            {
                long h = 1469598103934665603L;
                h = (h ^ (x & 0xffffffffL)) * 1099511628211L;
                h = (h ^ (z & 0xffffffffL)) * 1099511628211L;
                return h;
            }
        }

        // Order-independent per-segment fingerprint (min/max makes endpoint order irrelevant).
        private static long Seg(float3 a, float3 b)
        {
            long ha = Pt(a);
            long hb = Pt(b);
            return Mix(math.min(ha, hb), math.max(ha, hb));
        }

        private static long Mix(long a, long b)
        {
            unchecked { return a * 1099511628211L + b; }
        }
    }

    /// <summary>Host: broadcast the world fingerprint every ~10 s.</summary>
    public partial class StateHashSenderSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 600;

        private EntityQuery _edges, _nodes, _buildings, _blocks, _areas, _districts, _water;
        private Game.Simulation.CitySystem _city;
        private int _frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _edges = GetEntityQuery(StateHash.EdgeDesc());
            _nodes = GetEntityQuery(StateHash.NodeDesc());
            _buildings = GetEntityQuery(StateHash.BuildingDesc());
            _blocks = GetEntityQuery(StateHash.BlockDesc());
            _areas = GetEntityQuery(StateHash.AreaDesc());
            _districts = GetEntityQuery(StateHash.DistrictDesc());
            _water = GetEntityQuery(StateHash.WaterDesc());
            _city = World.GetOrCreateSystemManaged<Game.Simulation.CitySystem>();
            CS2M.Log.Info($"[Hash] StateHashSenderSystem created (enabled={StateHash.Enabled})");
        }

        protected override void OnUpdate()
        {
            if (!StateHash.Enabled
                || NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING
                || NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            if (++_frame < SendEveryNFrames)
            {
                return;
            }

            _frame = 0;
            HashBundle b = StateHash.Compute(EntityManager, _edges, _nodes, _buildings, _blocks,
                _areas, _districts, _water, _city);
            Command.SendToAll?.Invoke(b.ToCommand());
        }
    }

    /// <summary>
    ///     Clients: compare the host's fingerprint against local state. Only flags a metric that is
    ///     SETTLED-AND-DIVERGED — unchanged on both sides across samples yet still different — which
    ///     rules out the transient mismatch of a command still in flight. Two such confirmations
    ///     (~20 s) trigger a rate-limited chat warning suggesting "/resync"; every drift is logged in
    ///     detail so the exact category (roads / zones / buildings / areas) is known immediately.
    /// </summary>
    public partial class StateHashApplySystem : GameSystemBase
    {
        private EntityQuery _edges, _nodes, _buildings, _blocks, _areas, _districts, _water;
        private Game.Simulation.CitySystem _city;

        private HashBundle _lastLocal;
        private HashBundle _lastHost;
        private bool _haveLast;
        private int _strikes;
        private double _lastWarnedAt;

        protected override void OnCreate()
        {
            base.OnCreate();
            _edges = GetEntityQuery(StateHash.EdgeDesc());
            _nodes = GetEntityQuery(StateHash.NodeDesc());
            _buildings = GetEntityQuery(StateHash.BuildingDesc());
            _blocks = GetEntityQuery(StateHash.BlockDesc());
            _areas = GetEntityQuery(StateHash.AreaDesc());
            _districts = GetEntityQuery(StateHash.DistrictDesc());
            _water = GetEntityQuery(StateHash.WaterDesc());
            _city = World.GetOrCreateSystemManaged<Game.Simulation.CitySystem>();
            CS2M.Log.Info($"[Hash] StateHashApplySystem created (enabled={StateHash.Enabled})");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                _strikes = 0;
                _haveLast = false;
                return;
            }

            if (!StateHash.Enabled || !RemoteStateHashQueue.TryTake(out StateHashCommand cmd))
            {
                return;
            }

            HashBundle local = StateHash.Compute(EntityManager, _edges, _nodes, _buildings, _blocks,
                _areas, _districts, _water, _city);
            HashBundle host = HashBundle.FromCommand(cmd);

            if (_haveLast)
            {
                var drifts = new List<string>();
                Check(drifts, "roads", local.EdgeHash, host.EdgeHash, _lastLocal.EdgeHash, _lastHost.EdgeHash, local.Edges, host.Edges);
                Check(drifts, "nodes", local.NodeHash, host.NodeHash, _lastLocal.NodeHash, _lastHost.NodeHash, local.Nodes, host.Nodes);
                Check(drifts, "buildings", local.BuildingHash, host.BuildingHash, _lastLocal.BuildingHash, _lastHost.BuildingHash, local.Buildings, host.Buildings);
                Check(drifts, "zones", local.ZoneHash, host.ZoneHash, _lastLocal.ZoneHash, _lastHost.ZoneHash, local.ZoneBlocks, host.ZoneBlocks);
                Check(drifts, "areas", local.AreaHash, host.AreaHash, _lastLocal.AreaHash, _lastHost.AreaHash, -1, -1);
                Check(drifts, "synced", local.SyncedObjects, host.SyncedObjects, _lastLocal.SyncedObjects, _lastHost.SyncedObjects, local.SyncedObjects, host.SyncedObjects);
                Check(drifts, "districts", local.Districts, host.Districts, _lastLocal.Districts, _lastHost.Districts, local.Districts, host.Districts);
                Check(drifts, "water", local.WaterSources, host.WaterSources, _lastLocal.WaterSources, _lastHost.WaterSources, local.WaterSources, host.WaterSources);

                if (drifts.Count > 0)
                {
                    _strikes++;
                    CS2M.Log.Info($"[Hash] DRIFT strike={_strikes} [{string.Join(", ", drifts)}] " +
                                  $"money {local.Money}vs{host.Money}");
                    if (_strikes >= 2)
                    {
                        SyncHealth.SetDrift(true, string.Join(", ", drifts));
                        Warn(drifts);
                    }
                }
                else
                {
                    if (_strikes > 0)
                    {
                        CS2M.Log.Verbose("[Hash] converged (drift cleared)");
                    }

                    _strikes = 0;
                    SyncHealth.SetDrift(false, "");
                }
            }

            _lastLocal = local;
            _lastHost = host;
            _haveLast = true;
        }

        // A metric is a confirmed drift only when BOTH sides held steady since the last sample yet
        // still disagree — an in-flight command shows as "changed", not "settled", so it is ignored.
        private static void Check(List<string> drifts, string name, long local, long host,
            long lastLocal, long lastHost, int localCount, int hostCount)
        {
            bool settled = local == lastLocal && host == lastHost;
            if (settled && local != host)
            {
                drifts.Add(localCount >= 0 ? $"{name} {localCount}vs{hostCount}(hash)" : $"{name}(hash)");
            }
        }

        private void Warn(List<string> drifts)
        {
            _strikes = 0;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now - _lastWarnedAt < 300.0)
            {
                return; // at most once per 5 min
            }

            _lastWarnedAt = now;
            try
            {
                CS2M.API.Chat.Instance?.PrintChatMessage("CS2M",
                    $"worlds drifting apart ({string.Join(", ", drifts)}) — ask the host to type /resync");
            }
            catch
            {
            }
        }
    }
}
