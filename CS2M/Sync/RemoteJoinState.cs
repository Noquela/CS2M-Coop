using System.Collections.Generic;

namespace CS2M.Sync
{
    /// <summary>Tracks which remote players are currently joining, so <see cref="JoinPauseSystem"/> can
    /// pause the simulation while anyone is loading in.</summary>
    public static class RemoteJoinState
    {
        private static readonly object Lock = new object();
        private static readonly HashSet<string> Joining = new HashSet<string>();
        private static int _completedJoins;

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
                    // A remote that WAS joining is now PLAYING — count the completed join so the
                    // over-the-wire host roteiro starts only once the client is live (it used to gate
                    // on PlayerListJoined, which only ever holds the local host — so it never fired).
                    if (Joining.Remove(username ?? "")) { _completedJoins++; }
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

        /// <summary>How many remote clients finished joining (joining=true -&gt; false) this session.
        /// The over-the-wire host roteiro waits on this instead of PlayerListJoined (which only holds
        /// the local host — the bug that kept the roteiro from ever firing with two real sims).</summary>
        public static int CompletedJoins
        {
            get
            {
                lock (Lock)
                {
                    return _completedJoins;
                }
            }
        }

        public static void Clear()
        {
            lock (Lock)
            {
                Joining.Clear();
                _completedJoins = 0;
            }
        }
    }
}
