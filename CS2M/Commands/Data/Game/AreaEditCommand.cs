using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Edit of a building-owned work area (a farm's field, a mine's dig zone…): ships the full
    ///     polygon + the owner's identity. The receiver rewrites the matching owned area's nodes (or
    ///     creates it if missing) and the game's area systems re-triangulate the visual.
    /// </summary>
    public class AreaEditCommand : CommandBase
    {
        public ulong OwnerSyncId { get; set; }
        public string OwnerPrefabName { get; set; }
        public float OwnerX { get; set; }
        public float OwnerY { get; set; }
        public float OwnerZ { get; set; }

        public string PrefabType { get; set; }
        public string PrefabName { get; set; }

        public float[] Xs { get; set; }
        public float[] Ys { get; set; }
        public float[] Zs { get; set; }
        public float[] Els { get; set; }

        /// <summary>v46: true = bulldozed (the receiver deletes the matching area).</summary>
        public bool Delete { get; set; }

        /// <summary>v46: standalone areas (surfaces/pavement — no owner) have OwnerPrefabName null;
        /// they match by prefab + polygon center on the receiver.</summary>
        public float CenterX { get; set; }
        public float CenterZ { get; set; }

        /// <summary>
        ///     v59 IDENTITY FIX: stable id for the field's DIRECT owner (<c>Owner.m_Owner</c> — a
        ///     farm's "Agriculture Area Placeholder", or the building itself when it owns the field
        ///     directly). Minted once by the host (<see cref="CS2M.Sync.CS2M_SyncIdSystem.Allocate"/>)
        ///     the first time this owner is seen and stamped on it, then shipped on every later edit
        ///     of the SAME field so the receiver resolves it by EXACT id — never by position — after
        ///     the first message. 0 when the sender could not resolve/mint one (older build, or a
        ///     client-initiated edit — see AreaEditDetectorSystem.TryResolveAnchor).
        /// </summary>
        public ulong OwnerAnchorId { get; set; }

        /// <summary>v59: prefab name of the OwnerAnchorId entity. Used ONLY the first time the
        /// receiver sees this OwnerAnchorId (nothing registered locally yet) to find its own
        /// matching local entity: among its own objects with this prefab name that own at least one
        /// Extractor-tagged Area (a type filter, not just a name+radius guess), pick the one nearest
        /// BuildingX/Y/Z (if resolved) or OwnerX/Y/Z otherwise, then register it under OwnerAnchorId
        /// so every later edit resolves by id alone. See AreaEditApplySystem.ResolveOwnerByAnchor.</summary>
        public string OwnerAnchorPrefabName { get; set; }

        /// <summary>v59: stable SyncId of the BUILDING the sender's own structural+spatial walk
        /// (FindAnchor) found for OwnerAnchorId's entity, 0 if none. Used only as a HINT for the
        /// receiver's one-time resolve: the receiver reads the BUILDING's own LIVE Transform via this
        /// id instead of trusting a shipped snapshot, since OwnerX/Y/Z is the owner's OWN position,
        /// which can be arbitrarily far from the building once the field has been resized (the root
        /// cause of the 110 m divergence this fix addresses).</summary>
        public ulong BuildingSyncId { get; set; }

        /// <summary>v59: ordinal among Extractor-tagged entries of the OwnerAnchorId entity's own
        /// <c>Game.Areas.SubArea</c> buffer (the engine-maintained reverse index of areas it owns —
        /// see <c>Game.Serialization.SubAreaSystem</c>). Discriminates when one owner has more than
        /// one Extractor-tagged sub-area; 0 in the overwhelmingly common single-field case.</summary>
        public int SubAreaIndex { get; set; }
    }
}
