using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DevCommandsGuard
{
    public enum VisibilityMode
    {
        All,
        Admin,
        Disabled
    }

    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class DevCommandsGuardPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.valheim.devcommandsguard";
        public const string PluginName = "Valheim_DevCommandsGuard";
        public const string PluginVersion = "1.0.2";

        internal static DevCommandsGuardPlugin Instance = null!;
        internal static ManualLogSource Log = null!;

        // Core Server Configs
        internal static ConfigEntry<bool> RejectClientsWithDevCommands = null!;
        internal static ConfigEntry<bool> RejectClientsWithoutPlugin = null!;
        internal static ConfigEntry<string> CustomTagFormat = null!;
        internal static ConfigEntry<string> CustomTagColor = null!;

        // Highlight & Tag Visibility Configs
        internal static ConfigEntry<VisibilityMode> HighlightVisibility = null!;
        internal static ConfigEntry<VisibilityMode> TagVisibility = null!;

        // Guarded Command Toggles (Server-side enabled by default)
        internal static ConfigEntry<bool> GuardConsoleArg = null!;
        internal static ConfigEntry<bool> GuardDevCommands = null!;
        internal static ConfigEntry<bool> GuardGod = null!;
        internal static ConfigEntry<bool> GuardDebugMode = null!;
        internal static ConfigEntry<bool> GuardFly = null!;
        internal static ConfigEntry<bool> GuardSpawn = null!;
        internal static ConfigEntry<bool> GuardTp = null!;

        // Per-Event Game Chat Log Visibility (Default: Admin)
        internal static ConfigEntry<VisibilityMode> ChatLogConsoleArg = null!;
        internal static ConfigEntry<VisibilityMode> ChatLogDevCommands = null!;
        internal static ConfigEntry<VisibilityMode> ChatLogGod = null!;
        internal static ConfigEntry<VisibilityMode> ChatLogDebugMode = null!;
        internal static ConfigEntry<VisibilityMode> ChatLogFly = null!;
        internal static ConfigEntry<VisibilityMode> ChatLogSpawn = null!;
        internal static ConfigEntry<VisibilityMode> ChatLogTp = null!;

        // RPC Names
        internal const string RpcClientHandshake = "DCG_ClientHandshake";
        internal const string RpcServerSyncState = "DCG_ServerSyncState";
        internal const string RpcAdminCommand = "DCG_AdminCommand";
        internal const string RpcAdminCommandFeedback = "DCG_AdminCommandFeedback";
        internal const string RpcClientGuardEvent = "DCG_ClientGuardEvent";
        internal const string RpcServerChatLog = "DCG_ServerChatLog";

        // ZDO Keys
        internal const string ZdoDebugOrGodKey = "DCG_DebugOrGod";
        internal const string ZdoIsAdminKey = "DCG_IsAdmin";

        // Runtime Tracking
        internal static readonly HashSet<string> HandshakedPeers = new HashSet<string>();
        internal static readonly HashSet<string> RejectedPeers = new HashSet<string>();
        internal static readonly Dictionary<string, (bool HasConsoleArg, bool HasDevCommands, bool ConsoleJoinLogged)> PeerClientStates = new Dictionary<string, (bool, bool, bool)>();
        internal static readonly HashSet<string> ActivePeerViolations = new HashSet<string>();
        internal static bool LocalHasConsoleArg;
        internal static bool LocalHasUsedActionGuardCommand;

        // Client-Synchronized State
        internal static bool SynchronizedAdmin;
        internal static string ActiveTagFormat = "[DEV] {name}";
        internal static string ActiveTagColor = "red";
        internal static VisibilityMode ActiveHighlightVisibility = VisibilityMode.Admin;
        internal static VisibilityMode ActiveTagVisibility = VisibilityMode.All;
        internal static bool ActiveGuardGod = true;
        internal static bool ActiveGuardDebugMode = true;
        internal static bool ActiveGuardFly = true;
        internal static bool ActiveGuardSpawn = true;
        internal static bool ActiveGuardTp = true;

        private readonly Harmony _harmony = new Harmony(PluginGUID);

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            RejectClientsWithDevCommands = Config.Bind(
                "Server",
                "RejectClientsWithDevCommands",
                true,
                "If true, rejects connections from non-admin clients launched with -console or with devcommands enabled."
            );

            RejectClientsWithoutPlugin = Config.Bind(
                "Server",
                "RejectClientsWithoutPlugin",
                true,
                "If true, rejects connections from non-admin clients that do not have DevCommandsGuard installed."
            );

            CustomTagFormat = Config.Bind(
                "Server",
                "TagFormat",
                "[DEV] {name}",
                "Tag format for non-admin players with guarded commands enabled. Use {name} as placeholder for the player's name."
            );

            CustomTagColor = Config.Bind(
                "Server",
                "TagColor",
                "red",
                "Color of the tag name (e.g. red, #FF0000, yellow, orange) for non-admin players with guarded commands enabled."
            );

            HighlightVisibility = Config.Bind(
                "Visibility",
                "HighlightVisibility",
                VisibilityMode.Admin,
                "Who can see the 3D red character highlight on non-admin players using guarded commands (All, Admin, Disabled)."
            );

            TagVisibility = Config.Bind(
                "Visibility",
                "TagVisibility",
                VisibilityMode.All,
                "Who can see the red tag on overhead names and in chat for non-admin players using guarded commands (All, Admin, Disabled)."
            );

            GuardConsoleArg = Config.Bind("Guards", "GuardConsoleArg", true, "Track and guard -console launch argument.");
            GuardDevCommands = Config.Bind("Guards", "GuardDevCommands", true, "Track and guard devcommands toggle.");
            GuardGod = Config.Bind("Guards", "GuardGod", true, "Track and guard god mode.");
            GuardDebugMode = Config.Bind("Guards", "GuardDebugMode", true, "Track and guard debugmode.");
            GuardFly = Config.Bind("Guards", "GuardFly", true, "Track and guard fly mode.");
            GuardSpawn = Config.Bind("Guards", "GuardSpawn", true, "Track and guard spawn command.");
            GuardTp = Config.Bind("Guards", "GuardTp", true, "Track and guard tp / goto command.");

            ChatLogConsoleArg = Config.Bind("ChatLogs", "ChatLogConsoleArg", VisibilityMode.Admin, "Game chat log visibility when a user joins with -console (All, Admin, Disabled).");
            ChatLogDevCommands = Config.Bind("ChatLogs", "ChatLogDevCommands", VisibilityMode.Admin, "Game chat log visibility when a user toggles devcommands (All, Admin, Disabled).");
            ChatLogGod = Config.Bind("ChatLogs", "ChatLogGod", VisibilityMode.Admin, "Game chat log visibility when a user toggles god mode (All, Admin, Disabled).");
            ChatLogDebugMode = Config.Bind("ChatLogs", "ChatLogDebugMode", VisibilityMode.Admin, "Game chat log visibility when a user toggles debugmode (All, Admin, Disabled).");
            ChatLogFly = Config.Bind("ChatLogs", "ChatLogFly", VisibilityMode.Admin, "Game chat log visibility when a user toggles fly mode (All, Admin, Disabled).");
            ChatLogSpawn = Config.Bind("ChatLogs", "ChatLogSpawn", VisibilityMode.Admin, "Game chat log visibility when a user executes spawn (All, Admin, Disabled).");
            ChatLogTp = Config.Bind("ChatLogs", "ChatLogTp", VisibilityMode.Admin, "Game chat log visibility when a user executes tp / goto (All, Admin, Disabled).");

            SyncLocalFieldsFromConfig();

            RejectClientsWithDevCommands.SettingChanged += (_, _) => EnforceActiveConnections();
            RejectClientsWithoutPlugin.SettingChanged += (_, _) => EnforceActiveConnections();
            CustomTagFormat.SettingChanged += (_, _) => BroadcastConfigToAllClients();
            CustomTagColor.SettingChanged += (_, _) => BroadcastConfigToAllClients();
            HighlightVisibility.SettingChanged += (_, _) => BroadcastConfigToAllClients();
            TagVisibility.SettingChanged += (_, _) => BroadcastConfigToAllClients();
            GuardGod.SettingChanged += (_, _) => BroadcastConfigToAllClients();
            GuardDebugMode.SettingChanged += (_, _) => BroadcastConfigToAllClients();
            GuardFly.SettingChanged += (_, _) => BroadcastConfigToAllClients();
            GuardSpawn.SettingChanged += (_, _) => BroadcastConfigToAllClients();
            GuardTp.SettingChanged += (_, _) => BroadcastConfigToAllClients();

            LocalHasConsoleArg = HasConsoleCommandLineArg();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Local -console arg: {LocalHasConsoleArg}");

            _harmony.PatchAll();
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }

        internal static void SyncLocalFieldsFromConfig()
        {
            ActiveTagFormat = CustomTagFormat.Value;
            ActiveTagColor = CustomTagColor.Value;
            ActiveHighlightVisibility = HighlightVisibility.Value;
            ActiveTagVisibility = TagVisibility.Value;
            ActiveGuardGod = GuardGod.Value;
            ActiveGuardDebugMode = GuardDebugMode.Value;
            ActiveGuardFly = GuardFly.Value;
            ActiveGuardSpawn = GuardSpawn.Value;
            ActiveGuardTp = GuardTp.Value;
        }

        internal static bool HasConsoleCommandLineArg()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                return args.Any(a => string.Equals(a, "-console", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsPeerAdmin(ZNet znet, ZNetPeer peer)
        {
            if (znet == null || peer?.m_socket == null) return false;
            string hostName = peer.m_socket.GetHostName();
            return znet.m_adminList != null && znet.ListContainsId(znet.m_adminList, hostName);
        }

        internal static bool IsLocalAdmin()
        {
            if (ZNet.instance == null) return false;
            if (ZNet.instance.IsServer()) return true;
            return SynchronizedAdmin;
        }

        internal static bool CanLocalSee(VisibilityMode mode)
        {
            return mode switch
            {
                VisibilityMode.All => true,
                VisibilityMode.Admin => IsLocalAdmin(),
                _ => false
            };
        }

        internal static string PackSyncSettings()
        {
            return $"{(int)HighlightVisibility.Value}|{(int)TagVisibility.Value}|{(GuardGod.Value ? 1 : 0)},{(GuardDebugMode.Value ? 1 : 0)},{(GuardFly.Value ? 1 : 0)},{(GuardSpawn.Value ? 1 : 0)},{(GuardTp.Value ? 1 : 0)}";
        }

        internal static void UnpackSyncSettings(string packed)
        {
            if (string.IsNullOrEmpty(packed)) return;
            string[] sections = packed.Split('|');
            if (sections.Length >= 3)
            {
                if (int.TryParse(sections[0], out int hVis)) ActiveHighlightVisibility = (VisibilityMode)hVis;
                if (int.TryParse(sections[1], out int tVis)) ActiveTagVisibility = (VisibilityMode)tVis;

                string[] parts = sections[2].Split(',');
                if (parts.Length >= 5)
                {
                    ActiveGuardGod = parts[0] == "1";
                    ActiveGuardDebugMode = parts[1] == "1";
                    ActiveGuardFly = parts[2] == "1";
                    ActiveGuardSpawn = parts[3] == "1";
                    ActiveGuardTp = parts[4] == "1";
                }
            }
        }

        internal static void BroadcastConfigToAllClients()
        {
            SyncLocalFieldsFromConfig();

            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            string syncSettings = PackSyncSettings();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.IsReady())
                {
                    bool isAdmin = IsPeerAdmin(ZNet.instance, peer);
                    peer.m_rpc.Invoke(
                        RpcServerSyncState,
                        isAdmin,
                        CustomTagFormat.Value,
                        CustomTagColor.Value,
                        syncSettings
                    );
                }
            }
        }

        internal static bool IsGuardEnabledForEvent(string eventType)
        {
            return eventType.ToLowerInvariant() switch
            {
                "console" => GuardConsoleArg.Value,
                "devcommands" => GuardDevCommands.Value,
                "god" => GuardGod.Value,
                "debugmode" => GuardDebugMode.Value,
                "fly" => GuardFly.Value,
                "spawn" => GuardSpawn.Value,
                "tp" => GuardTp.Value,
                _ => true
            };
        }

        internal static VisibilityMode GetChatLogVisibilityForEvent(string eventType)
        {
            return eventType.ToLowerInvariant() switch
            {
                "console" => ChatLogConsoleArg.Value,
                "devcommands" => ChatLogDevCommands.Value,
                "god" => ChatLogGod.Value,
                "debugmode" => ChatLogDebugMode.Value,
                "fly" => ChatLogFly.Value,
                "spawn" => ChatLogSpawn.Value,
                "tp" => ChatLogTp.Value,
                _ => VisibilityMode.Admin
            };
        }

        internal static void RecordAndBroadcastGuardEvent(string playerName, string peerAddress, bool isAdmin, string eventType, string stateOrDetail)
        {
            if (!IsGuardEnabledForEvent(eventType)) return;

            string role = isAdmin ? "Admin" : "User";
            string displayUser = string.IsNullOrWhiteSpace(playerName) ? peerAddress : $"{playerName} ({peerAddress})";
            string logMessage;

            switch (eventType.ToLowerInvariant())
            {
                case "console":
                    logMessage = $"[DCG] {role} {displayUser} joined with -console argument.";
                    break;
                case "spawn":
                case "tp":
                    logMessage = $"[DCG] {role} {displayUser} executed guarded command '{eventType}': {stateOrDetail}";
                    break;
                default:
                    logMessage = $"[DCG] {role} {displayUser} {stateOrDetail} guarded command '{eventType}'.";
                    break;
            }

            // 1. Always log to Server Console (BepInEx + in-game Console if hosted)
            Log.LogWarning(logMessage);
            Console.instance?.Print(logMessage);

            // 2. Broadcast to Game Chat according to per-command visibility configuration
            VisibilityMode chatVis = GetChatLogVisibilityForEvent(eventType);
            if (chatVis == VisibilityMode.Disabled) return;

            string coloredChatMsg = $"<color=#FF6B6B>{logMessage}</color>";

            // Show on local host chat if applicable
            if (ZNet.instance != null && ZNet.instance.IsServer() && CanLocalSee(chatVis))
            {
                DisplayChatLogMessage(coloredChatMsg);
            }

            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                bool peerIsAdmin = IsPeerAdmin(ZNet.instance, peer);
                if (chatVis == VisibilityMode.All || (chatVis == VisibilityMode.Admin && peerIsAdmin))
                {
                    peer.m_rpc?.Invoke(RpcServerChatLog, coloredChatMsg);
                }
            }
        }

        internal static void DisplayChatLogMessage(string message)
        {
            try
            {
                if (Chat.instance != null)
                {
                    Chat.instance.AddString(message);
                    Chat.instance.m_hideTimer = 0f;
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to display chat log message: {ex}");
            }
        }

        internal static string FormatTaggedPlayerName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName) || rawName.StartsWith("<color=", StringComparison.Ordinal))
            {
                return rawName;
            }

            string format = string.IsNullOrEmpty(ActiveTagFormat) ? "{name}" : ActiveTagFormat;
            string color = string.IsNullOrEmpty(ActiveTagColor) ? "red" : ActiveTagColor;

            string formattedName = format.Contains("{name}")
                ? format.Replace("{name}", rawName)
                : $"{format} {rawName}";

            return $"<color={color}>{formattedName}</color>";
        }

        internal static int EnforceActiveConnections()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return 0;

            int disconnectedCount = 0;
            var peers = ZNet.instance.GetPeers().ToList();

            foreach (ZNetPeer peer in peers)
            {
                if (peer?.m_socket == null) continue;
                if (IsPeerAdmin(ZNet.instance, peer)) continue;

                string address = peer.m_socket.GetEndPointString();

                if (RejectClientsWithoutPlugin.Value && !HandshakedPeers.Contains(address))
                {
                    Log.LogWarning($"Disconnecting active non-admin peer {address}: missing {PluginName} plugin.");
                    peer.m_rpc?.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                    ZNet.instance.Disconnect(peer);
                    disconnectedCount++;
                    continue;
                }

                if (RejectClientsWithDevCommands.Value &&
                    PeerClientStates.TryGetValue(address, out var state) &&
                    ((GuardConsoleArg.Value && state.HasConsoleArg) || (GuardDevCommands.Value && state.HasDevCommands)))
                {
                    Log.LogWarning($"Disconnecting active non-admin peer {address} (consoleArg={state.HasConsoleArg}, devCommands={state.HasDevCommands}).");
                    RejectedPeers.Add(address);
                    peer.m_rpc?.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
                    ZNet.instance.Disconnect(peer);
                    disconnectedCount++;
                }
            }

            return disconnectedCount;
        }

        internal static string ExecuteAdminConfigCommand(string[] args, out bool changed)
        {
            changed = false;
            if (args.Length == 0 || string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
            {
                return $"[DCG] RejectDev={RejectClientsWithDevCommands.Value}, RequirePlugin={RejectClientsWithoutPlugin.Value}, " +
                       $"Highlight={HighlightVisibility.Value}, Tag={TagVisibility.Value}, TagFormat=\"{CustomTagFormat.Value}\", TagColor=\"{CustomTagColor.Value}\" | " +
                       $"Guards(console={GuardConsoleArg.Value}, devcommands={GuardDevCommands.Value}, god={GuardGod.Value}, debugmode={GuardDebugMode.Value}, fly={GuardFly.Value}, spawn={GuardSpawn.Value}, tp={GuardTp.Value}) | " +
                       $"ChatLogs(console={ChatLogConsoleArg.Value}, devcommands={ChatLogDevCommands.Value}, god={ChatLogGod.Value}, debugmode={ChatLogDebugMode.Value}, fly={ChatLogFly.Value}, spawn={ChatLogSpawn.Value}, tp={ChatLogTp.Value})";
            }

            string sub = args[0].ToLowerInvariant();
            if (sub == "reject" || sub == "rejectdev" || sub == "devcommands")
            {
                bool newVal = args.Length > 1 ? ParseBool(args[1], !RejectClientsWithDevCommands.Value) : !RejectClientsWithDevCommands.Value;
                RejectClientsWithDevCommands.Value = newVal;
                Instance.Config.Save();
                int kicked = newVal ? EnforceActiveConnections() : 0;
                changed = true;
                return $"[DCG] RejectClientsWithDevCommands set to {newVal} (saved, disconnected {kicked} non-admin peer(s)).";
            }

            if (sub == "rejectmissing" || sub == "requireplugin")
            {
                bool newVal = args.Length > 1 ? ParseBool(args[1], !RejectClientsWithoutPlugin.Value) : !RejectClientsWithoutPlugin.Value;
                RejectClientsWithoutPlugin.Value = newVal;
                Instance.Config.Save();
                int kicked = newVal ? EnforceActiveConnections() : 0;
                changed = true;
                return $"[DCG] RejectClientsWithoutPlugin set to {newVal} (saved, disconnected {kicked} non-admin peer(s)).";
            }

            if (sub == "highlight" || sub == "highlightvisibility")
            {
                if (args.Length < 2 || !TryParseVisibility(args[1], out VisibilityMode mode))
                {
                    return "Usage: dcg highlight <all | admin | disabled>";
                }
                HighlightVisibility.Value = mode;
                Instance.Config.Save();
                BroadcastConfigToAllClients();
                changed = true;
                return $"[DCG] HighlightVisibility set to {mode} (saved).";
            }

            if (sub == "tagvisibility" || sub == "tagvis" || sub == "chattag")
            {
                if (args.Length < 2 || !TryParseVisibility(args[1], out VisibilityMode mode))
                {
                    return "Usage: dcg tagvis <all | admin | disabled>";
                }
                TagVisibility.Value = mode;
                Instance.Config.Save();
                BroadcastConfigToAllClients();
                changed = true;
                return $"[DCG] TagVisibility (overhead & chat tag) set to {mode} (saved).";
            }

            if (sub == "guard")
            {
                if (args.Length < 3 || !ParseBoolExact(args[2], out bool guardEnabled))
                {
                    return "Usage: dcg guard <all | console | devcommands | god | debugmode | fly | spawn | tp> <on | off>";
                }
                string target = args[1].ToLowerInvariant();
                if (!SetGuardConfig(target, guardEnabled))
                {
                    return "Unknown guard target. Valid targets: all, console, devcommands, god, debugmode, fly, spawn, tp";
                }
                Instance.Config.Save();
                BroadcastConfigToAllClients();
                changed = true;
                return $"[DCG] Guard '{target}' set to {guardEnabled} (saved).";
            }

            if (sub == "log" || sub == "chatlog")
            {
                if (args.Length < 3 || !TryParseVisibility(args[2], out VisibilityMode logMode))
                {
                    return "Usage: dcg log <all | console | devcommands | god | debugmode | fly | spawn | tp> <all | admin | disabled>";
                }
                string target = args[1].ToLowerInvariant();
                if (!SetChatLogConfig(target, logMode))
                {
                    return "Unknown log target. Valid targets: all, console, devcommands, god, debugmode, fly, spawn, tp";
                }
                Instance.Config.Save();
                changed = true;
                return $"[DCG] ChatLog visibility for '{target}' set to {logMode} (saved).";
            }

            if (sub == "tagcolor" || sub == "color")
            {
                if (args.Length < 2) return "Usage: dcg tagcolor <color>";
                CustomTagColor.Value = args[1];
                Instance.Config.Save();
                BroadcastConfigToAllClients();
                changed = true;
                return $"[DCG] TagColor set to \"{CustomTagColor.Value}\" (saved).";
            }

            if (sub == "tag" || sub == "tagformat")
            {
                if (args.Length < 2) return "Usage: dcg tagformat <format with {name}>";
                if (args.Length == 2 && TryParseVisibility(args[1], out VisibilityMode visMode))
                {
                    TagVisibility.Value = visMode;
                    Instance.Config.Save();
                    BroadcastConfigToAllClients();
                    changed = true;
                    return $"[DCG] TagVisibility (overhead & chat tag) set to {visMode} (saved).";
                }
                CustomTagFormat.Value = string.Join(" ", args.Skip(1));
                Instance.Config.Save();
                BroadcastConfigToAllClients();
                changed = true;
                return $"[DCG] TagFormat set to \"{CustomTagFormat.Value}\" (saved).";
            }

            if (ParseBoolExact(sub, out bool directToggle))
            {
                RejectClientsWithDevCommands.Value = directToggle;
                Instance.Config.Save();
                int kicked = directToggle ? EnforceActiveConnections() : 0;
                changed = true;
                return $"[DCG] RejectClientsWithDevCommands set to {directToggle} (saved, disconnected {kicked} non-admin peer(s)).";
            }

            return "Usage: dcg [status | reject <on|off> | requireplugin <on|off> | highlight <all|admin|disabled> | tagvis <all|admin|disabled> | guard <cmd|all> <on|off> | log <cmd|all> <all|admin|disabled> | tagcolor <color> | tagformat <format>]";
        }

        private static bool SetGuardConfig(string target, bool enabled)
        {
            switch (target)
            {
                case "all":
                    GuardConsoleArg.Value = enabled;
                    GuardDevCommands.Value = enabled;
                    GuardGod.Value = enabled;
                    GuardDebugMode.Value = enabled;
                    GuardFly.Value = enabled;
                    GuardSpawn.Value = enabled;
                    GuardTp.Value = enabled;
                    return true;
                case "console":
                case "-console":
                    GuardConsoleArg.Value = enabled;
                    return true;
                case "devcommands":
                    GuardDevCommands.Value = enabled;
                    return true;
                case "god":
                    GuardGod.Value = enabled;
                    return true;
                case "debug":
                case "debugmode":
                    GuardDebugMode.Value = enabled;
                    return true;
                case "fly":
                    GuardFly.Value = enabled;
                    return true;
                case "spawn":
                    GuardSpawn.Value = enabled;
                    return true;
                case "tp":
                case "goto":
                    GuardTp.Value = enabled;
                    return true;
                default:
                    return false;
            }
        }

        private static bool SetChatLogConfig(string target, VisibilityMode mode)
        {
            switch (target)
            {
                case "all":
                    ChatLogConsoleArg.Value = mode;
                    ChatLogDevCommands.Value = mode;
                    ChatLogGod.Value = mode;
                    ChatLogDebugMode.Value = mode;
                    ChatLogFly.Value = mode;
                    ChatLogSpawn.Value = mode;
                    ChatLogTp.Value = mode;
                    return true;
                case "console":
                case "-console":
                    ChatLogConsoleArg.Value = mode;
                    return true;
                case "devcommands":
                    ChatLogDevCommands.Value = mode;
                    return true;
                case "god":
                    ChatLogGod.Value = mode;
                    return true;
                case "debug":
                case "debugmode":
                    ChatLogDebugMode.Value = mode;
                    return true;
                case "fly":
                    ChatLogFly.Value = mode;
                    return true;
                case "spawn":
                    ChatLogSpawn.Value = mode;
                    return true;
                case "tp":
                case "goto":
                    ChatLogTp.Value = mode;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseVisibility(string input, out VisibilityMode mode)
        {
            switch (input.ToLowerInvariant())
            {
                case "all":
                case "everyone":
                case "everybody":
                case "public":
                    mode = VisibilityMode.All;
                    return true;
                case "admin":
                case "admins":
                    mode = VisibilityMode.Admin;
                    return true;
                case "disabled":
                case "disable":
                case "off":
                case "none":
                case "false":
                    mode = VisibilityMode.Disabled;
                    return true;
                default:
                    mode = VisibilityMode.Admin;
                    return false;
            }
        }

        private static bool ParseBool(string input, bool fallback)
        {
            return ParseBoolExact(input, out bool val) ? val : fallback;
        }

        private static bool ParseBoolExact(string input, out bool result)
        {
            switch (input.ToLowerInvariant())
            {
                case "1":
                case "true":
                case "on":
                case "enable":
                case "enabled":
                case "yes":
                    result = true;
                    return true;
                case "0":
                case "false":
                case "off":
                case "disable":
                case "disabled":
                case "no":
                    result = false;
                    return true;
                default:
                    result = false;
                    return false;
            }
        }
    }

    [HarmonyPatch(typeof(ZNet))]
    internal static class ZNetPatches
    {
        [HarmonyPatch(nameof(ZNet.OnNewConnection))]
        [HarmonyPostfix]
        private static void OnNewConnection_Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
            {
                peer.m_rpc.Register<bool, bool>(DevCommandsGuardPlugin.RpcClientHandshake, (rpc, hasConsoleArg, hasDevCommands) =>
                {
                    OnServerReceiveHandshake(__instance, peer, hasConsoleArg, hasDevCommands);
                });

                peer.m_rpc.Register<string>(DevCommandsGuardPlugin.RpcAdminCommand, (rpc, commandLine) =>
                {
                    OnServerReceiveAdminCommand(__instance, peer, commandLine);
                });

                peer.m_rpc.Register<string, string>(DevCommandsGuardPlugin.RpcClientGuardEvent, (rpc, eventType, detail) =>
                {
                    OnServerReceiveGuardEvent(__instance, peer, eventType, detail);
                });
            }
            else
            {
                peer.m_rpc.Register<bool, string, string, string>(
                    DevCommandsGuardPlugin.RpcServerSyncState,
                    (rpc, isAdmin, tagFormat, tagColor, syncSettings) =>
                    {
                        DevCommandsGuardPlugin.SynchronizedAdmin = isAdmin;
                        DevCommandsGuardPlugin.ActiveTagFormat = string.IsNullOrEmpty(tagFormat) ? "{name}" : tagFormat;
                        DevCommandsGuardPlugin.ActiveTagColor = string.IsNullOrEmpty(tagColor) ? "red" : tagColor;
                        DevCommandsGuardPlugin.UnpackSyncSettings(syncSettings);
                    }
                );

                peer.m_rpc.Register<string>(DevCommandsGuardPlugin.RpcAdminCommandFeedback, (rpc, message) =>
                {
                    Console.instance?.Print(message);
                    DevCommandsGuardPlugin.DisplayChatLogMessage(message);
                });

                peer.m_rpc.Register<string>(DevCommandsGuardPlugin.RpcServerChatLog, (rpc, chatLogMessage) =>
                {
                    DevCommandsGuardPlugin.DisplayChatLogMessage(chatLogMessage);
                });
            }
        }

        [HarmonyPatch(nameof(ZNet.RPC_ClientHandshake))]
        [HarmonyPostfix]
        private static void RPC_ClientHandshake_Postfix(ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer())
            {
                DevCommandsGuardPlugin.LocalHasUsedActionGuardCommand = false;
                bool hasConsole = DevCommandsGuardPlugin.LocalHasConsoleArg;
                bool hasCheats = Console.instance != null && Console.instance.IsCheatsEnabled();
                rpc.Invoke(DevCommandsGuardPlugin.RpcClientHandshake, hasConsole, hasCheats);
            }
        }

        private static void OnServerReceiveHandshake(ZNet znet, ZNetPeer peer, bool hasConsoleArg, bool hasDevCommands)
        {
            string address = peer.m_socket.GetEndPointString();
            DevCommandsGuardPlugin.HandshakedPeers.Add(address);

            bool alreadyLoggedConsole = DevCommandsGuardPlugin.PeerClientStates.TryGetValue(address, out var prev) && prev.ConsoleJoinLogged;
            bool isAdmin = DevCommandsGuardPlugin.IsPeerAdmin(znet, peer);

            if (hasConsoleArg && !alreadyLoggedConsole)
            {
                alreadyLoggedConsole = true;
                DevCommandsGuardPlugin.RecordAndBroadcastGuardEvent(peer.m_playerName, address, isAdmin, "console", "enabled");
            }

            if (prev.HasDevCommands != hasDevCommands && hasDevCommands)
            {
                DevCommandsGuardPlugin.RecordAndBroadcastGuardEvent(peer.m_playerName, address, isAdmin, "devcommands", "enabled");
            }

            DevCommandsGuardPlugin.PeerClientStates[address] = (hasConsoleArg, hasDevCommands, alreadyLoggedConsole);

            peer.m_rpc.Invoke(
                DevCommandsGuardPlugin.RpcServerSyncState,
                isAdmin,
                DevCommandsGuardPlugin.CustomTagFormat.Value,
                DevCommandsGuardPlugin.CustomTagColor.Value,
                DevCommandsGuardPlugin.PackSyncSettings()
            );

            bool rejectConsole = DevCommandsGuardPlugin.GuardConsoleArg.Value && hasConsoleArg;
            bool rejectDev = DevCommandsGuardPlugin.GuardDevCommands.Value && hasDevCommands;

            if (!isAdmin && DevCommandsGuardPlugin.RejectClientsWithDevCommands.Value && (rejectConsole || rejectDev))
            {
                DevCommandsGuardPlugin.Log.LogWarning(
                    $"Rejecting non-admin peer {address} (consoleArg={hasConsoleArg}, devCommands={hasDevCommands})");
                DevCommandsGuardPlugin.RejectedPeers.Add(address);
                peer.m_rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
                znet.Disconnect(peer);
            }
        }

        private static void OnServerReceiveGuardEvent(ZNet znet, ZNetPeer peer, string eventType, string detail)
        {
            if (peer?.m_socket == null) return;
            string address = peer.m_socket.GetEndPointString();
            bool isAdmin = DevCommandsGuardPlugin.IsPeerAdmin(znet, peer);

            if (!isAdmin && (eventType == "spawn" || eventType == "tp") && DevCommandsGuardPlugin.IsGuardEnabledForEvent(eventType))
            {
                DevCommandsGuardPlugin.ActivePeerViolations.Add(address);
            }

            DevCommandsGuardPlugin.RecordAndBroadcastGuardEvent(peer.m_playerName, address, isAdmin, eventType, detail);
        }

        private static void OnServerReceiveAdminCommand(ZNet znet, ZNetPeer peer, string commandLine)
        {
            if (!DevCommandsGuardPlugin.IsPeerAdmin(znet, peer))
            {
                peer.m_rpc.Invoke(DevCommandsGuardPlugin.RpcAdminCommandFeedback, "[DCG] Permission denied: you are not a server admin.");
                return;
            }

            string[] parts = (commandLine ?? string.Empty)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            string response = DevCommandsGuardPlugin.ExecuteAdminConfigCommand(parts, out _);
            DevCommandsGuardPlugin.Log.LogInfo($"Admin {peer.m_socket.GetHostName()} executed dcg: {response}");
            peer.m_rpc.Invoke(DevCommandsGuardPlugin.RpcAdminCommandFeedback, response);
        }

        [HarmonyPatch(nameof(ZNet.SendPeerInfo))]
        [HarmonyPrefix]
        private static bool SendPeerInfo_Prefix(ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer()) return true;

            ZNetPeer peer = __instance.GetPeer(rpc);
            if (peer == null) return true;

            string address = peer.m_socket.GetEndPointString();
            bool isAdmin = DevCommandsGuardPlugin.IsPeerAdmin(__instance, peer);

            if (!isAdmin && DevCommandsGuardPlugin.RejectedPeers.Contains(address))
            {
                return false;
            }

            if (!isAdmin &&
                DevCommandsGuardPlugin.RejectClientsWithoutPlugin.Value &&
                !DevCommandsGuardPlugin.HandshakedPeers.Contains(address))
            {
                DevCommandsGuardPlugin.Log.LogWarning(
                    $"Rejecting non-admin peer {address}: missing {DevCommandsGuardPlugin.PluginName} handshake.");
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                __instance.Disconnect(peer);
                return false;
            }

            return true;
        }

        [HarmonyPatch(nameof(ZNet.Disconnect))]
        [HarmonyPostfix]
        private static void Disconnect_Postfix(ZNetPeer peer)
        {
            if (peer?.m_socket != null)
            {
                string address = peer.m_socket.GetEndPointString();
                DevCommandsGuardPlugin.HandshakedPeers.Remove(address);
                DevCommandsGuardPlugin.RejectedPeers.Remove(address);
                DevCommandsGuardPlugin.PeerClientStates.Remove(address);
                DevCommandsGuardPlugin.ActivePeerViolations.Remove(address);
            }
        }
    }

    [HarmonyPatch(typeof(Terminal))]
    internal static class TerminalPatches
    {
        private static bool _prevCheats;
        private static bool _prevGod;
        private static bool _prevDebug;
        private static bool _prevFly;

        [HarmonyPatch(nameof(Terminal.InitTerminal))]
        [HarmonyPostfix]
        private static void InitTerminal_Postfix()
        {
            try
            {
                if (Terminal.commands != null && Terminal.commands.ContainsKey("dcg"))
                {
                    return;
                }

                Terminal.ConsoleEvent action = args =>
                {
                    if (args?.Args == null) return;
                    string[] subArgs = args.Args.Skip(1).ToArray();
                    if (ZNet.instance == null)
                    {
                        args.Context?.AddString("[DCG] Must be connected to a world/server.");
                        return;
                    }

                    if (ZNet.instance.IsServer())
                    {
                        string msg = DevCommandsGuardPlugin.ExecuteAdminConfigCommand(subArgs, out _);
                        args.Context?.AddString(msg);
                    }
                    else
                    {
                        if (!DevCommandsGuardPlugin.IsLocalAdmin())
                        {
                            args.Context?.AddString("[DCG] Permission denied: you are not a server admin.");
                            return;
                        }

                        ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                        serverPeer?.m_rpc.Invoke(DevCommandsGuardPlugin.RpcAdminCommand, string.Join(" ", subArgs));
                    }
                };

                string description = "DevCommandsGuard admin config: dcg [status | reject | requireplugin | highlight | tagvis | guard | log | tagcolor | tagformat]";

                foreach (var ctor in typeof(Terminal.ConsoleCommand).GetConstructors())
                {
                    var parameters = ctor.GetParameters();
                    if (parameters.Length >= 3 &&
                        parameters[0].ParameterType == typeof(string) &&
                        parameters[1].ParameterType == typeof(string) &&
                        parameters[2].ParameterType == typeof(Terminal.ConsoleEvent))
                    {
                        object[] invokeArgs = new object[parameters.Length];
                        invokeArgs[0] = "dcg";
                        invokeArgs[1] = description;
                        invokeArgs[2] = action;
                        for (int i = 3; i < parameters.Length; i++)
                        {
                            invokeArgs[i] = parameters[i].HasDefaultValue
                                ? parameters[i].DefaultValue
                                : (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null);
                        }
                        ctor.Invoke(invokeArgs);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                DevCommandsGuardPlugin.Log.LogError($"Failed to register dcg terminal command: {ex}");
            }
        }

        [HarmonyPatch(nameof(Terminal.TryRunCommand))]
        [HarmonyPrefix]
        private static void TryRunCommand_Prefix()
        {
            _prevCheats = Console.instance != null && Console.instance.IsCheatsEnabled();
            _prevGod = Player.m_localPlayer != null && Player.m_localPlayer.InGodMode();
            _prevDebug = Player.m_debugMode;
            _prevFly = Player.m_localPlayer != null && Player.m_localPlayer.InDebugFlyMode();
        }

        [HarmonyPatch(nameof(Terminal.TryRunCommand))]
        [HarmonyPostfix]
        private static void TryRunCommand_Postfix(string text)
        {
            try
            {
                if (ZNet.instance == null) return;

                string trimmed = (text ?? string.Empty).Trim();
                string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

                // Fallback dcg handling if command dictionary didn't catch it
                if (!ZNet.instance.IsServer() && cmd == "dcg" && (Terminal.commands == null || !Terminal.commands.ContainsKey("dcg")))
                {
                    string[] subArgs = parts.Skip(1).ToArray();
                    if (!DevCommandsGuardPlugin.IsLocalAdmin())
                    {
                        Console.instance?.Print("[DCG] Permission denied: you are not a server admin.");
                    }
                    else
                    {
                        ZNetPeer sp = ZNet.instance.GetServerPeer();
                        sp?.m_rpc.Invoke(DevCommandsGuardPlugin.RpcAdminCommand, string.Join(" ", subArgs));
                    }
                }

                bool nowCheats = Console.instance != null && Console.instance.IsCheatsEnabled();
                bool nowGod = Player.m_localPlayer != null && Player.m_localPlayer.InGodMode();
                bool nowDebug = Player.m_debugMode;
                bool nowFly = Player.m_localPlayer != null && Player.m_localPlayer.InDebugFlyMode();

                if (_prevCheats != nowCheats)
                {
                    SendOrRecordLocalEvent("devcommands", nowCheats ? "enabled" : "disabled");
                }
                if (_prevGod != nowGod)
                {
                    SendOrRecordLocalEvent("god", nowGod ? "enabled" : "disabled");
                }
                if (_prevDebug != nowDebug)
                {
                    SendOrRecordLocalEvent("debugmode", nowDebug ? "enabled" : "disabled");
                }
                if (_prevFly != nowFly)
                {
                    SendOrRecordLocalEvent("fly", nowFly ? "enabled" : "disabled");
                }

                // Action commands: spawn and tp / goto
                if (nowCheats && (cmd == "spawn" || cmd == "tp" || cmd == "goto"))
                {
                    string normalizedEvent = (cmd == "goto") ? "tp" : cmd;
                    bool guardActive = normalizedEvent == "spawn"
                        ? DevCommandsGuardPlugin.ActiveGuardSpawn
                        : DevCommandsGuardPlugin.ActiveGuardTp;

                    if (guardActive)
                    {
                        DevCommandsGuardPlugin.LocalHasUsedActionGuardCommand = true;
                    }
                    SendOrRecordLocalEvent(normalizedEvent, trimmed);
                }

                if (!ZNet.instance.IsServer())
                {
                    ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                    if (serverPeer != null && nowCheats)
                    {
                        serverPeer.m_rpc.Invoke(
                            DevCommandsGuardPlugin.RpcClientHandshake,
                            DevCommandsGuardPlugin.LocalHasConsoleArg,
                            true
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                DevCommandsGuardPlugin.Log.LogError($"Error in TryRunCommand_Postfix: {ex}");
            }
        }

        internal static void SendOrRecordLocalEvent(string eventType, string detail)
        {
            if (ZNet.instance == null) return;

            if (ZNet.instance.IsServer())
            {
                string playerName = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "ServerHost";
                DevCommandsGuardPlugin.RecordAndBroadcastGuardEvent(playerName, "local", true, eventType, detail);
            }
            else
            {
                ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                serverPeer?.m_rpc.Invoke(DevCommandsGuardPlugin.RpcClientGuardEvent, eventType, detail);
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.FixedUpdate))]
    internal static class PlayerStatePatches
    {
        private static readonly Dictionary<long, Color[]> OriginalColors = new Dictionary<long, Color[]>();
        private static bool _lastTrackedDebugMode;
        private static bool _lastTrackedFlyMode;
        private static bool _lastTrackedGodMode;

        private static void Postfix(Player __instance)
        {
            if (__instance.m_nview == null || !__instance.m_nview.IsValid()) return;

            ZDO zdo = __instance.m_nview.GetZDO();
            if (zdo == null) return;

            if (__instance == Player.m_localPlayer && __instance.m_nview.IsOwner())
            {
                bool curGod = __instance.InGodMode();
                bool curDebug = Player.m_debugMode;
                bool curFly = __instance.InDebugFlyMode();

                // Detect keybind toggles (e.g. Z key for fly mode in debugmode)
                if (curGod != _lastTrackedGodMode)
                {
                    _lastTrackedGodMode = curGod;
                }
                if (curDebug != _lastTrackedDebugMode)
                {
                    _lastTrackedDebugMode = curDebug;
                }
                if (curFly != _lastTrackedFlyMode)
                {
                    _lastTrackedFlyMode = curFly;
                    TerminalPatches.SendOrRecordLocalEvent("fly", curFly ? "enabled" : "disabled");
                }

                bool violation =
                    (DevCommandsGuardPlugin.ActiveGuardGod && curGod) ||
                    (DevCommandsGuardPlugin.ActiveGuardDebugMode && curDebug) ||
                    (DevCommandsGuardPlugin.ActiveGuardFly && curFly) ||
                    DevCommandsGuardPlugin.LocalHasUsedActionGuardCommand;

                bool isAdmin = DevCommandsGuardPlugin.IsLocalAdmin();

                if (zdo.GetBool(DevCommandsGuardPlugin.ZdoDebugOrGodKey) != violation)
                {
                    zdo.Set(DevCommandsGuardPlugin.ZdoDebugOrGodKey, violation);
                }
                if (zdo.GetBool(DevCommandsGuardPlugin.ZdoIsAdminKey) != isAdmin)
                {
                    zdo.Set(DevCommandsGuardPlugin.ZdoIsAdminKey, isAdmin);
                }
            }

            bool isViolator = zdo.GetBool(DevCommandsGuardPlugin.ZdoDebugOrGodKey)
                              && !zdo.GetBool(DevCommandsGuardPlugin.ZdoIsAdminKey);

            bool shouldHighlightModel = isViolator && DevCommandsGuardPlugin.CanLocalSee(DevCommandsGuardPlugin.ActiveHighlightVisibility);

            ApplyModelHighlight(__instance, zdo.m_uid.ID, shouldHighlightModel);
        }

        private static void ApplyModelHighlight(Player player, long id, bool highlight)
        {
            Renderer[] renderers = player.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0) return;

            if (highlight)
            {
                if (!OriginalColors.ContainsKey(id))
                {
                    var saved = new List<Color>();
                    foreach (Renderer r in renderers)
                    {
                        foreach (Material mat in r.materials)
                        {
                            saved.Add(mat.HasProperty("_Color") ? mat.color : Color.white);
                        }
                    }
                    OriginalColors[id] = saved.ToArray();
                }

                foreach (Renderer r in renderers)
                {
                    foreach (Material mat in r.materials)
                    {
                        if (mat.HasProperty("_Color"))
                        {
                            mat.color = Color.red;
                        }
                        if (mat.HasProperty("_EmissionColor"))
                        {
                            mat.EnableKeyword("_EMISSION");
                            mat.SetColor("_EmissionColor", Color.red * 0.6f);
                        }
                    }
                }
            }
            else if (OriginalColors.TryGetValue(id, out Color[] savedColors))
            {
                int idx = 0;
                foreach (Renderer r in renderers)
                {
                    foreach (Material mat in r.materials)
                    {
                        if (mat.HasProperty("_Color") && idx < savedColors.Length)
                        {
                            mat.color = savedColors[idx];
                        }
                        if (mat.HasProperty("_EmissionColor"))
                        {
                            mat.SetColor("_EmissionColor", Color.black);
                        }
                        idx++;
                    }
                }
                OriginalColors.Remove(id);
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetHoverName))]
    internal static class PlayerHoverNamePatch
    {
        private static void Postfix(Player __instance, ref string __result)
        {
            if (__instance.m_nview == null || !__instance.m_nview.IsValid()) return;
            ZDO zdo = __instance.m_nview.GetZDO();
            if (zdo == null) return;

            bool isViolator = zdo.GetBool(DevCommandsGuardPlugin.ZdoDebugOrGodKey)
                              && !zdo.GetBool(DevCommandsGuardPlugin.ZdoIsAdminKey);

            if (isViolator && DevCommandsGuardPlugin.CanLocalSee(DevCommandsGuardPlugin.ActiveTagVisibility))
            {
                __result = DevCommandsGuardPlugin.FormatTaggedPlayerName(__result);
            }
        }
    }

    [HarmonyPatch(typeof(Chat), nameof(Chat.OnNewChatMessage))]
    internal static class ChatTagPatch
    {
        private static void Prefix(GameObject go, ref UserInfo sender)
        {
            try
            {
                if (!DevCommandsGuardPlugin.CanLocalSee(DevCommandsGuardPlugin.ActiveTagVisibility)) return;
                if (go == null) return;

                Player player = go.GetComponent<Player>();
                if (player == null || player.m_nview == null || !player.m_nview.IsValid()) return;

                ZDO zdo = player.m_nview.GetZDO();
                if (zdo == null) return;

                bool isViolator = zdo.GetBool(DevCommandsGuardPlugin.ZdoDebugOrGodKey)
                                  && !zdo.GetBool(DevCommandsGuardPlugin.ZdoIsAdminKey);

                if (isViolator && !string.IsNullOrEmpty(sender.Name))
                {
                    sender.Name = DevCommandsGuardPlugin.FormatTaggedPlayerName(sender.Name);
                }
            }
            catch (Exception ex)
            {
                DevCommandsGuardPlugin.Log.LogError($"Error in ChatTagPatch: {ex}");
            }
        }
    }
}
