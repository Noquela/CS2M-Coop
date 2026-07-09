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
        // v61 AREA-FIX (fazenda-plantada-em-sessao-nunca-sincroniza, 2-sim 07/07): a session-born work
        // area's owner/placeholder can still be mid-spawn on the exact scan it is first sighted, so
        // FindAnchor/prefab lookup can transiently fail. The OLD code baselined WorkAreaHash BEFORE
        // knowing whether the send would succeed, so a failed send there was adopted as "known" forever
        // and the lot's initial shape never shipped (2-sim: zero [Area] traffic all session, client kept
        // its own divergent placement-template shape). Track per-area retry attempts so an unresolved
        // anchor is retried on the NEXT ~1 Hz scan instead of being silently accepted as baselined, with
        // a bounded give-up so a genuinely orphaned area (owner destroyed mid-spawn) doesn't retry forever.
        private readonly Dictionary<Entity, int> _pendingAnchorScans = new Dictionary<Entity, int>();
        private const int AnchorPendingScanLimit = 30;
        private EntityQuery _workAreas;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // v51 FIELD FIX: editing an existing work area only marks it Updated — never Applied —
            // so the Applied-based query below NEVER saw real edits ("my farm field doesn't show up
            // until /resync"). Poll owned areas at ~1 Hz and diff their polygon hash instead.
            // SCOPE (fix 04/07): only RESOURCE work areas — the field the PLAYER draws. Was matching EVERY
            // owned area, which shipped 90+ cosmetic Surface/Space sub-areas (Grass/Sand/Walking/Hangaround/
            // Park) per building — those are regenerated locally by the SUBAREAS handler when the owner
            // syncs, so re-shipping them just caused unmatchable rewrites/deletes on the client (the "farm
            // didn't sync" mess Bruno saw).
            //
            // v68 AREA-FIX (fazenda-do-CLIENT-nunca-carimba, 2-sim 09/07 00:37): scope was hard-coded to
            // Game.Areas.Extractor ONLY. But the two area types AreaSpawnSystem grows objects into are
            // Extractor OR Storage (decomp AreaSpawnSystem.cs:151-153; ExtractorArea.cs:34 adds Extractor,
            // StorageArea.cs:24 adds Storage) — a livestock/pasture or storage-yard field can be Storage-only
            // and was INVISIBLE to this query, so its polygon never shipped (docs/game-map/dossiers/area.md:
            // 250-255 flagged this exact gap; AreaSubObjectSystems.cs:122-126 already scopes its sub-object
            // scan to Any{Extractor,Storage} — this mirrors it). Surface/Space cosmetic sub-areas carry
            // NEITHER, so they stay excluded (the v51 concern above still holds).
            _workAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Extractor>(),
                    ComponentType.ReadOnly<Game.Areas.Storage>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Areas.District>(),
                    ComponentType.ReadOnly<Game.Areas.MapTile>(),
                },
            });
            // Same scope as _workAreas: resource work areas only (Extractor OR Storage — see v68 note
            // above), never cosmetic Surface/Space.
            _appliedAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Extractor>(),
                    ComponentType.ReadOnly<Game.Areas.Storage>(),
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
            //
            // GAP FIX (audit 07/07, gated CS2M_DELFIX=1): CS2M_RemotePlaced is stamped once at creation
            // (DistrictApplySystem.cs, AreaEditApplySystem's rewrite/standalone paths) and NEVER removed,
            // so excluding it here meant a district/area/field the LOCAL player bulldozed, but that had
            // been CREATED by a remote command, could never match this query — the delete never shipped
            // and the other PC's copy lived forever. DeleteDetectorSystem hit the identical bug for
            // objects/buildings in v56 and fixed it the same way: key the delete-echo guard off
            // CS2M_RemoteDeleted (stamped ONLY in the same frame ApplyDelete below applies a REMOTE
            // delete, see CascadeDeleteUtil.cs) instead of CS2M_RemotePlaced. AreaEditApplySystem runs at
            // Modification1 and this detector at ModificationEnd (Mod.cs), so the tag is always visible
            // here before Game.Common.CleanUpSystem destroys the entity at Cleanup, same frame.
            _deletedAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = DelFix.Enabled
                    ? new[]
                    {
                        ComponentType.ReadOnly<Temp>(),
                        ComponentType.ReadOnly<Game.Areas.MapTile>(),
                        ComponentType.ReadOnly<CS2M_RemoteDeleted>(),
                    }
                    : new[]
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

            // v68 AREA-FIX (ROOT CAUSE of "fazenda-do-CLIENT-nunca-carimba", 2-sim 09/07 00:37): the
            // ~1 Hz polygon-diff scanner (ScanWorkAreaEdits, driven by _scanCounter ticking every OnUpdate)
            // is the ONLY path that ships a SESSION-BORN field's initial shape — a field a CLIENT placed is
            // created ON THE HOST via a CreationDefinition (RemotePlacementApplySystem.CreateSubAreas), so
            // it gains Created (never Applied) and carries CS2M_RemotePlaced → it is NEVER in _appliedAreas
            // and can only heal through this scanner. BUT the scanner's own query (_workAreas) was NOT in
            // RequireAnyForUpdate — only the three TRANSIENT queries were (Applied/standalone/Deleted are
            // 1-frame tags, empty in steady state). So once world-load churn subsided, OnUpdate was gated
            // OFF entirely: _scanCounter froze, the scanner never ran, and the field never shipped — exactly
            // the observed symptom (ZERO [Area] DETECT+SEND and ZERO give-up warnings over ~3 min, because
            // the whole OnUpdate body, warnings included, never executed). _workAreas is persistently
            // non-empty whenever any farm/extractor/storage field exists, so listing it here keeps OnUpdate
            // alive every frame; the ~1 Hz throttle still lives in _scanCounter, and idle cost is tiny
            // (the create loop iterates the usually-empty _appliedAreas; standalone/deleted early-out on
            // IsEmptyIgnoreFilter).
            RequireAnyForUpdate(_appliedAreas, _appliedStandalone, _deletedAreas, _workAreas);
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

            bool isServer = NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER;

            NativeArray<Entity> areas = _appliedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (!_recentlySent.Add(area))
                    {
                        continue;
                    }

                    // AUTHORITY (derive-once, same rule as ScanWorkAreaEdits below): _appliedAreas is scoped
                    // to Extractor-tagged fields only (farm/forestry/ore/oil/fish work areas — see the
                    // query's doc comment), i.e. lots each PC's AreaSpawnSystem derives with its OWN local
                    // RNG. Before AreaSpawnSuppressSystem defaulted to ON, both host and client independently
                    // spawned-then-shipped their own just-created shape here and raced each other. With the
                    // client's AreaSpawnSystem now suppressed by default, the host is the only machine still
                    // deriving these fields, so it must be the only one sending the freshly-created shape.
                    if (!isServer)
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
                    TryResolveAnchor(area, isServer, out _, out ulong anchorId, out string anchorPrefabName,
                        out ulong anchorBuildingSyncId, out _, out int subAreaIndex);
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
                        OwnerAnchorId = anchorId,
                        OwnerAnchorPrefabName = anchorPrefabName,
                        BuildingSyncId = anchorBuildingSyncId,
                        SubAreaIndex = subAreaIndex,
                    });
                    CS2M.Log.Info($"[Area] DETECT+SEND name={prefab.name} owner={ownerPrefab.name} nodes={nodes.Length} anchorId={anchorId}");
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
                ScanWorkAreaEdits(isServer);
            }
        }

        /// <summary>v51: ~1 Hz polygon-hash diff over owned areas — the only reliable signal for a
        /// player RESHAPING a work area (vanilla marks the entity Updated, never Applied). First
        /// sight is normally a silent baseline; a first-sight field seen AFTER warmup (a lot BORN
        /// this session — see the v61 AREA-FIX block below) instead gets its initial shape SHIPPED,
        /// since it never goes through the Applied-tag create loop above. The apply system updates
        /// the shared hash so a remotely applied rewrite is never bounced back.</summary>
        private void ScanWorkAreaEdits(bool isServer)
        {
            _scanPasses++;
            NativeArray<Entity> areas = _workAreas.ToEntityArray(Allocator.Temp);
            // DIAGNOSTIC (v68, discrete — ~1/min): the scanner ran but the Extractor/Storage query holds
            // no field. If a farm is visible in a BldgDump while this keeps firing, the field's area is
            // neither Extractor nor Storage (a scope gap beyond v68) — the one signal that would prove it.
            if (isServer && areas.Length == 0 && _scanPasses % 60 == 0)
            {
                CS2M.Log.Info("[Area] scan: zero work areas in query (no Extractor/Storage field present)");
            }

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

                    // Known area, no polygon change → nothing to ship.
                    if (!firstSight && known == hash)
                    {
                        continue;
                    }

                    // Anchor = the nearest ancestor up the owner chain that has a Transform (a building, or
                    // a farm's "Agriculture Area Placeholder"). Using the OWNER anchor (not the polygon
                    // centre) is what makes a RESIZE sync: the field's centroid MOVES when you redraw it, but
                    // its owner does not. Old code skipped any non-Building owner outright, so the farm work-
                    // area never synced. Walk up to 4 links to reach a Transform. Computed first because the
                    // authority rule below keys off CS2M_RemotePlaced on it.
                    Entity anchor = FindAnchor(EntityManager.GetComponentData<Owner>(area).m_Owner);

                    // AUTHORITY (v70 BIDIRECTIONAL, replaces the old host-only model): the ORIGINATOR of a
                    // field — the machine where the PLAYER actually drew the polygon — ships its shape; the
                    // other machine ADOPTS. The discriminator is CS2M_RemotePlaced on the anchor: it is
                    // stamped (runtime-only, never serialized) by RemotePlacementApplySystem on a building
                    // this machine MATERIALISED from a remote placement. A materialised building's field is a
                    // locally-GENERATED DEFAULT (the host's small nodes=4 lot for a farm a CLIENT drew, or
                    // vice-versa) — NOT the player's real shape — so the remote-placed side must NEVER ship
                    // it. This is exactly the reported bug: the host used to force-ship its generated default
                    // and CLOBBER the client's custom polygon. Now the remote-placed side stays silent and
                    // the non-remote-placed originator ships its real polygon; the other side adopts it.
                    //
                    // ANTI-OSCILLATION: only ONE side of any field is remote-placed (the materialiser), so
                    // exactly ONE side ships a session-born first shape — the two can never ping-pong (the old
                    // 585-vs-593 drift came from BOTH sides shipping their own locally-spawned shape). A KNOWN
                    // reshape (below) may ship from either side, but the receiver's apply stamps WorkAreaHash
                    // with the applied shape, so an adopted rewrite is seen as known==hash next scan and never
                    // echoes back. Trace: A draws S → A ships S, baselines hash(S). B adopts S, apply sets
                    // WorkAreaHash(B)=hash(S). Next scan B sees known==hash → no send. Stops. (See ApplyOne.)
                    bool isSessionBornFirstSight = false;
                    if (firstSight)
                    {
                        bool anchorIsRemotePlaced = anchor != Entity.Null
                            && EntityManager.HasComponent<CS2M_RemotePlaced>(anchor);

                        // Materialised-from-remote-placement default → adopt our current shape as the
                        // baseline and stay silent; the originating machine ships the player's real polygon.
                        if (anchorIsRemotePlaced)
                        {
                            WorkAreaHash.Set(area, hash);
                            _pendingAnchorScans.Remove(area);
                            continue;
                        }

                        // NOT remote-placed. During warmup this is a save-loaded field — identical on both
                        // PCs (the CS2M_RemotePlaced tag is runtime-only, so a save-loaded field can never
                        // carry it) → silent baseline, no need to ship. PAST warmup it is a lot the LOCAL
                        // player placed THIS session (originated HERE — host OR client) whose field spawned
                        // with a shape the other PC cannot match → ship the initial polygon. The send below
                        // is shared with the known-edit path.
                        if (_scanPasses <= WarmupScans)
                        {
                            WorkAreaHash.Set(area, hash);
                            _pendingAnchorScans.Remove(area);
                            continue;
                        }

                        isSessionBornFirstSight = true;
                    }
                    else
                    {
                        // Known area with a real polygon change: a RESHAPE by the local player, on EITHER
                        // machine now (bidirectional). Baseline immediately so the same shape never ships
                        // twice; the remote apply stamps WorkAreaHash with the applied shape, so an adopted
                        // rewrite never echoes back (anti-oscillation, see the AUTHORITY note above).
                        WorkAreaHash.Set(area, hash);
                    }

                    if (anchor == Entity.Null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab,
                            out PrefabBase prefab) || prefab == null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(anchor).m_Prefab,
                            out PrefabBase ownerPrefab) || ownerPrefab == null)
                    {
                        if (isSessionBornFirstSight)
                        {
                            // v61 AREA-FIX: the anchor/prefab isn't resolvable YET (owner/placeholder still
                            // mid-spawn) — do NOT baseline, or this lot's initial shape never ships (the bug
                            // this fixes). Leave it OUT of WorkAreaHash so the next ~1 Hz scan sees firstSight
                            // again and retries, up to a bounded number of attempts.
                            _pendingAnchorScans.TryGetValue(area, out int attempts);
                            attempts++;
                            if (attempts >= AnchorPendingScanLimit)
                            {
                                WorkAreaHash.Set(area, hash);
                                _pendingAnchorScans.Remove(area);
                                CS2M.Log.Warn($"[Area] WARN giving up first-sight anchor resolution after {attempts} scans, baselining without send entity={area.Index}");
                            }
                            else
                            {
                                _pendingAnchorScans[area] = attempts;
                                // DIAGNOSTIC (v68, discrete): progress every 10 attempts so a stuck first-
                                // sight anchor (H1: owner/placeholder never resolving) is visible BEFORE the
                                // give-up above, instead of ~30 s of silence.
                                if (attempts % 10 == 0)
                                {
                                    Entity rawOwner = EntityManager.HasComponent<Owner>(area)
                                        ? EntityManager.GetComponentData<Owner>(area).m_Owner
                                        : Entity.Null;
                                    CS2M.Log.Info($"[Area] first-sight anchor still unresolved attempt={attempts}/{AnchorPendingScanLimit} entity={area.Index} rawOwner={rawOwner.Index} anchorResolved={anchor != Entity.Null}");
                                }
                            }
                        }

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
                    TryResolveAnchor(area, isServer, out _, out ulong anchorId, out string anchorPrefabName,
                        out ulong anchorBuildingSyncId, out _, out int subAreaIndex);
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
                        OwnerAnchorId = anchorId,
                        OwnerAnchorPrefabName = anchorPrefabName,
                        BuildingSyncId = anchorBuildingSyncId,
                        SubAreaIndex = subAreaIndex,
                    });

                    if (isSessionBornFirstSight)
                    {
                        // v61 AREA-FIX: only baseline NOW, after the send actually happened — see the doc
                        // comment above for why baselining any earlier reproduced the "never ships" bug.
                        WorkAreaHash.Set(area, hash);
                        _pendingAnchorScans.Remove(area);
                        CS2M.Log.Info($"[Area] DETECT+SEND first-sight (session-born lot) name={prefab.name} owner={ownerPrefab.name} nodes={nodes.Length} anchorId={anchorId}");
                    }
                    else
                    {
                        CS2M.Log.Info($"[Area] DETECT+SEND edit name={prefab.name} owner={ownerPrefab.name} nodes={nodes.Length} anchorId={anchorId} (polygon diff)");
                    }
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

        /// <summary>
        ///     v59 IDENTITY FIX: resolves the work-area's DIRECT owner (<c>Owner.m_Owner</c> — exact,
        ///     one hop; for a farm field that is its "Agriculture Area Placeholder", never the
        ///     building — see FindAnchor's doc comment for why walking further is unreliable) and
        ///     makes sure it carries a stable <see cref="CS2M_SyncId"/>, minting one via
        ///     <see cref="CS2M_SyncIdSystem.Allocate"/> the first time it is seen. Minted on the
        ///     ORIGINATOR — host OR client under the v70 bidirectional authority (see ScanWorkAreaEdits);
        ///     only the originator ships a field's first shape so exactly one machine mints its id (the
        ///     <paramref name="isServer"/> flag is retained for the callers' signature but no longer gates
        ///     minting). Also best-effort resolves a BUILDING for the anchor (reusing FindAnchor's own
        ///     building-search) purely as
        ///     a position HINT the receiver can use for its own one-time local search; never required
        ///     for correctness (the receiver's search is unbounded and type-filtered instead — see
        ///     AreaEditApplySystem.ResolveOwnerByAnchor).
        /// </summary>
        private bool TryResolveAnchor(Entity area, bool isServer, out Entity directOwner, out ulong anchorId,
            out string anchorPrefabName, out ulong buildingSyncId, out float3 buildingPos, out int subAreaIndex)
        {
            directOwner = Entity.Null;
            anchorId = 0;
            anchorPrefabName = null;
            buildingSyncId = 0;
            buildingPos = default;
            subAreaIndex = 0;

            if (!EntityManager.HasComponent<Owner>(area))
            {
                return false;
            }

            directOwner = EntityManager.GetComponentData<Owner>(area).m_Owner;
            if (directOwner == Entity.Null || !EntityManager.Exists(directOwner)
                || !EntityManager.HasComponent<Game.Objects.Transform>(directOwner)
                || !EntityManager.HasComponent<PrefabRef>(directOwner))
            {
                directOwner = Entity.Null;
                return false;
            }

            if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(directOwner).m_Prefab,
                    out PrefabBase ownerPrefab) || ownerPrefab == null)
            {
                return false;
            }

            anchorPrefabName = ownerPrefab.name;

            if (EntityManager.HasComponent<CS2M_SyncId>(directOwner))
            {
                anchorId = EntityManager.GetComponentData<CS2M_SyncId>(directOwner).m_Id;
            }
            else
            {
                // First sighting of this owner anywhere — mint its identity. Minted on the ORIGINATOR,
                // which under the v70 bidirectional authority (see ScanWorkAreaEdits) is whichever machine
                // the player drew the field on — host OR client. CS2M_SyncIdSystem is sender-allocates-by-
                // design (nonce-namespaced, so host/client ids never collide); only the originator ships a
                // field's FIRST shape, so exactly one machine ever mints this placeholder's id and the
                // receiver adopts it via ResolveOwnerByAnchor. The client used to bail here (host-only
                // model), which is why a client-drawn farm's shape could never ship.
                anchorId = CS2M_SyncIdSystem.Allocate();
                CS2M_SyncIdSystem.Register(EntityManager, directOwner, anchorId);
            }

            Entity building = FindAnchor(directOwner);
            if (building != Entity.Null && building != directOwner
                && EntityManager.HasComponent<Game.Buildings.Building>(building)
                && EntityManager.HasComponent<CS2M_SyncId>(building))
            {
                buildingSyncId = EntityManager.GetComponentData<CS2M_SyncId>(building).m_Id;
                buildingPos = EntityManager.GetComponentData<Game.Objects.Transform>(building).m_Position;
            }

            // Discriminator: ordinal among Extractor-tagged entries of directOwner's own SubArea
            // buffer (Game.Areas.SubArea — engine-maintained reverse index, Game/Serialization/
            // SubAreaSystem.cs:27-40) — lets the receiver pick the right field when an owner has more
            // than one.
            if (EntityManager.HasBuffer<Game.Areas.SubArea>(directOwner))
            {
                DynamicBuffer<Game.Areas.SubArea> subAreas =
                    EntityManager.GetBuffer<Game.Areas.SubArea>(directOwner, true);
                int ordinal = 0;
                for (int i = 0; i < subAreas.Length; i++)
                {
                    Entity candidate = subAreas[i].m_Area;
                    if (!EntityManager.HasComponent<Game.Areas.Extractor>(candidate))
                    {
                        continue;
                    }

                    if (candidate == area)
                    {
                        subAreaIndex = ordinal;
                        break;
                    }

                    ordinal++;
                }
            }

            return true;
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
                        // synced when it is a resource work area (Extractor OR Storage — v68, see the
                        // _workAreas note in OnCreate). Cosmetic sub-areas (Hangaround/Walking/Grass…) are
                        // regenerated LOCALLY per machine — the sim swaps them while the owner lives, and
                        // shipping that delete removed the OTHER PC's healthy local copy (the areas(hash)
                        // drift Bruno hit on 05/07).
                        if (!EntityManager.HasComponent<Game.Areas.Extractor>(area)
                            && !EntityManager.HasComponent<Game.Areas.Storage>(area))
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

            // v59 IDENTITY FIX: try the stable anchor id first (exact — see ResolveOwnerByAnchor's doc
            // comment); only a NEVER-before-seen anchor falls back to the legacy proximity/building-
            // syncid resolution (kept unchanged for save-era fields the sender could not identify).
            Entity owner = ResolveOwnerByAnchor(cmd, out bool ownerIsDirect);
            if (owner == Entity.Null)
            {
                owner = ResolveOwner(cmd);
                ownerIsDirect = false;
            }

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
            // Fast path: owner is the field's DIRECT parent (Owner.m_Owner) — read its own
            // Game.Areas.SubArea buffer (the engine-maintained reverse index, Game/Serialization/
            // SubAreaSystem.cs:27-40) instead of scanning+climbing every owned area in the world.
            Entity target = ownerIsDirect ? FindExtractorSubArea(owner, cmd.SubAreaIndex) : Entity.Null;

            if (target == Entity.Null)
            {
                // Legacy scan (unchanged): covers owner resolved via the old climb-to-building path,
                // and the rare case a freshly-registered anchor's SubArea buffer hasn't been
                // populated by SubAreaSystem yet.
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
            }

            if (target != Entity.Null)
            {
                if (!EntityManager.HasComponent<CS2M_RemotePlaced>(target))
                {
                    EntityManager.AddComponent<CS2M_RemotePlaced>(target); // echo guard
                }

                // v65.1 FIELD FIX (the "rewrite applies but nothing changes" farm bug): a placement-born
                // field is a SLAVE sub-area, and GeometrySystem regenerates a Slave's polygon FROM THE
                // OWNER'S TEMPLATE whenever it is Updated (decomp Areas/GeometrySystem.cs:95-98 →
                // GenerateSlaveArea :160-166 does nodes.Clear() + rebuild) — so our node write below was
                // wiped by the very Updated stamp that publishes it. The host's lot (AreaSpawnSystem-born)
                // is NOT Slave. Promote the client's copy to a free-standing area so the authoritative
                // polygon actually sticks.
                Game.Areas.Area areaData = EntityManager.GetComponentData<Game.Areas.Area>(target);
                if ((areaData.m_Flags & Game.Areas.AreaFlags.Slave) != 0)
                {
                    areaData.m_Flags &= ~Game.Areas.AreaFlags.Slave;
                    EntityManager.SetComponentData(target, areaData);
                    CS2M.Log.Info($"[Area] UNSLAVE entity={target.Index} (host-authoritative polygon must not be template-regenerated)");
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

            // GAP FIX (CS2M_DELFIX=1): stamp CS2M_RemoteDeleted, NOT CS2M_RemotePlaced — this target is
            // dying, and CS2M_RemotePlaced is the CREATION tag (paired with _deletedAreas' None list
            // above). Legacy (gate off) keeps stamping CS2M_RemotePlaced, matching the query unchanged.
            if (DelFix.Enabled)
            {
                if (!EntityManager.HasComponent<CS2M_RemoteDeleted>(target))
                {
                    EntityManager.AddComponent<CS2M_RemoteDeleted>(target); // echo guard (delete)
                }
            }
            else if (!EntityManager.HasComponent<CS2M_RemotePlaced>(target))
            {
                EntityManager.AddComponent<CS2M_RemotePlaced>(target); // legacy echo guard
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

        /// <summary>
        ///     v59 IDENTITY FIX: resolves the field's DIRECT owner by exact id first — works for every
        ///     message after the first one shipped for a given field (see AreaEditDetectorSystem.
        ///     TryResolveAnchor). Only when this exact id has never been seen locally does it fall back
        ///     to a ONE-TIME search: filtered by prefab name AND "owns at least one Extractor-tagged
        ///     area" (a TYPE filter the old ~3–5 m proximity guess in <see cref="ResolveOwner"/> never
        ///     had), preferring the candidate nearest the anchoring BUILDING's own LIVE position
        ///     (resolved via <see cref="AreaEditCommand.BuildingSyncId"/>, never a shipped snapshot —
        ///     the shipped OwnerX/Y/Z is the owner's OWN position, which drifts with the field's
        ///     centroid once resized). The match, once found, is registered under OwnerAnchorId
        ///     (<see cref="CS2M_SyncIdSystem.Register"/>) so every LATER edit of this SAME field
        ///     resolves by id alone — no more repeated guessing, ever, for this field.
        /// </summary>
        private Entity ResolveOwnerByAnchor(AreaEditCommand cmd, out bool resolvedIsDirectOwner)
        {
            resolvedIsDirectOwner = false;
            if (cmd.OwnerAnchorId == 0)
            {
                return Entity.Null;
            }

            if (CS2M_SyncIdSystem.Map.TryGetValue(cmd.OwnerAnchorId, out Entity known)
                && EntityManager.Exists(known) && !EntityManager.HasComponent<Deleted>(known))
            {
                resolvedIsDirectOwner = true;
                return known;
            }

            if (string.IsNullOrEmpty(cmd.OwnerAnchorPrefabName))
            {
                return Entity.Null;
            }

            Entity building = Entity.Null;
            bool haveBuildingHint = cmd.BuildingSyncId != 0
                && CS2M_SyncIdSystem.Map.TryGetValue(cmd.BuildingSyncId, out building)
                && EntityManager.Exists(building) && !EntityManager.HasComponent<Deleted>(building)
                && EntityManager.HasComponent<Game.Objects.Transform>(building);
            float3 hintPos = haveBuildingHint
                ? EntityManager.GetComponentData<Game.Objects.Transform>(building).m_Position
                : new float3(cmd.OwnerX, cmd.OwnerY, cmd.OwnerZ);

            EntityQuery anchorCandidates = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            // BUG FIX (2-sim 07/07, "fazenda casou a 90 m"): this used to be a genuinely UNBOUNDED
            // nearest-match search — same prefab name + "owns an Extractor field" was treated as
            // proof enough, on the theory that a field can sit arbitrarily far from its OWNER once
            // resized (see the old comment this replaces). That reasoning ignored saves with MORE
            // THAN ONE farm of the same work type (identical placeholder prefab, e.g. multiple
            // "Agriculture Area Placeholder - Livestock" lots in Saegertown): with no distance cap,
            // "nearest of all candidates on the WHOLE MAP" is not the same claim as "the same farm" —
            // it silently accepts whichever OTHER farm's placeholder happens to be closest to hintPos,
            // even 90 m away, and rewrites that farm's field instead (host center (87,-9) landed on a
            // field the client reported at (73,79)). A wrong rewrite is worse than none: the radar
            // still flags a mismatch afterwards and the host just resends, whereas a bad apply silently
            // corrupts an unrelated farm. Bound the tiebreak to AnchorBuildingSearchRadius — the same
            // radius FindAnchor/FindAnchorApply already rely on for "a placeholder sits within its own
            // building's footprint" — and refuse to guess past it.
            Entity best = Entity.Null;
            float bestDSq = AnchorBuildingSearchRadius * AnchorBuildingSearchRadius;
            Entity nearestAny = Entity.Null;
            float nearestAnyDSq = float.MaxValue;
            NativeArray<Entity> ents = anchorCandidates.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(cand).m_Prefab,
                            out PrefabBase p) || p == null || p.name != cmd.OwnerAnchorPrefabName)
                    {
                        continue;
                    }

                    // Type filter: must actually own an Extractor-tagged area — narrows the field-name
                    // match down to plausible owners before the (now bounded) distance check below.
                    if (!OwnsExtractorArea(cand))
                    {
                        continue;
                    }

                    float3 candPos = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = candPos.x - hintPos.x;
                    float dz = candPos.z - hintPos.z;
                    float d = dx * dx + dz * dz;
                    if (d < nearestAnyDSq)
                    {
                        nearestAnyDSq = d;
                        nearestAny = cand;
                    }

                    if (d < bestDSq)
                    {
                        bestDSq = d;
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
                CS2M_SyncIdSystem.Register(EntityManager, best, cmd.OwnerAnchorId);
                resolvedIsDirectOwner = true;
                CS2M.Log.Info($"[Area] ANCHOR-RESOLVE id={cmd.OwnerAnchorId} name={cmd.OwnerAnchorPrefabName} "
                    + $"entity={best.Index} (one-time, now cached)");
            }
            else if (nearestAny != Entity.Null)
            {
                // A same-prefab, same-type candidate exists but sits outside the plausible radius —
                // almost certainly a DIFFERENT farm. Skip rather than rewrite the wrong one; the caller
                // falls back to the tighter legacy ResolveOwner (3-5 m), and if that also fails the
                // command is parked/retried, then dropped — never mis-applied.
                CS2M.Log.Info($"[Area] SKIP rewrite anchor-too-far name={cmd.OwnerAnchorPrefabName} "
                    + $"dist={math.sqrt(nearestAnyDSq):F0} limit={AnchorBuildingSearchRadius:F0}");
            }

            return best;
        }

        /// <summary>True if <paramref name="candidate"/> owns at least one Extractor-tagged Area,
        /// preferring its own (engine-maintained) SubArea buffer and falling back to a direct scan of
        /// <see cref="_ownedAreas"/> if that buffer was never allocated for this entity's archetype.</summary>
        private bool OwnsExtractorArea(Entity candidate)
        {
            if (EntityManager.HasBuffer<Game.Areas.SubArea>(candidate))
            {
                DynamicBuffer<Game.Areas.SubArea> subAreas =
                    EntityManager.GetBuffer<Game.Areas.SubArea>(candidate, true);
                for (int i = 0; i < subAreas.Length; i++)
                {
                    if (EntityManager.HasComponent<Game.Areas.Extractor>(subAreas[i].m_Area))
                    {
                        return true;
                    }
                }

                return false;
            }

            NativeArray<Entity> areas = _ownedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity area in areas)
                {
                    if (EntityManager.HasComponent<Game.Areas.Extractor>(area)
                        && EntityManager.HasComponent<Owner>(area)
                        && EntityManager.GetComponentData<Owner>(area).m_Owner == candidate)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                areas.Dispose();
            }

            return false;
        }

        /// <summary>Reads <paramref name="owner"/>'s own Game.Areas.SubArea buffer (the engine-
        /// maintained reverse index of areas it owns) and returns the <paramref name="subAreaIndex"/>-th
        /// Extractor-tagged entry — the discriminator for when one owner has more than one field.
        /// Entity.Null if the buffer doesn't exist yet (owner freshly registered, SubAreaSystem hasn't
        /// caught up) or has no such entry — callers fall back to the legacy scan in that case.</summary>
        private Entity FindExtractorSubArea(Entity owner, int subAreaIndex)
        {
            if (!EntityManager.HasBuffer<Game.Areas.SubArea>(owner))
            {
                return Entity.Null;
            }

            DynamicBuffer<Game.Areas.SubArea> subAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(owner, true);
            int ordinal = 0;
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity candidate = subAreas[i].m_Area;
                if (!EntityManager.HasComponent<Game.Areas.Extractor>(candidate))
                {
                    continue;
                }

                if (ordinal == subAreaIndex)
                {
                    return candidate;
                }

                ordinal++;
            }

            return Entity.Null;
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
