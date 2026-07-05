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

        private Game.Prefabs.PrefabSystem _prefabSystem;
        private EntityQuery _movedQuery;
        private EntityQuery _moveTemps;
        private EntityQuery _movedNativeQuery;
        private EntityQuery _movedOwnedUpgrades; // v55: installed service upgrades (Owner-bearing) being relocated
        private readonly Dictionary<Entity, float3> _lastPos = new Dictionary<Entity, float3>();

        // v48: while the move tool drags, the Temp copy points at the original via Temp.m_Original —
        // and the ORIGINAL still holds its pre-move transform. Caching it solves the "old position"
        // problem for natives (only entities in this cache are considered player-relocated).
        private readonly Dictionary<Entity, Game.Objects.Transform> _preMove =
            new Dictionary<Entity, Game.Objects.Transform>();
        private int _clearCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>();
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
            // Temp copies the move tool creates while dragging (point at the original entity).
            _moveTemps = GetEntityQuery(
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Game.Objects.Transform>());

            // Natives that just got Updated with a transform change (only trusted when the entity
            // is in the pre-move cache — i.e. a move-tool drag actually targeted it).
            _movedNativeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<Game.Prefabs.PrefabRef>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Static>(),
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
            });

            // v55: installed service upgrades/extensions being relocated. They carry Owner (so both the
            // id and native queries above exclude them) but no shared SyncId. Gated on the SAME _preMove
            // cache (only entities the move tool actually dragged) — that gate is the echo guard: a
            // remotely-applied move never creates a Temp, so it never enters _preMove.
            _movedOwnedUpgrades = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<Game.Prefabs.PrefabRef>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
            });

            RequireAnyForUpdate(_movedQuery, _moveTemps, _movedNativeQuery, _movedOwnedUpgrades);
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
                _preMove.Clear();
            }

            CachePreMoveOriginals();

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

            DetectNativeMoves();
            DetectOwnedUpgradeMoves();
        }

        /// <summary>v55: an installed service upgrade the move tool relocated. Only entities in the
        /// _preMove cache (populated by a real move-tool drag) are considered, which is the echo guard.
        /// Addressed by owner (SyncId else prefab+pos) + the upgrade prefab + its OLD position.</summary>
        private void DetectOwnedUpgradeMoves()
        {
            if (_movedOwnedUpgrades.IsEmptyIgnoreFilter || _preMove.Count == 0)
            {
                return;
            }

            NativeArray<Entity> ents = _movedOwnedUpgrades.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_preMove.TryGetValue(e, out Game.Objects.Transform oldTf))
                    {
                        continue; // game-driven Updated, not a player relocation
                    }

                    Game.Objects.Transform tf = EntityManager.GetComponentData<Game.Objects.Transform>(e);
                    if (math.distance(oldTf.m_Position, tf.m_Position) <= MoveEpsilon)
                    {
                        continue; // drag still in progress (or cancelled)
                    }

                    Entity prefabEnt = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(e).m_Prefab;
                    // Real service upgrades/extensions only — other owned sub-objects are derived on both PCs.
                    if (!EntityManager.HasComponent<Game.Prefabs.ServiceUpgradeData>(prefabEnt)
                        && !EntityManager.HasComponent<Game.Prefabs.BuildingExtensionData>(prefabEnt))
                    {
                        _preMove.Remove(e);
                        continue;
                    }

                    _preMove.Remove(e);
                    Entity owner = EntityManager.GetComponentData<Owner>(e).m_Owner;
                    if (!EntityManager.Exists(owner)
                        || !EntityManager.HasComponent<Game.Objects.Transform>(owner)
                        || !EntityManager.HasComponent<Game.Prefabs.PrefabRef>(owner))
                    {
                        continue;
                    }

                    if (!_prefabSystem.TryGetPrefab(prefabEnt, out Game.Prefabs.PrefabBase prefab) || prefab == null
                        || !_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(owner).m_Prefab,
                            out Game.Prefabs.PrefabBase ownerPrefab) || ownerPrefab == null)
                    {
                        continue;
                    }

                    Game.Objects.Transform ownerTf = EntityManager.GetComponentData<Game.Objects.Transform>(owner);
                    Command.SendToAll?.Invoke(new MoveCommand
                    {
                        IsOwnedUpgrade = true,
                        OwnerSyncId = EntityManager.HasComponent<CS2M_SyncId>(owner)
                            ? EntityManager.GetComponentData<CS2M_SyncId>(owner).m_Id : 0,
                        OwnerPrefabName = ownerPrefab.name,
                        OwnerX = ownerTf.m_Position.x, OwnerY = ownerTf.m_Position.y, OwnerZ = ownerTf.m_Position.z,
                        PrefabType = prefab.GetType().Name, PrefabName = prefab.name,
                        OldX = oldTf.m_Position.x, OldY = oldTf.m_Position.y, OldZ = oldTf.m_Position.z,
                        PosX = tf.m_Position.x, PosY = tf.m_Position.y, PosZ = tf.m_Position.z,
                        RotX = tf.m_Rotation.value.x, RotY = tf.m_Rotation.value.y,
                        RotZ = tf.m_Rotation.value.z, RotW = tf.m_Rotation.value.w,
                    });
                    CS2M.Log.Info($"[Move] DETECT+SEND owned-upgrade name={prefab.name} owner={ownerPrefab.name} " +
                                  $"old=({oldTf.m_Position.x:F1},{oldTf.m_Position.z:F1}) new=({tf.m_Position.x:F1},{tf.m_Position.z:F1})");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }

        /// <summary>While the move tool drags, remember each original's still-unchanged transform.</summary>
        private void CachePreMoveOriginals()
        {
            if (_moveTemps.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> temps = _moveTemps.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity t in temps)
                {
                    Entity original = EntityManager.GetComponentData<Temp>(t).m_Original;
                    if (original == Entity.Null || _preMove.ContainsKey(original)
                        || !EntityManager.HasComponent<Game.Objects.Transform>(original)
                        || EntityManager.HasComponent<CS2M_SyncId>(original))
                    {
                        continue; // synced entities use the id path; only natives need the cache
                    }

                    _preMove[original] =
                        EntityManager.GetComponentData<Game.Objects.Transform>(original);
                }
            }
            finally
            {
                temps.Dispose();
            }
        }

        /// <summary>v48: a native the move tool touched just changed position — ship old+new, stamp
        /// a fresh SyncId locally and in the command so both sides share the identity from now on.</summary>
        private void DetectNativeMoves()
        {
            if (_movedNativeQuery.IsEmptyIgnoreFilter || _preMove.Count == 0)
            {
                return;
            }

            NativeArray<Entity> ents = _movedNativeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_preMove.TryGetValue(e, out Game.Objects.Transform oldTf))
                    {
                        continue; // game-driven Updated, not a player relocation
                    }

                    Game.Objects.Transform tf = EntityManager.GetComponentData<Game.Objects.Transform>(e);
                    if (math.distance(oldTf.m_Position, tf.m_Position) <= MoveEpsilon)
                    {
                        continue; // drag still in progress (or cancelled)
                    }

                    _preMove.Remove(e);
                    if (!_prefabSystem.TryGetPrefab(
                            EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(e).m_Prefab,
                            out Game.Prefabs.PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    ulong id = CS2M_SyncIdSystem.Allocate();
                    CS2M_SyncIdSystem.Register(EntityManager, e, id);
                    _lastPos[e] = tf.m_Position; // it now has an id — feed the id-based baseline

                    Command.SendToAll?.Invoke(new MoveCommand
                    {
                        SyncId = id,
                        PosX = tf.m_Position.x, PosY = tf.m_Position.y, PosZ = tf.m_Position.z,
                        RotX = tf.m_Rotation.value.x, RotY = tf.m_Rotation.value.y,
                        RotZ = tf.m_Rotation.value.z, RotW = tf.m_Rotation.value.w,
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        OldX = oldTf.m_Position.x, OldY = oldTf.m_Position.y, OldZ = oldTf.m_Position.z,
                    });
                    CS2M.Log.Info($"[Move] DETECT+SEND native name={prefab.name} " +
                                  $"old=({oldTf.m_Position.x:F1},{oldTf.m_Position.z:F1}) new=({tf.m_Position.x:F1},{tf.m_Position.z:F1}) id={id}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }
    }
}
