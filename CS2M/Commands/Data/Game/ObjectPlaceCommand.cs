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
    }
}
