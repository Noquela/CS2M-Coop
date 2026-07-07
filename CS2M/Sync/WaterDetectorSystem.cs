using System.Collections.Generic;
using CS2M.API.Commands;
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
    ///     Detects newly-placed water sources by tracking which <c>WaterSourceData</c> entities it has
    ///     seen: the first pass caches the baseline (existing sources in the save aren't re-sent), then
    ///     any new source is broadcast (position + parameters). Remote-created sources carry
    ///     <c>CS2M_RemotePlaced</c>, which the CREATE path excludes (echo guard).
    ///
    ///     GAP FIX (audit 07/07, gated <c>CS2M_DELFIX=1</c>): <c>CS2M_RemotePlaced</c> is never removed,
    ///     so excluding it from THIS query (the one that also feeds the <see cref="_seen"/> baseline used
    ///     to detect a vanished/deleted source) meant a remotely-created source NEVER entered <c>_seen</c>
    ///     — a LOCAL delete of it was therefore never observed and never propagated. When the gate is on,
    ///     the query drops the RemotePlaced exclusion (remote-placed sources are tracked too, so their
    ///     local deletion is caught by the existing gone-detection below); the per-entity check in
    ///     <c>OnUpdate</c> still skips them for the CREATE broadcast so they are never re-announced as
    ///     new. The existing <see cref="WaterSync"/> entity-keyed echo guard (not a component tag) already
    ///     covers the reverse direction — a source deleted BY a remote command — so no new echo risk.
    /// </summary>
    public partial class WaterDetectorSystem : GameSystemBase
    {
        // Last state BROADCAST to (or adopted from) the remotes: position + the editable params.
        private struct Known
        {
            public float3 Pos;
            public float Radius, Height, Multiplier, Polluted;
            public int ConstantDepth;
            // Issue #8: cross-PC identity shipped with every move/edit/delete (0 = save source).
            // Cached here so a VANISHED source can still be addressed by id after the entity is gone.
            public ulong SyncId;
        }

        // In-flight local edit being debounced: last observed params + how many frames they held still.
        private struct Pending
        {
            public float Radius, Height, Multiplier, Polluted;
            public int ConstantDepth;
            public int StableFrames;
        }

        // The water tool's EditSource drag writes radius/height/rate into the live entity EVERY frame
        // (decomp Tools/WaterToolSystem.cs / dossier water.md) — sending on first diff would flood one
        // command per drag frame. An edit is broadcast only after the params sit unchanged this long.
        private const int EditSettleFrames = 30; // ~0.5 s

        private EntityQuery _sources;
        // Entity → last known position, so a REMOVED source can still be addressed on the wire.
        private readonly Dictionary<Entity, Known> _seen = new Dictionary<Entity, Known>();
        private readonly Dictionary<Entity, Pending> _pending = new Dictionary<Entity, Pending>();
        private bool _baselineDone;

        private static Known Snap(float3 pos, WaterSourceData w, ulong syncId) => new Known
        {
            Pos = pos,
            Radius = w.m_Radius, Height = w.m_Height, Multiplier = w.m_Multiplier,
            Polluted = w.m_Polluted, ConstantDepth = w.m_ConstantDepth,
            SyncId = syncId,
        };

        /// <summary>The entity's cross-PC id, or 0 for a save-loaded source.</summary>
        private ulong IdOf(Entity e)
        {
            return EntityManager.HasComponent<CS2M_SyncId>(e)
                ? EntityManager.GetComponentData<CS2M_SyncId>(e).m_Id
                : 0UL;
        }

        private static bool ParamsDiffer(Known k, WaterSourceData w)
        {
            return math.abs(k.Radius - w.m_Radius) > 1e-3f
                   || math.abs(k.Height - w.m_Height) > 1e-3f
                   || math.abs(k.Multiplier - w.m_Multiplier) > 1e-3f
                   || math.abs(k.Polluted - w.m_Polluted) > 1e-3f
                   || k.ConstantDepth != w.m_ConstantDepth;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _sources = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<WaterSourceData>(), ComponentType.ReadOnly<Transform>() },
                None = DelFix.Enabled
                    ? new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() }
                    : new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<CS2M_RemotePlaced>() },
            });
            CS2M.Log.Info("[Water] WaterDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            var current = new HashSet<Entity>();
            NativeArray<Entity> ents = _sources.ToEntityArray(Allocator.Temp);
            try
            {
                if (!_baselineDone)
                {
                    foreach (Entity e in ents)
                    {
                        _seen[e] = Snap(EntityManager.GetComponentData<Transform>(e).m_Position,
                            EntityManager.GetComponentData<WaterSourceData>(e), IdOf(e));
                    }

                    _baselineDone = true;
                    return;
                }

                foreach (Entity e in ents)
                {
                    current.Add(e);
                    float3 pos = EntityManager.GetComponentData<Transform>(e).m_Position;
                    if (_seen.TryGetValue(e, out Known last))
                    {
                        WaterSourceData wm = EntityManager.GetComponentData<WaterSourceData>(e);

                        // A relocation keeps the same entity, so the create/delete paths never fire. Sync a
                        // MOVE once the source has drifted past a threshold from the last position the remotes
                        // know about (_seen is only updated on send, so a slow drag accumulates; sub-threshold
                        // tool/terrain nudges never trigger). Skip a move we just applied from the network.
                        if (math.distancesq(pos.xz, last.Pos.xz) > 4f) // > 2 m from the last-broadcast spot
                        {
                            if (WaterSync.ConsumeRemoteMove(pos, 4f))
                            {
                                last.Pos = pos; // this relocation came FROM the network — adopt, don't echo
                                _seen[e] = last;
                                continue;
                            }

                            Command.SendToAll?.Invoke(new WaterCommand
                            {
                                Move = true,
                                SyncId = last.SyncId,
                                OldX = last.Pos.x, OldZ = last.Pos.z,
                                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                                Radius = wm.m_Radius, Height = wm.m_Height, Multiplier = wm.m_Multiplier,
                                Polluted = wm.m_Polluted, ConstantDepth = wm.m_ConstantDepth,
                            });
                            CS2M.Log.Info($"[Water] DETECT+SEND move ({last.Pos.x:F0},{last.Pos.z:F0})->({pos.x:F0},{pos.z:F0})");
                            // Only the POSITION is adopted: ApplyMove ignores the param fields, so a
                            // simultaneous radius/height edit must stay pending for the EDIT path below.
                            last.Pos = pos;
                            _seen[e] = last;
                        }

                        // v59: in-place param edit (EditSource drag) — same entity, same spot, new
                        // radius/height/rate. Debounced: broadcast only after the values settle.
                        if (ParamsDiffer(last, wm))
                        {
                            if (_pending.TryGetValue(e, out Pending p)
                                && math.abs(p.Radius - wm.m_Radius) < 1e-3f
                                && math.abs(p.Height - wm.m_Height) < 1e-3f
                                && math.abs(p.Multiplier - wm.m_Multiplier) < 1e-3f
                                && math.abs(p.Polluted - wm.m_Polluted) < 1e-3f
                                && p.ConstantDepth == wm.m_ConstantDepth)
                            {
                                if (++p.StableFrames < EditSettleFrames)
                                {
                                    _pending[e] = p;
                                    continue;
                                }

                                _pending.Remove(e);
                                _seen[e] = Snap(pos, wm, last.SyncId); // remotes get (or already have) these params
                                if (WaterSync.ConsumeRemoteEdit(pos, 4f))
                                {
                                    continue; // this edit came FROM the network — adopt, don't echo
                                }

                                Command.SendToAll?.Invoke(new WaterCommand
                                {
                                    Edit = true,
                                    SyncId = last.SyncId,
                                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                                    Radius = wm.m_Radius, Height = wm.m_Height, Multiplier = wm.m_Multiplier,
                                    Polluted = wm.m_Polluted, ConstantDepth = wm.m_ConstantDepth,
                                });
                                CS2M.Log.Info($"[Water] DETECT+SEND edit pos=({pos.x:F0},{pos.z:F0}) " +
                                              $"r={wm.m_Radius:F1} h={wm.m_Height:F1} m={wm.m_Multiplier:F2}");
                            }
                            else
                            {
                                _pending[e] = new Pending
                                {
                                    Radius = wm.m_Radius, Height = wm.m_Height, Multiplier = wm.m_Multiplier,
                                    Polluted = wm.m_Polluted, ConstantDepth = wm.m_ConstantDepth,
                                    StableFrames = 0,
                                };
                            }
                        }
                        else
                        {
                            _pending.Remove(e);
                        }

                        continue;
                    }

                    // DELFIX: a remote-placed source can now reach here (query no longer excludes it) —
                    // track it silently so its later local deletion is observed, but never announce it as
                    // a fresh LOCAL creation (it already exists on every other PC).
                    if (EntityManager.HasComponent<CS2M_RemotePlaced>(e))
                    {
                        // Issue #8: the remote apply already registered the sender's id — adopt it.
                        _seen[e] = Snap(pos, EntityManager.GetComponentData<WaterSourceData>(e), IdOf(e));
                        continue;
                    }

                    WaterSourceData w = EntityManager.GetComponentData<WaterSourceData>(e);
                    // Issue #8: mint the cross-PC identity at creation — every later move/edit/delete
                    // names the source directly instead of guessing by proximity.
                    ulong syncId = CS2M_SyncIdSystem.Allocate();
                    CS2M_SyncIdSystem.Register(EntityManager, e, syncId);
                    _seen[e] = Snap(pos, w, syncId);
                    Command.SendToAll?.Invoke(new WaterCommand
                    {
                        SyncId = syncId,
                        PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                        Radius = w.m_Radius, Height = w.m_Height, Multiplier = w.m_Multiplier,
                        Polluted = w.m_Polluted, ConstantDepth = w.m_ConstantDepth,
                    });
                    CS2M.Log.Info($"[Water] DETECT+SEND pos=({pos.x:F0},{pos.z:F0}) r={w.m_Radius} id={syncId}");
                }
            }
            finally
            {
                ents.Dispose();
            }

            // v50: sources that VANISHED since the last scan → sync the removal (they used to live
            // forever on the other PCs, flooding "out of nowhere" — field report). Removals we
            // applied ourselves from the network are consumed silently (echo guard).
            List<Entity> gone = null;
            foreach (KeyValuePair<Entity, Known> kv in _seen)
            {
                if (!current.Contains(kv.Key))
                {
                    (gone ?? (gone = new List<Entity>())).Add(kv.Key);
                }
            }

            if (gone == null)
            {
                return;
            }

            foreach (Entity e in gone)
            {
                float3 pos = _seen[e].Pos;
                ulong goneId = _seen[e].SyncId;
                _seen.Remove(e);
                _pending.Remove(e);
                if (WaterSync.ConsumeRemoteDelete(e))
                {
                    continue; // this removal came FROM the network — don't bounce it back
                }

                Command.SendToAll?.Invoke(new WaterCommand
                {
                    SyncId = goneId,
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    Delete = true,
                });
                CS2M.Log.Info($"[Water] DETECT+SEND delete pos=({pos.x:F0},{pos.z:F0})");
            }
        }
    }

    /// <summary>Echo bookkeeping for synced water-source removals AND moves.</summary>
    public static class WaterSync
    {
        private static readonly HashSet<Entity> RemoteDeletes = new HashSet<Entity>();
        // Positions a remote MOVE just repositioned a source to; the detector consumes the nearest match
        // so it doesn't bounce the relocation back (the moved source has no CS2M_RemotePlaced to exclude).
        private static readonly List<float3> RemoteMoves = new List<float3>();
        // Positions a remote EDIT just rewrote params at — same echo problem, same position-keyed answer.
        private static readonly List<float3> RemoteEdits = new List<float3>();
        private static readonly object Lock = new object();

        public static void MarkRemoteDelete(Entity e)
        {
            lock (Lock) { RemoteDeletes.Add(e); }
        }

        public static bool ConsumeRemoteDelete(Entity e)
        {
            lock (Lock) { return RemoteDeletes.Remove(e); }
        }

        public static void MarkRemoteMove(float3 pos)
        {
            lock (Lock) { RemoteMoves.Add(pos); }
        }

        /// <summary>Consume the marked remote-move whose target is within <paramref name="epsSq"/> (m²) of
        /// <paramref name="pos"/>. Returns true if this move originated from the network.</summary>
        public static bool ConsumeRemoteMove(float3 pos, float epsSq)
        {
            lock (Lock)
            {
                for (int i = 0; i < RemoteMoves.Count; i++)
                {
                    if (math.distancesq(RemoteMoves[i].xz, pos.xz) <= epsSq)
                    {
                        RemoteMoves.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }
        }

        public static void MarkRemoteEdit(float3 pos)
        {
            lock (Lock) { RemoteEdits.Add(pos); }
        }

        /// <summary>Consume the marked remote-edit within <paramref name="epsSq"/> (m²) of
        /// <paramref name="pos"/>. Returns true if this param change originated from the network.</summary>
        public static bool ConsumeRemoteEdit(float3 pos, float epsSq)
        {
            lock (Lock)
            {
                for (int i = 0; i < RemoteEdits.Count; i++)
                {
                    if (math.distancesq(RemoteEdits[i].xz, pos.xz) <= epsSq)
                    {
                        RemoteEdits.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }
        }

        public static void Clear()
        {
            lock (Lock) { RemoteDeletes.Clear(); RemoteMoves.Clear(); RemoteEdits.Clear(); }
        }
    }
}
