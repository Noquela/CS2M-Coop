using System.Text;
using CS2M.API.Networking;
using CS2M.Commands.Data.Game;
using CS2M.Networking;
using CS2M.UI;
using Game;

namespace CS2M.Sync
{
    /// <summary>
    ///     v55: always-visible player panel. Pushes the roster (self + connected peers, each with a
    ///     per-player color and latency) to the UI ~1 Hz, from the roster the host already broadcasts
    ///     (<see cref="PlayerStatsSync"/>). Colors come from the same palette the cursors use, so a
    ///     player reads the same in the panel, on their cursor and in their preview.
    /// </summary>
    public partial class PlayerPanelSystem : GameSystemBase
    {
        private const int PushEveryNFrames = 60; // ~1 Hz

        // Matches CursorOverlay.Palette / PlayerCursorSystem.PaletteHex — one color per player.
        private static readonly string[] Palette =
        {
            "#3399FF", "#FF7333", "#4DD959", "#E64DCC", "#FFD933",
        };

        private UISystem _ui;
        private int _frame;
        private string _last = "";

        protected override void OnCreate()
        {
            base.OnCreate();
            _ui = World.GetOrCreateSystemManaged<UISystem>();
            CS2M.Log.Info("[Panel] PlayerPanelSystem created");
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
                json = "[]";
            }
            else
            {
                string me = string.IsNullOrEmpty(local.Username) ? "Você" : local.Username;
                var sb = new StringBuilder(128);
                sb.Append("[{\"n\":\"").Append(Escape(me))
                  .Append(local.PlayerType == PlayerType.SERVER ? " (host)" : "")
                  .Append("\",\"p\":-1,\"c\":\"#e8eaed\"}");

                PlayerStatsCommand roster = PlayerStatsSync.Get();
                if (roster?.Names != null)
                {
                    for (int i = 0; i < roster.Names.Length; i++)
                    {
                        string name = roster.Names[i];
                        if (string.IsNullOrEmpty(name) || name == me)
                        {
                            continue;
                        }

                        int id = roster.Ids != null && i < roster.Ids.Length ? roster.Ids[i] : i;
                        int ping = roster.Pings != null && i < roster.Pings.Length ? roster.Pings[i] : 0;
                        string color = Palette[((id % Palette.Length) + Palette.Length) % Palette.Length];
                        sb.Append(",{\"n\":\"").Append(Escape(name)).Append("\",\"p\":").Append(ping)
                          .Append(",\"c\":\"").Append(color).Append("\"}");
                    }
                }

                sb.Append(']');
                json = sb.ToString();
            }

            if (json != _last)
            {
                _last = json;
                _ui?.SetPlayerPanel(json);
            }
        }

        private static string Escape(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
