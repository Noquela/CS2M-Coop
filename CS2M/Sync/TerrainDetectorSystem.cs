using CS2M.API.Commands;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Detects the local player's active terraforming brush (the tool creates <c>BrushDefinition</c>
    ///     entities while editing) and broadcasts the stroke at a throttled rate. Our apply calls
    ///     ApplyBrush directly (it does NOT create a BrushDefinition), so there's nothing to echo.
    ///     Best-effort — see <see cref="TerrainCommand"/>.
    /// </summary>
    public partial class TerrainDetectorSystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;
        private EntityQuery _brushes;
        private int _throttle;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _brushes = GetEntityQuery(ComponentType.ReadOnly<BrushDefinition>());
            RequireForUpdate(_brushes);
            CS2M.Log.Info("[Terrain] TerrainDetectorSystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            if (++_throttle < 12)
            {
                return; // ~5 Hz while editing
            }

            _throttle = 0;

            NativeArray<Entity> ents = _brushes.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in ents)
                {
                    BrushDefinition bd = EntityManager.GetComponentData<BrushDefinition>(e);
                    if (bd.m_Strength == 0f)
                    {
                        continue;
                    }

                    // v59: ship the full TerraformingData — m_Type alone made every resource/material
                    // paint arrive as a HEIGHT edit on the receiver (dossier terrain.md §6.1).
                    int type = 0;
                    int target = 0; // TerraformingTarget.Height
                    if (bd.m_Tool != Entity.Null && EntityManager.HasComponent<TerraformingData>(bd.m_Tool))
                    {
                        TerraformingData td = EntityManager.GetComponentData<TerraformingData>(bd.m_Tool);
                        type = (int) td.m_Type;
                        target = (int) td.m_Target;
                    }

                    if (target == (int) TerraformingTarget.None)
                    {
                        continue; // GenerateBrushesSystem cancels these locally too — nothing happened
                    }

                    // v59: brush prefab NAME so the receiver reproduces the falloff shape. The definition
                    // entity carries CreationDefinition.m_Prefab = the BrushPrefab (GenerateBrushesSystem).
                    string brushName = null;
                    if (EntityManager.HasComponent<CreationDefinition>(e))
                    {
                        Entity bp = EntityManager.GetComponentData<CreationDefinition>(e).m_Prefab;
                        if (bp != Entity.Null && _prefabSystem.TryGetPrefab(bp, out PrefabBase pb) && pb != null)
                        {
                            brushName = pb.name;
                        }
                    }

                    float3 pos = bd.m_Target;
                    Command.SendToAll?.Invoke(new TerrainCommand
                    {
                        Type = type, Target = target,
                        PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                        StartX = bd.m_Start.x, StartY = bd.m_Start.y, StartZ = bd.m_Start.z,
                        Angle = bd.m_Angle,
                        Size = bd.m_Size, Strength = bd.m_Strength,
                        BrushPrefab = brushName,
                    });
                    CS2M.Log.Info($"[Terrain] DETECT+SEND type={type} target={target} pos=({pos.x:F0},{pos.z:F0}) " +
                                  $"size={bd.m_Size} brush={brushName}");
                    break; // one stroke sample per tick
                }
            }
            finally
            {
                ents.Dispose();
            }
        }
    }
}
