using System.Collections.Generic;
using Colossal.Mathematics;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.City;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Materializes objects placed by remote players.
    ///
    ///     v10+ approach ("Option B" — direct archetype instantiation): we create the real entity from
    ///     the prefab's baked <c>ObjectData.m_Archetype</c> and set the same components the vanilla
    ///     <c>GenerateObjectsSystem.CreateObject</c> sets (Transform, PrefabRef, PseudoRandomSeed,
    ///     Elevation). The archetype already contains <c>Created</c> + <c>Updated</c>.
    ///
    ///     v38: this system now runs BEFORE Modification1 (it used to run at Modification5, after the
    ///     consumers — sub-objects never spawned because SubObjectSystem@Mod2B never saw Created).
    ///     Building SUB-NETS (e.g. a transformer's invisible road path) are not created by any system
    ///     reacting to Created; the vanilla path is definition-based, so we replicate
    ///     Game.Simulation.BuildingConstructionSystem.CreateNets: one CreationDefinition(Permanent,
    ///     m_Owner=building) + NetCourse per prefab SubNet entry. Each PC generates its own sub-nets
    ///     deterministically from the prefab — they are never synced over the wire.
    ///
    ///     v38 economy: on the HOST, remote placements are charged their construction cost. The
    ///     builder's own local charge gets overwritten by the ~1 Hz host money sync, so without this
    ///     the client would effectively build for free.
    /// </summary>
    public partial class RemotePlacementApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private CitySystem _citySystem;
        private CityConfigurationSystem _cityConfigSystem;
        private readonly List<Entity> _pendingDefinitions = new List<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            _cityConfigSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            CS2M.Log.Info("[Place] RemotePlacementApplySystem created (direct-archetype mode)");
        }

        protected override void OnUpdate()
        {
            // Sub-net definitions injected last frame were consumed by GenerateNodes/Edges — clean up.
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

            while (RemotePlacementQueue.TryDequeueObject(out ObjectPlaceCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] apply failed in RemotePlacementApplySystem: {ex.Message}"); }
            }
        }

        private void ApplyOne(ObjectPlaceCommand cmd)
        {
            // Idempotency guard: if this SyncId already maps to a live object (duplicate/reordered
            // packet, or a resync re-send), don't place it twice. Safe — SyncIds are globally unique.
            if (cmd.SyncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(cmd.SyncId, out Entity existing)
                && EntityManager.Exists(existing) && !EntityManager.HasComponent<Deleted>(existing))
            {
                CS2M.Log.Info($"[Place] SKIP duplicate syncId={cmd.SyncId} (already placed)");
                return;
            }

            // 1. Resolve the prefab (machine-independent id → local prefab entity).
            var hash = new Colossal.Hash128(new uint4(cmd.Hash0, cmd.Hash1, cmd.Hash2, cmd.Hash3));
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, hash);

            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info(
                    $"[Place] RESOLVE-FAIL prefab not found type={cmd.PrefabType} name={cmd.PrefabName} " +
                    "(different mods/assets between the two PCs?)");
                return;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out Entity prefabEntity))
            {
                CS2M.Log.Info($"[Place] RESOLVE-FAIL no prefab entity for name={cmd.PrefabName}");
                return;
            }

            // 2. Grab the prefab's baked archetype (already includes Created + Updated
            //    and every component this object type needs).
            if (!EntityManager.HasComponent<ObjectData>(prefabEntity))
            {
                CS2M.Log.Info($"[Place] APPLY-FAIL prefab {cmd.PrefabName} has no ObjectData/archetype (not a placeable object?)");
                return;
            }

            ObjectData objectData = EntityManager.GetComponentData<ObjectData>(prefabEntity);
            if (!objectData.m_Archetype.Valid)
            {
                CS2M.Log.Info($"[Place] APPLY-FAIL prefab {cmd.PrefabName} archetype is invalid");
                return;
            }

            var position = new float3(cmd.PosX, cmd.PosY, cmd.PosZ);
            var rotation = new quaternion(cmd.RotX, cmd.RotY, cmd.RotZ, cmd.RotW);

            // v44: a sim spawn (host-authoritative growable) REPLACES whatever stood on that lot —
            // this is how level-ups converge when the old building predates the session (no sync id).
            if (cmd.Source == 1)
            {
                ClearLotFor(position);
            }

            // 3. Create the real object from the archetype and set its transform/identity.
            //    (CreateEntity()+SetArchetype avoids the CreateEntity(ReadOnlySpan) overload
            //    which won't compile on net472 — no ReadOnlySpan there.)
            Entity obj = EntityManager.CreateEntity();
            EntityManager.SetArchetype(obj, objectData.m_Archetype);
            SetOrAdd(obj, new Game.Objects.Transform(position, rotation));
            SetOrAdd(obj, new PrefabRef(prefabEntity));

            // Deterministic visual seed (color/mesh variation) so both PCs match.
            if (EntityManager.HasComponent<PseudoRandomSeed>(obj))
            {
                EntityManager.SetComponentData(obj, new PseudoRandomSeed((ushort) cmd.RandomSeed));
            }
            else
            {
                EntityManager.AddComponentData(obj, new PseudoRandomSeed((ushort) cmd.RandomSeed));
            }

            // Vertical offset for elevated placements (0 for ground objects).
            if (cmd.Elevation != 0f || cmd.ElevationFlags != 0)
            {
                SetOrAdd(obj, new Elevation(cmd.Elevation, (ElevationFlags) cmd.ElevationFlags));
            }

            // v50: road-side attachment (bus-stop shelters, taxi stands, mailboxes…). The baked
            // archetype does NOT carry Attached (the tool adds it at placement time) — so decide by
            // the prefab's placement flags and ADD it ourselves. AttachSystem skips RoadSide prefabs
            // in its find-parent job (the tool resolves those), so we resolve the edge from the
            // shipped point-on-curve hint; AttachSystem's reference job then registers the
            // SubObject on the edge because the entity is Updated.
            if (string.IsNullOrEmpty(cmd.OwnerPrefabName) && cmd.OwnerSyncId == 0
                && NeedsEdgeAttach(prefabEntity))
            {
                var hint = new float3(cmd.OwnerX, cmd.OwnerY, cmd.OwnerZ);
                if (hint.x == 0f && hint.z == 0f)
                {
                    hint = position; // old-version sender: fall back to the stop's own position
                }

                if (FindNearestEdge(hint, out Entity edge, out float curvePos, out float dist))
                {
                    SetOrAdd(obj, new Attached(edge, Entity.Null, curvePos));
                    CS2M.Log.Info($"[Place] ATTACHED name={cmd.PrefabName} edge={edge.Index} t={curvePos:F3} d={dist:F1}");
                }
                else
                {
                    CS2M.Log.Info($"[Place] WARN no edge to attach name={cmd.PrefabName} (stop stays loose)");
                }
            }

            // v44: service-building extension — attach to the resolved owner; the game's
            // ServiceUpgradeSystem (reacting to Created+Owner, and we run before Mod1) then wires
            // InstalledUpgrade and the upgrade's effects on the parent itself.
            if (!string.IsNullOrEmpty(cmd.OwnerPrefabName) || cmd.OwnerSyncId != 0)
            {
                Entity owner = ResolveOwner(cmd);
                if (owner == Entity.Null)
                {
                    CS2M.Log.Info($"[Place] SKIP extension noOwner name={cmd.PrefabName} owner={cmd.OwnerPrefabName}");
                    EntityManager.AddComponent<Deleted>(obj);
                    return;
                }

                SetOrAdd(obj, new Owner(owner));
                if (EntityManager.HasComponent<Attached>(obj))
                {
                    EntityManager.SetComponentData(obj, new Attached(owner, Entity.Null, 0f));
                }

                if (!EntityManager.HasComponent<Updated>(owner))
                {
                    EntityManager.AddComponent<Updated>(owner);
                }
            }

            // Echo guard: mark this as remotely-created so our detector skips it.
            EntityManager.AddComponent<CS2M_RemotePlaced>(obj);

            // The archetype should already carry these, but guarantee the post-processing
            // systems fire (spatial index, rendering, sub-object/lot generation, zoning).
            if (!EntityManager.HasComponent<Created>(obj))
            {
                EntityManager.AddComponent<Created>(obj);
            }

            if (!EntityManager.HasComponent<Updated>(obj))
            {
                EntityManager.AddComponent<Updated>(obj);
            }

            // Same cross-PC id as the sender's entity, so later move/delete resolves here too.
            CS2M_SyncIdSystem.Register(EntityManager, obj, cmd.SyncId);

            CS2M.Log.Info(
                $"[Place] APPLIED name={cmd.PrefabName} entity={obj.Index} syncId={cmd.SyncId} prefabEntity={prefabEntity.Index} " +
                $"pos=({position.x:F1},{position.y:F1},{position.z:F1}) seed={cmd.RandomSeed} " +
                $"hasTransform={EntityManager.HasComponent<Game.Objects.Transform>(obj)} " +
                $"hasBuilding={EntityManager.HasComponent<Game.Buildings.Building>(obj)}");

            // 4. Building sub-nets (invisible road paths, power connections…) — definition-injected,
            //    consumed by GenerateNodes/Edges this same frame (we run before Modification1).
            CreateSubNets(prefabEntity, obj, new Game.Objects.Transform(position, rotation), cmd.RandomSeed, cmd.PrefabName);

            // 4b. v45: building sub-AREAS (a farm's field, a mine's dig area…) — same definition
            //     pattern, replicating BuildingConstructionSystem.CreateAreas. Without this a remote
            //     farm arrived with no working area at all.
            CreateSubAreas(prefabEntity, obj, new Game.Objects.Transform(position, rotation), cmd.RandomSeed, cmd.PrefabName);

            // 5. Host-authoritative economy: debit the construction cost for remote builds.
            if (NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.SERVER)
            {
                ChargeConstructionCost(prefabEntity, cmd.PrefabName);
            }
        }

        /// <summary>
        ///     Replicates <c>BuildingConstructionSystem.CreateAreas</c>: one Permanent definition per
        ///     prefab SubArea entry, with the polygon transformed to world space. Placeholder entries
        ///     (random area selection) are skipped — not used by extractor/service defaults.
        /// </summary>
        private void CreateSubAreas(Entity prefabEntity, Entity owner, Game.Objects.Transform transform,
            int randomSeed, string prefabName)
        {
            if (!EntityManager.HasBuffer<Game.Prefabs.SubArea>(prefabEntity)
                || !EntityManager.HasBuffer<Game.Prefabs.SubAreaNode>(prefabEntity))
            {
                return;
            }

            DynamicBuffer<Game.Prefabs.SubArea> subAreas =
                EntityManager.GetBuffer<Game.Prefabs.SubArea>(prefabEntity);
            DynamicBuffer<Game.Prefabs.SubAreaNode> subAreaNodes =
                EntityManager.GetBuffer<Game.Prefabs.SubAreaNode>(prefabEntity);
            if (subAreas.Length == 0)
            {
                return;
            }

            int created = 0;
            for (int i = 0; i < subAreas.Length; i++)
            {
                Game.Prefabs.SubArea subArea = subAreas[i];
                if (subArea.m_Prefab == Entity.Null
                    || EntityManager.HasBuffer<Game.Prefabs.PlaceholderObjectElement>(subArea.m_Prefab))
                {
                    continue; // placeholder = random pick; defaults we care about are direct prefabs
                }

                int count = subArea.m_NodeRange.y - subArea.m_NodeRange.x + 1;
                if (count <= 0)
                {
                    continue;
                }

                Entity def = EntityManager.CreateEntity();
                EntityManager.AddComponentData(def, new CreationDefinition
                {
                    m_Prefab = subArea.m_Prefab,
                    m_Owner = owner,
                    m_RandomSeed = randomSeed * 17 + i,
                    m_Flags = CreationFlags.Permanent,
                });
                EntityManager.AddComponent<Updated>(def);

                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.AddBuffer<Game.Areas.Node>(def);
                nodes.ResizeUninitialized(count);
                int src = Game.Tools.ObjectToolBaseSystem.GetFirstNodeIndex(subAreaNodes, subArea.m_NodeRange);
                int dst = 0;
                for (int j = subArea.m_NodeRange.x; j <= subArea.m_NodeRange.y; j++)
                {
                    float3 local = subAreaNodes[src].m_Position;
                    float3 world = ObjectUtils.LocalToWorld(transform, local);
                    int parentMesh = subAreaNodes[src].m_ParentMesh;
                    float elevation = math.select(float.MinValue, local.y, parentMesh >= 0);
                    nodes[dst] = new Game.Areas.Node(world, elevation);
                    dst++;
                    if (++src == subArea.m_NodeRange.y)
                    {
                        src = subArea.m_NodeRange.x;
                    }
                }

                _pendingDefinitions.Add(def);
                created++;
            }

            if (created > 0)
            {
                CS2M.Log.Info($"[Place] SUBAREAS name={prefabName} defs={created}");
            }
        }

        /// <summary>Deletes any building standing within ~2 m of the incoming sim spawn's lot
        /// (the pre-level-up twin, or a stray local growable from before suppression kicked in).</summary>
        private void ClearLotFor(float3 position)
        {
            EntityQuery buildings = GetEntityQuery(new EntityQueryDesc
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
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            Unity.Collections.NativeArray<Entity> ents =
                buildings.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    var p = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - position.x;
                    float dz = p.z - position.z;
                    if (dx * dx + dz * dz >= 4f)
                    {
                        continue;
                    }

                    if (!EntityManager.HasComponent<CS2M_RemotePlaced>(cand))
                    {
                        EntityManager.AddComponent<CS2M_RemotePlaced>(cand); // no delete echo
                    }

                    EntityManager.AddComponent<Deleted>(cand);
                    CS2M.Log.Verbose($"[Grow] replaced local building entity={cand.Index} at spawn lot");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }

        /// <summary>True for objects that live attached to a net edge: any transport-stop prefab
        /// (bus/tram/taxi stop objects — EU_TaxiStop02 ships without the RoadSide flag, learned in
        /// the selftest) plus the road-side placement flags AttachSystem's find-parent job skips.</summary>
        private bool NeedsEdgeAttach(Entity prefabEntity)
        {
            if (EntityManager.HasComponent<Game.Prefabs.TransportStopData>(prefabEntity))
            {
                return true;
            }

            if (!EntityManager.HasComponent<PlaceableObjectData>(prefabEntity))
            {
                return false;
            }

            // NOT OwnerSide — that means "attach beside the OWNER object" (transformers etc.),
            // and snapping those to the nearest road would be wrong.
            Game.Objects.PlacementFlags flags =
                EntityManager.GetComponentData<PlaceableObjectData>(prefabEntity).m_Flags;
            return (flags & (Game.Objects.PlacementFlags.RoadSide
                             | Game.Objects.PlacementFlags.Shoreline)) != 0;
        }

        /// <summary>Nearest live net edge to a world point (3D distance along the curve — depth
        /// separates surface roads from buried pipes). Returns the curve parameter for Attached.</summary>
        private bool FindNearestEdge(float3 point, out Entity edge, out float curvePos, out float dist)
        {
            edge = Entity.Null;
            curvePos = 0f;
            dist = float.MaxValue;

            EntityQuery edges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Edge>(),
                    ComponentType.ReadOnly<Game.Net.Curve>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            Unity.Collections.NativeArray<Entity> ents = edges.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(cand);
                    // Cheap reject: point far outside the segment's bounding reach.
                    float reach = curve.m_Length * 0.5f + 20f;
                    float3 mid = MathUtils.Position(curve.m_Bezier, 0.5f);
                    if (math.distancesq(mid, point) > reach * reach)
                    {
                        continue;
                    }

                    // MathUtils.Distance RETURNS the distance and outputs the curve parameter.
                    float t;
                    float d = MathUtils.Distance(curve.m_Bezier, point, out t);
                    if (d < dist)
                    {
                        dist = d;
                        curvePos = t;
                        edge = cand;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return edge != Entity.Null && dist <= 16f;
        }

        /// <summary>Resolve the extension's parent building: synced id first, else nearest
        /// same-prefab building at the shipped position (native buildings have no id).</summary>
        private Entity ResolveOwner(ObjectPlaceCommand cmd)
        {
            if (cmd.OwnerSyncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(cmd.OwnerSyncId, out Entity byId)
                && EntityManager.Exists(byId) && !EntityManager.HasComponent<Deleted>(byId))
            {
                return byId;
            }

            EntityQuery buildings = GetEntityQuery(new EntityQueryDesc
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

            Entity best = Entity.Null;
            float bestD = 9f; // 3 m
            Unity.Collections.NativeArray<Entity> ents =
                buildings.ToEntityArray(Unity.Collections.Allocator.Temp);
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

            return best;
        }

        /// <summary>
        ///     Replicates <c>Game.Simulation.BuildingConstructionSystem.CreateNets</c>: average shared
        ///     node positions per <c>m_NodeIndex</c>, then one Permanent definition per SubNet entry.
        /// </summary>
        private void CreateSubNets(Entity prefabEntity, Entity owner, Game.Objects.Transform transform,
            int randomSeed, string prefabName)
        {
            if (!EntityManager.HasBuffer<Game.Prefabs.SubNet>(prefabEntity))
            {
                return;
            }

            DynamicBuffer<Game.Prefabs.SubNet> subNets = EntityManager.GetBuffer<Game.Prefabs.SubNet>(prefabEntity);
            if (subNets.Length == 0)
            {
                return;
            }

            // Average the endpoints that share a node index (vanilla CreateNets does exactly this).
            var nodePositions = new List<float4>();
            for (int i = 0; i < subNets.Length; i++)
            {
                Game.Prefabs.SubNet subNet = subNets[i];
                if (subNet.m_NodeIndex.x >= 0)
                {
                    while (nodePositions.Count <= subNet.m_NodeIndex.x)
                    {
                        nodePositions.Add(default);
                    }

                    nodePositions[subNet.m_NodeIndex.x] += new float4(subNet.m_Curve.a, 1f);
                }

                if (subNet.m_NodeIndex.y >= 0)
                {
                    while (nodePositions.Count <= subNet.m_NodeIndex.y)
                    {
                        nodePositions.Add(default);
                    }

                    nodePositions[subNet.m_NodeIndex.y] += new float4(subNet.m_Curve.d, 1f);
                }
            }

            for (int j = 0; j < nodePositions.Count; j++)
            {
                nodePositions[j] /= math.max(1f, nodePositions[j].w);
            }

            ComponentLookup<NetGeometryData> netGeometryData = GetComponentLookup<NetGeometryData>(true);
            bool lefthand = _cityConfigSystem.leftHandTraffic;
            int created = 0;

            for (int k = 0; k < subNets.Length; k++)
            {
                Game.Prefabs.SubNet subNet = Game.Net.NetUtils.GetSubNet(subNets, k, lefthand, ref netGeometryData);
                if (subNet.m_Prefab == Entity.Null)
                {
                    continue;
                }

                Entity def = EntityManager.CreateEntity();
                EntityManager.AddComponentData(def, new CreationDefinition
                {
                    m_Prefab = subNet.m_Prefab,
                    m_Owner = owner,
                    // Deterministic per-PC: derive from the synced building seed + index.
                    m_RandomSeed = randomSeed * 31 + k,
                    m_Flags = CreationFlags.Permanent,
                });

                NetCourse course = default;
                course.m_Curve = ObjectUtils.LocalToWorld(transform.m_Position, transform.m_Rotation, subNet.m_Curve);
                course.m_StartPosition.m_Position = course.m_Curve.a;
                course.m_StartPosition.m_Rotation =
                    Game.Net.NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve), transform.m_Rotation);
                course.m_StartPosition.m_CourseDelta = 0f;
                course.m_StartPosition.m_Elevation = subNet.m_Curve.a.y;
                course.m_StartPosition.m_ParentMesh = subNet.m_ParentMesh.x;
                if (subNet.m_NodeIndex.x >= 0)
                {
                    course.m_StartPosition.m_Position = ObjectUtils.LocalToWorld(
                        transform.m_Position, transform.m_Rotation, nodePositions[subNet.m_NodeIndex.x].xyz);
                }

                course.m_EndPosition.m_Position = course.m_Curve.d;
                course.m_EndPosition.m_Rotation =
                    Game.Net.NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve), transform.m_Rotation);
                course.m_EndPosition.m_CourseDelta = 1f;
                course.m_EndPosition.m_Elevation = subNet.m_Curve.d.y;
                course.m_EndPosition.m_ParentMesh = subNet.m_ParentMesh.y;
                if (subNet.m_NodeIndex.y >= 0)
                {
                    course.m_EndPosition.m_Position = ObjectUtils.LocalToWorld(
                        transform.m_Position, transform.m_Rotation, nodePositions[subNet.m_NodeIndex.y].xyz);
                }

                course.m_Length = MathUtils.Length(course.m_Curve);
                course.m_FixedIndex = -1;
                course.m_StartPosition.m_Flags |= CoursePosFlags.IsFirst | CoursePosFlags.DisableMerge;
                course.m_EndPosition.m_Flags |= CoursePosFlags.IsLast | CoursePosFlags.DisableMerge;
                if (course.m_StartPosition.m_Position.Equals(course.m_EndPosition.m_Position))
                {
                    course.m_StartPosition.m_Flags |= CoursePosFlags.IsLast;
                    course.m_EndPosition.m_Flags |= CoursePosFlags.IsFirst;
                }

                EntityManager.AddComponentData(def, course);

                if (subNet.m_Upgrades != default(CompositionFlags))
                {
                    EntityManager.AddComponentData(def, new Game.Net.Upgraded { m_Flags = subNet.m_Upgrades });
                }

                EntityManager.AddComponent<Updated>(def);
                _pendingDefinitions.Add(def);
                created++;
            }

            if (created > 0)
            {
                CS2M.Log.Info($"[Place] SUBNETS name={prefabName} defs={created}");
            }
        }

        /// <summary>
        ///     Mirrors <c>Game.Tools.ToolApplySystem.ApplyJob</c>: sum of Temp.m_Cost →
        ///     <c>PlayerMoney.Subtract</c> on the city entity. Remote applies bypass the tool flow, so
        ///     the host debits explicitly; the ~1 Hz money sync then propagates the corrected balance.
        /// </summary>
        private void ChargeConstructionCost(Entity prefabEntity, string prefabName)
        {
            if (!EntityManager.HasComponent<PlaceableObjectData>(prefabEntity))
            {
                return; // not purchasable (decorative props) — free is vanilla-correct
            }

            int cost = (int) EntityManager.GetComponentData<PlaceableObjectData>(prefabEntity).m_ConstructionCost;
            if (cost <= 0)
            {
                return;
            }

            Entity city = _citySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<PlayerMoney>(city))
            {
                return;
            }

            PlayerMoney pm = EntityManager.GetComponentData<PlayerMoney>(city);
            if (pm.m_Unlimited)
            {
                return;
            }

            pm.Subtract(cost); // clamps at ±2e9, same as vanilla
            EntityManager.SetComponentData(city, pm);
            CS2M.Log.Info($"[Place] CHARGED cost={cost} prefab={prefabName} cash={pm.money}");
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
