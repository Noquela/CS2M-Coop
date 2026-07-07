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
    ///     hashed by the zone's NAME (FNV via <c>ZoneSync.NameHash</c>) — <c>Cell.m_Zone.m_Index</c>
    ///     is a per-boot registration index and is NOT cross-machine comparable (the wire ships
    ///     names for the same reason). Every accumulation uses commutative addition, so entity
    ///     iteration order never affects the result.
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
        public int Routes;
        public long RouteHash;
        public long FeeHash;
        public long TaxHash;
        public long PolicyHash;
        public long WaterHash;
        public long BudgetHash;
        public long LoanHash;
        public long TerrainHash;

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
                Routes = Routes,
                RouteHash = RouteHash,
                FeeHash = FeeHash,
                TaxHash = TaxHash,
                PolicyHash = PolicyHash,
                WaterHash = WaterHash,
                BudgetHash = BudgetHash,
                LoanHash = LoanHash,
                TerrainHash = TerrainHash,
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
                Routes = c.Routes,
                RouteHash = c.RouteHash,
                FeeHash = c.FeeHash,
                TaxHash = c.TaxHash,
                PolicyHash = c.PolicyHash,
                WaterHash = c.WaterHash,
                BudgetHash = c.BudgetHash,
                LoanHash = c.LoanHash,
                TerrainHash = c.TerrainHash,
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

        // Service PREFABS (not world entities) — the budget sliders live per service prefab; no
        // Temp/Deleted filter needed. Same enumeration BudgetDetectorSystem polls.
        public static EntityQueryDesc ServiceDesc() => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Game.Prefabs.ServiceData>() },
        };

        public static EntityQueryDesc RouteDesc() => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Game.Routes.Route>(),
                ComponentType.ReadOnly<Game.Routes.RouteWaypoint>(),
            },
            None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
        };

        public static HashBundle Compute(EntityManager em, EntityQuery edges, EntityQuery nodes,
            EntityQuery buildings, EntityQuery blocks, EntityQuery areas, EntityQuery districts,
            EntityQuery water, EntityQuery routes, Game.Simulation.CitySystem city,
            Game.Simulation.TaxSystem tax, Game.Prefabs.PrefabSystem prefabs,
            Game.Simulation.CityServiceBudgetSystem budget, EntityQuery services,
            Game.Simulation.TerrainSystem terrain)
        {
            // O hash de zonas folda o NOME via ZoneSync — o registro tem que existir antes do primeiro
            // sample nos DOIS lados (senão NameHash=0 pra tudo). Idempotente, custo zero após a 1ª vez.
            ZoneSync.EnsureBuilt(em, prefabs);

            var b = new HashBundle();
            b.EdgeHash = AccEdges(em, edges, out b.Edges);
            b.NodeHash = AccNodes(em, nodes, out b.Nodes);
            b.BuildingHash = AccBuildings(em, buildings, out b.Buildings);
            b.ZoneHash = AccBlocks(em, blocks, out b.ZoneBlocks);
            b.AreaHash = AccAreas(em, areas, out int _);
            b.RouteHash = AccRoutes(em, routes, out b.Routes);
            b.Districts = districts.CalculateEntityCount();
            b.WaterHash = AccWater(em, water, out b.WaterSources);
            b.SyncedObjects = CS2M_SyncIdSystem.Map.Count;
            b.Money = ReadMoney(em, city);
            b.FeeHash = AccFees(em, city);
            b.TaxHash = AccTax(tax);
            b.PolicyHash = AccPolicies(em, city, prefabs);
            b.BudgetHash = AccBudgets(em, budget, services, prefabs);
            b.LoanHash = AccLoan(em, city);
            b.TerrainHash = AccTerrain(terrain);
            return b;
        }

        // Coarse heightmap fingerprint: 32×32 samples over the terrain bounds, quantized to 2 m.
        // Terrain replay is best-effort (per-stroke delta scales with local frame time), so a fine
        // quantum would cry wolf after every terraform session; 2 m keeps the residue quiet while a
        // stroke that never crossed over (many meters) still lights up. Grid positions derive from
        // GetTerrainBounds(), identical on both machines (fixed map extents).
        private static long AccTerrain(Game.Simulation.TerrainSystem terrain)
        {
            if (terrain == null)
            {
                return 0;
            }

            try
            {
                Game.Simulation.TerrainHeightData hd = terrain.GetHeightData(false);
                if (!hd.isCreated)
                {
                    return 0;
                }

                UnityEngine.Bounds bounds = terrain.GetTerrainBounds(); // fixed 14336×14336 map extents
                const int N = 32;
                float3 min = bounds.min, max = bounds.max;
                float2 step = new float2(max.x - min.x, max.z - min.z) / N;
                long acc = 0;
                for (int i = 0; i < N; i++)
                {
                    for (int j = 0; j < N; j++)
                    {
                        var p = new float3(min.x + (i + 0.5f) * step.x, 0f, min.z + (j + 0.5f) * step.y);
                        float h = Game.Simulation.TerrainUtils.SampleHeight(ref hd, p);
                        acc = unchecked(acc + Mix(i * N + j, (long) math.round(h * 0.5f))); // 2 m quantum
                    }
                }

                return acc;
            }
            catch
            {
                return 0; // heightmap unavailable this frame — transient 0 never settles into a drift
            }
        }

        // Service-budget sliders folded per (service prefab NAME, percentage) — the same enumeration
        // BudgetDetectorSystem diffs. Budget sync existed but the radar never watched it, so a missed
        // BudgetCommand skewed income/expenses invisibly.
        private static long AccBudgets(EntityManager em, Game.Simulation.CityServiceBudgetSystem budget,
            EntityQuery services, Game.Prefabs.PrefabSystem prefabs)
        {
            if (budget == null)
            {
                return 0;
            }

            NativeArray<Entity> ents = services.ToEntityArray(Allocator.Temp);
            long acc = 0;
            try
            {
                foreach (Entity e in ents)
                {
                    if (!em.GetComponentData<Game.Prefabs.ServiceData>(e).m_BudgetAdjustable)
                    {
                        continue;
                    }

                    if (!prefabs.TryGetPrefab(e, out Game.Prefabs.PrefabBase pb) || pb == null)
                    {
                        continue;
                    }

                    acc = unchecked(acc + Mix(StableHash(pb.name), budget.GetServiceBudget(e)));
                }
            }
            finally { ents.Dispose(); }

            return acc;
        }

        // Loan.m_Amount on the City. m_LastModified is a per-machine simulation frame index (decomp
        // Tools/LoanSystem.cs LoanActionJob) — folding it would manufacture permanent false drift.
        private static long AccLoan(EntityManager em, Game.Simulation.CitySystem city)
        {
            Entity c = city.City;
            if (c == Entity.Null || !em.HasComponent<Game.Simulation.Loan>(c))
            {
                return 0;
            }

            return em.GetComponentData<Game.Simulation.Loan>(c).m_Amount;
        }

        // City policy buffer (active flag + adjustment) keyed by policy prefab NAME (cross-machine stable,
        // unlike the prefab entity index). A policy toggled on one PC but not the other now shows as drift;
        // before, policies drove the sim invisibly to the radar.
        private static long AccPolicies(EntityManager em, Game.Simulation.CitySystem city,
            Game.Prefabs.PrefabSystem prefabs)
        {
            Entity c = city.City;
            if (c == Entity.Null || !em.HasBuffer<Game.Policies.Policy>(c))
            {
                return 0;
            }

            DynamicBuffer<Game.Policies.Policy> buf = em.GetBuffer<Game.Policies.Policy>(c, true);
            long acc = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                if (!prefabs.TryGetPrefab(buf[i].m_Policy, out Game.Prefabs.PrefabBase pb) || pb == null)
                {
                    continue;
                }

                bool active = (buf[i].m_Flags & Game.Policies.PolicyFlags.Active) != 0;
                // Order-independent: fold each policy by name so buffer order never matters. StableHash
                // (NOT string.GetHashCode, which .NET may randomize per-process → false cross-machine drift).
                acc = unchecked(acc + Mix(Mix(StableHash(pb.name), active ? 1 : 0),
                    (long) math.round(buf[i].m_Adjustment * 100f)));
            }

            return acc;
        }

        // Tax rates (per-category ints from TaxSystem) — cross-machine stable. A tax desync would only show
        // up SLOWLY via money before; folding the rates makes it an immediate drift signal.
        private static long AccTax(Game.Simulation.TaxSystem tax)
        {
            if (tax == null)
            {
                return 0;
            }

            NativeArray<int> rates = tax.GetTaxRates();
            if (!rates.IsCreated)
            {
                return 0;
            }

            long acc = 0;
            for (int i = 0; i < rates.Length; i++)
            {
                acc = unchecked(acc + Mix(i, rates[i]));
            }

            return acc;
        }

        // City ServiceFee buffer folded per (PlayerResource, fee). Fees drive consumption/happiness/income
        // but never move any entity, so a fee-only divergence was invisible to the radar before this.
        private static long AccFees(EntityManager em, Game.Simulation.CitySystem city)
        {
            Entity c = city.City;
            if (c == Entity.Null || !em.HasBuffer<Game.City.ServiceFee>(c))
            {
                return 0;
            }

            DynamicBuffer<Game.City.ServiceFee> fees = em.GetBuffer<Game.City.ServiceFee>(c, true);
            long acc = 0;
            for (int i = 0; i < fees.Length; i++)
            {
                acc = unchecked(acc + Mix((int) fees[i].m_Resource, (long) math.round(fees[i].m_Fee * 1000f)));
            }

            return acc;
        }

        // Per-line fingerprint: RouteNumber folded with each waypoint's world position. Catches a reroute,
        // a line created on one PC only, or a deletion that didn't cross over — none of which move any edge
        // or node, so they were previously invisible to the radar.
        private static long AccRoutes(EntityManager em, EntityQuery q, out int count)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            count = arr.Length;
            long acc = 0;
            try
            {
                foreach (Entity e in arr)
                {
                    long r = em.HasComponent<Game.Routes.RouteNumber>(e)
                        ? em.GetComponentData<Game.Routes.RouteNumber>(e).m_Number
                        : 0;

                    // v55: fold visibility (HiddenRoute) so a hide/show that failed to sync shows as drift.
                    r = Mix(r, em.HasComponent<Game.Routes.HiddenRoute>(e) ? 1 : 0);

                    DynamicBuffer<Game.Routes.RouteWaypoint> wps = em.GetBuffer<Game.Routes.RouteWaypoint>(e, true);
                    for (int i = 0; i < wps.Length; i++)
                    {
                        Entity w = wps[i].m_Waypoint;
                        if (w != Entity.Null && em.HasComponent<Game.Routes.Position>(w))
                        {
                            r = Mix(r, Pt(em.GetComponentData<Game.Routes.Position>(w).m_Position));
                        }
                    }

                    acc = unchecked(acc + r);
                }
            }
            finally { arr.Dispose(); }

            return acc;
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
                    // Fold in the edge's COMPOSITION flags (trees/sidewalks/sound barriers/quays…). Upgrades
                    // change the composition, not the bezier — without this a road-upgrade desync is invisible
                    // to the radar. NetCompositionData.m_Flags is a semantic uint identical across machines
                    // (like Cell.m_Zone.m_Index), unlike the composition entity index. Edge Upgraded itself is
                    // transient (baked into composition same-frame), so the composition is the stable input.
                    long up = 0;
                    if (em.HasComponent<Composition>(e))
                    {
                        up = CompFlags(em, em.GetComponentData<Composition>(e).m_Edge);
                    }

                    acc = unchecked(acc + Mix(Seg(c.m_Bezier.a, c.m_Bezier.d), up));
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
                    // Fold in the node's junction upgrade flags (traffic lights / stop signs / roundabout /
                    // crosswalks). These live as persistent Upgraded.General on the node and never move it,
                    // so without this a junction-control desync is invisible to the radar.
                    long up = 0;
                    if (em.HasComponent<Upgraded>(e))
                    {
                        up = (uint) em.GetComponentData<Upgraded>(e).m_Flags.m_General;
                    }

                    acc = unchecked(acc + Mix(Pt(em.GetComponentData<Node>(e).m_Position), up));
                }
            }
            finally { arr.Dispose(); }

            return acc;
        }

        // DIAGNOSTIC (roads): dump every node's XZ position as one sorted line to CS2M.log so a host/client
        // node-count divergence can be pinned to the EXACT phantom node (diff the two [NodeDump] lines).
        // Gated on env CS2M_NODEDUMP=1 so it never runs in normal play. Removed once the junction bug is closed.
        public static bool NodeDumpOn =>
            System.Environment.GetEnvironmentVariable("CS2M_NODEDUMP") == "1";

        public static void DumpNodes(EntityManager em, EntityQuery q, string tag)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            try
            {
                var list = new List<string>(arr.Length);
                foreach (Entity e in arr)
                {
                    Unity.Mathematics.float3 p = em.GetComponentData<Node>(e).m_Position;
                    int deg = 0;
                    if (em.HasBuffer<Game.Net.ConnectedEdge>(e))
                    {
                        DynamicBuffer<Game.Net.ConnectedEdge> ce = em.GetBuffer<Game.Net.ConnectedEdge>(e, true);
                        for (int i = 0; i < ce.Length; i++)
                        {
                            Entity ed = ce[i].m_Edge;
                            if (em.Exists(ed) && !em.HasComponent<Deleted>(ed)) { deg++; }
                        }
                    }

                    list.Add($"{p.x:F1}/{p.z:F1}:{deg}"); // pos + live degree (junction vs dead-end)
                }

                list.Sort();
                CS2M.Log.Info($"[NodeDump:{tag}] count={list.Count} {string.Join(" ", list)}");
            }
            finally { arr.Dispose(); }
        }

        // v56: which CellFlags bits are folded into the dump's per-cell "~<hex>" suffix. Chosen against
        // decomp/Game/Game/Zones/CellFlags.cs so the dump goes blind exactly where the screen doesn't:
        //   - Visible/Shared/Occupied are the literal inputs (together with Selected) of the game's own
        //     ZoneUtils.GetColorIndex (decomp Zones/ZoneUtils.cs:141-147), which Prefabs/ZoneSystem.cs:
        //     251-253 calls to pick which of the 3 paint variants (normal/occupied/selected) a cell
        //     renders as.
        //   - Blocked/Overridden gate whether a cell's zone paint is drawn AT ALL, independent of color:
        //     a cell can carry the identical m_Zone name + BuildOrder on both machines yet paint
        //     differently because one PC thinks it lost the block-overlap contest and the other doesn't
        //     (CellOverlapJobs.cs:110,546 and CellBlockJobs.cs:421-426 set/clear Blocked;
        //     CellOccupyJobs.cs:246,337,672-674 set/clear Overridden during building placement).
        //   EXCLUDED — not render state, or transient/local-only:
        //   - Selected (0x40): tool-drag preview only (GenerateZonesSystem.cs:501, ApplyZonesSystem.cs:
        //     122-128, ZoneToolSystem.cs:141), live only during an active local zone-brush stroke and
        //     gone by the time both sides settle — comparing it would manufacture drift out of whichever
        //     player happens to be dragging a brush.
        //   - Updating (0x100): scratch bit, cleared same-job before the buffer settles
        //     (CellCheckHelpers.cs:483) — never observed set in a converged state.
        //   - Redundant (0x80): overlap-resolution bookkeeping that CellCheckHelpers.cs:475 and
        //     LotSizeJobs.cs:109 always test OR'd with Blocked — it never carries render information
        //     Blocked doesn't already carry, and (like the m_Index-vs-name bug this dump exists to avoid
        //     repeating) is recomputed by BlockSystem's overlap job on every touch, so folding it
        //     independently risks a brand-new false-positive drift axis.
        //   - Roadside/RoadLeft/RoadRight/RoadBack (0x4,0x200,0x400,0x800): CellBlockJobs.cs-internal
        //     bookkeeping for a cell's direction relative to its owning road; never read by
        //     GetColorIndex nor by any buildability check.
        private const Game.Zones.CellFlags BlockDumpRenderMask =
            Game.Zones.CellFlags.Blocked | Game.Zones.CellFlags.Shared | Game.Zones.CellFlags.Visible |
            Game.Zones.CellFlags.Overridden | Game.Zones.CellFlags.Occupied;

        // DIAGNOSTIC (zones): dump every zone block as position/size(/BuildOrder) plus its per-cell zone
        // NAMES (run-length compressed, spaces→_ so statediff can whitespace-split). Names, not indices —
        // indices are per-machine. Diffing [BlockDump:HOST] vs [BlockDump:CLIENT] pins a zones drift
        // to the exact block: missing block = the road-derived block itself diverged (BuildOrder
        // cascade); same block+different cells = paint/index divergence.
        //
        // v56: two additions the radar's ZoneHash folds away (by design — see AccBlocks) but the SCREEN
        // renders, so the dump was blind to them even when the eye wasn't: (1) each cell token gets a
        // "~<hex>" suffix — the render-relevant CellFlags bits (BlockDumpRenderMask, see above) — whenever
        // m_State != 0, so a same-name-different-paint cell (blocked/occupied/overridden/shared/visible
        // differs) shows up as a differing run-length token instead of comparing equal; (2) the block
        // header gains ":o<m_Order>" when the block carries a Game.Zones.BuildOrder component — the
        // overlap tie-breaker (BlockSystem.cs:403-404) that decides which of two overlapping zone claims
        // wins, so a same-block-different-BuildOrder divergence (paint identical, priority isn't) is now
        // visible instead of silently folded into "no diff". Env-gated like DumpNodes.
        public static void DumpBlocks(EntityManager em, EntityQuery q, string tag)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            try
            {
                var list = new List<string>(arr.Length);
                foreach (Entity e in arr)
                {
                    Game.Zones.Block b = em.GetComponentData<Game.Zones.Block>(e);
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"{b.m_Position.x:F0}/{b.m_Position.z:F0}:{b.m_Size.x}x{b.m_Size.y}");
                    if (em.HasComponent<Game.Zones.BuildOrder>(e))
                    {
                        sb.Append($":o{em.GetComponentData<Game.Zones.BuildOrder>(e).m_Order}");
                    }

                    sb.Append('=');
                    if (em.HasBuffer<Game.Zones.Cell>(e))
                    {
                        DynamicBuffer<Game.Zones.Cell> buf = em.GetBuffer<Game.Zones.Cell>(e, true);
                        string cur = null;
                        int run = 0;
                        for (int i = 0; i < buf.Length; i++)
                        {
                            Game.Zones.Cell cell = buf[i];
                            string n = ZoneSync.Name(cell.m_Zone.m_Index);
                            n = n.Length == 0 ? "-" : n.Replace(' ', '_');
                            if (cell.m_State != 0)
                            {
                                n += $"~{(int) (cell.m_State & BlockDumpRenderMask):X}";
                            }

                            if (n == cur)
                            {
                                run++;
                                continue;
                            }

                            if (cur != null)
                            {
                                sb.Append($"{run}*{cur}|");
                            }

                            cur = n;
                            run = 1;
                        }

                        if (cur != null)
                        {
                            sb.Append($"{run}*{cur}");
                        }
                    }

                    list.Add(sb.ToString());
                }

                list.Sort(System.StringComparer.Ordinal);
                CS2M.Log.Info($"[BlockDump:{tag}] count={list.Count} {string.Join(" ", list)}");
            }
            finally { arr.Dispose(); }
        }

        // DIAGNOSTIC (roads): dump every edge as its canonical endpoint pair so a host/client edge-COUNT
        // divergence (roads NvsM) can be pinned to the exact phantom edge. Env-gated like DumpNodes.
        public static void DumpEdges(EntityManager em, EntityQuery q, string tag)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            try
            {
                var list = new List<string>(arr.Length);
                foreach (Entity e in arr)
                {
                    if (!em.HasComponent<Game.Net.Curve>(e))
                    {
                        continue;
                    }

                    Unity.Mathematics.float3 a = em.GetComponentData<Game.Net.Curve>(e).m_Bezier.a;
                    Unity.Mathematics.float3 d = em.GetComponentData<Game.Net.Curve>(e).m_Bezier.d;
                    // Canonicalise endpoint order so both PCs render the same string regardless of edge dir.
                    bool aFirst = a.x < d.x || (a.x == d.x && a.z <= d.z);
                    list.Add(aFirst
                        ? $"{a.x:F0}/{a.z:F0}-{d.x:F0}/{d.z:F0}"
                        : $"{d.x:F0}/{d.z:F0}-{a.x:F0}/{a.z:F0}");
                }

                list.Sort();
                CS2M.Log.Info($"[EdgeDump:{tag}] count={list.Count} {string.Join(" ", list)}");
            }
            finally { arr.Dispose(); }
        }

        // DIAGNOSTIC (areas): dump every area as center + node-count + owned-flag so an areas(hash)
        // divergence pins to the exact area present/shaped-differently on one side. Env-gated.
        public static void DumpAreas(EntityManager em, EntityQuery q, string tag)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            try
            {
                var list = new List<string>(arr.Length);
                foreach (Entity e in arr)
                {
                    // Same contract filter as AccAreas — the localizer must diff the SAME set the radar hashes.
                    if (!AreaInContract(em, e))
                    {
                        continue;
                    }

                    Unity.Mathematics.float3 c = em.GetComponentData<Game.Areas.Geometry>(e).m_CenterPosition;
                    int nodes = em.HasBuffer<Game.Areas.Node>(e) ? em.GetBuffer<Game.Areas.Node>(e, true).Length : 0;
                    int owned = em.HasComponent<Game.Common.Owner>(e) ? 1 : 0;
                    list.Add($"{c.x:F0}/{c.z:F0}:n{nodes}:o{owned}");
                }

                list.Sort();
                CS2M.Log.Info($"[AreaDump:{tag}] count={list.Count} {string.Join(" ", list)}");
            }
            finally { arr.Dispose(); }
        }

        // DIAGNOSTIC (buildings): dump every building's position + prefab NAME so a host/client
        // building-COUNT divergence (buildings NvsM) can be pinned to the exact phantom/missing
        // building. SAME query filter as AccBuildings (BuildingDesc: All Building+Transform, None
        // Temp/Deleted/Owner) so the dump and the hash never disagree on what counts as "a building".
        // Env-gated like DumpNodes. Prefab name resolved via PrefabSystem.TryGetPrefab(PrefabRef.m_Prefab)
        // — same lookup DeleteDetectorSystem uses to get a cross-machine-stable identifier (prefab
        // ENTITY index is per-boot, but the name is not). Long dumps (400+ buildings) are split across
        // "part=k" lines so no single log line explodes; statediff.py's parser joins the parts back.
        public static void DumpBuildings(EntityManager em, EntityQuery q, Game.Prefabs.PrefabSystem prefabs, string tag)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            try
            {
                var list = new List<string>(arr.Length);
                foreach (Entity e in arr)
                {
                    Unity.Mathematics.float3 p = em.GetComponentData<Game.Objects.Transform>(e).m_Position;
                    string name = "?";
                    if (em.HasComponent<Game.Prefabs.PrefabRef>(e))
                    {
                        Entity prefabEntity = em.GetComponentData<Game.Prefabs.PrefabRef>(e).m_Prefab;
                        if (prefabs.TryGetPrefab(prefabEntity, out Game.Prefabs.PrefabBase pb) && pb != null)
                        {
                            name = pb.name.Replace(' ', '_'); // spaces would break statediff's whitespace split
                        }
                    }

                    list.Add($"{p.x:F1}/{p.z:F1}:{name}");
                }

                list.Sort(System.StringComparer.Ordinal);

                const int ChunkSize = 400;
                if (list.Count <= ChunkSize)
                {
                    CS2M.Log.Info($"[BldgDump:{tag}] count={list.Count} {string.Join(" ", list)}");
                }
                else
                {
                    for (int part = 0, i = 0; i < list.Count; part++, i += ChunkSize)
                    {
                        int n = math.min(ChunkSize, list.Count - i);
                        CS2M.Log.Info($"[BldgDump:{tag}] part={part} count={list.Count} " +
                                      $"{string.Join(" ", list.GetRange(i, n))}");
                    }
                }
            }
            finally { arr.Dispose(); }
        }

        // Semantic composition flags (General/Left/Right) of a net composition entity — cross-machine stable
        // because they are the same enum bits on both PCs, unlike the composition entity's index.
        private static long CompFlags(EntityManager em, Entity comp)
        {
            if (comp == Entity.Null || !em.HasComponent<Game.Prefabs.NetCompositionData>(comp))
            {
                return 0;
            }

            Game.Prefabs.CompositionFlags f = em.GetComponentData<Game.Prefabs.NetCompositionData>(comp).m_Flags;
            return Mix((uint) f.m_General, Mix((uint) f.m_Left, (uint) f.m_Right));
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
                            // Fold the zone's NAME hash, never m_Index: the index is assigned per-boot in
                            // prefab registration order (ZoneSystem.GetNextIndex) and is NOT cross-machine
                            // stable — ZoneSync exists precisely because of that (the wire carries names).
                            // Folding the raw index made this radar scream a permanent zones drift even
                            // with pixel-identical paint (594vs594 hash-diff right after join, teste 2).
                            cells = unchecked(cells + Mix(i, ZoneSync.NameHash(buf[i].m_Zone.m_Index)));
                        }
                    }

                    acc = unchecked(acc + Mix(Pt(b.m_Position), Mix(Mix(b.m_Size.x, b.m_Size.y), cells)));
                }
            }
            finally { arr.Dispose(); }

            return acc;
        }

        // Water source POSITIONS (not just the count) — a relocated source (v55 water-move) is a same-count
        // change the count alone never caught. Transform is added by the apply, so guard for it.
        private static long AccWater(EntityManager em, EntityQuery q, out int count)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            count = arr.Length;
            long acc = 0;
            try
            {
                foreach (Entity e in arr)
                {
                    if (em.HasComponent<Game.Objects.Transform>(e))
                    {
                        // v59: fold the editable params too — an in-place radius/height/rate edit moves no
                        // entity, so the position-only fold was blind to it (the exact gap WaterCommand.Edit
                        // closes). Quantized against float noise; Y stays out (anchored to LOCAL terrain).
                        var w = em.GetComponentData<Game.Simulation.WaterSourceData>(e);
                        long p = Mix((long) math.round(w.m_Radius * 10f),
                            (long) math.round(w.m_Height * 10f));
                        p = Mix(p, (long) math.round(w.m_Multiplier * 100f));
                        p = Mix(p, (long) math.round(w.m_Polluted * 100f));
                        p = Mix(p, w.m_ConstantDepth);
                        acc = unchecked(acc +
                            Mix(Pt(em.GetComponentData<Game.Objects.Transform>(e).m_Position), p));
                    }
                }
            }
            finally { arr.Dispose(); }

            return acc;
        }

        /// <summary>An area is IN the sync contract unless it is a building-owned decorative sub-area
        /// (Owner present, no Extractor): those regenerate LOCALLY per PC (Surface/Space/Hangaround/
        /// Walking…) and are excluded from sync BY DESIGN — hashing them makes the radar cry wolf forever
        /// (the 634-vs-637 phantom-surface drift). Extractor fields (farm/forestry/ore) are owned AND
        /// synced; districts/standalone player surfaces carry no Owner.</summary>
        internal static bool AreaInContract(EntityManager em, Entity e)
        {
            return !em.HasComponent<Game.Common.Owner>(e)
                   || em.HasComponent<Game.Areas.Extractor>(e);
        }

        private static long AccAreas(EntityManager em, EntityQuery q, out int count)
        {
            NativeArray<Entity> arr = q.ToEntityArray(Allocator.Temp);
            count = 0;
            long acc = 0;
            try
            {
                foreach (Entity e in arr)
                {
                    if (!AreaInContract(em, e))
                    {
                        continue;
                    }

                    count++;
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

        // FNV-1a over the string's chars — deterministic across machines/processes, unlike
        // string.GetHashCode() (which .NET can randomize per-process, breaking a cross-machine compare).
        private static long StableHash(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return 0;
            }

            unchecked
            {
                long h = 1469598103934665603L;
                for (int i = 0; i < s.Length; i++)
                {
                    h = (h ^ s[i]) * 1099511628211L;
                }

                return h;
            }
        }
    }

    /// <summary>Host: broadcast the world fingerprint every ~10 s.</summary>
    public partial class StateHashSenderSystem : GameSystemBase
    {
        private const int SendEveryNFrames = 600;

        private EntityQuery _edges, _nodes, _buildings, _blocks, _areas, _districts, _water, _routes,
            _services;
        private Game.Simulation.CitySystem _city;
        private Game.Simulation.TaxSystem _tax;
        private Game.Prefabs.PrefabSystem _prefabs;
        private Game.Simulation.CityServiceBudgetSystem _budget;
        private Game.Simulation.TerrainSystem _terrain;
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
            _routes = GetEntityQuery(StateHash.RouteDesc());
            _services = GetEntityQuery(StateHash.ServiceDesc());
            _city = World.GetOrCreateSystemManaged<Game.Simulation.CitySystem>();
            _tax = World.GetOrCreateSystemManaged<Game.Simulation.TaxSystem>();
            _prefabs = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>();
            _budget = World.GetOrCreateSystemManaged<Game.Simulation.CityServiceBudgetSystem>();
            _terrain = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
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
                _areas, _districts, _water, _routes, _city, _tax, _prefabs, _budget, _services, _terrain);
            Command.SendToAll?.Invoke(b.ToCommand());

            if (StateHash.NodeDumpOn)
            {
                StateHash.DumpNodes(EntityManager, _nodes, "HOST");
                StateHash.DumpEdges(EntityManager, _edges, "HOST");
                StateHash.DumpAreas(EntityManager, _areas, "HOST");
                StateHash.DumpBlocks(EntityManager, _blocks, "HOST");
                StateHash.DumpBuildings(EntityManager, _buildings, _prefabs, "HOST");
            }
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
        private EntityQuery _edges, _nodes, _buildings, _blocks, _areas, _districts, _water, _routes,
            _services;
        private Game.Simulation.CitySystem _city;
        private Game.Simulation.TaxSystem _tax;
        private Game.Prefabs.PrefabSystem _prefabs;
        private Game.Simulation.CityServiceBudgetSystem _budget;
        private Game.Simulation.TerrainSystem _terrain;

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
            _routes = GetEntityQuery(StateHash.RouteDesc());
            _services = GetEntityQuery(StateHash.ServiceDesc());
            _city = World.GetOrCreateSystemManaged<Game.Simulation.CitySystem>();
            _tax = World.GetOrCreateSystemManaged<Game.Simulation.TaxSystem>();
            _prefabs = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>();
            _budget = World.GetOrCreateSystemManaged<Game.Simulation.CityServiceBudgetSystem>();
            _terrain = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
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
                _areas, _districts, _water, _routes, _city, _tax, _prefabs, _budget, _services, _terrain);
            HashBundle host = HashBundle.FromCommand(cmd);

            if (_haveLast)
            {
                var drifts = new List<string>();
                Check(drifts, "roads", local.EdgeHash, host.EdgeHash, _lastLocal.EdgeHash, _lastHost.EdgeHash, local.Edges, host.Edges);
                Check(drifts, "nodes", local.NodeHash, host.NodeHash, _lastLocal.NodeHash, _lastHost.NodeHash, local.Nodes, host.Nodes);
                Check(drifts, "buildings", local.BuildingHash, host.BuildingHash, _lastLocal.BuildingHash, _lastHost.BuildingHash, local.Buildings, host.Buildings);
                Check(drifts, "zones", local.ZoneHash, host.ZoneHash, _lastLocal.ZoneHash, _lastHost.ZoneHash, local.ZoneBlocks, host.ZoneBlocks);
                Check(drifts, "areas", local.AreaHash, host.AreaHash, _lastLocal.AreaHash, _lastHost.AreaHash, -1, -1);
                Check(drifts, "routes", local.RouteHash, host.RouteHash, _lastLocal.RouteHash, _lastHost.RouteHash, local.Routes, host.Routes);
                Check(drifts, "fees", local.FeeHash, host.FeeHash, _lastLocal.FeeHash, _lastHost.FeeHash, -1, -1);
                Check(drifts, "tax", local.TaxHash, host.TaxHash, _lastLocal.TaxHash, _lastHost.TaxHash, -1, -1);
                Check(drifts, "policies", local.PolicyHash, host.PolicyHash, _lastLocal.PolicyHash, _lastHost.PolicyHash, -1, -1);
                Check(drifts, "budget", local.BudgetHash, host.BudgetHash, _lastLocal.BudgetHash, _lastHost.BudgetHash, -1, -1);
                Check(drifts, "loan", local.LoanHash, host.LoanHash, _lastLocal.LoanHash, _lastHost.LoanHash, -1, -1);
                Check(drifts, "terrain", local.TerrainHash, host.TerrainHash, _lastLocal.TerrainHash, _lastHost.TerrainHash, -1, -1);
                Check(drifts, "synced", local.SyncedObjects, host.SyncedObjects, _lastLocal.SyncedObjects, _lastHost.SyncedObjects, local.SyncedObjects, host.SyncedObjects);
                Check(drifts, "districts", local.Districts, host.Districts, _lastLocal.Districts, _lastHost.Districts, local.Districts, host.Districts);
                Check(drifts, "water", local.WaterHash, host.WaterHash, _lastLocal.WaterHash, _lastHost.WaterHash, local.WaterSources, host.WaterSources);

                // v56: cell-FLAG divergence (overlap visibility) is deliberately invisible to the zones
                // hash, so it never enters `drifts` — dump blocks EVERY sample under NODEDUMP (like the
                // host does) so statediff always has a CLIENT side to pair, drift or no drift. (First
                // placed drift-gated by mistake — client produced zero dumps on a flags-only split.)
                if (StateHash.NodeDumpOn)
                {
                    StateHash.DumpBlocks(EntityManager, _blocks, "CLIENT");
                    // Areas too (farm fields): the drift-gated dump below never fires when the areas
                    // hash is blind to the divergence (AreaInContract filtering / polygon shape not
                    // fully folded) — same lesson as the flags-only zone split.
                    StateHash.DumpAreas(EntityManager, _areas, "CLIENT");
                }

                if (drifts.Count > 0)
                {
                    _strikes++;
                    CS2M.Log.Info($"[Hash] DRIFT strike={_strikes} [{string.Join(", ", drifts)}] " +
                                  $"money {local.Money}vs{host.Money}");

                    // DIAGNOSTIC: on a node/road divergence, dump this side's nodes+edges so the phantom
                    // node/edge is pinpointable by diffing against the host's [NodeDump/EdgeDump:HOST] lines.
                    if (StateHash.NodeDumpOn && drifts.Exists(d => d.StartsWith("nodes")))
                    {
                        StateHash.DumpNodes(EntityManager, _nodes, "CLIENT");
                    }

                    if (StateHash.NodeDumpOn && drifts.Exists(d => d.StartsWith("roads") || d.StartsWith("nodes")))
                    {
                        StateHash.DumpEdges(EntityManager, _edges, "CLIENT");
                    }

                    if (StateHash.NodeDumpOn && drifts.Exists(d => d.StartsWith("areas")))
                    {
                        StateHash.DumpAreas(EntityManager, _areas, "CLIENT");
                    }

                    // (zones BlockDump agora roda todo sample, fora do gate de drift — ver acima.)

                    if (StateHash.NodeDumpOn && drifts.Exists(d => d.StartsWith("buildings")))
                    {
                        StateHash.DumpBuildings(EntityManager, _buildings, _prefabs, "CLIENT");
                    }
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
