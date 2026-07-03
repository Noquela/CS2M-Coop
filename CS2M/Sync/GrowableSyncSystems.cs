using System;
using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     EXPERIMENTAL (disable with CS2M_GROWABLE_SYNC=0): host-authoritative growables.
    ///
    ///     The zone simulation is not deterministic, so historically each PC grew a DIFFERENT city —
    ///     the single biggest visual divergence. This flips growables to host authority: the HOST
    ///     detects every building its own sim spawns (Created + Building, no Applied/Owner — player
    ///     placements carry Applied and are handled by the placement detector) and syncs it like a
    ///     placement (Source=1). Level-ups converge too: the sim deletes the old synced building
    ///     (id-based delete sync, no bulldoze gate) and spawns the upgraded one.
    ///
    ///     Clients meanwhile suppress their own zone spawning (see GrowableSuppressSystem), so the
    ///     physical city is identical on every PC. Population/citizens inside stay per-PC (emergent);
    ///     "/resync" remains the deep reconciler.
    /// </summary>
    public partial class GrowableDetectorSystem : GameSystemBase
    {
        public static readonly bool Enabled_ =
            Environment.GetEnvironmentVariable("CS2M_GROWABLE_SYNC") != "0";

        private PrefabSystem _prefabSystem;
        private EntityQuery _spawned;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _spawned = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Applied>(), // player placements: handled by PlacementDetector
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<CS2M_RemotePlaced>(),
                    ComponentType.ReadOnly<CS2M_SyncId>(),
                },
            });
            RequireForUpdate(_spawned);
            CS2M.Log.Info($"[Grow] GrowableDetectorSystem created (enabled={Enabled_})");
        }

        protected override void OnUpdate()
        {
            if (!Enabled_
                || NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING
                || NetworkInterface.Instance.LocalPlayer.PlayerType != PlayerType.SERVER)
            {
                return;
            }

            NativeArray<Entity> ents = _spawned.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab,
                            out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    var tf = EntityManager.GetComponentData<Game.Objects.Transform>(e);
                    var cmd = new ObjectPlaceCommand
                    {
                        PrefabType = prefab.GetType().Name,
                        PrefabName = prefab.name,
                        PosX = tf.m_Position.x, PosY = tf.m_Position.y, PosZ = tf.m_Position.z,
                        RotX = tf.m_Rotation.value.x, RotY = tf.m_Rotation.value.y,
                        RotZ = tf.m_Rotation.value.z, RotW = tf.m_Rotation.value.w,
                        RandomSeed = EntityManager.HasComponent<PseudoRandomSeed>(e)
                            ? EntityManager.GetComponentData<PseudoRandomSeed>(e).m_Seed
                            : 0,
                        Source = 1,
                        SyncId = CS2M_SyncIdSystem.Allocate(),
                    };

                    CS2M_SyncIdSystem.Register(EntityManager, e, cmd.SyncId);
                    Command.SendToAll?.Invoke(cmd);
                    CS2M.Log.Verbose($"[Grow] DETECT+SEND spawn name={cmd.PrefabName} syncId={cmd.SyncId}");
                }
            }
            finally
            {
                ents.Dispose();
            }
        }
    }

    /// <summary>
    ///     Client side of host-authoritative growables: while connected as a CLIENT, the local zone
    ///     spawn simulation is disabled (the host's spawns arrive as synced placements instead).
    ///     Restored the moment the session ends, so single-player behaves normally.
    /// </summary>
    public partial class GrowableSuppressSystem : GameSystemBase
    {
        private Game.Simulation.ZoneSpawnSystem _zoneSpawn;
        private Game.Simulation.DestroyAbandonedSystem _destroyAbandoned;
        private bool _suppressed;

        protected override void OnCreate()
        {
            base.OnCreate();
            _zoneSpawn = World.GetOrCreateSystemManaged<Game.Simulation.ZoneSpawnSystem>();
            _destroyAbandoned = World.GetOrCreateSystemManaged<Game.Simulation.DestroyAbandonedSystem>();
            CS2M.Log.Info("[Grow] GrowableSuppressSystem created");
        }

        protected override void OnUpdate()
        {
            bool shouldSuppress = GrowableDetectorSystem.Enabled_
                                  && NetworkInterface.Instance.LocalPlayer.PlayerStatus == PlayerStatus.PLAYING
                                  && NetworkInterface.Instance.LocalPlayer.PlayerType == PlayerType.CLIENT;

            if (shouldSuppress == _suppressed)
            {
                return;
            }

            _suppressed = shouldSuppress;
            _zoneSpawn.Enabled = !shouldSuppress;
            // v50: abandoned-building teardown is the host's call too — its delete arrives as a
            // DeleteCommand (synced growables by id; native ones now sync from the host's sim).
            _destroyAbandoned.Enabled = !shouldSuppress;
            CS2M.Log.Info(shouldSuppress
                ? "[Grow] client zone spawning + abandoned teardown SUPPRESSED (host-authoritative growables)"
                : "[Grow] client zone spawning + abandoned teardown restored");
        }
    }
}
