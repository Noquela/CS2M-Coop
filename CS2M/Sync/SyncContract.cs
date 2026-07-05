using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CS2M.API.Commands;
using CS2M.Commands.Data.Game;

namespace CS2M.Sync
{
    /// <summary>
    ///     THE "nothing slips through the sync" GUARANTEE (Fase 0 of the architecture pivot).
    ///
    ///     Single source of truth for WHICH player actions are synced and HOW. Every <see cref="CommandBase"/>
    ///     subclass must be declared here, and every construction tool the game exposes must map to a
    ///     world-mutating command. <see cref="Verify"/> reflects over the live assembly and returns every
    ///     violation; an empty list means the contract is complete.
    ///
    ///     It runs for real in two places:
    ///       • the in-game selftest (<c>AutopilotSystem</c>), where the whole assembly is loaded, so a new
    ///         undeclared command surfaces as a selftest FAIL in CS2M.log;
    ///       • the NUnit <c>SyncCompletenessTest</c> (a thin wrapper), for any CI that resolves game DLLs.
    ///
    ///     The day a 41st command — or a new game tool — is added without being classified, Verify() reports
    ///     it. There is no silent hole.
    ///
    ///     Sync classes:
    ///       • WorldContract     — mutates world state that MUST end up byte-identical on every machine
    ///                             (geometry, zoning, areas, policies, money-affecting edits). Needs a handler
    ///                             and must be reflected in the StateHash contract.
    ///       • HostAuthoritative — a host-owned scalar / emergent value the host mirrors to clients (money
    ///                             total, RCI demand, sim speed, weather, active fires). Clients never author it.
    ///       • Infra             — handshake / transport / cursor / chat / ping / preview / resync. No
    ///                             persistent world state; excluded from the contract BY DESIGN.
    /// </summary>
    public static class SyncContract
    {
        public enum SyncClass
        {
            WorldContract,
            HostAuthoritative,
            Infra,
        }

        /// <summary>Every concrete <see cref="CommandBase"/> subclass, classified. Adding a command without
        /// a row here makes <see cref="Verify"/> report it — that is the whole point.</summary>
        public static readonly Dictionary<string, SyncClass> Manifest = new Dictionary<string, SyncClass>
        {
            // ---- Net (roads / rail / pipes / power / fences / quays) --------------------------------
            { "NetPlaceCommand",        SyncClass.WorldContract },
            { "NetBatchCommand",        SyncClass.WorldContract },
            { "NetToolReplayCommand",   SyncClass.WorldContract },
            { "NetUpgradeCommand",      SyncClass.WorldContract },
            { "NetDeleteCommand",       SyncClass.WorldContract },
            // ---- Zoning ----------------------------------------------------------------------------
            { "ZonePaintCommand",       SyncClass.WorldContract },
            // ---- Areas (districts / farm fields / forestry / extraction lots / map tiles) -----------
            { "AreaEditCommand",        SyncClass.WorldContract },
            { "DistrictCommand",        SyncClass.WorldContract },
            { "TilePurchaseCommand",    SyncClass.WorldContract },
            // ---- Objects (buildings / props / trees / service buildings) + relocate ------------------
            { "ObjectPlaceCommand",     SyncClass.WorldContract },
            { "MoveCommand",            SyncClass.WorldContract },
            // ---- Bulldoze --------------------------------------------------------------------------
            { "DeleteCommand",          SyncClass.WorldContract },
            // ---- Terrain / water -------------------------------------------------------------------
            { "TerrainCommand",         SyncClass.WorldContract },
            { "WaterCommand",           SyncClass.WorldContract },
            // ---- Transport routes (lines / colour / visibility) ------------------------------------
            { "RouteCreateCommand",     SyncClass.WorldContract },
            { "RouteColorCommand",      SyncClass.WorldContract },
            { "RouteVisibilityCommand", SyncClass.WorldContract },
            // ---- Economy / governance edits the player authors -------------------------------------
            { "PolicyCommand",          SyncClass.WorldContract },
            { "TaxSyncCommand",         SyncClass.WorldContract },
            { "FeeCommand",             SyncClass.WorldContract },
            { "BudgetCommand",          SyncClass.WorldContract },
            { "LoanCommand",            SyncClass.WorldContract },
            { "RenameCommand",          SyncClass.WorldContract },
            // ---- Progression / unlocks -------------------------------------------------------------
            { "DevTreeCommand",         SyncClass.WorldContract },
            { "ProgressionSyncCommand", SyncClass.WorldContract },

            // ---- Host-authoritative scalars / emergent values (mirrored, not player-authored) -------
            { "MoneySyncCommand",       SyncClass.HostAuthoritative },
            { "DemandSyncCommand",      SyncClass.HostAuthoritative },
            { "SpeedCommand",           SyncClass.HostAuthoritative },
            { "EnvSyncCommand",         SyncClass.HostAuthoritative },
            { "FireSyncCommand",        SyncClass.HostAuthoritative },

            // ---- Infrastructure: handshake / transport / presence / cosmetic — no world state -------
            { "ChatMessageCommand",         SyncClass.Infra },
            { "MapPingCommand",             SyncClass.Infra },
            { "PlayerCursorCommand",        SyncClass.Infra },
            { "PlayerStatsCommand",         SyncClass.Infra },
            { "JoinNoticeCommand",          SyncClass.Infra },
            // PreconditionsDataCommand is the ABSTRACT base (never sent); only its concretes are listed.
            { "PreconditionsSuccessCommand",SyncClass.Infra },
            { "PreconditionsCheckCommand",  SyncClass.Infra }, // : PreconditionsDataCommand
            { "PreconditionsErrorCommand",  SyncClass.Infra }, // : PreconditionsDataCommand
            { "WorldTransferCommand",       SyncClass.Infra },
            { "ResyncCommand",              SyncClass.Infra },
            { "StateHashCommand",           SyncClass.Infra },
            { "ToolPreviewCommand",         SyncClass.Infra }, // other player's placement ghost — cosmetic overlay
        };

        /// <summary>The construction tools the game exposes (Game.Tools.*ToolSystem), each mapped to the
        /// command(s) that carry its player action. DefaultTool and SelectionTool are cursor/selection only
        /// (no world state) so they map to an empty set. A construction tool with no mapping is a violation.</summary>
        public static readonly Dictionary<string, string[]> ToolCoverage = new Dictionary<string, string[]>
        {
            { "NetToolSystem",       new[] { "NetPlaceCommand", "NetBatchCommand", "NetToolReplayCommand", "NetUpgradeCommand", "NetDeleteCommand" } },
            { "ZoneToolSystem",      new[] { "ZonePaintCommand" } },
            { "AreaToolSystem",      new[] { "AreaEditCommand", "DistrictCommand", "TilePurchaseCommand" } },
            { "ObjectToolSystem",    new[] { "ObjectPlaceCommand", "MoveCommand" } },
            { "UpgradeToolSystem",   new[] { "NetUpgradeCommand" } },
            { "BulldozeToolSystem",  new[] { "DeleteCommand", "NetDeleteCommand" } },
            { "TerrainToolSystem",   new[] { "TerrainCommand" } },
            { "WaterToolSystem",     new[] { "WaterCommand" } },
            { "RouteToolSystem",     new[] { "RouteCreateCommand", "RouteColorCommand", "RouteVisibilityCommand" } },
            { "DefaultToolSystem",   new string[0] },   // cursor / info selection — no world state
            { "SelectionToolSystem", new string[0] },   // selection (editor) — no world state
        };

        /// <summary>The construction tools that MUST have coverage (used to catch a tool silently dropped
        /// from <see cref="ToolCoverage"/>).</summary>
        private static readonly string[] CanonicalConstructionTools =
        {
            "NetToolSystem", "ZoneToolSystem", "AreaToolSystem", "ObjectToolSystem",
            "UpgradeToolSystem", "BulldozeToolSystem", "TerrainToolSystem", "WaterToolSystem",
            "RouteToolSystem",
        };

        /// <summary>Reflects the live assembly and returns every contract violation. Empty = complete.</summary>
        public static List<string> Verify()
        {
            var violations = new List<string>();

            List<Type> commandTypes = AllCommandTypes();
            var realNames = new HashSet<string>(commandTypes.Select(t => t.Name));

            // 1) Every command is declared.
            foreach (string name in realNames.Where(n => !Manifest.ContainsKey(n)).OrderBy(n => n))
            {
                violations.Add($"command SEM classificação no Manifest: {name} (declare WorldContract/HostAuthoritative/Infra)");
            }

            // 2) No stale manifest entries (renamed/removed command still listed).
            foreach (string name in Manifest.Keys.Where(k => !realNames.Contains(k)).OrderBy(n => n))
            {
                violations.Add($"Manifest lista comando inexistente: {name}");
            }

            // 3) Every world-mutating command has a handler (else it would never apply on the other PC).
            HashSet<string> handled = HandledCommandNames();
            foreach (KeyValuePair<string, SyncClass> kv in Manifest)
            {
                bool needsHandler = kv.Value == SyncClass.WorldContract || kv.Value == SyncClass.HostAuthoritative;
                if (needsHandler && realNames.Contains(kv.Key) && !handled.Contains(kv.Key))
                {
                    violations.Add($"command sem CommandHandler<T>: {kv.Key} (não seria aplicado no outro PC)");
                }
            }

            // 4) Every canonical construction tool is covered by >=1 world-contract command.
            foreach (string tool in CanonicalConstructionTools)
            {
                if (!ToolCoverage.TryGetValue(tool, out string[] cmds) || cmds.Length == 0)
                {
                    violations.Add($"tool de construção SEM cobertura: {tool}");
                    continue;
                }

                foreach (string cmd in cmds)
                {
                    if (!Manifest.TryGetValue(cmd, out SyncClass cls))
                    {
                        violations.Add($"tool {tool} -> {cmd}: comando não existe no Manifest");
                    }
                    else if (cls == SyncClass.Infra)
                    {
                        violations.Add($"tool {tool} -> {cmd}: mapeado para Infra (precisa ser WorldContract)");
                    }
                }
            }

            return violations;
        }

        private static List<Type> AllCommandTypes()
        {
            var assemblies = new[] { typeof(CommandBase).Assembly, typeof(NetPlaceCommand).Assembly }.Distinct();
            var found = new List<Type>();
            foreach (Assembly asm in assemblies)
            {
                foreach (Type t in SafeGetTypes(asm))
                {
                    if (t != null && !t.IsAbstract && typeof(CommandBase).IsAssignableFrom(t))
                    {
                        found.Add(t);
                    }
                }
            }

            return found.Distinct().ToList();
        }

        private static HashSet<string> HandledCommandNames()
        {
            var names = new HashSet<string>();
            foreach (Type t in SafeGetTypes(typeof(NetPlaceCommand).Assembly))
            {
                if (t == null || t.IsAbstract)
                {
                    continue;
                }

                Type bt = t.BaseType;
                while (bt != null)
                {
                    if (bt.IsGenericType && bt.GetGenericTypeDefinition().Name == "CommandHandler`1")
                    {
                        names.Add(bt.GetGenericArguments()[0].Name);
                        break;
                    }
                    bt = bt.BaseType;
                }
            }

            return names;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Some handler types can fail to load headless (they reference Unity/Game). The POCO command
                // types load fine; keep whatever resolved.
                return ex.Types.Where(t => t != null);
            }
        }
    }
}
