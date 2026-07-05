using System.Collections.Generic;
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
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>Thread-safe queue for incoming host fire events.</summary>
    public static class FireSync
    {
        private static readonly Queue<FireSyncCommand> Queue = new Queue<FireSyncCommand>();
        private static readonly object Lock = new object();

        /// <summary>CS2M_FIRE_SYNC=0 turns the whole feature off (both sides).</summary>
        public static bool Enabled =>
            System.Environment.GetEnvironmentVariable("CS2M_FIRE_SYNC") != "0";

        public static void Enqueue(FireSyncCommand cmd)
        {
            lock (Lock) { Queue.Enqueue(cmd); }
        }

        public static bool TryDequeue(out FireSyncCommand cmd)
        {
            lock (Lock)
            {
                if (Queue.Count > 0)
                {
                    cmd = Queue.Dequeue();
                    return true;
                }

                cmd = null;
                return false;
            }
        }

        public static void Clear()
        {
            lock (Lock) { Queue.Clear(); }
        }
    }

    /// <summary>
    ///     HOST: watches the simulation for objects catching fire / being put out / collapsing and
    ///     broadcasts each transition. Snapshot-diff (OnFire is added/removed on existing entities,
    ///     so there is no Created tag to key on). First Destroyed scan is a silent baseline — saves
    ///     can come with old rubble.
    /// </summary>
    public partial class FireDetectorSystem : GameSystemBase
    {
        private const int ScanEveryNFrames = 30;

        private struct FireIdentity
        {
            public ulong SyncId;
            public string PrefabName;
            public float3 Position;
        }

        private PrefabSystem _prefabSystem;
        private EntityQuery _onFire;
        private EntityQuery _destroyed;
        private int _frame;

        private readonly Dictionary<Entity, FireIdentity> _burning = new Dictionary<Entity, FireIdentity>();
        private readonly HashSet<Entity> _destroyedKnown = new HashSet<Entity>();
        private bool _destroyedBaselineBuilt;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _onFire = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Events.OnFire>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _destroyed = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Destroyed>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    // Top-level only: the receiver's Destroy event cascades sub-objects itself,
                    // and terraforming kills whole parks one flower pot at a time otherwise.
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            CS2M.Log.Info("[Fire] FireDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            LocalPlayer local = NetworkInterface.Instance.LocalPlayer;
            if (local.PlayerStatus != PlayerStatus.PLAYING || local.PlayerType != PlayerType.SERVER
                || !FireSync.Enabled)
            {
                return;
            }

            if (++_frame < ScanEveryNFrames)
            {
                return;
            }

            _frame = 0;

            try
            {
                ScanFires();
                ScanDestroyed();
            }
            catch (System.Exception ex)
            {
                CS2M.Log.Info($"[Guard] fire detector failed: {ex.Message}");
            }
        }

        private void ScanFires()
        {
            var current = new HashSet<Entity>();
            NativeArray<Entity> ents = _onFire.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    current.Add(e);
                    if (_burning.ContainsKey(e))
                    {
                        continue;
                    }

                    if (!TryIdentify(e, out FireIdentity id))
                    {
                        continue;
                    }

                    _burning[e] = id;
                    float intensity = EntityManager.GetComponentData<Game.Events.OnFire>(e).m_Intensity;
                    Send(0, id, intensity);
                    CS2M.Log.Info($"[Fire] DETECT start prefab={id.PrefabName} pos=({id.Position.x:F0},{id.Position.z:F0}) syncId={id.SyncId}");
                }
            }
            finally
            {
                ents.Dispose();
            }

            // Fires that vanished since the last scan → extinguished (or the entity is gone).
            List<Entity> ended = null;
            foreach (KeyValuePair<Entity, FireIdentity> kv in _burning)
            {
                if (!current.Contains(kv.Key))
                {
                    (ended ?? (ended = new List<Entity>())).Add(kv.Key);
                }
            }

            if (ended == null)
            {
                return;
            }

            foreach (Entity e in ended)
            {
                FireIdentity id = _burning[e];
                _burning.Remove(e);

                // A COLLAPSE removes OnFire AND destroys the entity together — indistinguishable from a plain
                // extinguish if we only look at "OnFire gone". Using the identity cached at ignition (when the
                // entity was healthy), send Kind 2 (collapse) when the building is now Destroyed/gone so the
                // receiver DESTROYS it — else a building that burns DOWN on one PC survives on every other
                // (a persistent buildings-count divergence the radar flags; reproduced in the 2-sim). This
                // is why ScanDestroyed missed it: its TryIdentify fails on the torn-down entity (no
                // PrefabRef/Transform), so no Kind 2 was ever sent — the cached identity fixes that.
                bool collapsed = !EntityManager.Exists(e) || EntityManager.HasComponent<Destroyed>(e);
                Send(collapsed ? (byte) 2 : (byte) 1, id, 0f);
                CS2M.Log.Info($"[Fire] DETECT {(collapsed ? "collapse" : "end")} prefab={id.PrefabName} " +
                              $"pos=({id.Position.x:F0},{id.Position.z:F0})");
            }
        }

        private void ScanDestroyed()
        {
            bool baseline = !_destroyedBaselineBuilt;
            NativeArray<Entity> ents = _destroyed.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    if (!_destroyedKnown.Add(e) || baseline)
                    {
                        continue;
                    }

                    if (!TryIdentify(e, out FireIdentity id))
                    {
                        continue;
                    }

                    Send(2, id, 0f);
                    CS2M.Log.Info($"[Fire] DETECT destroyed prefab={id.PrefabName} pos=({id.Position.x:F0},{id.Position.z:F0})");
                }
            }
            finally
            {
                ents.Dispose();
            }

            _destroyedBaselineBuilt = true;
        }

        private bool TryIdentify(Entity e, out FireIdentity id)
        {
            id = default;
            PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(e);
            if (!_prefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) || prefab == null)
            {
                return false;
            }

            id.PrefabName = prefab.name;
            id.Position = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
            id.SyncId = EntityManager.HasComponent<CS2M_SyncId>(e)
                ? EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id
                : 0;
            return true;
        }

        private static void Send(byte kind, FireIdentity id, float intensity)
        {
            Command.SendToAll?.Invoke(new FireSyncCommand
            {
                Kind = kind,
                TargetSyncId = id.SyncId,
                PrefabName = id.PrefabName,
                PosX = id.Position.x,
                PosY = id.Position.y,
                PosZ = id.Position.z,
                Intensity = intensity,
            });
        }
    }

    /// <summary>
    ///     CLIENT: mirrors the host's fire transitions. Start/end replicate the vanilla
    ///     IgniteSystem / FireSimulationSystem component changes; collapse injects a real
    ///     <c>Destroy</c> event so the local DestroySystem does the full vanilla teardown
    ///     (rubble surface, consumer removal, sub-object handling…). Runs before Modification1.
    /// </summary>
    public partial class FireApplySystem : GameSystemBase
    {
        // Destroy events injected last frame were consumed by DestroySystem — clean them up so
        // the teardown never runs twice on the same target.
        private readonly List<Entity> _pendingEvents = new List<Entity>();

        protected override void OnUpdate()
        {
            for (int i = 0; i < _pendingEvents.Count; i++)
            {
                if (EntityManager.Exists(_pendingEvents[i]))
                {
                    EntityManager.DestroyEntity(_pendingEvents[i]);
                }
            }

            _pendingEvents.Clear();

            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (FireSync.TryDequeue(out FireSyncCommand cmd))
            {
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] fire apply failed: {ex.Message}"); }
            }
        }

        private void ApplyOne(FireSyncCommand cmd)
        {
            Entity target = Resolve(cmd);
            if (target == Entity.Null)
            {
                CS2M.Log.Info($"[Fire] SKIP unresolved kind={cmd.Kind} prefab={cmd.PrefabName} pos=({cmd.PosX:F0},{cmd.PosZ:F0})");
                return;
            }

            switch (cmd.Kind)
            {
                case 0: // started — replicate IgniteSystem's add, backed by a REAL fire event
                    if (!EntityManager.HasComponent<Game.Events.OnFire>(target))
                    {
                        // A null m_Event makes any live FireSimulation kill the fire in <1 s (no
                        // FireData to evolve intensity) — create the same event entity the vanilla
                        // FireHazardSystem.CreateFireEvent makes, so the fire behaves normally
                        // wherever the sim runs (host during /validate; clients keep sim off).
                        Entity fireEvent = CreateFireEvent(target);
                        _liveFireEvents[target] = fireEvent;
                        EntityManager.AddComponentData(target,
                            new Game.Events.OnFire(fireEvent, cmd.Intensity));
                        MarkBatchesUpdated(target);
                    }

                    break;

                case 1: // extinguished — replicate FireSimulationSystem's remove
                    if (EntityManager.HasComponent<Game.Events.OnFire>(target))
                    {
                        EntityManager.RemoveComponent<Game.Events.OnFire>(target);
                        MarkBatchesUpdated(target);
                    }

                    ReleaseFireEvent(target);
                    break;

                case 2: // collapsed — one real Destroy event; DestroySystem does the vanilla teardown
                    if (EntityManager.HasComponent<Game.Events.OnFire>(target))
                    {
                        EntityManager.RemoveComponent<Game.Events.OnFire>(target);
                    }

                    ReleaseFireEvent(target);

                    if (!EntityManager.HasComponent<Destroyed>(target))
                    {
                        Entity ev = EntityManager.CreateEntity();
                        EntityManager.AddComponent<Game.Common.Event>(ev);
                        EntityManager.AddComponentData(ev, new Game.Objects.Destroy(target, Entity.Null));
                        _pendingEvents.Add(ev);
                    }

                    break;
            }

            CS2M.Log.Info($"[Fire] APPLIED kind={cmd.Kind} prefab={cmd.PrefabName} entity={target.Index}");
        }

        // One live fire-event entity per burning target (mirrors FireHazardSystem.CreateFireEvent);
        // released when the host reports end/collapse.
        private readonly Dictionary<Entity, Entity> _liveFireEvents = new Dictionary<Entity, Entity>();

        /// <summary>Creates the same event entity vanilla makes for a fire: the first fire-event
        /// prefab's archetype + PrefabRef + a TargetElement pointing at the burning object.</summary>
        private Entity CreateFireEvent(Entity target)
        {
            EntityQuery firePrefabs = GetEntityQuery(
                ComponentType.ReadOnly<Game.Prefabs.FireData>(),
                ComponentType.ReadOnly<Game.Prefabs.EventData>());
            NativeArray<Entity> prefabs = firePrefabs.ToEntityArray(Allocator.Temp);
            try
            {
                if (prefabs.Length == 0)
                {
                    return Entity.Null;
                }

                Entity eventPrefab = prefabs[0];
                Game.Prefabs.EventData eventData =
                    EntityManager.GetComponentData<Game.Prefabs.EventData>(eventPrefab);
                if (!eventData.m_Archetype.Valid)
                {
                    return Entity.Null;
                }

                Entity ev = EntityManager.CreateEntity();
                EntityManager.SetArchetype(ev, eventData.m_Archetype);
                if (EntityManager.HasComponent<PrefabRef>(ev))
                {
                    EntityManager.SetComponentData(ev, new PrefabRef(eventPrefab));
                }
                else
                {
                    EntityManager.AddComponentData(ev, new PrefabRef(eventPrefab));
                }

                DynamicBuffer<Game.Events.TargetElement> targets =
                    EntityManager.HasBuffer<Game.Events.TargetElement>(ev)
                        ? EntityManager.GetBuffer<Game.Events.TargetElement>(ev)
                        : EntityManager.AddBuffer<Game.Events.TargetElement>(ev);
                targets.Add(new Game.Events.TargetElement(target));
                return ev;
            }
            finally
            {
                prefabs.Dispose();
            }
        }

        private void ReleaseFireEvent(Entity target)
        {
            if (_liveFireEvents.TryGetValue(target, out Entity ev))
            {
                _liveFireEvents.Remove(target);
                if (ev != Entity.Null && EntityManager.Exists(ev) && !EntityManager.HasComponent<Deleted>(ev))
                {
                    EntityManager.AddComponent<Deleted>(ev);
                }
            }
        }

        private void MarkBatchesUpdated(Entity target)
        {
            if (!EntityManager.HasComponent<BatchesUpdated>(target))
            {
                EntityManager.AddComponent<BatchesUpdated>(target);
            }

            // Visual refresh for attached service upgrades too (vanilla does the same).
            if (EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(target))
            {
                DynamicBuffer<Game.Buildings.InstalledUpgrade> ups =
                    EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(target);
                for (int i = 0; i < ups.Length; i++)
                {
                    Entity up = ups[i].m_Upgrade;
                    if (EntityManager.Exists(up) && !EntityManager.HasComponent<Game.Buildings.Building>(up)
                        && !EntityManager.HasComponent<BatchesUpdated>(up))
                    {
                        EntityManager.AddComponent<BatchesUpdated>(up);
                    }
                }
            }
        }

        private Entity Resolve(FireSyncCommand cmd)
        {
            if (cmd.TargetSyncId != 0 && CS2M_SyncIdSystem.Map.TryGetValue(cmd.TargetSyncId, out Entity byId)
                && EntityManager.Exists(byId) && !EntityManager.HasComponent<Deleted>(byId))
            {
                return byId;
            }

            // Native fallback: same prefab within ~2 m of the shipped position.
            EntityQuery candidates = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            Entity best = Entity.Null;
            float bestD = 4f;
            NativeArray<Entity> ents = candidates.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity cand in ents)
                {
                    var p = EntityManager.GetComponentData<Game.Objects.Transform>(cand).m_Position;
                    float dx = p.x - cmd.PosX;
                    float dz = p.z - cmd.PosZ;
                    float d = dx * dx + dz * dz;
                    if (d >= bestD)
                    {
                        continue;
                    }

                    PrefabRef pr = EntityManager.GetComponentData<PrefabRef>(cand);
                    if (!prefabSystem.TryGetPrefab(pr.m_Prefab, out PrefabBase prefab) || prefab == null
                        || prefab.name != cmd.PrefabName)
                    {
                        continue;
                    }

                    bestD = d;
                    best = cand;
                }
            }
            finally
            {
                ents.Dispose();
            }

            return best;
        }
    }

    /// <summary>
    ///     CLIENT: keeps the local fire simulation off while playing (the host owns fire) and
    ///     restores it outside multiplayer. Same pattern as GrowableSuppressSystem.
    /// </summary>
    public partial class FireSuppressSystem : GameSystemBase
    {
        private Game.Simulation.FireHazardSystem _hazard;
        private Game.Simulation.FireSimulationSystem _sim;
        private Game.Simulation.FireRescueDispatchSystem _dispatch;
        private Game.Simulation.WeatherDamageSystem _weatherDamage;
        private Game.Simulation.WeatherHazardSystem _weatherHazard;
        private Game.Simulation.CondemnedBuildingSystem _condemned;
        private bool _suppressed;

        protected override void OnCreate()
        {
            base.OnCreate();
            _hazard = World.GetOrCreateSystemManaged<Game.Simulation.FireHazardSystem>();
            _sim = World.GetOrCreateSystemManaged<Game.Simulation.FireSimulationSystem>();
            _dispatch = World.GetOrCreateSystemManaged<Game.Simulation.FireRescueDispatchSystem>();
            _weatherDamage = World.GetOrCreateSystemManaged<Game.Simulation.WeatherDamageSystem>();
            _weatherHazard = World.GetOrCreateSystemManaged<Game.Simulation.WeatherHazardSystem>();
            _condemned = World.GetOrCreateSystemManaged<Game.Simulation.CondemnedBuildingSystem>();
        }

        protected override void OnUpdate()
        {
            LocalPlayer local = NetworkInterface.Instance.LocalPlayer;
            bool wantSuppress = FireSync.Enabled
                                && local.PlayerStatus == PlayerStatus.PLAYING
                                && local.PlayerType == PlayerType.CLIENT;

            if (wantSuppress == _suppressed)
            {
                return;
            }

            _suppressed = wantSuppress;
            _hazard.Enabled = !wantSuppress;
            _sim.Enabled = !wantSuppress;
            _dispatch.Enabled = !wantSuppress;
            // v50 garimpo: every sim path that DAMAGES or DEMOLISHES structures on its own is the
            // host's call — lightning/weather damage and condemned-building teardown would otherwise
            // fire independently on each PC (structural drift). Host detects the resulting
            // OnFire/Destroyed/Deleted and syncs them.
            _weatherDamage.Enabled = !wantSuppress;
            _weatherHazard.Enabled = !wantSuppress;
            _condemned.Enabled = !wantSuppress;
            CS2M.Log.Info($"[Fire] local fire/weather-damage/condemned sim {(wantSuppress ? "SUPPRESSED (host owns them)" : "restored")}");
        }
    }
}
