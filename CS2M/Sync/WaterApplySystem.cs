using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Objects;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Materializes a water source placed by a remote player. A water source is a plain entity, so we
    ///     create it directly with <c>WaterSourceData</c> + <c>Transform</c> (+ Created/Updated); the
    ///     game's <c>WaterSystem</c> simulates the water from it. Tagged <c>CS2M_RemotePlaced</c> so our
    ///     detector doesn't echo it back.
    /// </summary>
    public partial class WaterApplySystem : GameSystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
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
                ApplyOne(cmd);
            }
        }

        private void ApplyOne(WaterCommand cmd)
        {
            Entity e = EntityManager.CreateEntity();
            EntityManager.AddComponentData(e, new WaterSourceData
            {
                m_Radius = cmd.Radius,
                m_Height = cmd.Height,
                m_Multiplier = cmd.Multiplier,
                m_Polluted = cmd.Polluted,
                m_ConstantDepth = cmd.ConstantDepth,
            });
            EntityManager.AddComponentData(e, new Transform(new float3(cmd.PosX, cmd.PosY, cmd.PosZ), quaternion.identity));
            EntityManager.AddComponent<CS2M_RemotePlaced>(e);
            EntityManager.AddComponent<Created>(e);
            EntityManager.AddComponent<Updated>(e);

            CS2M.Log.Info($"[Water] APPLIED pos=({cmd.PosX:F0},{cmd.PosZ:F0}) r={cmd.Radius} entity={e.Index}");
        }
    }
}
