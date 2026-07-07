using System.Collections.Generic;
using Game;
using Game.Common;
using Unity.Collections;
using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Maps a cross-PC <see cref="CS2M_SyncId"/> to the local entity, so delete/move/upgrade
    ///     commands can name a target both PCs agree on. The dictionary is a cache; the query is the
    ///     source of truth (the game destroys+recreates entities on upgrades/node-splits), so
    ///     <see cref="TryResolve"/> self-heals by rebuilding on a miss.
    ///
    ///     Id allocation: the SENDER allocates the id and ships it inside the create command; the
    ///     receiver stamps the same id. Ids are namespaced by a per-process random nonce (NOT by
    ///     SenderId, which CS2M never assigns — it is always 0), so two players never collide.
    /// </summary>
    public partial class CS2M_SyncIdSystem : GameSystemBase
    {
        public static readonly Dictionary<ulong, Entity> Map = new Dictionary<ulong, Entity>();

        // 24-bit random per-process nonce in the high bits + a 40-bit local counter.
        // Issue #14: the random draw alone is only PROBABLY unique (System.Random seeds from tick
        // count — two processes with equal uptime-ms draw the SAME nonce). The join handshake now
        // makes it certain: the client ships its nonce in PreconditionsCheckCommand and the host
        // assigns a fresh one (OverrideNonce) if it collides with any nonce already in the session.
        private static ulong SessionNonce = (ulong) (new System.Random().Next() & 0xFFFFFF);
        private static ulong _counter;

        /// <summary>This process's id-namespace nonce (shipped in the join handshake).</summary>
        public static ulong Nonce => SessionNonce;

        /// <summary>Adopt the host-assigned nonce (issue #14). Called at join time, before any local
        /// allocation happens in the new session — already-allocated ids keep their old prefix, which
        /// is safe: the override only steers FUTURE allocations away from the collision.</summary>
        public static void OverrideNonce(ulong nonce)
        {
            nonce &= 0xFFFFFF;
            if (nonce == 0 || nonce == SessionNonce)
            {
                return;
            }

            CS2M.Log.Info($"[Id] NONCE override {SessionNonce} -> {nonce} (host-assigned, collision avoided)");
            SessionNonce = nonce;
        }

        private EntityQuery _query;

        /// <summary>Allocate a globally-unique id for a locally-initiated placement (sender side).</summary>
        public static ulong Allocate()
        {
            ulong id = (SessionNonce << 40) | (++_counter & 0xFFFFFFFFFFUL);
            return id;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _query = GetEntityQuery(
                ComponentType.ReadOnly<CS2M_SyncId>(),
                ComponentType.Exclude<Deleted>());
            CS2M.Log.Info($"[Id] CS2M_SyncIdSystem created (nonce={SessionNonce})");
        }

        protected override void OnUpdate()
        {
            // Pure service; nothing per-frame.
        }

        /// <summary>Stamp an entity with a sync id and cache the mapping. Safe to call twice.</summary>
        public static void Register(EntityManager em, Entity e, ulong id)
        {
            if (id == 0 || !em.Exists(e))
            {
                return;
            }

            if (em.HasComponent<CS2M_SyncId>(e))
            {
                em.SetComponentData(e, new CS2M_SyncId { m_Id = id });
            }
            else
            {
                em.AddComponentData(e, new CS2M_SyncId { m_Id = id });
            }

            Map[id] = e;
            CS2M.Log.Verbose($"[Id] REGISTER id={id} entity={e.Index}");
        }

        /// <summary>Resolve a sync id to the current local entity, rebuilding the cache on a miss.</summary>
        public bool TryResolve(ulong id, out Entity e)
        {
            EntityManager em = EntityManager;
            if (Map.TryGetValue(id, out e) && em.Exists(e) && !em.HasComponent<Deleted>(e))
            {
                return true;
            }

            CS2M.Log.Info($"[Id] RESOLVE-STALE id={id} — rebuilding");
            Rebuild();
            if (Map.TryGetValue(id, out e) && em.Exists(e))
            {
                return true;
            }

            CS2M.Log.Info($"[Id] RESOLVE-MISS id={id}");
            return false;
        }

        /// <summary>Re-scan every live CS2M_SyncId entity into the cache.</summary>
        public void Rebuild()
        {
            Map.Clear();
            NativeArray<Entity> ents = _query.ToEntityArray(Allocator.Temp);
            NativeArray<CS2M_SyncId> ids = _query.ToComponentDataArray<CS2M_SyncId>(Allocator.Temp);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    Map[ids[i].m_Id] = ents[i];
                }

                CS2M.Log.Info($"[Id] REBUILD count={ents.Length}");
            }
            finally
            {
                ents.Dispose();
                ids.Dispose();
            }
        }

        /// <summary>Drop the cache when a session ends.</summary>
        public static void Clear()
        {
            Map.Clear();
        }
    }
}
