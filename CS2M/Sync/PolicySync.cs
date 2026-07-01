using System.Collections.Generic;

namespace CS2M.Sync
{
    /// <summary>
    ///     Shared state for city-policy sync: a snapshot (policyName → active) used to diff the City's
    ///     Policy buffer, plus an async echo guard. Applying a remote toggle raises an event that changes
    ///     the buffer a frame or two later; the apply marks (name,active) here so the detector, when it
    ///     sees that change, consumes the mark instead of echoing it back.
    /// </summary>
    public static class PolicySync
    {
        public static readonly Dictionary<string, bool> Snapshot = new Dictionary<string, bool>();
        private static readonly Dictionary<string, int> Echo = new Dictionary<string, int>();

        public static void MarkApplied(string name, bool active)
        {
            Echo[name + "|" + active] = 40;
        }

        public static bool ConsumeEcho(string name, bool active)
        {
            return Echo.Remove(name + "|" + active);
        }

        public static void Tick()
        {
            if (Echo.Count == 0)
            {
                return;
            }

            var keys = new List<string>(Echo.Keys);
            foreach (string k in keys)
            {
                int v = Echo[k] - 1;
                if (v <= 0)
                {
                    Echo.Remove(k);
                }
                else
                {
                    Echo[k] = v;
                }
            }
        }

        public static void Clear()
        {
            Snapshot.Clear();
            Echo.Clear();
        }
    }
}
