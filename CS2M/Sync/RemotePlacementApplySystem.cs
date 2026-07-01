using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Materializes objects placed by remote players.
    ///
    ///     v10+ approach ("Option B" — direct archetype instantiation): instead of
    ///     handing a definition entity to <c>GenerateObjectsSystem</c> and hoping it
    ///     consumes it before our cleanup (which failed in the first 2-PC test:
    ///     <c>COMMIT-DEF</c> logged but the object never appeared → <c>APPEARED-MISS</c>,
    ///     a frame-ordering problem), we create the real entity ourselves from the
    ///     prefab's baked <c>ObjectData.m_Archetype</c> and set the same components the
    ///     vanilla <c>GenerateObjectsSystem.CreateObject</c> sets (Transform, PrefabRef,
    ///     PseudoRandomSeed, Elevation). The archetype already contains <c>Created</c> +
    ///     <c>Updated</c>, so all downstream systems (search index, meshes, sub-objects,
    ///     zoning, effects) pick it up. This is fully synchronous — no timing/duplicate
    ///     risk — and we get the created entity immediately so we can tag it as remote.
    ///
    ///     Every step logs under <c>[Place]</c> so the in-game debug session can see
    ///     exactly what happened.
    /// </summary>
    public partial class RemotePlacementApplySystem : GameSystemBase
    {
        private PrefabSystem _prefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            CS2M.Log.Info("[Place] RemotePlacementApplySystem created (direct-archetype mode)");
        }

        protected override void OnUpdate()
        {
            if (NetworkInterface.Instance.LocalPlayer.PlayerStatus != PlayerStatus.PLAYING)
            {
                return;
            }

            while (RemotePlacementQueue.TryDequeueObject(out ObjectPlaceCommand cmd))
            {
                ApplyOne(cmd);
            }
        }

        private void ApplyOne(ObjectPlaceCommand cmd)
        {
            // 1. Resolve the prefab (machine-independent id → local prefab entity).
            var hash = new Colossal.Hash128(new uint4(cmd.Hash0, cmd.Hash1, cmd.Hash2, cmd.Hash3));
            var prefabId = new PrefabID(cmd.PrefabType, cmd.PrefabName, hash);

            if (!_prefabSystem.TryGetPrefab(prefabId, out PrefabBase prefab) || prefab == null)
            {
                CS2M.Log.Info(
                    $"[Place] RESOLVE-FAIL prefab not found type={cmd.PrefabType} name={cmd.PrefabName} " +
                    "(different mods/assets between the two PCs?)");
                return;
            }

            if (!_prefabSystem.TryGetEntity(prefab, out Entity prefabEntity))
            {
                CS2M.Log.Info($"[Place] RESOLVE-FAIL no prefab entity for name={cmd.PrefabName}");
                return;
            }

            // 2. Grab the prefab's baked archetype (already includes Created + Updated
            //    and every component this object type needs).
            if (!EntityManager.HasComponent<ObjectData>(prefabEntity))
            {
                CS2M.Log.Info($"[Place] APPLY-FAIL prefab {cmd.PrefabName} has no ObjectData/archetype (not a placeable object?)");
                return;
            }

            ObjectData objectData = EntityManager.GetComponentData<ObjectData>(prefabEntity);
            if (!objectData.m_Archetype.Valid)
            {
                CS2M.Log.Info($"[Place] APPLY-FAIL prefab {cmd.PrefabName} archetype is invalid");
                return;
            }

            var position = new float3(cmd.PosX, cmd.PosY, cmd.PosZ);
            var rotation = new quaternion(cmd.RotX, cmd.RotY, cmd.RotZ, cmd.RotW);

            // 3. Create the real object from the archetype and set its transform/identity.
            //    (CreateEntity()+SetArchetype avoids the CreateEntity(ReadOnlySpan) overload
            //    which won't compile on net472 — no ReadOnlySpan there.)
            Entity obj = EntityManager.CreateEntity();
            EntityManager.SetArchetype(obj, objectData.m_Archetype);
            SetOrAdd(obj, new Game.Objects.Transform(position, rotation));
            SetOrAdd(obj, new PrefabRef(prefabEntity));

            // Deterministic visual seed (color/mesh variation) so both PCs match.
            if (EntityManager.HasComponent<PseudoRandomSeed>(obj))
            {
                EntityManager.SetComponentData(obj, new PseudoRandomSeed((ushort) cmd.RandomSeed));
            }
            else
            {
                EntityManager.AddComponentData(obj, new PseudoRandomSeed((ushort) cmd.RandomSeed));
            }

            // Vertical offset for elevated placements (0 for ground objects).
            if (cmd.Elevation != 0f || cmd.ElevationFlags != 0)
            {
                SetOrAdd(obj, new Elevation(cmd.Elevation, (ElevationFlags) cmd.ElevationFlags));
            }

            // Echo guard: mark this as remotely-created so our detector skips it.
            EntityManager.AddComponent<CS2M_RemotePlaced>(obj);

            // The archetype should already carry these, but guarantee the post-processing
            // systems fire (spatial index, rendering, sub-object/lot generation, zoning).
            if (!EntityManager.HasComponent<Created>(obj))
            {
                EntityManager.AddComponent<Created>(obj);
            }

            if (!EntityManager.HasComponent<Updated>(obj))
            {
                EntityManager.AddComponent<Updated>(obj);
            }

            CS2M.Log.Info(
                $"[Place] APPLIED name={cmd.PrefabName} entity={obj.Index} prefabEntity={prefabEntity.Index} " +
                $"pos=({position.x:F1},{position.y:F1},{position.z:F1}) seed={cmd.RandomSeed} " +
                $"hasTransform={EntityManager.HasComponent<Game.Objects.Transform>(obj)} " +
                $"hasBuilding={EntityManager.HasComponent<Game.Buildings.Building>(obj)}");
        }

        private void SetOrAdd<T>(Entity e, T data) where T : unmanaged, IComponentData
        {
            if (EntityManager.HasComponent<T>(e))
            {
                EntityManager.SetComponentData(e, data);
            }
            else
            {
                EntityManager.AddComponentData(e, data);
            }
        }
    }
}
