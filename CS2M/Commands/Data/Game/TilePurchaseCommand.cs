using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Map-tile purchase (expanding the buildable area). Tiles are a fixed per-map grid, so each
    ///     purchased tile is addressed by its area center position; the receiver unlocks the matching
    ///     tile the same way the game does (remove <c>Native</c> + <c>Updated</c>). Without this, one
    ///     player builds on land the others don't own. (Cost mirroring is v2 — the game computes tile
    ///     cost from feature analysis at selection time, which the detector can't reconstruct.)
    /// </summary>
    public class TilePurchaseCommand : CommandBase
    {
        public float[] Xs { get; set; }
        public float[] Zs { get; set; }
    }
}
