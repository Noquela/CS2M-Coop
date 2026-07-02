using Colossal.Mathematics;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Split-cascade detection (v39). When a new road crosses an existing one, the game deletes the
    ///     original edge and creates split pieces IN THE SAME FRAME. Those pieces are *derived* events:
    ///     the receiving PC performs the exact same split on its own copy when the causal road arrives,
    ///     so re-syncing the pieces (and the intermediate delete) duplicates segments — seen in the
    ///     first v38 2-PC session. A piece is recognized as split-born when both of its endpoints lie
    ///     on a same-frame deleted curve AND it is a proper sub-segment (shorter than the original —
    ///     a full-length match is a road *replacement*, which is a real player action and must sync).
    /// </summary>
    internal static class NetSplitUtil
    {
        private const float TolSq = 2.25f; // 1.5 m in XZ

        public static bool IsSplitPiece(Bezier4x3 piece, float pieceLength, Bezier4x3 original, float originalLength)
        {
            if (originalLength <= 0f || pieceLength >= originalLength * 0.9f)
            {
                return false; // same size = replacement, not a split
            }

            return DistSqXZ(original, piece.a) < TolSq && DistSqXZ(original, piece.d) < TolSq;
        }

        /// <summary>Min squared XZ distance from <paramref name="p"/> to the curve (sampled).</summary>
        private static float DistSqXZ(Bezier4x3 curve, float3 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i <= 16; i++)
            {
                float3 c = MathUtils.Position(curve, i / 16f);
                float dx = c.x - p.x;
                float dz = c.z - p.z;
                float d = dx * dx + dz * dz;
                if (d < best)
                {
                    best = d;
                }
            }

            return best;
        }
    }
}
