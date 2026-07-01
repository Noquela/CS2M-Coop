using System.Reflection;
using Colossal.IO.AssetDatabase;
using CS2M.Commands;
using CS2M.Commands.ApiServer;
using CS2M.Mods;
using CS2M.Networking;
using CS2M.Settings;
using CS2M.Sync;
using CS2M.UI;
using Game;
using Game.Modding;
using HarmonyLib;
using LiteNetLib;

namespace CS2M
{
    /// <summary>
    ///     The base mod class for instantiation by the game.
    /// </summary>
    public class Mod : IMod
    {
        private const string HarmonyPatchID = "com.citiesskylinesmultiplayer";

        /// <summary>
        ///     The mod's default name.
        /// </summary>
        public const string Name = nameof(CS2M);

        /// <summary>
        ///     Gets the active instance reference.
        /// </summary>
        public static Mod Instance { get; private set; }

        /// <summary>
        ///     Gets the mod's active settings configuration.
        /// </summary>
        internal ModSettings Settings { get; private set; }

        /// <summary>
        ///     Called by the game when the mod is loaded.
        /// </summary>
        /// <param name="updateSystem">Game update system.</param>
        public void OnLoad(UpdateSystem updateSystem)
        {
            // Set instance reference.
            Instance = this;
            Log.Info($"Loading {Name} version {Assembly.GetExecutingAssembly().GetName().Version}");

            // Register mod settings to game options UI.
            Log.Info("Loading Mod Settings");
            Settings = new ModSettings(this);
            Settings.RegisterInOptionsUI();

            // Load saved settings.
            AssetDatabase.global.LoadSettings(Name, Settings, new ModSettings(this));
            Settings.OnSetLoggingLevel(Settings.LoggingLevel);
            Log.Info("Configured and initialised mod settings");

            CommandInternal.Instance = new CommandInternal();
            ApiCommand.Instance = new ApiCommand();

            NetDebug.Logger = new NetLogWrapper();

            ModSupport.Instance.Init();

            // Patch methods
            var harmony = new Harmony(HarmonyPatchID);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Set up systems
            updateSystem.UpdateBefore<NetworkingSystem>(SystemUpdatePhase.PreSimulation);
            updateSystem.UpdateAt<UISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<PlayerCursorSystem>(SystemUpdatePhase.Rendering);

            // Object placement sync (buildings/props/trees placed with the Object/Line tool).
            // Detector runs just before ModificationEnd (where Applied is visible, matching
            // Anarchy's AnarchyPlopSystem). The remote-apply system runs at Modification5,
            // AFTER GenerateObjectsSystem (Modification1), so each injected definition is
            // consumed exactly once before we destroy it (no duplicate objects).
            updateSystem.UpdateBefore<PlacementDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<RemotePlacementApplySystem>(SystemUpdatePhase.Modification5);
            Log.Info("Loading complete");
        }

        public void OnDispose()
        {
            new Harmony(HarmonyPatchID).UnpatchAll(HarmonyPatchID);

            ModSupport.Instance.DestroyConnections();

            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
