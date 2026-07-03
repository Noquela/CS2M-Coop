using CS2M.API.Networking;
using CS2M.Networking;
using CS2M.UI;
using Game;

namespace CS2M.Sync
{
    /// <summary>
    ///     v55: pushes a co-op sync-health status to the on-screen badge ~1 Hz. HOST shows it is the
    ///     authority; CLIENTS show "in sync" (green) or, when the StateHash detector flags persistent
    ///     divergence, "divergência" (red) + which category — so a player TRUSTS what they see instead
    ///     of wondering. Rendering phase (works while paused); only pushes when the value changes.
    /// </summary>
    public partial class SyncStatusSystem : GameSystemBase
    {
        private const int PushEveryNFrames = 60; // ~1 Hz

        private UISystem _ui;
        private int _frame;
        private string _last = "";

        protected override void OnCreate()
        {
            base.OnCreate();
            _ui = World.GetOrCreateSystemManaged<UISystem>();
            CS2M.Log.Info("[Sync] SyncStatusSystem created");
        }

        protected override void OnUpdate()
        {
            if (++_frame < PushEveryNFrames)
            {
                return;
            }

            _frame = 0;

            LocalPlayer local = NetworkInterface.Instance.LocalPlayer;
            string json;
            if (local.PlayerStatus != PlayerStatus.PLAYING)
            {
                json = "{\"state\":\"off\"}";
            }
            else if (local.PlayerType == PlayerType.SERVER)
            {
                json = "{\"state\":\"host\",\"text\":\"Co-op ativo (host)\"}";
            }
            else
            {
                SyncHealth.Get(out bool drifting, out string info);
                json = drifting
                    ? "{\"state\":\"drift\",\"text\":\"Divergência: " + Escape(info) + " — /resync\"}"
                    : "{\"state\":\"synced\",\"text\":\"Em sync\"}";
            }

            if (json != _last)
            {
                _last = json;
                _ui?.SetSyncStatus(json);
            }
        }

        private static string Escape(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
