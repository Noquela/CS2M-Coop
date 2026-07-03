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
                    if (cmd.Delete) { ApplyDelete(cmd); } else { ApplyOne(cmd); }
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
            });
            EntityManager.AddComponentData(e, new Transform(new float3(cmd.PosX, y, cmd.PosZ), quaternion.identity));
            EntityManager.AddComponent<CS2M_RemotePlaced>(e);
            EntityManager.AddComponent<Created>(e);
            EntityManager.AddComponent<Updated>(e);

            CS2M.Log.Info($"[Water] APPLIED pos=({cmd.PosX:F0},{cmd.PosZ:F0}) yLocal={y:F1} (sender {cmd.PosY:F1}) r={cmd.Radius} entity={e.Index}");
        }

        /// <summary>Removes the nearest water source (any origin) within ~10 m of the address.</summary>
        private void ApplyDelete(WaterCommand cmd)
        {
            EntityQuery sources = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<WaterSourceData>(), ComponentType.ReadOnly<Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            Entity best = Entity.Null;
            float bestD = 100f; // 10 m²
            NativeArray<Entity> ents = sources.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    float3 p = EntityManager.GetComponentData<Transform>(cand).m_Position;
                    float dx = p.x - cmd.PosX, dz = p.z - cmd.PosZ;
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
