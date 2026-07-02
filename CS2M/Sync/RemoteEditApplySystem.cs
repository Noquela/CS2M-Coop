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

            if (!EntityManager.HasComponent<CS2M_RemotePlaced>(e))
            {
                EntityManager.AddComponent<CS2M_RemotePlaced>(e);
            }

            EntityManager.AddComponent<Deleted>(e);
            if (cmd.SyncId != 0)
            {
                CS2M_SyncIdSystem.Map.Remove(cmd.SyncId);
            }

            CS2M.Log.Info($"[Del] APPLIED id={cmd.SyncId} entity={e.Index}" +
                          (cmd.SyncId == 0 ? $" (native {cmd.PrefabName})" : ""));
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
            if (!_idSystem.TryResolve(cmd.SyncId, out Entity e) || !EntityManager.Exists(e))
            {
                CS2M.Log.Info($"[Move] SKIP unresolved id={cmd.SyncId}");
                return;
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

            CS2M.Log.Info($"[Move] APPLIED id={cmd.SyncId} pos=({cmd.PosX:F1},{cmd.PosY:F1},{cmd.PosZ:F1}) entity={e.Index}");
        }
    }
}
