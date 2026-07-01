using System.Collections.Generic;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Thread-safe registry of the latest known cursor position (and name) for
    ///     each remote player, keyed by their network player id (<c>SenderId</c>).
    ///     Written by <c>PlayerCursorHandler</c> when a cursor packet arrives and
    ///     read by <c>PlayerCursorSystem</c> when drawing overlays each frame.
    ///
    ///     The stored position is only replaced when a packet reports a VALID cursor
    ///     (the game's raycast returns a real world point). Idle packets (Valid=false)
    ///     keep the last real spot instead of blinking off.
    /// </summary>
    public static class RemotePlayerCursors
    {
        public struct CursorState
        {
            /// <summary>Last known valid world position (interpolation target).</summary>
            public float3 Target;

            /// <summary>True once at least one valid position was received.</summary>
            public bool Visible;

            /// <summary>Display name of the remote player.</summary>
            public string Username;
        }

        private static readonly Dictionary<int, CursorState> Cursors = new Dictionary<int, CursorState>();
        private static readonly object Lock = new object();

        public static void Update(int playerId, float x, float y, float z, bool valid, string username)
        {
            lock (Lock)
            {
                Cursors.TryGetValue(playerId, out CursorState state);
                if (valid)
                {
                    state.Target = new float3(x, y, z);
                    state.Visible = true;
                }

                if (!string.IsNullOrEmpty(username))
                {
                    state.Username = username;
                }

                // When invalid we keep the previous target/visibility unchanged.
                Cursors[playerId] = state;
            }
        }

        public static void Remove(int playerId)
        {
            lock (Lock)
            {
                Cursors.Remove(playerId);
            }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                Cursors.Clear();
            }
        }

        /// <summary>Returns a copy of the current cursors so callers can iterate safely.</summary>
        public static List<KeyValuePair<int, CursorState>> Snapshot()
        {
            lock (Lock)
            {
                return new List<KeyValuePair<int, CursorState>>(Cursors);
            }
        }
    }
}
