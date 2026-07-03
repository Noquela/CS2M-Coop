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

            // Register CS2M UI strings from C# for every supported locale, so the connect menus
            // keep their labels regardless of game language or how the .mjs was built. (The original
            // mod embedded these in its UI bundle; rebuilding the bundle dropped them.) Re-registers
            // if the locale list grows after load, and dedups so a source is only added once/locale.
            RegisterLocalization();

            CommandInternal.Instance = new CommandInternal();
            ApiCommand.Instance = new ApiCommand();

            NetDebug.Logger = new NetLogWrapper();

            ModSupport.Instance.Init();

            // v50: "saved the game" notice — any player's successful save is announced to everyone
            // via the regular chat command (relayed by the host, so all 3+ players see it).
            Game.SceneFlow.GameManager.instance.onGameSaveLoad += (saveName, previewUri, start, success) =>
            {
                try
                {
                    if (start || !success)
                    {
                        return;
                    }

                    var local = Networking.NetworkInterface.Instance.LocalPlayer;
                    if (local.PlayerStatus != CS2M.API.Networking.PlayerStatus.PLAYING)
                    {
                        return;
                    }

                    string user = string.IsNullOrEmpty(local.Username) ? "Player" : local.Username;
                    CommandInternal.Instance.SendToAll(new Commands.Data.Internal.ChatMessageCommand
                    {
                        Username = Name,
                        Message = $"💾 {user} saved the game ({saveName})",
                    });
                    API.Chat.Instance?.PrintGameMessage($"💾 saved the game ({saveName})");
                    Log.Info($"[Save] announced save '{saveName}'");
                }
                catch (System.Exception ex)
                {
                    Log.Info($"[Guard] save notice failed: {ex.Message}");
                }
            };

            // Patch methods
            var harmony = new Harmony(HarmonyPatchID);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Set up systems
            updateSystem.UpdateBefore<NetworkingSystem>(SystemUpdatePhase.PreSimulation);
            updateSystem.UpdateAt<UISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<PlayerCursorSystem>(SystemUpdatePhase.Rendering);
            // v50: host broadcasts the player roster (names + latency) ~1 Hz for the player panel.
            updateSystem.UpdateAt<PlayerStatsSenderSystem>(SystemUpdatePhase.Rendering);
            // v51: host-authoritative RCI demand bars (clients suppress local demand sim + mirror).
            updateSystem.UpdateAt<DemandSyncSystem>(SystemUpdatePhase.Rendering);

            // Object placement sync (buildings/props/trees placed with the Object/Line tool).
            // Detector runs just before ModificationEnd (where Applied is visible, matching
            // Anarchy's AnarchyPlopSystem). The remote-apply system runs at Modification5,
            // AFTER GenerateObjectsSystem (Modification1), so each injected definition is
            // consumed exactly once before we destroy it (no duplicate objects).
            // Cross-PC entity id service (must exist before apply/detect systems resolve ids).
            updateSystem.UpdateAt<CS2M_SyncIdSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateBefore<PlacementDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            // v38: BEFORE Modification1 (was Modification5). Created/Updated must survive into the
            // consumers — SubObjectSystem@Mod2B spawns sub-objects, and the sub-net definitions we
            // inject are consumed by GenerateNodes/Edges@Mod1/2 — all within this same frame.
            updateSystem.UpdateBefore<RemotePlacementApplySystem>(SystemUpdatePhase.Modification1);

            // Money sync (host broadcasts authoritative cash; clients snap to it).
            updateSystem.UpdateBefore<MoneySyncSenderSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<MoneySyncApplySystem>(SystemUpdatePhase.Modification5);

            // Tax-rate sync (any player changes taxes -> diff-broadcast the whole array).
            updateSystem.UpdateBefore<TaxDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<TaxApplySystem>(SystemUpdatePhase.Modification5);

            // City-policy sync (toggle a policy -> raise the same Modify event on the other PCs).
            // Apply runs at Modification3 — BEFORE Game.Policies.ModifiedSystem (Modification4) which
            // consumes the Event+Modify entity — so the toggle is processed the same frame.
            updateSystem.UpdateBefore<PolicyDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<PolicyApplySystem>(SystemUpdatePhase.Modification3);

            // Service-budget sync (funding sliders — SetServiceBudget by service prefab name).
            updateSystem.UpdateBefore<BudgetDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<BudgetApplySystem>(SystemUpdatePhase.Modification5);

            // District sync (paint a district area — boundary polygon via AreaData archetype).
            updateSystem.UpdateBefore<DistrictDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            // v41: BEFORE Modification1 so the area consumers (triangulation/labels) see Created/Updated.
            updateSystem.UpdateBefore<DistrictApplySystem>(SystemUpdatePhase.Modification1);

            // Water-source sync (WaterSourceData entities — the game's WaterSystem simulates from them).
            updateSystem.UpdateBefore<WaterDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<WaterApplySystem>(SystemUpdatePhase.Modification5);

            // Terraforming sync (best-effort brush replay via TerrainSystem.ApplyBrush).
            updateSystem.UpdateBefore<TerrainDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<TerrainApplySystem>(SystemUpdatePhase.Modification5);

            // Delete/move sync of synced objects (by CS2M_SyncId).
            updateSystem.UpdateBefore<DeleteDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateBefore<MoveDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            // v41: BEFORE Modification1 (was Modification5). Deleted/Updated added at Mod5 were never
            // seen by the cleanup consumers (References/SubObjects/etc. run Mod2B-4) but CleanUp still
            // destroyed the entity at end of frame -> dangling references -> delayed NATIVE crashes.
            updateSystem.UpdateBefore<RemoteEditApplySystem>(SystemUpdatePhase.Modification1);

            // Net sync (roads/rails/pipes/power/fences — one pipeline).
            updateSystem.UpdateBefore<NetDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            // v38: BEFORE Modification1 (was Modification5 — a dead zone: every net consumer had
            // already run and CleanUpSystem stripped Updated the same frame, leaving hollow edges
            // with no geometry/mesh/zone blocks). The injected Permanent definitions are consumed by
            // GenerateNodesSystem@Mod1/GenerateEdgesSystem@Mod2 and the full pipeline runs this frame.
            updateSystem.UpdateBefore<NetPlaceApplySystem>(SystemUpdatePhase.Modification1);

            // Net bulldoze + upgrade sync (delete / composition flags, edges addressed by position).
            updateSystem.UpdateBefore<NetEditDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateBefore<NetUpgradeDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            // v41 CRASH FIX: BEFORE Modification1 (was Modification5). A Deleted edge marked at Mod5
            // was destroyed at end of frame WITHOUT References/Lane/Block/search-tree cleanup ever
            // seeing the tag (those run Mod2B-4) -> dangling edge references -> native crash moments
            // later ("random" crashes when bulldozing roads). Same phase lesson as edge creation.
            updateSystem.UpdateBefore<NetEditApplySystem>(SystemUpdatePhase.Modification1);

            // Progression sync (host broadcasts XP; clients advance milestones from it).
            updateSystem.UpdateBefore<ProgressionSenderSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<ProgressionApplySystem>(SystemUpdatePhase.Modification5);

            // Dev-tree purchase sync ("skill tree" — Unlock event by node prefab name).
            updateSystem.UpdateBefore<DevTreeDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<DevTreeApplySystem>(SystemUpdatePhase.Modification5);

            // Zoning sync (paint/dezone by ZonePrefab id over a world rect).
            updateSystem.UpdateBefore<ZoneDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<ZonePaintApplySystem>(SystemUpdatePhase.Modification5);

            // Pause the sim for everyone while a player is joining (+ chat notice).
            updateSystem.UpdateAt<JoinPauseSystem>(SystemUpdatePhase.Rendering);

            // Continuous sim-speed sync (host-authoritative) so both PCs tick in lockstep count.
            // Rendering phase so it runs (and can un-pause) even while the sim is paused.
            updateSystem.UpdateAt<SpeedSyncSenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<SpeedSyncApplySystem>(SystemUpdatePhase.Rendering);

            // Environment sync (weather overrides + shared clock) — host-authoritative scalars.
            updateSystem.UpdateAt<EnvSyncSenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<EnvSyncApplySystem>(SystemUpdatePhase.Rendering);

            // World-fingerprint drift detector (host broadcasts counts; clients suggest /resync).
            updateSystem.UpdateAt<StateHashSenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<StateHashApplySystem>(SystemUpdatePhase.Rendering);

            // Map-tile purchase sync (owned-tile diff; unlock mirrors MapTilePurchaseSystem.UnlockTile).
            updateSystem.UpdateBefore<TileDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<TileApplySystem>(SystemUpdatePhase.Modification5);

            // City-loan sync (take/repay mirrors on every PC; host reconciles the money delta).
            updateSystem.UpdateBefore<LoanDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<LoanApplySystem>(SystemUpdatePhase.Modification5);

            // Rename sync (buildings + districts, via the game's own NameSystem).
            updateSystem.UpdateBefore<RenameDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<RenameApplySystem>(SystemUpdatePhase.Modification5);

            // Work-area edit sync (farm fields, mine dig zones — building-owned Areas).
            updateSystem.UpdateBefore<AreaEditDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateBefore<AreaEditApplySystem>(SystemUpdatePhase.Modification1);

            // Transport-line sync (create/re-route/color; delete rides DeleteCommand, schedule /
            // out-of-service / vehicle count / ticket price ride the policy sync, names ride the
            // rename sync). Apply CREATES entities → must run before Modification1 (creation law).
            updateSystem.UpdateBefore<RouteDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateBefore<RouteApplySystem>(SystemUpdatePhase.Modification1);
            // v50 NOTE: do NOT add UpdateBefore<RemotePlacementApplySystem, RouteApplySystem> here —
            // the two-type overload REGISTERS THE SYSTEM A SECOND TIME (anchored on the other), so it
            // updated twice per frame and its second run destroyed the same frame's injected sub-net/
            // sub-area definitions before the consumers saw them (v50 selftest crash). The stop-before-
            // line ordering is handled inside RouteApplySystem instead (1-frame defer on unresolved
            // SyncId connections).

            // EXPERIMENTAL: host-authoritative growables (CS2M_GROWABLE_SYNC=0 disables).
            // Host detects sim spawns before ModificationEnd; clients suppress their zone spawning.
            updateSystem.UpdateBefore<GrowableDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<GrowableSuppressSystem>(SystemUpdatePhase.Rendering);

            // v50: host-authoritative fires (CS2M_FIRE_SYNC=0 disables). Host detects OnFire /
            // Destroyed transitions; clients suppress local fire sim and mirror the events.
            // Apply runs before Modification1 (collapse injects a Destroy event the same frame).
            updateSystem.UpdateBefore<FireDetectorSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateBefore<FireApplySystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAt<FireSuppressSystem>(SystemUpdatePhase.Rendering);

            // v51: structural health watchdog — logs [Invariant] VIOLATION the moment duplicated
            // edges / orphaned children / dead attachments appear (field diagnosis in real time).
            updateSystem.UpdateAt<InvariantCheckSystem>(SystemUpdatePhase.Rendering);

            // Headless self-test driver. Completely inert unless CS2M_AUTOPILOT is set, so the
            // normal build is unaffected. Runs at UIUpdate (same group as UISystem) so the client
            // half can auto-connect from the main menu; the host half auto-hosts + runs a scripted
            // placement test once a client joins. See AutopilotSystem + tools/autotest.
            updateSystem.UpdateAt<AutopilotSystem>(SystemUpdatePhase.UIUpdate);
            Log.Info("Loading complete");
        }

        private static readonly LocaleSource LocaleSourceInstance = new LocaleSource();
        private static readonly System.Collections.Generic.HashSet<string> AddedLocales =
            new System.Collections.Generic.HashSet<string>();
        private bool _localeHooked;

        private void RegisterLocalization()
        {
            try
            {
                Colossal.Localization.LocalizationManager loc =
                    Game.SceneFlow.GameManager.instance.localizationManager;
                AddLocaleSources(loc);
                if (!_localeHooked)
                {
                    _localeHooked = true;
                    loc.onSupportedLocalesChanged += () => AddLocaleSources(loc);
                }
            }
            catch (System.Exception e)
            {
                Log.Info($"[Loc] failed to register localization: {e.Message}");
            }
        }

        private static void AddLocaleSources(Colossal.Localization.LocalizationManager loc)
        {
            int added = 0;
            foreach (string locale in loc.GetSupportedLocales())
            {
                if (AddedLocales.Add(locale))
                {
                    loc.AddSource(locale, LocaleSourceInstance);
                    added++;
                }
            }

            if (added > 0)
            {
                Log.Info($"[Loc] registered CS2M UI localization for {added} new locale(s)");
            }
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
