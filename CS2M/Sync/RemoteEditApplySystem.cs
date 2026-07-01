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

        protected override void OnCreate()
        {
            base.OnCreate();
            _idSystem = World.GetOrCreateSystemManaged<CS2M_SyncIdSystem>();
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
                ApplyDelete(del);
            }

            while (RemoteEditQueue.TryMove(out MoveCommand mv))
            {
                ApplyMove(mv);
            }
        }

        private void ApplyDelete(DeleteCommand cmd)
        {
            if (!_idSystem.TryResolve(cmd.SyncId, out Entity e) || !EntityManager.Exists(e))
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
            CS2M_SyncIdSystem.Map.Remove(cmd.SyncId);
            CS2M.Log.Info($"[Del] APPLIED id={cmd.SyncId} entity={e.Index}");
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
