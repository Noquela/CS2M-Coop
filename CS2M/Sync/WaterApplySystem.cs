using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Objects;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Materializes (or removes) a water source placed by a remote player. A water source is a
    ///     plain entity (WaterSourceData + Transform); the game's WaterSystem simulates from it.
    ///     v50 field fixes: the Y is anchored to the LOCAL terrain height (terrain sync is
    ///     best-effort — an absolute Y could float above local ground and flood a neighborhood),
    ///     and removals now sync (a deleted source used to live forever on the other PCs).
    /// </summary>
    public partial class WaterApplySystem : GameSystemBase
    {
        private TerrainSystem _terrain;

        protected override void OnCreate()
        {
            base.OnCreate();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            CS2M.Log.Info("[Water] WaterApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemoteWaterQueue.TryDequeue(out WaterCommand cmd))
            {
                try
                {
                    if (cmd.Delete) { ApplyDelete(cmd); }
                    else if (cmd.Move) { ApplyMove(cmd); }
                    else if (cmd.Edit) { ApplyEdit(cmd); }
                    else { ApplyOne(cmd); }
                }
                catch (System.Exception ex) { CS2M.Log.Info($"[Guard] apply failed in WaterApplySystem: {ex.Message}"); }
            }
        }

        private void ApplyOne(WaterCommand cmd)
        {
            // Anchor to OUR terrain: lake/stream heights are terrain-relative in the game, and the
            // source entity itself must sit on the ground here, not at the sender's altitude.
            float y = cmd.PosY;
            try
            {
                TerrainHeightData hd = _terrain.GetHeightData(true);
                y = TerrainUtils.SampleHeight(ref hd, new float3(cmd.PosX, cmd.PosY, cmd.PosZ));
            }
            catch
            {
                // heightmap unavailable this frame — sender Y is still a sane fallback
            }

            Entity e = EntityManager.CreateEntity();
            EntityManager.AddComponentData(e, new WaterSourceData
            {
                m_Radius = cmd.Radius,
                m_Height = cmd.Height,
                m_Multiplier = cmd.Multiplier,
                m_Polluted = cmd.Polluted,
                m_ConstantDepth = cmd.ConstantDepth,
                // WaterSimulation.cs:58 multiplica o raio efetivo por m_Modifier; o jogo força 1f em todo
                // caminho próprio (WaterSourceData.cs:55) e WaterSourceInitializeSystem só corrige entidades
                // com PrefabRef — sem esta linha a fonte remota nasce com raio efetivo ZERO (morta).
                m_Modifier = 1f,
            });
            EntityManager.AddComponentData(e, new Transform(new float3(cmd.PosX, y, cmd.PosZ), quaternion.identity));
            EntityManager.AddComponent<CS2M_RemotePlaced>(e);
            EntityManager.AddComponent<Created>(e);
            EntityManager.AddComponent<Updated>(e);
            // Issue #8: stamp the sender's cross-PC id so later move/edit/delete resolve exactly.
            CS2M_SyncIdSystem.Register(EntityManager, e, cmd.SyncId);

            CS2M.Log.Info($"[Water] APPLIED pos=({cmd.PosX:F0},{cmd.PosZ:F0}) yLocal={y:F1} (sender {cmd.PosY:F1}) r={cmd.Radius} entity={e.Index} id={cmd.SyncId}");
        }

        /// <summary>Repositions the nearest source (any origin) within ~10 m of the OLD address to the new
        /// XZ, anchored to LOCAL terrain height. Marks the target position so our detector doesn't echo the
        /// move back (the moved source is a plain entity with no CS2M_RemotePlaced tag to exclude it).</summary>
        private void ApplyMove(WaterCommand cmd)
        {
            Entity best = ResolveSource(cmd.SyncId, cmd.OldX, cmd.OldZ);
            if (best == Entity.Null)
            {
                CS2M.Log.Info($"[Water] SKIP move noMatch old=({cmd.OldX:F0},{cmd.OldZ:F0})");
                return;
            }

            float y = cmd.PosY;
            try
            {
                TerrainHeightData hd = _terrain.GetHeightData(true);
                y = TerrainUtils.SampleHeight(ref hd, new float3(cmd.PosX, cmd.PosY, cmd.PosZ));
            }
            catch
            {
                // heightmap unavailable — sender Y is a sane fallback
            }

            var newPos = new float3(cmd.PosX, y, cmd.PosZ);
            EntityManager.SetComponentData(best, new Transform(newPos, quaternion.identity));
            if (!EntityManager.HasComponent<Updated>(best))
            {
                EntityManager.AddComponent<Updated>(best);
            }

            WaterSync.MarkRemoteMove(newPos); // detector must not bounce this relocation back
            CS2M.Log.Info($"[Water] APPLIED move ({cmd.OldX:F0},{cmd.OldZ:F0})->({cmd.PosX:F0},{cmd.PosZ:F0}) yLocal={y:F1} entity={best.Index}");
        }

        /// <summary>v59: overwrites the editable params (radius/height/rate/pollution/depth) of the
        /// nearest source in place — the water tool's EditSource drag never creates or moves an entity,
        /// so none of the other paths ever fired for it. m_Id is preserved (it addresses the source in
        /// the save) and m_Modifier stays 1f (same dead-source trap as ApplyOne). Marks the position so
        /// our detector doesn't bounce the edit back.</summary>
        private void ApplyEdit(WaterCommand cmd)
        {
            Entity best = ResolveSource(cmd.SyncId, cmd.PosX, cmd.PosZ);
            if (best == Entity.Null)
            {
                CS2M.Log.Info($"[Water] SKIP edit noMatch pos=({cmd.PosX:F0},{cmd.PosZ:F0})");
                return;
            }

            WaterSourceData w = EntityManager.GetComponentData<WaterSourceData>(best);
            w.m_Radius = cmd.Radius;
            w.m_Height = cmd.Height;
            w.m_Multiplier = cmd.Multiplier;
            w.m_Polluted = cmd.Polluted;
            w.m_ConstantDepth = cmd.ConstantDepth;
            w.m_Modifier = 1f;
            EntityManager.SetComponentData(best, w);
            if (!EntityManager.HasComponent<Updated>(best))
            {
                EntityManager.AddComponent<Updated>(best);
            }

            float3 pos = EntityManager.GetComponentData<Transform>(best).m_Position;
            WaterSync.MarkRemoteEdit(pos); // detector must not bounce this param change back
            CS2M.Log.Info($"[Water] APPLIED edit pos=({cmd.PosX:F0},{cmd.PosZ:F0}) r={cmd.Radius:F1} " +
                          $"h={cmd.Height:F1} m={cmd.Multiplier:F2} entity={best.Index}");
        }

        /// <summary>Issue #8: single identity rule for every water op — SyncId first (exact), then the
        /// legacy nearest-within-10 m fallback for save-loaded sources (or a post-transfer world where
        /// the id tag didn't survive the save).</summary>
        private Entity ResolveSource(ulong syncId, float x, float z)
        {
            if (syncId != 0
                && CS2M_SyncIdSystem.Map.TryGetValue(syncId, out Entity byId)
                && EntityManager.Exists(byId)
                && !EntityManager.HasComponent<Deleted>(byId)
                && EntityManager.HasComponent<WaterSourceData>(byId))
            {
                return byId;
            }

            return FindNearestSource(x, z, out _);
        }

        /// <summary>Nearest live water source within ~10 m² of (x,z).</summary>
        private Entity FindNearestSource(float x, float z, out float bestDistSq)
        {
            EntityQuery sources = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<WaterSourceData>(), ComponentType.ReadOnly<Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            Entity best = Entity.Null;
            bestDistSq = 100f; // 10 m²
            NativeArray<Entity> ents = sources.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    float3 p = EntityManager.GetComponentData<Transform>(cand).m_Position;
                    float dx = p.x - x, dz = p.z - z;
                    float d = dx * dx + dz * dz;
                    if (d < bestDistSq)
                    {
                        bestDistSq = d;
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

        /// <summary>Removes the addressed source (SyncId first, else nearest within ~10 m).</summary>
        private void ApplyDelete(WaterCommand cmd)
        {
            Entity best = ResolveSource(cmd.SyncId, cmd.PosX, cmd.PosZ);
            if (best == Entity.Null)
            {
                CS2M.Log.Info($"[Water] SKIP delete noMatch pos=({cmd.PosX:F0},{cmd.PosZ:F0})");
                return;
            }

            WaterSync.MarkRemoteDelete(best); // our detector must not bounce this removal back
            EntityManager.AddComponent<Deleted>(best);
            CS2M.Log.Info($"[Water] APPLIED delete pos=({cmd.PosX:F0},{cmd.PosZ:F0}) entity={best.Index}");
        }
    }
}
