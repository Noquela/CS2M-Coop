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
            public DateTime LastObjLogUtc; // throttle do radar de log de preview de objeto

            // v56: object-placement footprints (parallel arrays; empty/null = none this update).
            public float[] ObjPosX;
            public float[] ObjPosY;
            public float[] ObjPosZ;
            public float[] ObjRotY;
            public float[] ObjSizeX;
            public float[] ObjSizeZ;
        }

        private static readonly Dictionary<int, PreviewState> Previews = new Dictionary<int, PreviewState>();
        private static readonly object Lock = new object();

        public static void Update(int playerId, bool active, Bezier4x3 curve, string username)
        {
            Update(playerId, active, curve, username, null, null, null, null, null, null);
        }

        /// <summary>v56 overload: also carries the sender's object-placement footprints (may be null/empty).</summary>
        public static void Update(
            int playerId, bool active, Bezier4x3 curve, string username,
            float[] objPosX, float[] objPosY, float[] objPosZ,
            float[] objRotY, float[] objSizeX, float[] objSizeZ)
        {
            lock (Lock)
            {
                Previews.TryGetValue(playerId, out PreviewState s);
                s.Active = active;
                if (active)
                {
                    s.Curve = curve;
                }

                // Object footprints are independent of the road Active flag — a sender can be placing
                // an object with no road ghost at all, so store whatever arrived (even if empty, which
                // just means "no object ghost right now").
                // Radar de validação (throttled 1×/5s por player): prova nos logs que o preview de
                // OBJETO chegou do outro lado — o render em si só a tela valida.
                if (objPosX != null && objPosX.Length > 0
                    && (DateTime.UtcNow - s.LastObjLogUtc).TotalSeconds > 5)
                {
                    s.LastObjLogUtc = DateTime.UtcNow;
                    CS2M.Log.Info($"[Preview] OBJ recv player={playerId} n={objPosX.Length} " +
                                  $"at=({objPosX[0]:F0},{objPosZ[0]:F0}) size=({objSizeX[0]:F1}x{objSizeZ[0]:F1})");
                }

                s.ObjPosX = objPosX;
                s.ObjPosY = objPosY;
                s.ObjPosZ = objPosZ;
                s.ObjRotY = objRotY;
                s.ObjSizeX = objSizeX;
                s.ObjSizeZ = objSizeZ;

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
