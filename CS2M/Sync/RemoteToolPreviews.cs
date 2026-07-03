using System;
using System.Collections.Generic;
using Colossal.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Thread-safe store of each remote player's live tool-preview curve (keyed by network
    ///     <c>SenderId</c>). Written by <c>ToolPreviewHandler</c> when a preview packet arrives and
    ///     read by <c>ToolPreviewSystem</c> when drawing overlays each frame. Mirrors
    ///     <see cref="RemotePlayerCursors"/>.
    /// </summary>
    public static class RemoteToolPreviews
    {
        public struct PreviewState
        {
            public Bezier4x3 Curve;
            public bool Active;
            public string Username;
            public DateTime LastUtc;
        }

        private static readonly Dictionary<int, PreviewState> Previews = new Dictionary<int, PreviewState>();
        private static readonly object Lock = new object();

        public static void Update(int playerId, bool active, Bezier4x3 curve, string username)
        {
            lock (Lock)
            {
                Previews.TryGetValue(playerId, out PreviewState s);
                s.Active = active;
                if (active)
                {
                    s.Curve = curve;
                }

                s.LastUtc = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(username))
                {
                    s.Username = username;
                }

                Previews[playerId] = s;
            }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                Previews.Clear();
            }
        }

        public static List<KeyValuePair<int, PreviewState>> Snapshot()
        {
            lock (Lock)
            {
                return new List<KeyValuePair<int, PreviewState>>(Previews);
            }
        }
    }
}
