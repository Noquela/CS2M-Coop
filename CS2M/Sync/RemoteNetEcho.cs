using System.Collections.Generic;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Echo guard for nets. A remotely-applied road produces a *fresh* Edge entity, so the generic
    ///     <c>CS2M_RemotePlaced</c> tag (which is on the definition, not the produced edge) isn't enough.
    ///     The apply system marks a segment hash (order-independent, quantized endpoints + prefab) when
    ///     it injects a net; the detector skips any edge whose hash is still marked. TTL of a few frames
    ///     covers the definition→edge latency.
    /// </summary>
    public static class RemoteNetEcho
    {
        private const int TtlFrames = 20;
        private static readonly Dictionary<int, int> Ttl = new Dictionary<int, int>();

        public static int SegHash(float3 a, float3 d, string prefabName)
        {
            int ax = Quant(a.x), az = Quant(a.z), dx = Quant(d.x), dz = Quant(d.z);
            // order-independent: normalize endpoint order
            if (ax > dx || (ax == dx && az > dz))
            {
                int t;
                t = ax; ax = dx; dx = t;
                t = az; az = dz; dz = t;
            }

            int h = 17;
            h = h * 31 + ax;
            h = h * 31 + az;
            h = h * 31 + dx;
            h = h * 31 + dz;
            h = h * 31 + (prefabName != null ? prefabName.GetHashCode() : 0);
            return h;
        }

        private static int Quant(float v)
        {
            return (int) math.round(v * 2f); // 0.5 m grid
        }

        public static void Mark(int hash)
        {
            Ttl[hash] = TtlFrames;
        }

        public static bool IsRecent(int hash)
        {
            return Ttl.ContainsKey(hash);
        }

        /// <summary>Decrement all TTLs once per frame; call from the detector's OnUpdate.</summary>
        public static void Tick()
        {
            if (Ttl.Count == 0)
            {
                return;
            }

            var keys = new List<int>(Ttl.Keys);
            foreach (int k in keys)
            {
                int v = Ttl[k] - 1;
                if (v <= 0)
                {
                    Ttl.Remove(k);
                }
                else
                {
                    Ttl[k] = v;
                }
            }
        }

        public static void Clear()
        {
            Ttl.Clear();
        }
    }
}
