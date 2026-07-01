using System.Collections.Generic;

namespace CS2M.Sync
{
    /// <summary>Tracks which remote players are currently joining, so <see cref="JoinPauseSystem"/> can
    /// pause the simulation while anyone is loading in.</summary>
    public static class RemoteJoinState
    {
        private static readonly object Lock = new object();
        private static readonly HashSet<string> Joining = new HashSet<string>();

        public static void Update(string username, bool joining)
        {
            lock (Lock)
            {
                if (joining)
                {
                    Joining.Add(username ?? "");
                }
                else
                {
                    Joining.Remove(username ?? "");
                }
            }
        }

        public static bool AnyJoining
        {
            get
            {
                lock (Lock)
                {
                    return Joining.Count > 0;
                }
            }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                Joining.Clear();
            }
        }
    }
}
