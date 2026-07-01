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
        private readonly HashSet<Entity> _seen = new HashSet<Entity>();
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

            NativeArray<Entity> ents = _sources.ToEntityArray(Allocator.Temp);
            try
            {
                if (!_baselineDone)
                {
                    foreach (Entity e in ents)
                    {
                        _seen.Add(e);
                    }

                    _baselineDone = true;
                    return;
                }

                foreach (Entity e in ents)
                {
                    if (!_seen.Add(e))
                    {
                        continue; // already known
                    }

                    WaterSourceData w = EntityManager.GetComponentData<WaterSourceData>(e);
                    float3 pos = EntityManager.GetComponentData<Transform>(e).m_Position;
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
        }
    }
}
