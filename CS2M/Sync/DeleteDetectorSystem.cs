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

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects when the local player bulldozes a synced object (one carrying <c>CS2M_SyncId</c>)
    ///     and broadcasts a <see cref="DeleteCommand"/>. Only top-level objects (no <c>Owner</c>) are
    ///     sent — the game cascades sub-object deletion. Objects we deleted from a remote command carry
    ///     <c>CS2M_RemotePlaced</c> and are excluded (echo guard).
    /// </summary>
    public partial class DeleteDetectorSystem : GameSystemBase
    {
        private EntityQuery _deletedQuery;
        private readonly HashSet<ulong> _recentlySent = new HashSet<ulong>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _deletedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Static>(),
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                },
            });
            RequireForUpdate(_deletedQuery);
            CS2M.Log.Info("[Del] DeleteDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_clearCounter >= 120)
            {
                _clearCounter = 0;
                _recentlySent.Clear();
            }

            NativeArray<Entity> ents = _deletedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    ulong id = EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id;
                    if (id == 0 || !_recentlySent.Add(id))
                    {
                        continue;
                    }

                    Command.SendToAll?.Invoke(new DeleteCommand { SyncId = id });
                    CS2M_SyncIdSystem.Map.Remove(id);
                    CS2M.Log.Info($"[Del] DETECT+SEND id={id} entity={e.Index}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }
    }
}
