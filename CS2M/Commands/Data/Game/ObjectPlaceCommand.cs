using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when the local player places an object with the Object Tool
    ///     (service buildings, signature buildings, ploppable buildings, props,
    ///     trees, etc). Carries everything the other PC needs to recreate the exact
    ///     same object at the same spot: a machine-independent prefab identity, the
    ///     world transform, the random seed (so procedural variation/color matches)
    ///     and the elevation.
    /// </summary>
    /// <remarks>
    ///     Entity ids are per-machine and never sent. The prefab is identified by
    ///     <see cref="PrefabType"/> + <see cref="PrefabName"/> (+ hash for modded
    ///     assets), which is resolved back to a local prefab entity on the receiver.
    ///     All fields are MessagePack-friendly primitives.
    /// </remarks>
    public class ObjectPlaceCommand : CommandBase
    {
        /// <summary>Cross-PC stable id for this object (see <c>CS2M_SyncId</c>), so it can later be moved/deleted.</summary>
        public ulong SyncId { get; set; }

        // --- Prefab identity (machine-independent). ---
        /// <summary>Prefab C# class name, e.g. "BuildingPrefab" / "StaticObjectPrefab".</summary>
        public string PrefabType { get; set; }

        /// <summary>Asset name, e.g. "Police Station 01".</summary>
        public string PrefabName { get; set; }

        // Colossal.Hash128 = uint4. Zero for all base-game content (resolved by
        // type+name alone); non-zero only for modded/Paradox-Mods assets (v6).
        public uint Hash0 { get; set; }
        public uint Hash1 { get; set; }
        public uint Hash2 { get; set; }
        public uint Hash3 { get; set; }

        // --- World transform (Game.Objects.Transform). ---
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float RotW { get; set; }

        // --- Determinism + placement extras. ---
        /// <summary>CreationDefinition.m_RandomSeed → PseudoRandomSeed; keeps color/mesh variation identical.</summary>
        public int RandomSeed { get; set; }

        /// <summary>Vertical offset above the ground (0 for ground-placed objects).</summary>
        public float Elevation { get; set; }

        /// <summary>Game.Objects.ElevationFlags as a byte (0 when the object has no Elevation component).</summary>
        public byte ElevationFlags { get; set; }

        // --- v44: service-building EXTENSIONS (hospital wings etc.). ---
        // Extensions are objects OWNED by a parent building. When these fields are set the
        // receiver attaches the created object to the resolved owner; the game's
        // ServiceUpgradeSystem (reacting to Created+Owner) wires InstalledUpgrade/effects itself.
        public ulong OwnerSyncId { get; set; }
        public string OwnerPrefabName { get; set; }
        public float OwnerX { get; set; }
        public float OwnerY { get; set; }
        public float OwnerZ { get; set; }

        /// <summary>v44: 0 = player-placed; 1 = spawned by the HOST's zone simulation (growable).
        /// Sim spawns replace any overlapping older building on the receiver (level-up flow).</summary>
        public byte Source { get; set; }

        /// <summary>v55: for a tree/plant, its growth STATE + growth byte (Game.Objects.Tree). The base
        /// Object Tool's AgeMask (and the Tree Controller mod) lets a player force the age independent of
        /// RandomSeed, so a tree planted "Adult"/age-locked arrived young on the other PC. HasTree gates it
        /// (non-tree objects leave it false, so the apply never touches a Tree component that isn't there).</summary>
        public bool HasTree { get; set; }

        public byte TreeState { get; set; }
        public byte TreeGrowth { get; set; }
    }
}
