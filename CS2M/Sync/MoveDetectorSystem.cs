using System.Collections.Generic;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Objects;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects when the local player relocates a synced object (SyncId entity gets <c>Updated</c>
    ///     with a changed <c>Transform</c>) and broadcasts a <see cref="MoveCommand"/>.
    ///     Restricted to <c>CS2M_SyncId</c> entities to avoid the flood of game-driven <c>Updated</c>s,
    ///     and gated on an actual position change vs. a cached baseline. Remote-applied moves carry
    ///     <c>CS2M_RemotePlaced</c> → excluded (echo guard). <c>None=[Created]</c> avoids re-sending a
    ///     brand-new placement as a move.
    ///
    ///     Known v1 limitation: the very first relocation right after placement may be swallowed while
    ///     the baseline is cached; subsequent moves sync.
    /// </summary>
    public partial class MoveDetectorSystem : GameSystemBase
    {
        private const float MoveEpsilon = 0.1f;

        private EntityQuery _movedQuery;
        private readonly Dictionary<Entity, float3> _lastPos = new Dictionary<Entity, float3>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _movedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Static>(),
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });
            RequireForUpdate(_movedQuery);
            CS2M.Log.Info("[Move] MoveDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // Periodically forget stale cache entries (entities get destroyed/rebuilt).
            if (++_clearCounter >= 600)
            {
                _clearCounter = 0;
                _lastPos.Clear();
            }

            NativeArray<Entity> ents = _movedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    Game.Objects.Transform tf = EntityManager.GetComponentData<Game.Objects.Transform>(e);
                    ulong id = EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id;
                    if (id == 0)
                    {
                        continue;
                    }

                    if (!_lastPos.TryGetValue(e, out float3 prev))
                    {
                        _lastPos[e] = tf.m_Position; // first sight: cache baseline, don't send
                        continue;
                    }

                    if (math.distance(prev, tf.m_Position) <= MoveEpsilon)
                    {
                        continue;
                    }

                    _lastPos[e] = tf.m_Position;
                    Command.SendToAll?.Invoke(new MoveCommand
                    {
                        SyncId = id,
                        PosX = tf.m_Position.x,
                        PosY = tf.m_Position.y,
                        PosZ = tf.m_Position.z,
                        RotX = tf.m_Rotation.value.x,
                        RotY = tf.m_Rotation.value.y,
                        RotZ = tf.m_Rotation.value.z,
                        RotW = tf.m_Rotation.value.w,
                    });
                    CS2M.Log.Info($"[Move] DETECT+SEND id={id} pos=({tf.m_Position.x:F1},{tf.m_Position.y:F1},{tf.m_Position.z:F1})");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }
    }
}
