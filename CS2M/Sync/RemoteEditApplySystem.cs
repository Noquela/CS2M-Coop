using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
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

            CS2M.Log.Info($"[Move] APPLIED id={cmd.SyncId} pos=({cmd.PosX:F1},{cmd.PosY:F1},{cmd.PosZ:F1}) entity={e.Index}" +
                          (nativeFirstTouch ? " (native first-touch)" : ""));
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
