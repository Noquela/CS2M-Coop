using System.Collections.Generic;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Global toggle for the SubNet/SubArea child-recompute on a remotely-applied building move
    /// (see <see cref="RemoteEditApplySystem.ApplyChildTransformDelta"/>). ON by default since
    /// 2026-07-07 — validated live in 2-sim + selftest 88 PASS/0 FAIL with every gated fix enabled
    /// together (no regression/echo/crash), same pattern as <c>OverdrawFix</c> in
    /// <see cref="NetPlaceApplySystem"/>.
    ///
    /// GAP this closes: the base game only recomposes a moved building's internal private road (SubNet)
    /// and lot/work-area (SubArea) TOOL-SIDE (decomp <c>ObjectToolBaseSystem.UpdateSubNets</c>/
    /// <c>UpdateSubAreas</c>, ~line 1670-2060) — it deletes the old sub-net/sub-area and re-emits it via
    /// CreationDefinition/NetCourse in local-to-building space, then re-creates it at the new transform.
    /// That never runs on the receiver (no tool involved), so a remote building's children stay at the
    /// OLD absolute position after <see cref="ApplyMove"/> only moves the building's own Transform —
    /// divergent pathfinding/zoning on the other PC. Confirmed: neither Game.Net nor Game.Areas has a
    /// LocalTransformCache/Attached-style reactive system for this (that exists only for
    /// Game.Objects.SubObject, which IS already covered — see SubObjectSystem). Set env
    /// <c>CS2M_MOVEFIX=0</c> to disable.</summary>
    public static class MoveFix
    {
        private static int _state = -1;

        public static bool Enabled
        {
            get
            {
                if (_state < 0)
                {
                    _state = System.Environment.GetEnvironmentVariable("CS2M_MOVEFIX") == "0" ? 0 : 1;
                }

                return _state == 1;
            }
        }
    }

    /// <summary>
    ///     Applies remote delete/move edits. Resolves the target by <c>CS2M_SyncId</c> (via
    ///     <see cref="CS2M_SyncIdSystem"/>), then: delete = <c>AddComponent&lt;Deleted&gt;</c> (game
    ///     cascades children); move = set <c>Transform</c> + <c>Updated</c> + <c>BatchesUpdated</c>.
    ///     The target keeps/gets <c>CS2M_RemotePlaced</c> so our own detectors don't echo it back.
    /// </summary>
    public partial class RemoteEditApplySystem : GameSystemBase
    {
        private CS2M_SyncIdSystem _idSystem;
        private Game.Prefabs.PrefabSystem _prefabSystem;
        private EntityQuery _nativeObjects;

        protected override void OnCreate()
        {
            base.OnCreate();
            _idSystem = World.GetOrCreateSystemManaged<CS2M_SyncIdSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>();
            _nativeObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Prefabs.PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Static>(),
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Common.Owner>(),
                },
            });
            CS2M.Log.Info("[Edit] RemoteEditApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteEditQueue.TryDelete(out DeleteCommand del))
            {
                try { ApplyDelete(del); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] delete apply failed: {ex.Message}"); }
            }

            while (RemoteEditQueue.TryMove(out MoveCommand mv))
            {
                try { ApplyMove(mv); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] move apply failed: {ex.Message}"); }
            }
        }

        private void ApplyDelete(DeleteCommand cmd)
        {
            if (cmd.TargetKind == 1)
            {
                ApplyRouteDelete(cmd);
                return;
            }

            Entity e;
            if (cmd.SyncId == 0)
            {
                // v42: native object (no cross-PC id) — nearest same-prefab match at the position.
                e = FindNative(cmd);
                if (e == Entity.Null)
                {
                    CS2M.Log.Info($"[Del] SKIP native noMatch name={cmd.PrefabName} pos=({cmd.PosX:F0},{cmd.PosZ:F0})");
                    return;
                }
            }
            else if (!_idSystem.TryResolve(cmd.SyncId, out e) || !EntityManager.Exists(e))
            {
                CS2M.Log.Info($"[Del] SKIP unresolved id={cmd.SyncId}");
                return;
            }

            if (EntityManager.HasComponent<Deleted>(e))
            {
                return;
            }

            // v51: cascade every live child too — replica buildings hold definition-made
            // sub-nets/sub-areas the vanilla cascade can miss ("part of the building stays behind").
            // Delete-echo stamping (CS2M_RemoteDeleted on target + children) happens inside the util —
            // NOT CS2M_RemotePlaced, which marks remote CREATION and would swallow a local player's
            // delete of anything a remote player built.
            CascadeDeleteUtil.DeleteWithChildren(EntityManager, e);
            if (cmd.SyncId != 0)
            {
                CS2M_SyncIdSystem.Map.Remove(cmd.SyncId);
            }

            CS2M.Log.Info($"[Del] APPLIED id={cmd.SyncId} entity={e.Index}" +
                          (cmd.SyncId == 0 ? $" (native {cmd.PrefabName})" : ""));
        }

        /// <summary>v49: a transport line — resolved by SyncId or prefab + RouteNumber. Only the
        /// route gets Deleted; both sides' ElementSystem cascades waypoints/segments.</summary>
        private void ApplyRouteDelete(DeleteCommand cmd)
        {
            Entity route = RouteResolver.Resolve(EntityManager, GetEntityQuery(
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadOnly<Game.Routes.RouteNumber>(),
                    ComponentType.ReadOnly<Game.Prefabs.PrefabRef>(),
                    ComponentType.Exclude<Game.Tools.Temp>(),
                    ComponentType.Exclude<Deleted>()),
                _prefabSystem, cmd.SyncId, cmd.PrefabName, cmd.Number);
            if (route == Entity.Null)
            {
                CS2M.Log.Info($"[Del] SKIP route noMatch id={cmd.SyncId} number={cmd.Number} name={cmd.PrefabName}");
                return;
            }

            // Mark every key the local detector might compute for this route (its own id can differ
            // from the command's when resolution fell back to prefab+number).
            RouteSync.MarkDeleteEcho(RouteSync.DeleteKey(cmd.SyncId, cmd.PrefabName, cmd.Number));
            RouteSync.MarkDeleteEcho(RouteSync.DeleteKey(0, cmd.PrefabName, cmd.Number));
            if (EntityManager.HasComponent<CS2M_SyncId>(route))
            {
                ulong localId = EntityManager.GetComponentData<CS2M_SyncId>(route).m_Id;
                RouteSync.MarkDeleteEcho(RouteSync.DeleteKey(localId, cmd.PrefabName, cmd.Number));
            }

            EntityManager.AddComponent<Deleted>(route);
            if (cmd.SyncId != 0)
            {
                CS2M_SyncIdSystem.Map.Remove(cmd.SyncId);
                RouteSync.Snapshot.Remove(cmd.SyncId);
            }

            CS2M.Log.Info($"[Del] APPLIED route id={cmd.SyncId} number={cmd.Number} entity={route.Index}");
        }

        /// <summary>Nearest object of the same prefab within 2 m of the sender's position (exact
        /// prefab preferred; falls back to any Static/Building at the spot within 1 m).</summary>
        private Entity FindNative(DeleteCommand cmd)
        {
            Entity exact = Entity.Null, loose = Entity.Null;
            float exactD = 4f, looseD = 1f; // squared meters
            Unity.Collections.NativeArray<Entity> ents =
                _nativeObjects.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    var p = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - cmd.PosX;
                    float dz = p.z - cmd.PosZ;
                    float d = dx * dx + dz * dz;
                    if (d >= exactD)
                    {
                        continue;
                    }

                    bool samePrefab = _prefabSystem.TryGetPrefab(
                            EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(cand).m_Prefab,
                            out Game.Prefabs.PrefabBase prefab)
                        && prefab != null && prefab.name == cmd.PrefabName;
                    if (samePrefab)
                    {
                        exactD = d;
                        exact = cand;
                    }
                    else if (d < looseD)
                    {
                        looseD = d;
                        loose = cand;
                    }
                }
            }
            finally
            {
                ents.Dispose();
            }

            return exact != Entity.Null ? exact : loose;
        }

        private void ApplyMove(MoveCommand cmd)
        {
            if (cmd.IsOwnedUpgrade)
            {
                ApplyOwnedUpgradeMove(cmd);
                return;
            }

            Entity e;
            bool nativeFirstTouch = false;
            if (!_idSystem.TryResolve(cmd.SyncId, out e) || !EntityManager.Exists(e))
            {
                // v48: native relocation — find the twin by prefab + OLD position.
                if (!string.IsNullOrEmpty(cmd.PrefabName))
                {
                    e = FindNative(new DeleteCommand
                    {
                        PrefabName = cmd.PrefabName,
                        PosX = cmd.OldX, PosY = cmd.OldY, PosZ = cmd.OldZ,
                    });
                    nativeFirstTouch = e != Entity.Null;
                }

                if (e == Entity.Null)
                {
                    CS2M.Log.Info($"[Move] SKIP unresolved id={cmd.SyncId} name={cmd.PrefabName}");
                    return;
                }
            }

            if (!EntityManager.HasComponent<Game.Objects.Transform>(e))
            {
                CS2M.Log.Info($"[Move] SKIP no-transform id={cmd.SyncId}");
                return;
            }

            if (!EntityManager.HasComponent<CS2M_RemotePlaced>(e))
            {
                EntityManager.AddComponent<CS2M_RemotePlaced>(e);
            }

            var tf = EntityManager.GetComponentData<Game.Objects.Transform>(e);
            tf.m_Position = new float3(cmd.PosX, cmd.PosY, cmd.PosZ);
            tf.m_Rotation = new quaternion(cmd.RotX, cmd.RotY, cmd.RotZ, cmd.RotW);
            EntityManager.SetComponentData(e, tf);

            if (!EntityManager.HasComponent<Updated>(e))
            {
                EntityManager.AddComponent<Updated>(e);
            }

            if (!EntityManager.HasComponent<BatchesUpdated>(e))
            {
                EntityManager.AddComponent<BatchesUpdated>(e);
            }

            // First-touch identity: the sender allocated this id for the native — register it here
            // too so both sides address this building by id from now on.
            if (nativeFirstTouch && cmd.SyncId != 0)
            {
                CS2M_SyncIdSystem.Register(EntityManager, e, cmd.SyncId);
            }

            // v56 (CS2M_MOVEFIX): the building moved, but its internal SubNet (private road) / SubArea
            // (lot) children are still stored at the OLD absolute position — see MoveFix's doc comment.
            // Requires the sender's old transform; older senders / first-touch natives without a captured
            // baseline leave HasOldTransform false and this quietly no-ops (pre-v56 behavior).
            if (MoveFix.Enabled && cmd.HasOldTransform)
            {
                try
                {
                    var oldTf = new Game.Objects.Transform
                    {
                        m_Position = new float3(cmd.OldX, cmd.OldY, cmd.OldZ),
                        m_Rotation = new quaternion(cmd.OldRotX, cmd.OldRotY, cmd.OldRotZ, cmd.OldRotW),
                    };
                    ApplyChildTransformDelta(e, oldTf, tf);
                }
                catch (System.Exception ex)
                {
                    CS2M.Log.Info($"[Guard] MOVEFIX child-delta failed id={cmd.SyncId}: {ex.Message}");
                }
            }

            CS2M.Log.Info($"[Move] APPLIED id={cmd.SyncId} pos=({cmd.PosX:F1},{cmd.PosY:F1},{cmd.PosZ:F1}) entity={e.Index}" +
                          (nativeFirstTouch ? " (native first-touch)" : ""));
        }

        /// <summary>v56 (CS2M_MOVEFIX): re-derive a moved building's SubNet (private road) and SubArea
        /// (lot/work-area) children by applying the SAME rigid transform (rotation then translation) the
        /// building itself just moved by. Exact — a Bezier curve's control points transform losslessly
        /// under a rigid (rotation+translation, no scale) transform, so this reproduces precisely what the
        /// tool-side <c>ObjectToolBaseSystem.UpdateSubNets</c>/<c>UpdateSubAreas</c> achieves by deleting
        /// and re-creating the children, without needing to replay the CreationDefinition/NetCourse
        /// pipeline over the network.
        ///
        /// IMPORTANT (decomp-confirmed): unlike Game.Areas (whose GeometrySystem reactively retriangulates
        /// from the Node buffer on Area+Updated — decomp Game.Areas.GeometrySystem.cs:753,779), Game.Net
        /// has NO reactive system that recomputes a connected Edge's Curve from a moved Node — that only
        /// happens tool-side (GenerateEdgesSystem, gated on CreationDefinition/Temp). Worse,
        /// Game.Net.NodeAlignSystem DOES reactively watch Node+Updated, but in the OPPOSITE direction: it
        /// re-derives the node's position/rotation FROM its connected edges' CURRENT Curve. So this method
        /// must set both the Edge.Curve control points AND the Node position/rotation itself, consistently,
        /// via the same delta — never rely on tagging Updated alone to "fix" geometry here (that pattern
        /// from HealNodePosition only works there because it doesn't touch edges).
        ///
        /// Buffer-invalidation discipline: every DynamicBuffer/entity ref this needs is read into a plain
        /// array/HashSet FIRST; only after all reads are done does it start calling AddComponent (the only
        /// structural change here, for Updated/BatchesUpdated) — see project lesson on buffers invalidating
        /// after a structural change.</summary>
        private void ApplyChildTransformDelta(Entity building, Game.Objects.Transform oldT, Game.Objects.Transform newT)
        {
            // A missing/corrupt old rotation (near-zero quaternion) would blow up math.inverse into NaNs —
            // bail rather than teleport children to NaN-land.
            if (math.lengthsq(oldT.m_Rotation.value) < 0.5f)
            {
                return;
            }

            bool hasSubNet = EntityManager.HasBuffer<Game.Net.SubNet>(building);
            bool hasSubArea = EntityManager.HasBuffer<Game.Areas.SubArea>(building);
            if (!hasSubNet && !hasSubArea)
            {
                return;
            }

            quaternion deltaRot = math.mul(newT.m_Rotation, math.inverse(oldT.m_Rotation));
            int nodeCount = 0, edgeCount = 0, areaCount = 0;

            if (hasSubNet)
            {
                // ---- Pass 1: snapshot the SubNet buffer's entity refs (they're either a lone Node or an
                // Edge — decomp ObjectToolBaseSystem.UpdateSubNets branches on m_NodeData/m_EdgeData).
                DynamicBuffer<Game.Net.SubNet> subNets = EntityManager.GetBuffer<Game.Net.SubNet>(building, true);
                var subNetRefs = new Entity[subNets.Length];
                for (int i = 0; i < subNets.Length; i++)
                {
                    subNetRefs[i] = subNets[i].m_SubNet;
                }

                // ---- Pass 2: transform every Edge's Curve control points (exact under a rigid transform)
                // and collect every touched Node (edge endpoints + any lone-node entries) — no structural
                // changes yet, so buffers/lookups obtained above stay valid throughout.
                var touchedNodes = new HashSet<Entity>();
                var subNetEdges = new List<Entity>();
                foreach (Entity sn in subNetRefs)
                {
                    if (sn == Entity.Null || !EntityManager.Exists(sn))
                    {
                        continue;
                    }

                    if (EntityManager.HasComponent<Game.Net.Edge>(sn) && EntityManager.HasComponent<Game.Net.Curve>(sn))
                    {
                        Game.Net.Edge edge = EntityManager.GetComponentData<Game.Net.Edge>(sn);
                        touchedNodes.Add(edge.m_Start);
                        touchedNodes.Add(edge.m_End);
                        subNetEdges.Add(sn);

                        Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(sn);
                        curve.m_Bezier.a = TransformDelta(curve.m_Bezier.a, oldT.m_Position, newT.m_Position, deltaRot);
                        curve.m_Bezier.b = TransformDelta(curve.m_Bezier.b, oldT.m_Position, newT.m_Position, deltaRot);
                        curve.m_Bezier.c = TransformDelta(curve.m_Bezier.c, oldT.m_Position, newT.m_Position, deltaRot);
                        curve.m_Bezier.d = TransformDelta(curve.m_Bezier.d, oldT.m_Position, newT.m_Position, deltaRot);
                        EntityManager.SetComponentData(sn, curve);
                        edgeCount++;
                    }
                    else if (EntityManager.HasComponent<Game.Net.Node>(sn))
                    {
                        touchedNodes.Add(sn);
                    }
                }

                // ---- Pass 3: move every touched node (position + rotation) — still no structural changes.
                foreach (Entity node in touchedNodes)
                {
                    if (node == Entity.Null || !EntityManager.Exists(node) || !EntityManager.HasComponent<Game.Net.Node>(node))
                    {
                        continue;
                    }

                    Game.Net.Node n = EntityManager.GetComponentData<Game.Net.Node>(node);
                    n.m_Position = TransformDelta(n.m_Position, oldT.m_Position, newT.m_Position, deltaRot);
                    n.m_Rotation = math.mul(deltaRot, n.m_Rotation);
                    EntityManager.SetComponentData(node, n);
                    nodeCount++;
                }

                // ---- Pass 4: NOW the structural changes (Updated/BatchesUpdated) — everything above already
                // read what it needed, so invalidation from these AddComponent calls is harmless.
                foreach (Entity edge in subNetEdges)
                {
                    MarkUpdated(edge);
                }

                foreach (Entity node in touchedNodes)
                {
                    if (EntityManager.Exists(node) && EntityManager.HasComponent<Game.Net.Node>(node))
                    {
                        MarkUpdated(node);
                    }
                }
            }

            if (hasSubArea)
            {
                DynamicBuffer<Game.Areas.SubArea> subAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(building, true);
                var areaRefs = new Entity[subAreas.Length];
                for (int i = 0; i < subAreas.Length; i++)
                {
                    areaRefs[i] = subAreas[i].m_Area;
                }

                // Game.Areas.GeometrySystem reactively retriangulates from the Node buffer on Area+Updated
                // (decomp GeometrySystem.cs:753,779) — so here, unlike SubNet, writing the Node buffer and
                // tagging Updated IS sufficient; no manual triangle/bounds recompute needed.
                foreach (Entity area in areaRefs)
                {
                    if (area == Entity.Null || !EntityManager.Exists(area) || !EntityManager.HasBuffer<Game.Areas.Node>(area))
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area);
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        Game.Areas.Node an = nodes[i];
                        an.m_Position = TransformDelta(an.m_Position, oldT.m_Position, newT.m_Position, deltaRot);
                        nodes[i] = an;
                    }

                    MarkUpdated(area);
                    areaCount++;
                }
            }

            CS2M.Log.Info($"[Move] MOVEFIX entity={building.Index} subNetEdges={edgeCount} subNetNodes={nodeCount} subAreas={areaCount}");
        }

        private static float3 TransformDelta(float3 p, float3 oldPos, float3 newPos, quaternion deltaRot)
        {
            return newPos + math.mul(deltaRot, p - oldPos);
        }

        private void MarkUpdated(Entity e)
        {
            if (!EntityManager.Exists(e))
            {
                return;
            }

            if (!EntityManager.HasComponent<Updated>(e)) { EntityManager.AddComponent<Updated>(e); }
            if (!EntityManager.HasComponent<BatchesUpdated>(e)) { EntityManager.AddComponent<BatchesUpdated>(e); }
        }

        /// <summary>v55: relocate an installed service upgrade — resolve the OWNER (SyncId else prefab+pos),
        /// then the child sub-object whose prefab matches nearest the OLD position, and set its transform.</summary>
        private void ApplyOwnedUpgradeMove(MoveCommand cmd)
        {
            Entity owner = Entity.Null;
            if (cmd.OwnerSyncId != 0 && _idSystem.TryResolve(cmd.OwnerSyncId, out owner) && EntityManager.Exists(owner))
            {
                // resolved by id
            }
            else if (!string.IsNullOrEmpty(cmd.OwnerPrefabName))
            {
                owner = FindNative(new DeleteCommand
                {
                    PrefabName = cmd.OwnerPrefabName, PosX = cmd.OwnerX, PosY = cmd.OwnerY, PosZ = cmd.OwnerZ,
                });
            }

            if (owner == Entity.Null || !EntityManager.HasBuffer<Game.Objects.SubObject>(owner))
            {
                CS2M.Log.Info($"[Move] SKIP owned-upgrade noOwner name={cmd.PrefabName} owner={cmd.OwnerPrefabName}");
                return;
            }

            var oldPos = new float3(cmd.OldX, cmd.OldY, cmd.OldZ);
            Entity best = Entity.Null;
            float bestD = 9f; // 3 m²
            DynamicBuffer<Game.Objects.SubObject> subs = EntityManager.GetBuffer<Game.Objects.SubObject>(owner, true);
            for (int i = 0; i < subs.Length; i++)
            {
                Entity child = subs[i].m_SubObject;
                if (!EntityManager.Exists(child)
                    || !EntityManager.HasComponent<Game.Objects.Transform>(child)
                    || !EntityManager.HasComponent<Game.Prefabs.PrefabRef>(child))
                {
                    continue;
                }

                if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(child).m_Prefab,
                        out Game.Prefabs.PrefabBase pb) || pb == null || pb.name != cmd.PrefabName)
                {
                    continue;
                }

                float3 cp = EntityManager.GetComponentData<Game.Objects.Transform>(child).m_Position;
                float d = math.distancesq(cp.xz, oldPos.xz);
                if (d < bestD)
                {
                    bestD = d;
                    best = child;
                }
            }

            if (best == Entity.Null)
            {
                CS2M.Log.Info($"[Move] SKIP owned-upgrade noChild name={cmd.PrefabName} nearOld=({cmd.OldX:F0},{cmd.OldZ:F0})");
                return;
            }

            var tf = EntityManager.GetComponentData<Game.Objects.Transform>(best);
            tf.m_Position = new float3(cmd.PosX, cmd.PosY, cmd.PosZ);
            tf.m_Rotation = new quaternion(cmd.RotX, cmd.RotY, cmd.RotZ, cmd.RotW);
            EntityManager.SetComponentData(best, tf);
            if (!EntityManager.HasComponent<Updated>(best)) { EntityManager.AddComponent<Updated>(best); }
            if (!EntityManager.HasComponent<BatchesUpdated>(best)) { EntityManager.AddComponent<BatchesUpdated>(best); }

            CS2M.Log.Info($"[Move] APPLIED owned-upgrade name={cmd.PrefabName} owner={cmd.OwnerPrefabName} " +
                          $"pos=({cmd.PosX:F1},{cmd.PosZ:F1}) entity={best.Index}");
        }
    }
}
