using Colossal.UI.Binding;
using CS2M.API;
using CS2M.Commands;
using CS2M.Commands.Data.Internal;
using CS2M.Networking;
using Game.UI.InGame;
using System;
using System.Collections.Generic;

namespace CS2M.UI
{
    /// <summary>
    /// Displays a chat to the users screen in a pop up bubble.
    /// Allows a user to send messages to other players and view
    /// events such as server startup and player connections.
    /// </summary>
    public class ChatPanel : EntityGamePanel, IChat
    {
        public readonly struct Message : IJsonWritable
        {
            public string Timestamp { get; }
            public string User { get; }
            public string Text { get; }

            public Message(string timestamp, string user, string text)
            {
                Timestamp = timestamp;
                User = user;
                Text = text;
            }

            public void Write(IJsonWriter writer)
            {
                writer.TypeBegin(this.GetType().FullName);
                writer.PropertyName("timestamp");
                writer.Write(this.Timestamp);
                writer.PropertyName("user");
                writer.Write(this.User);
                writer.PropertyName("text");
                writer.Write(this.Text);
                writer.TypeEnd();
            }
        }

        public ValueBinding<List<Message>> ChatMessages { get; }
        public ValueBinding<string> CurrentUsername { get; }
        public ValueBinding<string> LocalChatMessage { get; }
        public ValueBinding<string> PlayerList { get; }
        public TriggerBinding SendChatMessage { get; }
        public TriggerBinding<string> SetLocalChatMessage { get; }

        public override LayoutPosition position => LayoutPosition.Right;

        /// <summary>v50: singleton access for systems/handlers (all run on the main thread).</summary>
        public static ChatPanel Instance { get; private set; }

        public ChatPanel()
        {
            Chat.Instance = this;
            Instance = this;

            ChatMessages = new ValueBinding<List<Message>>(Mod.Name, nameof(ChatMessages), new List<Message>(),
                new ListWriter<Message>(new ValueWriter<Message>()));
            PlayerList = new ValueBinding<string>(Mod.Name, nameof(PlayerList), "[]");
            CurrentUsername = new ValueBinding<string>(Mod.Name, nameof(CurrentUsername), GetCurrentUsername());
            LocalChatMessage = new ValueBinding<string>(Mod.Name, nameof(LocalChatMessage), string.Empty);
            SendChatMessage = new TriggerBinding(Mod.Name, nameof(SendChatMessage), () => SendMessage());
            SetLocalChatMessage = new TriggerBinding<string>(Mod.Name, nameof(SetLocalChatMessage),
                message => UpdateChatMessage(message));
        }

        private void UpdateChatMessage(string message)
        {
            if (message.EndsWith("\n"))
            {
                // User has pressed 'Enter' so we send the message they have input.
                SendMessage();
            }
            else
            {
                LocalChatMessage.Update(message);
            }
        }

        private void SendMessage()
        {
            string username = GetCurrentUsername();
            if (string.IsNullOrEmpty(username))
            {
                username = "Local";
            }

            // Chat command: "/resync" makes the host re-stream the world to everyone (drift safety net).
            if (LocalChatMessage.value != null && LocalChatMessage.value.Trim() == "/resync")
            {
                Networking.NetworkInterface.Instance.ResyncAll();
                PrintChatMessage("CS2M", "Resync requested (host re-sending the world).");
                LocalChatMessage.Update(string.Empty);
                return;
            }

            // Chat command: "/validate" runs the full sync self-check in the CURRENT session and
            // prints PASS/FAIL per feature here — validates a build without any relaunch/env vars.
            if (LocalChatMessage.value != null && LocalChatMessage.value.Trim() == "/validate")
            {
                Sync.AutopilotSystem.RequestChatValidation();
                LocalChatMessage.Update(string.Empty);
                return;
            }

            // v50 chat command: "/ping" marks the spot under your mouse for everyone ("look here!").
            if (LocalChatMessage.value != null && LocalChatMessage.value.Trim() == "/ping")
            {
                if (Sync.PlayerCursorSystem.LastLocalCursorValid)
                {
                    var pos = Sync.PlayerCursorSystem.LastLocalCursorPos;
                    var ping = new Commands.Data.Game.MapPingCommand
                    {
                        X = pos.x, Y = pos.y, Z = pos.z,
                        Username = username,
                    };
                    API.Commands.Command.SendToAll?.Invoke(ping);
                    Sync.MapPingSync.Add(-1, pos, username); // draw locally too (id -1 = self color slot)
                    PrintGameMessage($"📍 pinged ({pos.x:F0}, {pos.z:F0})");
                }
                else
                {
                    PrintGameMessage("Point the mouse at the map, then type /ping.");
                }

                LocalChatMessage.Update(string.Empty);
                return;
            }

            // v50 chat command: "/players" prints the roster (name + latency to host).
            if (LocalChatMessage.value != null && LocalChatMessage.value.Trim() == "/players")
            {
                var stats = Sync.PlayerStatsSync.Get();
                if (stats?.Ids == null || stats.Ids.Length == 0)
                {
                    PrintGameMessage("No roster yet (host broadcasts it every second).");
                }
                else
                {
                    for (int i = 0; i < stats.Ids.Length; i++)
                    {
                        string ms = stats.Ids[i] == 0 ? "host" : $"{stats.Pings[i]} ms";
                        PrintGameMessage($"• {stats.Names[i]} — {ms}");
                    }
                }

                LocalChatMessage.Update(string.Empty);
                return;
            }

            PrintChatMessage(username, LocalChatMessage.value);

            ChatMessageCommand message = new ChatMessageCommand()
            {
                Username = GetCurrentUsername(),
                Message = LocalChatMessage.value
            };
            CommandInternal.Instance.SendToAll(message);

            LocalChatMessage.Update(string.Empty);
        }

        private void PrintMessage(string sender, string msg)
        {
            Log.Info($"Chat message: [{sender}] - {msg}");
            ChatMessages.value.Add(new Message(DateTime.Now.ToShortTimeString(), sender, msg));
            ChatMessages.TriggerUpdate();
        }

        /// <summary>
        /// Prints a game message to the ChatPanel
        /// </summary>
        /// <param name="msg">The message.</param>
        public void PrintGameMessage(string msg)
        {
            PrintMessage(Mod.Name, msg);
        }

        /// <summary>
        /// Prints a game message of a specific type to the ChatPanel
        /// </summary>
        /// <param name="type">The message type</param>
        /// <param name="msg">The message</param>
        /// <exception cref="NotImplementedException"></exception>
        public void PrintGameMessage(Chat.MessageType type, string msg)
        {
            // TODO: Format according to type
            PrintMessage(Mod.Name, msg);
        }

        /// <summary>
        /// Prints a chat message to the ChatPanel
        /// </summary>
        /// <param name="username">The name of the sending user.</param>
        /// <param name="msg">The message.</param>
        public void PrintChatMessage(string username, string msg)
        {
            PrintMessage(username, msg);
        }

        /// <summary>
        /// Fetches the username for the current player and returns it as a string.
        /// </summary>
        /// <returns>The username of the current player</returns>
        public string GetCurrentUsername()
        {
            return NetworkInterface.Instance.LocalPlayer.Username ?? string.Empty;
        }

        public void WelcomeChatMessage()
        {
            PrintGameMessage("Welcome to Cities: Skylines 2 Multiplayer!");
        }

        // Cursor palette in hex (same order as CursorOverlay.Palette) — panel dots match cursors.
        private static readonly string[] PaletteHex =
        {
            "#3399FF", "#FF7333", "#4DD959", "#E64DCC", "#FFD933",
        };

        /// <summary>v50: pushes the latest roster (PlayerStatsSync) into the player-panel binding.
        /// Called from the stats handler / host sender — both run on the main thread.</summary>
        public static void RefreshPlayerList()
        {
            ChatPanel panel = Instance;
            var stats = Sync.PlayerStatsSync.Get();
            if (panel == null)
            {
                return;
            }

            if (stats?.Ids == null)
            {
                panel.PlayerList.Update("[]");
                return;
            }

            var sb = new System.Text.StringBuilder(128);
            sb.Append('[');
            for (int i = 0; i < stats.Ids.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                int id = stats.Ids[i];
                string color = PaletteHex[((id % PaletteHex.Length) + PaletteHex.Length) % PaletteHex.Length];
                string name = (stats.Names != null && i < stats.Names.Length ? stats.Names[i] : "?") ?? "?";
                int ping = stats.Pings != null && i < stats.Pings.Length ? stats.Pings[i] : 0;
                sb.Append("{\"n\":\"").Append(name.Replace("\\", "\\\\").Replace("\"", "\\\""))
                    .Append("\",\"p\":").Append(ping)
                    .Append(",\"h\":").Append(id == 0 ? "true" : "false")
                    .Append(",\"c\":\"").Append(color).Append("\"}");
            }

            sb.Append(']');
            panel.PlayerList.Update(sb.ToString());
        }
    }
}
