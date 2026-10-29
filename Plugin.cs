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
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class DevCommandsGuardPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.valheim.devcommandsguard";
        public const string PluginName = "Valheim_DevCommandsGuard";
        public const string PluginVersion = "1.0.1";

        internal static DevCommandsGuardPlugin Instance = null!;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> RejectClientsWithDevCommands = null!;
        internal static ConfigEntry<bool> RejectClientsWithoutPlugin = null!;
        internal static ConfigEntry<string> CustomTagFormat = null!;
        internal static ConfigEntry<string> CustomTagColor = null!;

        internal const string RpcClientHandshake = "DCG_ClientHandshake";
        internal const string RpcServerSyncState = "DCG_ServerSyncState";
        internal const string RpcAdminCommand = "DCG_AdminCommand";
        internal const string RpcAdminCommandFeedback = "DCG_AdminCommandFeedback";

        internal const string ZdoDebugOrGodKey = "DCG_DebugOrGod";
        internal const string ZdoIsAdminKey = "DCG_IsAdmin";

        internal static readonly HashSet<string> HandshakedPeers = new HashSet<string>();
        internal static readonly HashSet<string> RejectedPeers = new HashSet<string>();
        internal static readonly Dictionary<string, (bool HasConsoleArg, bool HasDevCommands)> PeerClientStates = new Dictionary<string, (bool, bool)>();
        internal static bool LocalHasConsoleArg;

        internal static bool SynchronizedAdmin;
        internal static string ActiveTagFormat = "[DEV] {name}";
        internal static string ActiveTagColor = "red";

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
                "Tag format for non-admin players with debugmode or god mode enabled. Use {name} as placeholder for the player's name."
            );

            CustomTagColor = Config.Bind(
                "Server",
                "TagColor",
                "red",
                "Color of the overhead tag name (e.g. red, #FF0000, yellow, orange) for non-admin players with debugmode or god mode enabled."
            );

            ActiveTagFormat = CustomTagFormat.Value;
            ActiveTagColor = CustomTagColor.Value;

            RejectClientsWithDevCommands.SettingChanged += (_, _) => EnforceActiveConnections();
            RejectClientsWithoutPlugin.SettingChanged += (_, _) => EnforceActiveConnections();
            CustomTagFormat.SettingChanged += (_, _) => BroadcastConfigToAllClients();
            CustomTagColor.SettingChanged += (_, _) => BroadcastConfigToAllClients();

            LocalHasConsoleArg = HasConsoleCommandLineArg();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Local -console arg: {LocalHasConsoleArg}");

            _harmony.PatchAll();
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
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

        internal static void BroadcastConfigToAllClients()
        {
            ActiveTagFormat = CustomTagFormat.Value;
            ActiveTagColor = CustomTagColor.Value;

            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.IsReady())
                {
                    bool isAdmin = IsPeerAdmin(ZNet.instance, peer);
                    peer.m_rpc.Invoke(RpcServerSyncState, isAdmin, CustomTagFormat.Value, CustomTagColor.Value);
                }
            }
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
                    (state.HasConsoleArg || state.HasDevCommands))
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
                return $"[DCG] RejectClientsWithDevCommands={RejectClientsWithDevCommands.Value}, " +
                       $"RejectClientsWithoutPlugin={RejectClientsWithoutPlugin.Value}, " +
                       $"TagFormat=\"{CustomTagFormat.Value}\", TagColor=\"{CustomTagColor.Value}\"";
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

            return "Usage: dcg [status | reject <on|off> | requireplugin <on|off> | tagcolor <color> | tagformat <format>]";
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
            }
            else
            {
                peer.m_rpc.Register<bool, string, string>(DevCommandsGuardPlugin.RpcServerSyncState, (rpc, isAdmin, tagFormat, tagColor) =>
                {
                    DevCommandsGuardPlugin.SynchronizedAdmin = isAdmin;
                    DevCommandsGuardPlugin.ActiveTagFormat = string.IsNullOrEmpty(tagFormat) ? "{name}" : tagFormat;
                    DevCommandsGuardPlugin.ActiveTagColor = string.IsNullOrEmpty(tagColor) ? "red" : tagColor;
                });

                peer.m_rpc.Register<string>(DevCommandsGuardPlugin.RpcAdminCommandFeedback, (rpc, message) =>
                {
                    Console.instance?.Print(message);
                });
            }
        }

        [HarmonyPatch(nameof(ZNet.RPC_ClientHandshake))]
        [HarmonyPostfix]
        private static void RPC_ClientHandshake_Postfix(ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer())
            {
                bool hasConsole = DevCommandsGuardPlugin.LocalHasConsoleArg;
                bool hasCheats = Console.instance != null && Console.instance.IsCheatsEnabled();
                rpc.Invoke(DevCommandsGuardPlugin.RpcClientHandshake, hasConsole, hasCheats);
            }
        }

        private static void OnServerReceiveHandshake(ZNet znet, ZNetPeer peer, bool hasConsoleArg, bool hasDevCommands)
        {
            string address = peer.m_socket.GetEndPointString();
            DevCommandsGuardPlugin.HandshakedPeers.Add(address);
            DevCommandsGuardPlugin.PeerClientStates[address] = (hasConsoleArg, hasDevCommands);

            bool isAdmin = DevCommandsGuardPlugin.IsPeerAdmin(znet, peer);
            peer.m_rpc.Invoke(
                DevCommandsGuardPlugin.RpcServerSyncState,
                isAdmin,
                DevCommandsGuardPlugin.CustomTagFormat.Value,
                DevCommandsGuardPlugin.CustomTagColor.Value
            );

            if (!isAdmin && DevCommandsGuardPlugin.RejectClientsWithDevCommands.Value && (hasConsoleArg || hasDevCommands))
            {
                DevCommandsGuardPlugin.Log.LogWarning(
                    $"Rejecting non-admin peer {address} (consoleArg={hasConsoleArg}, devCommands={hasDevCommands})");
                DevCommandsGuardPlugin.RejectedPeers.Add(address);
                peer.m_rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorConnectFailed);
                znet.Disconnect(peer);
            }
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
            }
        }
    }

    [HarmonyPatch(typeof(Terminal))]
    internal static class TerminalPatches
    {
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

                string description = "DevCommandsGuard admin config: dcg [status | reject <on|off> | requireplugin <on|off> | tagcolor <color> | tagformat <format>]";

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
        [HarmonyPostfix]
        private static void TryRunCommand_Postfix(string text)
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.IsServer()) return;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    string trimmed = text.Trim();
                    if (trimmed.Equals("dcg", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("dcg ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Terminal.commands == null || !Terminal.commands.ContainsKey("dcg"))
                        {
                            string[] subArgs = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
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
                    }
                }

                ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                if (serverPeer != null && Console.instance != null && Console.instance.IsCheatsEnabled())
                {
                    serverPeer.m_rpc.Invoke(
                        DevCommandsGuardPlugin.RpcClientHandshake,
                        DevCommandsGuardPlugin.LocalHasConsoleArg,
                        true
                    );
                }
            }
            catch (Exception ex)
            {
                DevCommandsGuardPlugin.Log.LogError($"Error in TryRunCommand_Postfix: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.FixedUpdate))]
    internal static class PlayerStatePatches
    {
        private static readonly Dictionary<long, Color[]> OriginalColors = new Dictionary<long, Color[]>();

        private static void Postfix(Player __instance)
        {
            if (__instance.m_nview == null || !__instance.m_nview.IsValid()) return;

            ZDO zdo = __instance.m_nview.GetZDO();
            if (zdo == null) return;

            if (__instance == Player.m_localPlayer && __instance.m_nview.IsOwner())
            {
                bool violation = __instance.InGodMode() || __instance.InDebugFlyMode() || Player.m_debugMode;
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

            // Only admins can see the 3D red character highlight
            bool shouldHighlightModel = isViolator && DevCommandsGuardPlugin.IsLocalAdmin();

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

            bool highlight = zdo.GetBool(DevCommandsGuardPlugin.ZdoDebugOrGodKey)
                             && !zdo.GetBool(DevCommandsGuardPlugin.ZdoIsAdminKey);

            // Overhead tag name is visible to everyone, using server-configured format and color
            if (highlight && !string.IsNullOrEmpty(__result) && !__result.StartsWith("<color=", StringComparison.Ordinal))
            {
                string format = string.IsNullOrEmpty(DevCommandsGuardPlugin.ActiveTagFormat)
                    ? "{name}"
                    : DevCommandsGuardPlugin.ActiveTagFormat;
                string color = string.IsNullOrEmpty(DevCommandsGuardPlugin.ActiveTagColor)
                    ? "red"
                    : DevCommandsGuardPlugin.ActiveTagColor;

                string formattedName = format.Contains("{name}")
                    ? format.Replace("{name}", __result)
                    : $"{format} {__result}";

                __result = $"<color={color}>{formattedName}</color>";
            }
        }
    }
}
