extern alias ue;
using Game.Rendering;
using Unity.Mathematics;
using UColor = ue::UnityEngine.Color;

namespace CS2M.Sync
{
    /// <summary>
    ///     Draws a remote player's cursor into the overlay buffer. Kept in a plain
    ///     (non-system) class so the Unity Entities source generator does not process
    ///     it — that lets us use the <c>ue</c> extern alias to reference
    ///     <see cref="ue::UnityEngine.Color"/> without the MessagePack.UnityShims
    ///     <c>UnityEngine.Color</c> shim causing an ambiguous-type error.
    /// </summary>
    internal static class CursorOverlay
    {
        private static readonly UColor[] Palette =
        {
            new UColor(0.20f, 0.60f, 1.00f), // blue
            new UColor(1.00f, 0.45f, 0.20f), // orange
            new UColor(0.30f, 0.85f, 0.35f), // green
            new UColor(0.90f, 0.30f, 0.80f), // magenta
            new UColor(1.00f, 0.85f, 0.20f), // yellow
        };

        /// <summary>Stable per-player color (same palette as cursors — pings match the cursor).</summary>
        public static UColor ColorFor(int playerId)
        {
            return Palette[((playerId % Palette.Length) + Palette.Length) % Palette.Length];
        }

        public static void DrawCursor(OverlayRenderSystem.Buffer buffer, int playerId, float3 position)
        {
            UColor color = Palette[((playerId % Palette.Length) + Palette.Length) % Palette.Length];
            UColor faint = new UColor(color.r, color.g, color.b, 0.35f);

            // A solid inner dot and a faint outer ring so it reads at any zoom level.
            buffer.DrawCircle(color, position, 12f);
            buffer.DrawCircle(faint, position, 28f);
        }
    }
}
