using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue for remote work-area edits.</summary>
    public static class RemoteAreaQueue
    {
        private static readonly Queue<AreaEditCommand> Queue = new Queue<AreaEditCommand>();
        private static readonly object Lock = new object();

        public static void Enqueue(AreaEditCommand cmd)
        {
            lock (Lock) { Queue.Enqueue(cmd); }
        }

        public static bool TryDequeue(out AreaEditCommand cmd)
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
        }
    }

    /// <summary>
    ///     Detects edits to building-owned areas (repainting a farm field etc.): an Applied Area with
    ///     an Owner that is a building. Districts have their own sync; remote-written areas carry
    ///     <c>CS2M_RemotePlaced</c> (echo guard).
    /// </summary>
    public partial class AreaEditDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _appliedAreas;
        private EntityQuery _appliedStandalone;
        private EntityQuery _deletedAreas;
        private EntityQuery _anchorBuildings;
        private readonly HashSet<Entity> _recentlySent = new HashSet<Entity>();
        private int _clearCounter;
        private int _scanCounter;
        private int _scanPasses;
        // v57 AREA-FIX: bounded radius for the spatial nearest-Building fallback in FindAnchor — see its
        // doc comment. Must match AreaEditApplySystem.AnchorBuildingSearchRadius byte-for-byte.
        private const float AnchorBuildingSearchRadius = 30f;
        // ~4 scan passes (at ~1 Hz) of grace: a save's work-area fields are identical on both PCs, so their
        // first sighting at world-load needs no sync. A first-sight field AFTER warmup is a farm placed this
        // session whose field spawned divergently → the host ships its shape.
        private const int WarmupScans = 4;
        private EntityQuery _workAreas;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // v51 FIELD FIX: editing an existing work area only marks it Updated — never Applied —
            // so the Applied-based query below NEVER saw real edits ("my farm field doesn't show up
            // until /resync"). Poll owned areas at ~1 Hz and diff their polygon hash instead.
            // SCOPE (fix 04/07): only RESOURCE FIELDS (Game.Areas.Extractor = farm/forestry/ore/oil/fish
            // fields — the work area the PLAYER draws). Was matching EVERY owned area, which shipped 90+
            // cosmetic Surface/Space sub-areas (Grass/Sand/Walking/Hangaround/Park) per building — those are
            // regenerated locally by the SUBAREAS handler when the owner syncs, so re-shipping them just
            // caused unmatchable rewrites/deletes on the client (the "farm didn't sync" mess Bruno saw).
            _workAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<Game.Areas.Extractor>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.District>(),
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                },
            });
            // Same scope as _workAreas: only resource FIELDS (Extractor), never cosmetic Surface/Space.
            _appliedAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<Game.Areas.Extractor>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.District>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            // v46: standalone areas — surfaces/pavement painted with the area tool, no owner.
            _appliedStandalone = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Game.Areas.District>(), // districts have their own sync
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            // v46: bulldozed areas (surfaces, work areas AND districts) sync by prefab + center.
            _deletedAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });

            // v57 AREA-FIX: nearest-Building spatial search used by FindAnchor when the owner-chain walk
            // never reaches a Building (a farm's "Agriculture Area Placeholder" — see FindAnchor's doc
            // comment). Same shape as AreaEditApplySystem's `_buildings` query (kept separate, one per
            // class, to avoid a cross-class field dependency).
            _anchorBuildings = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            RequireAnyForUpdate(_appliedAreas, _appliedStandalone, _deletedAreas);
            CS2M.Log.Info("[Area] AreaEditDetectorSystem created");
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
                _recentlySent.Clear();
            }

            NativeArray<Entity> areas = _appliedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_recentlySent.Add(area))
                    {
                        continue;
                    }

                    // Anchor up the owner chain to a Transform (building, or a farm's placeholder) — same
                    // stable address used by the resize scanner, so a farm FIELD (placeholder-owned) syncs.
                    Entity owner = FindAnchor(EntityManager.GetComponentData<Owner>(area).m_Owner);
                    if (owner == Entity.Null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase prefab) || prefab == null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab,
                            out PrefabBase ownerPrefab) || ownerPrefab == null)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    var xs = new float[nodes.Length];
                    var ys = new float[nodes.Length];
                    var zs = new float[nodes.Length];
                    var els = new float[nodes.Length];
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        xs[i] = nodes[i].m_Position.x;
                        ys[i] = nodes[i].m_Position.y;
                        zs[i] = nodes[i].m_Position.z;
                        els[i] = nodes[i].m_Elevation;
                    }

                    var ownerTf = EntityManager.GetComponentData<Game.Objects.Transform>(owner);
                    Command.SendToAll?.Invoke(new AreaEditCommand
                    {
                        OwnerSyncId = EntityManager.HasComponent<CS2M_SyncId>(owner)
                            ? EntityManager.GetComponentData<CS2M_SyncId>(owner).m_Id
                            : 0,
                        OwnerPrefabName = ownerPrefab.name,
                        OwnerX = ownerTf.m_Position.x,
                        OwnerY = ownerTf.m_Position.y,
                        OwnerZ = ownerTf.m_Position.z,
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        Xs = xs, Ys = ys, Zs = zs, Els = els,
                    });
                    CS2M.Log.Info($"[Area] DETECT+SEND name={prefab.name} owner={ownerPrefab.name} nodes={nodes.Length}");
                }
            }
            finally
            {
                areas.Dispose();
            }

            DetectStandalone();
            DetectDeleted();

            if (++_scanCounter >= 60)
            {
                _scanCounter = 0;
                ScanWorkAreaEdits();
            }
        }

        /// <summary>v51: ~1 Hz polygon-hash diff over owned areas — the only reliable signal for a
        /// player RESHAPING a work area (vanilla marks the entity Updated, never Applied). First
        /// sight is a silent baseline; the apply system updates the shared hash so a remotely
        /// applied rewrite is never bounced back.</summary>
        private void ScanWorkAreaEdits()
        {
            _scanPasses++;
            bool isServer = NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER;
            NativeArray<Entity> areas = _workAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    if (nodes.Length == 0)
                    {
                        continue;
                    }

                    int hash = WorkAreaHash.Compute(nodes);
                    bool firstSight = !WorkAreaHash.TryGet(area, out int known);
                    WorkAreaHash.Set(area, hash);

                    // Known area, no polygon change → nothing to ship.
                    if (!firstSight && known == hash)
                    {
                        continue;
                    }

                    // AUTHORITY (Fase 1): the HOST is the single source of truth for a building-owned work-area
                    // (farm/extractor FIELD) shape. Each PC's own AreaSpawnSystem spawns the field locally and
                    // DIVERGENTLY; if the client also shipped its shape, the two would ping-pong forever (the
                    // 585-vs-593 areas drift). So only the host ships — the client keeps its locally-spawned
                    // field but gets rewritten to the host's polygon via the identity apply (owner+prefab).
                    // This is why the client is never field-less: no AREASUPPRESS needed.
                    //
                    // KNOWN TRADE-OFF (adversarial review, Fase 1): this ALSO means a CLIENT-initiated field
                    // RESHAPE does not propagate to the host — the host owns the shape. Common cases still work
                    // (host places/edits a farm → syncs; client places a farm → host spawns+ships its field), so
                    // this is an accepted Fase-1 limitation, not a bug. Lifting it (let known-change reshapes
                    // ship from either side, guarded by WorkAreaHash echo) needs a 2-sim to confirm no ping-pong.
                    if (!isServer)
                    {
                        continue;
                    }

                    // First-sight fields at WORLD-LOAD are identical on both PCs (loaded from the same save) →
                    // no need to ship. But a first-sight field AFTER warmup is a farm the host placed THIS
                    // session, whose field spawned with a shape the client can't match → ship the baseline so
                    // the client rewrites to the host's exact polygon. This is the real "farm field never syncs" fix.
                    if (firstSight && _scanPasses <= WarmupScans)
                    {
                        continue;
                    }

                    // Anchor = the nearest ancestor up the owner chain that has a Transform (a building, or
                    // a farm's "Agriculture Area Placeholder"). Using the OWNER anchor (not the polygon
                    // centre) is what makes a RESIZE sync: the field's centroid MOVES when you redraw it, but
                    // its owner does not. Old code skipped any non-Building owner outright, so the farm work-
                    // area never synced. Walk up to 4 links to reach a Transform.
                    Entity anchor = FindAnchor(EntityManager.GetComponentData<Owner>(area).m_Owner);
                    if (anchor == Entity.Null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase prefab) || prefab == null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(anchor).m_Prefab,
                            out PrefabBase ownerPrefab) || ownerPrefab == null)
                    {
                        continue;
                    }

                    Entity owner = anchor;

                    var xs = new float[nodes.Length];
                    var ys = new float[nodes.Length];
                    var zs = new float[nodes.Length];
                    var els = new float[nodes.Length];
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        xs[i] = nodes[i].m_Position.x;
                        ys[i] = nodes[i].m_Position.y;
                        zs[i] = nodes[i].m_Position.z;
                        els[i] = nodes[i].m_Elevation;
                    }

                    var ownerTf = EntityManager.GetComponentData<Game.Objects.Transform>(owner);
                    Command.SendToAll?.Invoke(new AreaEditCommand
                    {
                        OwnerSyncId = EntityManager.HasComponent<CS2M_SyncId>(owner)
                            ? EntityManager.GetComponentData<CS2M_SyncId>(owner).m_Id
                            : 0,
                        OwnerPrefabName = ownerPrefab.name,
                        OwnerX = ownerTf.m_Position.x,
                        OwnerY = ownerTf.m_Position.y,
                        OwnerZ = ownerTf.m_Position.z,
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        Xs = xs, Ys = ys, Zs = zs, Els = els,
                    });
                    CS2M.Log.Info($"[Area] DETECT+SEND edit name={prefab.name} owner={ownerPrefab.name} nodes={nodes.Length} (polygon diff)");
                }
            }
            finally
            {
                areas.Dispose();
            }
        }

        /// <summary>Nearest ancestor up the owner chain that IS the actual building (has Building +
        /// Transform + PrefabRef), preferred over any intermediate Transform+PrefabRef-carrying object
        /// (e.g. a farm's "Agriculture Area Placeholder" that sits between the field-Area and its
        /// building). BUG FIX (area-form-diverges): the old version returned the FIRST Transform+PrefabRef
        /// ancestor it met — for a farm field that's the placeholder, not the building — and the
        /// placeholder is a derived sub-object that never gets a <see cref="CS2M_SyncId"/> (it's
        /// deliberately excluded from placement sync, see PlacementDetectorSystem.DetectExtensions, to
        /// avoid duplicating it). Shipping OwnerSyncId=0 for the placeholder forced the receiver's
        /// ResolveOwner into a fragile ~3 m proximity guess, which silently resolved to the wrong anchor
        /// LEVEL (the building) while the receiver's own FindAnchorApply (unchanged) still returned the
        /// placeholder for its owned area — a level mismatch that made the "same anchor" comparison in
        /// ApplyOne always fail, so the host's shape was never written onto the client's manually-placed
        /// field (it fell through to CREATE a second, duplicate area instead). Walking through to the
        /// BUILDING gives both sides an anchor that (a) always carries a CS2M_SyncId (buildings register
        /// one on placement — PlacementDetectorSystem/RemotePlacementApplySystem) and (b) is reached
        /// identically by both FindAnchor (sender) and FindAnchorApply (receiver), since a placeholder's
        /// own Owner already points at the same building.
        ///
        /// v57 AREA-FIX (campo-de-fazenda-diverge, 2-sim 06/07): the walk above assumed a placeholder's
        /// Owner chain eventually REACHES its building — measured false for a livestock "Agriculture Area
        /// Placeholder": it has no Owner component leading to a Building at all (confirmed live: the
        /// detector log still showed owner=Placeholder, i.e. this method fell through to the structural
        /// `fallback`). Shipping OwnerSyncId=0 for that placeholder sent ResolveOwner into its OTHER
        /// fallback — a ~3 m "nearest Building" search using the PLACEHOLDER's own coordinates, with NO
        /// name/identity filter at all. On a map with more than one farm of the same work type (sharing
        /// the identical placeholder prefab), that search can silently land on a DIFFERENT farm's building
        /// if it happens to sit within 3 m of THIS farm's placeholder — the shipped polygon then overwrote
        /// that OTHER farm's field, ~170 m from where it belonged (host/client both reported the same node
        /// + owner COUNT, only the position differed — a wrong-target rewrite, not a malformed one).
        /// Fix: when the structural walk finds no Building, do a bounded SPATIAL search (see
        /// <see cref="AnchorBuildingSearchRadius"/>) for the nearest Building around the fallback anchor's
        /// OWN position and anchor there instead of on the placeholder itself. A farm's placeholder always
        /// sits within its own building's footprint, so this reliably finds the RIGHT building — which,
        /// once synced, always carries a CS2M_SyncId, so the receiver resolves it by EXACT id (never by
        /// position/name guessing). <see cref="AreaEditApplySystem.FindAnchorApply"/> mirrors this
        /// byte-for-byte so both sides land on the same anchor level. Falls back to the OLD behavior
        /// (return the raw structural fallback) when no Building is within the search radius. Entity.Null
        /// if nothing at all within 5 links.</summary>
        private Entity FindAnchor(Entity owner)
        {
            Entity e = owner;
            Entity fallback = Entity.Null;
            for (int guard = 0; e != Entity.Null && guard < 5; guard++)
            {
                if (!EntityManager.Exists(e))
                {
                    break;
                }

                if (EntityManager.HasComponent<Game.Objects.Transform>(e)
                    && EntityManager.HasComponent<PrefabRef>(e))
                {
                    if (EntityManager.HasComponent<Game.Buildings.Building>(e))
                    {
                        return e; // the actual building — stable, SyncId-bearing anchor
                    }

                    if (fallback == Entity.Null)
                    {
                        fallback = e; // remember the nearest Transform+PrefabRef in case no Building is found
                    }
                }

                if (!EntityManager.HasComponent<Owner>(e))
                {
                    break;
                }

                e = EntityManager.GetComponentData<Owner>(e).m_Owner;
            }

            if (fallback == Entity.Null)
            {
                return Entity.Null;
            }

            Entity nearBuilding = FindNearestBuilding(fallback, _anchorBuildings);
            return nearBuilding != Entity.Null ? nearBuilding : fallback;
        }

        /// <summary>Shared by FindAnchor/FindAnchorApply (mirrored in AreaEditApplySystem) — see
        /// FindAnchor's v57 doc comment for why this exists.</summary>
        private Entity FindNearestBuilding(Entity near, EntityQuery buildings)
        {
            if (!EntityManager.HasComponent<Game.Objects.Transform>(near))
            {
                return Entity.Null;
            }

            float3 pos = EntityManager.GetComponentData<Game.Objects.Transform>(near).m_Position;
            Entity best = Entity.Null;
            float bestDSq = AnchorBuildingSearchRadius * AnchorBuildingSearchRadius;
            NativeArray<Entity> candidates = buildings.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity b in candidates)
                {
                    float3 bp = EntityManager.GetComponentData<Game.Objects.Transform>(b).m_Position;
                    float dx = bp.x - pos.x;
                    float dz = bp.z - pos.z;
                    float d = dx * dx + dz * dz;
                    if (d < bestDSq)
                    {
                        bestDSq = d;
                        best = b;
                    }
                }
            }
            finally
            {
                candidates.Dispose();
            }

            return best;
        }

        private void DetectStandalone()
        {
            if (_appliedStandalone.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> areas = _appliedStandalone.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_recentlySent.Add(area))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    var xs = new float[nodes.Length];
                    var ys = new float[nodes.Length];
                    var zs = new float[nodes.Length];
                    var els = new float[nodes.Length];
                    float cx = 0f, cz = 0f;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        xs[i] = nodes[i].m_Position.x;
                        ys[i] = nodes[i].m_Position.y;
                        zs[i] = nodes[i].m_Position.z;
                        els[i] = nodes[i].m_Elevation;
                        cx += xs[i];
                        cz += zs[i];
                    }

                    Command.SendToAll?.Invoke(new AreaEditCommand
                    {
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        Xs = xs, Ys = ys, Zs = zs, Els = els,
                        CenterX = cx / nodes.Length,
                        CenterZ = cz / nodes.Length,
                    });
                    CS2M.Log.Info($"[Area] DETECT+SEND standalone name={prefab.name} nodes={nodes.Length}");
                }
            }
            finally
            {
                areas.Dispose();
            }
        }

        private void DetectDeleted()
        {
            if (_deletedAreas.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> areas = _deletedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_recentlySent.Add(area))
                    {
                        continue;
                    }

                    // v50.2 FIELD FIX: an owned area whose owner is dying is CASCADE, not a player
                    // action — the building delete already syncs and cascades the same sub-areas on
                    // every PC. Re-sending them (734 in one session, from the host's sim demolishing
                    // abandoned buildings) deleted walking areas and FARM FIELDS under living
                    // buildings on the other PCs. An owned area deleted while its owner LIVES is a
                    // real edit (clearing a work area) and still syncs.
                    if (EntityManager.HasComponent<Owner>(area))
                    {
                        // CONTRACT SCOPE (same rule as the create/edit queries): an owned area is only
                        // synced when it is a resource FIELD (Extractor). Cosmetic sub-areas (Hangaround/
                        // Walking/Grass…) are regenerated LOCALLY per machine — the sim swaps them while
                        // the owner lives, and shipping that delete removed the OTHER PC's healthy local copy
                        // (the areas(hash) drift Bruno hit on 05/07).
                        if (!EntityManager.HasComponent<Game.Areas.Extractor>(area))
                        {
                            continue;
                        }

                        Entity areaOwner = EntityManager.GetComponentData<Owner>(area).m_Owner;
                        if (!EntityManager.Exists(areaOwner)
                            || EntityManager.HasComponent<Deleted>(areaOwner))
                        {
                            continue;
                        }
                    }

                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    if (nodes.Length == 0)
                    {
                        continue;
                    }

                    float cx = 0f, cz = 0f;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        cx += nodes[i].m_Position.x;
                        cz += nodes[i].m_Position.z;
                    }

                    Command.SendToAll?.Invoke(new AreaEditCommand
                    {
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        Delete = true,
                        CenterX = cx / nodes.Length,
                        CenterZ = cz / nodes.Length,
                    });
                    CS2M.Log.Info($"[Area] DETECT+SEND delete name={prefab.name} center=({cx / nodes.Length:F0},{cz / nodes.Length:F0})");
                }
            }
            finally
            {
                areas.Dispose();
            }
        }
    }

    /// <summary>
    ///     Applies a remote work-area edit: resolves the owner building, rewrites the matching owned
    ///     area's polygon (or creates the area via a Permanent definition when missing). Runs before
    ///     Modification1 so the area triangulation/visual systems consume Updated the same frame.
    /// </summary>
    /// <summary>Shared polygon-hash snapshot per owned area — the edit-diff scanner's memory,
    /// updated by the apply system so remote rewrites never echo back.</summary>
    public static class WorkAreaHash
    {
        private static readonly System.Collections.Generic.Dictionary<Entity, int> Hashes =
            new System.Collections.Generic.Dictionary<Entity, int>();
        private static readonly object Lock = new object();

        public static int Compute(DynamicBuffer<Game.Areas.Node> nodes)
        {
            unchecked
            {
                int h = (int) 2166136261 ^ nodes.Length;
                for (int i = 0; i < nodes.Length; i++)
                {
                    h = (h * 16777619) ^ (int) math.round(nodes[i].m_Position.x * 10f);
                    h = (h * 16777619) ^ (int) math.round(nodes[i].m_Position.z * 10f);
                }

                return h;
            }
        }

        public static void Set(Entity e, int hash)
        {
            lock (Lock) { Hashes[e] = hash; }
        }

        public static bool TryGet(Entity e, out int hash)
        {
            lock (Lock) { return Hashes.TryGetValue(e, out hash); }
        }

        public static void Clear()
        {
            lock (Lock) { Hashes.Clear(); }
        }
    }

    public partial class AreaEditApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _ownedAreas;
        private EntityQuery _buildings;
        private readonly List<Entity> _pendingDefinitions = new List<Entity>();

        // A building-owned area (farm field, surface) can arrive BEFORE the owner building's SUBAREAS
        // handler has regenerated it locally. The old code SKIPPED create then (to avoid duplicating the
        // SUBAREAS copy) — but if the shipped shape differs from the regenerated one, that host-authoritative
        // shape was LOST and the field drifted forever (both sides baseline their own). Now we PARK the
        // command and retry: once the SUBAREAS area appears we rewrite it to the host's exact polygon; only
        // if it never appears within the window do we create it ourselves (SUBAREAS genuinely produced none).
        private struct PendingArea { public AreaEditCommand Cmd; public int FramesLeft; }
        private readonly List<PendingArea> _pendingAreas = new List<PendingArea>();
        private const int AreaRetryTtlFrames = 300; // ~5 s at 60 fps
        // v57 AREA-FIX: must match AreaEditDetectorSystem.AnchorBuildingSearchRadius byte-for-byte.
        private const float AnchorBuildingSearchRadius = 30f;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _ownedAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.District>(),
                },
            });
            _buildings = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            CS2M.Log.Info("[Area] AreaEditApplySystem created");
        }

        protected override void OnUpdate()
        {
            for (int i = 0; i < _pendingDefinitions.Count; i++)
            {
                if (EntityManager.Exists(_pendingDefinitions[i]))
                {
                    EntityManager.DestroyEntity(_pendingDefinitions[i]);
                }
            }

            _pendingDefinitions.Clear();

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // Retry parked owned-area rewrites first (owner/area may have materialised since).
            RetryPendingAreas();

            while (RemoteAreaQueue.TryDequeue(out AreaEditCommand cmd))
            {
                try
                {
                    if (!ApplyOne(cmd, lastTry: false))
                    {
                        _pendingAreas.Add(new PendingArea { Cmd = cmd, FramesLeft = AreaRetryTtlFrames });
                    }
                }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area apply failed: {ex.Message}"); }
            }
        }

        /// <summary>Retry parked owned-area commands; on TTL expiry force the create (SUBAREAS never made it).</summary>
        private void RetryPendingAreas()
        {
            for (int i = _pendingAreas.Count - 1; i >= 0; i--)
            {
                PendingArea p = _pendingAreas[i];
                p.FramesLeft--;
                bool last = p.FramesLeft <= 0;
                bool handled;
                try { handled = ApplyOne(p.Cmd, last); }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] area retry failed: {ex.Message}"); handled = true; }

                if (handled || last)
                {
                    _pendingAreas.RemoveAt(i);
                }
                else
                {
                    _pendingAreas[i] = p;
                }
            }
        }

        /// <summary>Returns true when handled (applied/created/invalid); false only when retryable
        /// (owner or its SUBAREAS-regenerated area not present yet). <paramref name="lastTry"/> forces the
        /// create path when the retry window is exhausted.</summary>
        private bool ApplyOne(AreaEditCommand cmd, bool lastTry)
        {
            if (cmd.Delete)
            {
                ApplyDelete(cmd);
                return true;
            }

            if (cmd.Xs == null || cmd.Zs == null || cmd.Xs.Length < 3)
            {
                return true;
            }

            // v46: standalone area (surface/pavement — no owner shipped).
            if (cmd.OwnerSyncId == 0 && string.IsNullOrEmpty(cmd.OwnerPrefabName))
            {
                ApplyStandalone(cmd);
                return true;
            }

            Entity owner = ResolveOwner(cmd);
            if (owner == Entity.Null)
            {
                // The owner building may not be placed on this PC yet — retry unless the window is spent.
                if (!lastTry)
                {
                    return false;
                }

                CS2M.Log.Info($"[Area] DROP noOwner owner={cmd.OwnerPrefabName} at=({cmd.OwnerX:F0},{cmd.OwnerZ:F0}) after retries");
                return true;
            }

            // Existing owned area of the same prefab → rewrite its polygon in place.
            Entity target = Entity.Null;
            NativeArray<Entity> areas = _ownedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    // Match by ANCHOR, not direct owner: a farm FIELD is owned by a placeholder whose owner
                    // is the building, and ResolveOwner may have resolved either — walking both sides to the
                    // same anchor makes them meet regardless of which level carried the Transform.
                    if (FindAnchorApply(EntityManager.GetComponentData<Owner>(area).m_Owner) != owner)
                    {
                        continue;
                    }

                    if (_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase p) && p != null && p.name == cmd.PrefabName)
                    {
                        target = area;
                        break;
                    }
                }
            }
            finally
            {
                areas.Dispose();
            }

            if (target != Entity.Null)
            {
                if (!EntityManager.HasComponent<CS2M_RemotePlaced>(target))
                {
                    EntityManager.AddComponent<CS2M_RemotePlaced>(target); // echo guard
                }

                if (!EntityManager.HasComponent<Updated>(target))
                {
                    EntityManager.AddComponent<Updated>(target);
                }

                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(target);
                nodes.ResizeUninitialized(cmd.Xs.Length);
                for (int i = 0; i < cmd.Xs.Length; i++)
                {
                    float el = cmd.Els != null && i < cmd.Els.Length ? cmd.Els[i] : float.MinValue;
                    nodes[i] = new Game.Areas.Node(new float3(cmd.Xs[i], cmd.Ys[i], cmd.Zs[i]), el);
                }

                // v51: update the shared polygon hash so the edit-diff scanner treats this remotely
                // applied shape as already-known (no bounce-back).
                WorkAreaHash.Set(target, WorkAreaHash.Compute(nodes));

                CS2M.Log.Info($"[Area] APPLIED rewrite name={cmd.PrefabName} nodes={cmd.Xs.Length} entity={target.Index}");
                return true;
            }

            // The owner BUILDING regenerates its sub-areas itself on the receiver (the object-place
            // SUBAREAS handler — same as sub-nets), so CREATING them here too would DUPLICATE every field/
            // surface. But the SUBAREAS-generated area may not EXIST yet when this command arrives, and its
            // regenerated shape can differ from the host's — so we PARK and retry: once it materialises the
            // rewrite path above stamps the host's exact polygon onto it. Only when the retry window is spent
            // (SUBAREAS genuinely produced nothing) do we fall through and create it ourselves — the fix for
            // "farm field never syncs / areas(hash) drifts forever". Non-building owners create immediately.
            if (EntityManager.HasComponent<Game.Buildings.Building>(owner) && !lastTry)
            {
                return false; // retry: wait for SUBAREAS, then rewrite to the host's shape
            }

            // No such area yet (or window spent) → create it via the vanilla Permanent-definition path.
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase areaPrefab) || areaPrefab == null
                || !_prefabSystem.TryGetEntity(areaPrefab, out Entity areaPrefabEntity))
            {
                CS2M.Log.Info($"[Area] RESOLVE-FAIL name={cmd.PrefabName}");
                return true;
            }

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = areaPrefabEntity,
                m_Owner = owner,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponent<Updated>(def);
            DynamicBuffer<Game.Areas.Node> defNodes = EntityManager.AddBuffer<Game.Areas.Node>(def);
            defNodes.ResizeUninitialized(cmd.Xs.Length);
            for (int i = 0; i < cmd.Xs.Length; i++)
            {
                float el = cmd.Els != null && i < cmd.Els.Length ? cmd.Els[i] : float.MinValue;
                defNodes[i] = new Game.Areas.Node(new float3(cmd.Xs[i], cmd.Ys[i], cmd.Zs[i]), el);
            }

            _pendingDefinitions.Add(def);
            CS2M.Log.Info($"[Area] APPLIED-DEF create name={cmd.PrefabName} nodes={cmd.Xs.Length}");
            return true;
        }

        /// <summary>Finds any non-tile area of the same prefab whose polygon center is nearest the
        /// shipped center (districts included — a bulldozed district syncs through here too).</summary>
        private Entity FindAreaByCenter(string prefabName, float cx, float cz, float maxDistSq)
        {
            EntityQuery all = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                },
            });

            Entity best = Entity.Null;
            float bestD = maxDistSq;
            NativeArray<Entity> areas = all.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase p) || p == null || p.name != prefabName)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    if (nodes.Length == 0)
                    {
                        continue;
                    }

                    float ax = 0f, az = 0f;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        ax += nodes[i].m_Position.x;
                        az += nodes[i].m_Position.z;
                    }

                    ax /= nodes.Length;
                    az /= nodes.Length;
                    float dx = ax - cx;
                    float dz = az - cz;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = area;
                    }
                }
            }
            finally
            {
                areas.Dispose();
            }

            return best;
        }

        private void ApplyDelete(AreaEditCommand cmd)
        {
            Entity target = FindAreaByCenter(cmd.PrefabName, cmd.CenterX, cmd.CenterZ, 100f);
            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[Area] SKIP delete noMatch name={cmd.PrefabName} at=({cmd.CenterX:F0},{cmd.CenterZ:F0})");
                return;
            }

            if (!EntityManager.HasComponent<CS2M_RemotePlaced>(target))
            {
                EntityManager.AddComponent<CS2M_RemotePlaced>(target); // echo guard
            }

            EntityManager.AddComponent<Deleted>(target);
            CS2M.Log.Info($"[Area] APPLIED delete name={cmd.PrefabName} entity={target.Index}");
        }

        private void ApplyStandalone(AreaEditCommand cmd)
        {
            Entity target = FindAreaByCenter(cmd.PrefabName, cmd.CenterX, cmd.CenterZ, 25f);
            if (target != Entity.Null)
            {
                if (!EntityManager.HasComponent<CS2M_RemotePlaced>(target))
                {
                    EntityManager.AddComponent<CS2M_RemotePlaced>(target);
                }

                if (!EntityManager.HasComponent<Updated>(target))
                {
                    EntityManager.AddComponent<Updated>(target);
                }

                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(target);
                nodes.ResizeUninitialized(cmd.Xs.Length);
                for (int i = 0; i < cmd.Xs.Length; i++)
                {
                    float el = cmd.Els != null && i < cmd.Els.Length ? cmd.Els[i] : float.MinValue;
                    nodes[i] = new Game.Areas.Node(new float3(cmd.Xs[i], cmd.Ys[i], cmd.Zs[i]), el);
                }

                CS2M.Log.Info($"[Area] APPLIED standalone rewrite name={cmd.PrefabName} nodes={cmd.Xs.Length}");
                return;
            }

            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, default(Colossal.Hash128));
            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase areaPrefab) || areaPrefab == null
                || !_prefabSystem.TryGetEntity(areaPrefab, out Entity areaPrefabEntity))
            {
                CS2M.Log.Info($"[Area] RESOLVE-FAIL standalone name={cmd.PrefabName}");
                return;
            }

            Entity def = EntityManager.CreateEntity();
            EntityManager.AddComponentData(def, new CreationDefinition
            {
                m_Prefab = areaPrefabEntity,
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponent<Updated>(def);
            DynamicBuffer<Game.Areas.Node> defNodes = EntityManager.AddBuffer<Game.Areas.Node>(def);
            defNodes.ResizeUninitialized(cmd.Xs.Length);
            for (int i = 0; i < cmd.Xs.Length; i++)
            {
                float el = cmd.Els != null && i < cmd.Els.Length ? cmd.Els[i] : float.MinValue;
                defNodes[i] = new Game.Areas.Node(new float3(cmd.Xs[i], cmd.Ys[i], cmd.Zs[i]), el);
            }

            _pendingDefinitions.Add(def);
            CS2M.Log.Info($"[Area] APPLIED-DEF standalone create name={cmd.PrefabName} nodes={cmd.Xs.Length}");
        }

        /// <summary>MUST mirror the detector's FindAnchor byte-for-byte: prefer the actual building over
        /// any intermediate Transform+PrefabRef anchor (e.g. a farm's placeholder), falling back to a
        /// bounded SPATIAL search for the nearest Building around the fallback anchor when no Building is
        /// in the owner chain (v57 AREA-FIX — see FindAnchor's doc comment for the wrong-target-rewrite
        /// bug this fixes), and finally to the nearest Transform+PrefabRef ancestor when no Building is
        /// found at all. Both sides need to land on the SAME anchor level for the "is this area's anchor
        /// == the resolved owner" comparison in ApplyOne to ever succeed.</summary>
        private Entity FindAnchorApply(Entity owner)
        {
            Entity e = owner;
            Entity fallback = Entity.Null;
            for (int guard = 0; e != Entity.Null && guard < 5; guard++)
            {
                if (!EntityManager.Exists(e))
                {
                    break;
                }

                if (EntityManager.HasComponent<Game.Objects.Transform>(e)
                    && EntityManager.HasComponent<PrefabRef>(e))
                {
                    if (EntityManager.HasComponent<Game.Buildings.Building>(e))
                    {
                        return e;
                    }

                    if (fallback == Entity.Null)
                    {
                        fallback = e;
                    }
                }

                if (!EntityManager.HasComponent<Owner>(e))
                {
                    break;
                }

                e = EntityManager.GetComponentData<Owner>(e).m_Owner;
            }

            if (fallback == Entity.Null)
            {
                return Entity.Null;
            }

            Entity nearBuilding = FindNearestBuildingApply(fallback);
            return nearBuilding != Entity.Null ? nearBuilding : fallback;
        }

        /// <summary>Shared by FindAnchorApply — mirrors AreaEditDetectorSystem.FindNearestBuilding
        /// byte-for-byte, reusing the already-existing <see cref="_buildings"/> query.</summary>
        private Entity FindNearestBuildingApply(Entity near)
        {
            if (!EntityManager.HasComponent<Game.Objects.Transform>(near))
            {
                return Entity.Null;
            }

            float3 pos = EntityManager.GetComponentData<Game.Objects.Transform>(near).m_Position;
            Entity best = Entity.Null;
            float bestDSq = AnchorBuildingSearchRadius * AnchorBuildingSearchRadius;
            NativeArray<Entity> candidates = _buildings.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity b in candidates)
                {
                    float3 bp = EntityManager.GetComponentData<Game.Objects.Transform>(b).m_Position;
                    float dx = bp.x - pos.x;
                    float dz = bp.z - pos.z;
                    float d = dx * dx + dz * dz;
                    if (d < bestDSq)
                    {
                        bestDSq = d;
                        best = b;
                    }
                }
            }
            finally
            {
                candidates.Dispose();
            }

            return best;
        }

        private Entity ResolveOwner(AreaEditCommand cmd)
        {
            if (cmd.OwnerSyncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(cmd.OwnerSyncId, out Entity byId)
                && EntityManager.Exists(byId) && !EntityManager.HasComponent<Deleted>(byId))
            {
                return byId;
            }

            Entity best = Entity.Null;
            float bestD = 9f;
            NativeArray<Entity> ents = _buildings.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    var p = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - cmd.OwnerX;
                    float dz = p.z - cmd.OwnerZ;
                    float d = dx * dx + dz * dz;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = cand;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            if (best != Entity.Null)
            {
                return best;
            }

            // FALLBACK: non-building anchor (a farm's "Agriculture Area Placeholder"). Match a nearby object
            // of the shipped prefab name — this is what lets the farm FIELD resolve its owner and sync.
            if (string.IsNullOrEmpty(cmd.OwnerPrefabName))
            {
                return Entity.Null;
            }

            EntityQuery anchors = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.ReadOnly<Game.Net.Node>(),
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                },
            });
            float bestD2 = 25f; // 5 m — placeholders sit a little off the field footprint
            NativeArray<Entity> cands = anchors.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in cands)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(cand).m_Prefab,
                            out PrefabBase p) || p == null || p.name != cmd.OwnerPrefabName)
                    {
                        continue;
                    }

                    var tf = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = tf.x - cmd.OwnerX;
                    float dz = tf.z - cmd.OwnerZ;
                    float d = dx * dx + dz * dz;
                    if (d < bestD2)
                    {
                        bestD2 = d;
                        best = cand;
                    }
                }
            }
            finally
            {
                cands.Dispose();
            }

            return best;
        }
    }
}
