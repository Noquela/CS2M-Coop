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
    ///     <c>CS2M_RemotePlaced</c>, which the query excludes (echo guard).
    /// </summary>
    public partial class WaterDetectorSystem : GameSystemBase
    {
        private EntityQuery _sources;
        // Entity → last known position, so a REMOVED source can still be addressed on the wire.
        private readonly Dictionary<Entity, float3> _seen = new Dictionary<Entity, float3>();
        private bool _baselineDone;

        protected override void OnCreate()
        {
            base.OnCreate();
            _sources = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<WaterSourceData>(), ComponentType.ReadOnly<Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<CS2M_RemotePlaced>() },
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
                        _seen[e] = EntityManager.GetComponentData<Transform>(e).m_Position;
                    }

                    _baselineDone = true;
                    return;
                }

                foreach (Entity e in ents)
                {
                    current.Add(e);
                    float3 pos = EntityManager.GetComponentData<Transform>(e).m_Position;
                    if (_seen.TryGetValue(e, out float3 last))
                    {
                        // A relocation keeps the same entity, so the create/delete paths never fire. Sync a
                        // MOVE once the source has drifted past a threshold from the last position the remotes
                        // know about (_seen is only updated on send, so a slow drag accumulates; sub-threshold
                        // tool/terrain nudges never trigger). Skip a move we just applied from the network.
                        if (math.distancesq(pos.xz, last.xz) > 4f) // > 2 m from the last-broadcast spot
                        {
                            if (WaterSync.ConsumeRemoteMove(pos, 4f))
                            {
                                _seen[e] = pos; // this relocation came FROM the network — adopt, don't echo
                                continue;
                            }

                            WaterSourceData wm = EntityManager.GetComponentData<WaterSourceData>(e);
                            Command.SendToAll?.Invoke(new WaterCommand
                            {
                                Move = true,
                                OldX = last.x, OldZ = last.z,
                                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                                Radius = wm.m_Radius, Height = wm.m_Height, Multiplier = wm.m_Multiplier,
                                Polluted = wm.m_Polluted, ConstantDepth = wm.m_ConstantDepth,
                            });
                            CS2M.Log.Info($"[Water] DETECT+SEND move ({last.x:F0},{last.z:F0})->({pos.x:F0},{pos.z:F0})");
                            _seen[e] = pos; // remotes now have it here
                        }

                        continue;
                    }

                    _seen[e] = pos;
                    WaterSourceData w = EntityManager.GetComponentData<WaterSourceData>(e);
                    Command.SendToAll?.Invoke(new WaterCommand
                    {
                        PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                        Radius = w.m_Radius, Height = w.m_Height, Multiplier = w.m_Multiplier,
                        Polluted = w.m_Polluted, ConstantDepth = w.m_ConstantDepth,
                    });
                    CS2M.Log.Info($"[Water] DETECT+SEND pos=({pos.x:F0},{pos.z:F0}) r={w.m_Radius}");
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
            foreach (KeyValuePair<Entity, float3> kv in _seen)
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
                float3 pos = _seen[e];
                _seen.Remove(e);
                if (WaterSync.ConsumeRemoteDelete(e))
                {
                    continue; // this removal came FROM the network — don't bounce it back
                }

                Command.SendToAll?.Invoke(new WaterCommand
                {
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

        public static void Clear()
        {
            lock (Lock) { RemoteDeletes.Clear(); RemoteMoves.Clear(); }
        }
    }
}
