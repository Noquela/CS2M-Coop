using Colossal.Mathematics;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Replays a remote terraforming stroke by calling the game's own
    ///     <c>TerrainSystem.ApplyBrush(type, area, brush, texture)</c> on the main thread (no definition
    ///     entity, so nothing to echo). A 1×1 white texture is used as a uniform brush falloff. Because
    ///     the per-call height delta depends on frame time, this is best-effort; the on-demand resync
    ///     reconciles drift.
    /// </summary>
    public partial class TerrainApplySystem : GameSystemBase
    {
        private TerrainSystem _terrain;
        private UnityEngine.Texture _brushTex;

        protected override void OnCreate()
        {
            base.OnCreate();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            // Built-in uniform white texture as the brush falloff (avoids the ambiguous UnityEngine.Color
            // shim from MessagePack.UnityShims).
            _brushTex = UnityEngine.Texture2D.whiteTexture;
            CS2M.Log.Info("[Terrain] TerrainApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // v50 field fix ("terrain tower"): this system runs in a simulation phase, so strokes
            // pile up while the game is paused/stalled and used to ALL land on the first unpaused
            // frame (each ApplyBrush scales with frame time → a mountain in one tick). Cap the
            // work per frame and drop the oldest backlog beyond a sane window.
            int dropped = 0;
            while (RemoteTerrainQueue.Count > 30 && RemoteTerrainQueue.TryDequeue(out _))
            {
                dropped++;
            }

            if (dropped > 0)
            {
                CS2M.Log.Info($"[Terrain] dropped {dropped} stale queued strokes (pause/stall backlog)");
            }

            int applied = 0;
            while (applied < 3 && RemoteTerrainQueue.TryDequeue(out TerrainCommand cmd))
            {
                applied++;
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] apply failed in TerrainApplySystem: {ex.Message}"); }
            }
        }

        private void ApplyOne(TerrainCommand cmd)
        {
            var pos = new float3(cmd.PosX, cmd.PosY, cmd.PosZ);
            var brush = new Brush
            {
                m_Tool = Entity.Null,
                m_Position = pos,
                m_Target = pos,
                m_Start = pos,
                m_Angle = 0f,
                m_Size = cmd.Size,
                m_Strength = cmd.Strength,
                m_Opacity = 1f,
            };
            var area = new Bounds2(
                new float2(pos.x - cmd.Size, pos.z - cmd.Size),
                new float2(pos.x + cmd.Size, pos.z + cmd.Size));

            _terrain.ApplyBrush((TerraformingType) cmd.Type, area, brush, _brushTex);
            CS2M.Log.Info($"[Terrain] APPLIED type={cmd.Type} pos=({pos.x:F0},{pos.z:F0}) size={cmd.Size} str={cmd.Strength}");
        }
    }
}
