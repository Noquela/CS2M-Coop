using Colossal.Mathematics;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Areas;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Replays a remote terraforming stroke through the SAME dispatch the game's
    ///     <c>ApplyBrushesSystem.OnUpdate</c> uses (decomp Tools/ApplyBrushesSystem.cs:269-289):
    ///     Height → <c>TerrainSystem.ApplyBrush</c>; Ore/Oil/FertileLand → the NaturalResourceSystem
    ///     cell map; GroundWater → the GroundWaterSystem cell map (both via a managed port of the
    ///     private <c>ApplyCellMapBrushJob</c>, run synchronously — a few hundred cells per stroke).
    ///     No definition entity is created, so there is nothing to echo.
    ///
    ///     v59: before this, EVERY stroke — including resource painting — was applied as a HEIGHT brush
    ///     with a zero slope anchor, no rotation and a uniform square falloff (dossier terrain.md §6).
    ///     Material painting is still not replayed (its vanilla path just registers a material index —
    ///     TerrainMaterialSystem — and the actual paint flow is NOT VERIFIED in the dossier): logged as
    ///     SKIP so the gap is visible, not silent.
    ///
    ///     Because the per-call height delta depends on frame time, replay stays best-effort; the
    ///     radar's TerrainHash (v59) turns persistent divergence into a /resync suggestion.
    /// </summary>
    public partial class TerrainApplySystem : GameSystemBase
    {
        private TerrainSystem _terrain;
        private NaturalResourceSystem _resources;
        private GroundWaterSystem _groundWater;
        private PrefabSystem _prefabSystem;
        private UnityEngine.Texture _brushTex;

        protected override void OnCreate()
        {
            base.OnCreate();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            _resources = World.GetOrCreateSystemManaged<NaturalResourceSystem>();
            _groundWater = World.GetOrCreateSystemManaged<GroundWaterSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // Built-in uniform white texture as the brush falloff FALLBACK (avoids the ambiguous
            // UnityEngine.Color shim from MessagePack.UnityShims). Used only when the sender's
            // BrushPrefab can't be resolved (old sender / missing prefab).
            _brushTex = UnityEngine.Texture2D.whiteTexture;
            CS2M.Log.Info("[Terrain] TerrainApplySystem created");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            // v50 field fix ("terrain tower"): this system runs in a simulation phase, so strokes
            // pile up while the game is paused/stalled and used to ALL land on the first unpaused
            // frame (each ApplyBrush scales with frame time → a mountain in one tick). Cap the
            // work per frame and drop the oldest backlog beyond a sane window.
            int dropped = 0;
            while (RemoteTerrainQueue.Count > 30 && RemoteTerrainQueue.TryDequeue(out _))
            {
                dropped++;
            }

            if (dropped > 0)
            {
                CS2M.Log.Info($"[Terrain] dropped {dropped} stale queued strokes (pause/stall backlog)");
            }

            int applied = 0;
            while (applied < 3 && RemoteTerrainQueue.TryDequeue(out TerrainCommand cmd))
            {
                applied++;
                try { ApplyOne(cmd); } catch (System.Exception ex) { CS2M.Log.Info($"[Guard] apply failed in TerrainApplySystem: {ex.Message}"); }
            }
        }

        private void ApplyOne(TerrainCommand cmd)
        {
            var pos = new float3(cmd.PosX, cmd.PosY, cmd.PosZ);
            var start = new float3(cmd.StartX, cmd.StartY, cmd.StartZ);
            if (math.all(start == float3.zero))
            {
                start = pos; // old sender (or no anchor yet) — pre-v59 behavior
            }

            Entity brushPrefab = ResolveBrushPrefab(cmd.BrushPrefab);
            var brush = new Brush
            {
                m_Tool = Entity.Null,
                m_Position = pos,
                m_Target = pos,
                m_Start = start,
                m_Angle = cmd.Angle,
                m_Size = cmd.Size,
                m_Strength = cmd.Strength,
                m_Opacity = 1f,
            };
            Bounds2 area = ToolUtils.GetBounds(brush);

            switch ((TerraformingTarget) cmd.Target)
            {
                case TerraformingTarget.Ore:
                    ApplyResourceBrush(brush, brushPrefab, MapFeature.Ore);
                    break;
                case TerraformingTarget.Oil:
                    ApplyResourceBrush(brush, brushPrefab, MapFeature.Oil);
                    break;
                case TerraformingTarget.FertileLand:
                    ApplyResourceBrush(brush, brushPrefab, MapFeature.FertileLand);
                    break;
                case TerraformingTarget.GroundWater:
                    ApplyGroundWaterBrush(brush, brushPrefab);
                    break;
                case TerraformingTarget.Material:
                    CS2M.Log.Info("[Terrain] SKIP material paint (replay path not verified — dossier terrain.md §6.1)");
                    break;
                default: // Height (0) — also every pre-v59 sender
                    ApplyHeight((TerraformingType) cmd.Type, area, brush, brushPrefab);
                    break;
            }

            CS2M.Log.Info($"[Terrain] APPLIED type={cmd.Type} target={cmd.Target} pos=({pos.x:F0},{pos.z:F0}) " +
                          $"size={cmd.Size} str={cmd.Strength} brush={cmd.BrushPrefab}");
        }

        /// <summary>Mirrors ApplyBrushesSystem.ApplyHeight (decomp:303-315) including its guards:
        /// Level/Slope with negative strength are dropped; a negative Soften doubles as positive.</summary>
        private void ApplyHeight(TerraformingType type, Bounds2 area, Brush brush, Entity brushPrefab)
        {
            if ((type == TerraformingType.Level || type == TerraformingType.Slope) && brush.m_Strength < 0f)
            {
                return; // vanilla drops these
            }

            if (type == TerraformingType.Soften && brush.m_Strength < 0f)
            {
                brush.m_Strength = math.abs(brush.m_Strength) * 2f;
            }

            UnityEngine.Texture tex = _brushTex;
            if (brushPrefab != Entity.Null)
            {
                BrushPrefab bp = _prefabSystem.GetPrefab<BrushPrefab>(brushPrefab);
                if (bp != null && bp.m_Texture != null)
                {
                    tex = bp.m_Texture;
                }
            }

            _terrain.ApplyBrush(type, area, brush, tex);
        }

        private void ApplyResourceBrush(Brush brush, Entity brushPrefab, MapFeature feature)
        {
            CellMapData<NaturalResourceCell> data = _resources.GetData(false, out JobHandle deps);
            deps.Complete(); // synchronous main-thread write; a stroke touches a few hundred cells
            ApplyCellMapBrush(data.m_Buffer, data.m_CellSize, data.m_TextureSize, brush, brushPrefab,
                (ref NaturalResourceCell cell, float s) =>
                {
                    switch (feature)
                    {
                        case MapFeature.Ore: ApplyAmount(ref cell.m_Ore, s); break;
                        case MapFeature.Oil: ApplyAmount(ref cell.m_Oil, s); break;
                        case MapFeature.FertileLand: ApplyAmount(ref cell.m_Fertility, s); break;
                    }
                });
        }

        private void ApplyGroundWaterBrush(Brush brush, Entity brushPrefab)
        {
            CellMapData<GroundWater> data = _groundWater.GetData(false, out JobHandle deps);
            deps.Complete();
            ApplyCellMapBrush(data.m_Buffer, data.m_CellSize, data.m_TextureSize, brush, brushPrefab,
                (ref GroundWater cell, float s) =>
                {
                    // GroundWaterModifier (decomp ApplyBrushesSystem.cs:70-84)
                    float amount = cell.m_Amount * 0.0001f + s;
                    cell.m_Amount = (short) math.clamp(UnityEngine.Mathf.RoundToInt(amount * 10000f), 0, 10000);
                    cell.m_Max = cell.m_Amount;
                });
        }

        // NaturalResourcesModifier.Apply (decomp ApplyBrushesSystem.cs:56-66)
        private static void ApplyAmount(ref NaturalResourceAmount a, float s)
        {
            float amount = a.m_Base * 0.0001f + s;
            a.m_Base = (ushort) math.clamp(UnityEngine.Mathf.RoundToInt(amount * 10000f), 0, 10000);
        }

        private delegate void CellModify<TCell>(ref TCell cell, float strength) where TCell : struct;

        /// <summary>
        ///     Managed port of the private <c>ApplyBrushesSystem.ApplyCellMapBrushJob</c> (decomp
        ///     ApplyBrushesSystem.cs:87-183 + the coord setup at 318-345): rasterizes the rotated
        ///     brush-cell grid (falloff opacity) against the target cell map and applies
        ///     strength × covered-area per cell. Ported 1:1 so both machines do the same math.
        /// </summary>
        private void ApplyCellMapBrush<TCell>(Unity.Collections.NativeArray<TCell> buffer, float2 cellSize,
            int2 textureSize, Brush brush, Entity brushPrefab, CellModify<TCell> modify)
            where TCell : struct
        {
            if (brushPrefab == Entity.Null
                || !EntityManager.HasComponent<BrushData>(brushPrefab)
                || !EntityManager.HasBuffer<BrushCell>(brushPrefab))
            {
                CS2M.Log.Info("[Terrain] SKIP cell-map brush (brush prefab unresolved — old sender?)");
                return;
            }

            BrushData brushData = EntityManager.GetComponentData<BrushData>(brushPrefab);
            DynamicBuffer<BrushCell> brushCells = EntityManager.GetBuffer<BrushCell>(brushPrefab, true);
            if (math.any(brushData.m_Resolution == 0) || brushCells.Length == 0)
            {
                return;
            }

            Bounds2 bounds2 = ToolUtils.GetBounds(brush);
            float4 invCell = (1f / cellSize).xyxy;
            float4 texSizeAdd = ((float2) textureSize * 0.5f).xyxy;
            int4 coords = (int4) math.floor(new float4(bounds2.min, bounds2.max) * invCell + texSizeAdd);
            coords = math.clamp(coords, 0, textureSize.xyxy - 1);

            quaternion q = quaternion.RotateY(brush.m_Angle);
            float2 axisX = math.mul(q, new float3(1f, 0f, 0f)).xz;
            float2 axisZ = math.mul(q, new float3(0f, 0f, 1f)).xz;
            float2 brushCellSize = brush.m_Size / (float2) brushData.m_Resolution;
            float4 invBrushCell = (1f / brushCellSize).xyxy;
            float4 brushResAdd = ((float2) brushData.m_Resolution * 0.5f).xyxy;
            float strengthPerArea = brush.m_Strength / (cellSize.x * cellSize.y);

            for (int row = coords.y; row <= coords.w; row++)
            {
                Bounds2 cell = default;
                cell.min.y = (row - texSizeAdd.y) * cellSize.y - brush.m_Position.z;
                cell.max.y = cell.min.y + cellSize.y;
                for (int i = coords.x; i <= coords.z; i++)
                {
                    cell.min.x = (i - texSizeAdd.x) * cellSize.x - brush.m_Position.x;
                    cell.max.x = cell.min.x + cellSize.x;
                    var corners = new float4(cell.min, cell.max);
                    var px = new float4(math.dot(corners.xy, axisX), math.dot(corners.xw, axisX),
                        math.dot(corners.zy, axisX), math.dot(corners.zw, axisX));
                    var pz = new float4(math.dot(corners.xy, axisZ), math.dot(corners.xw, axisZ),
                        math.dot(corners.zy, axisZ), math.dot(corners.zw, axisZ));
                    int4 bc = (int4) math.floor(
                        new float4(math.cmin(px), math.cmin(pz), math.cmax(px), math.cmax(pz))
                        * invBrushCell + brushResAdd);
                    bc = math.clamp(bc, 0, brushData.m_Resolution.xyxy - 1);

                    float covered = 0f;
                    for (int j = bc.y; j <= bc.w; j++)
                    {
                        float2 rowMin = axisZ * ((j - brushResAdd.y) * brushCellSize.y);
                        float2 rowMax = axisZ * ((j + 1 - brushResAdd.y) * brushCellSize.y);
                        for (int k = bc.x; k <= bc.z; k++)
                        {
                            BrushCell bCell = brushCells[k + brushData.m_Resolution.x * j];
                            if (bCell.m_Opacity < 0.0001f)
                            {
                                continue;
                            }

                            float2 colMin = axisX * ((k - brushResAdd.x) * brushCellSize.x);
                            float2 colMax = axisX * ((k + 1 - brushResAdd.x) * brushCellSize.x);
                            var quad = new Quad2(rowMin + colMin, rowMin + colMax,
                                rowMax + colMax, rowMax + colMin);
                            if (MathUtils.Intersect(cell, quad, out float overlap))
                            {
                                covered += bCell.m_Opacity * overlap;
                            }
                        }
                    }

                    covered *= strengthPerArea;
                    if (math.abs(covered) >= 0.0001f)
                    {
                        int idx = i + textureSize.x * row;
                        TCell c = buffer[idx];
                        modify(ref c, covered);
                        buffer[idx] = c;
                    }
                }
            }
        }

        private Entity ResolveBrushPrefab(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Entity.Null;
            }

            var prefabId = new PrefabID(nameof(BrushPrefab), name, default(Colossal.Hash128));
            if (_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) && prefab != null
                && _prefabSystem.TryGetEntity(prefab, out Entity e))
            {
                return e;
            }

            CS2M.Log.Info($"[Terrain] brush prefab unresolved name={name}");
            return Entity.Null;
        }
    }
}
