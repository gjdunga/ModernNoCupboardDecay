using System;
using System.Collections.Generic;
using System.Globalization;
using Oxide.Core;
using Rust;
using UnityEngine;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    [Info("ModernNoCupboardDecay", "Gabriel", "3.6.0")]
    [Description("Prevents decay for anything within a Tool Cupboard radius, with wipe-aware timer UI, team-aware auth, live config commands, and localization.")]
    public class ModernNoCupboardDecay : RustPlugin
    {
        // -----------------------
        //  Config & Diagnostics
        // -----------------------

        private ConfigData configData;

        private bool pluginInitialized;
        private string lastStatusMessage = "Plugin not yet initialized.";

        private enum WipeMode
        {
            Manual = 0,
            Weekly = 1,
            BiWeekly = 2,
            Monthly = 3
        }

        private WipeMode currentWipeMode = WipeMode.Manual;

        private const string permAdmin = "modernnocupboarddecay.admin";

        // CUI element name
        private const string WipeUiName = "MNCD_WipeTimer";

        #region Config

        private class ConfigData
        {
            /// <summary>
            /// If true, the entity owner must be authorized on a TC in range
            /// (or on the same team as an authorized player) for decay to be disabled.
            /// If false, anything within radius is protected regardless of owner.
            /// </summary>
            public bool CheckAuth { get; set; } = false;

            /// <summary>
            /// When CheckAuth is true, teammates of authorized players / TC owner
            /// are also protected.
            /// </summary>
            public bool TeamAwareProtection { get; set; } = true;

            /// <summary>
            /// Radius in meters around each Tool Cupboard where decay is disabled.
            /// Usually matches vanilla TC building privilege radius (~30).
            /// </summary>
            public float EntityRadius { get; set; } = 30f;

            /// <summary>
            /// If true, automatically detect wipe mode from server.tags
            /// ("weekly", "biweekly", "monthly").
            /// </summary>
            public bool AutoDetectWipeFromTags { get; set; } = true;

            /// <summary>
            /// Override wipe mode if auto-detection is disabled or fails.
            /// Allowed values: Manual, Weekly, BiWeekly, Monthly.
            /// </summary>
            public string WipeModeOverride { get; set; } = "Manual";

            /// <summary>
            /// Unix time (UTC seconds) when this wipe started.
            /// Used to compute "time remaining in wipe".
            /// </summary>
            public long WipeStartUnixTime { get; set; } = 0;

            /// <summary>
            /// Enables the TC wipe-timer UI panel when a TC is opened.
            /// </summary>
            public bool EnableTcWipeUI { get; set; } = true;

            /// <summary>
            /// Background color for the wipe UI panel (RGBA).
            /// </summary>
            public string UiBackgroundColor { get; set; } = "0.05 0.05 0.05 0.85";

            /// <summary>
            /// Text color for the wipe UI panel.
            /// </summary>
            public string UiTextColor { get; set; } = "0.9 0.9 0.9 1.0";

            /// <summary>
            /// AnchorMin for the wipe UI panel (x y).
            /// </summary>
            public string UiAnchorMin { get; set; } = "0.4 0.92";

            /// <summary>
            /// AnchorMax for the wipe UI panel (x y).
            /// </summary>
            public string UiAnchorMax { get; set; } = "0.6 0.98";
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating new configuration with default values.");
            configData = new ConfigData();
        }

        private void LoadVariables()
        {
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                    throw new Exception("Config file contained null, using defaults.");

                lastStatusMessage = "Configuration loaded successfully.";
            }
            catch (Exception e)
            {
                lastStatusMessage = $"Config load error, using defaults: {e.Message}";
                PrintWarning(lastStatusMessage);
                LoadDefaultConfig();
            }

            SaveConfig(configData);
        }

        private void SaveConfig(ConfigData config)
        {
            Config.WriteObject(config, true);
        }

        #endregion

        #region Localization

        private void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                // General / status
                ["Status.Report"] = "[{0}] v{1} | State: {2} | Radius: {3} | CheckAuth: {4} | TeamAware: {5} | AutoDetect: {6} | WipeMode: {7} (Override: {8}) | WipeRemaining: {9} | Status: {10}",

                // Permissions / errors
                ["Error.NoPermission"] = "[MNCD] You do not have permission to change settings.",
                ["Error.ConfigOption"] = "[MNCD] Unknown option. Valid options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Error.ConfigApply"] = "[MNCD] Error applying '{0}': {1}",
                ["Error.CheckAuthValue"] = "[MNCD] checkauth requires true/false.",
                ["Error.TeamAwareValue"] = "[MNCD] teamaware requires true/false.",
                ["Error.AutoDetectValue"] = "[MNCD] autodetect requires true/false.",
                ["Error.RadiusValue"] = "[MNCD] radius requires a positive numeric value in meters.",
                ["Error.WipeModeValue"] = "[MNCD] wipemode requires one of: Manual, Weekly, BiWeekly, Monthly.",
                ["Error.BoolExpected"] = "[MNCD] {0} requires true/false.",

                // Usage
                ["Usage.MncdSet.Chat"] = "[MNCD] Usage: /mncdset <option> <value>. Options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Usage.MncdSet.Console"] = "[MNCD] Usage: mncd.set <option> <value>. Options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",

                // Config changes
                ["Config.CheckAuth.Set"] = "[MNCD] CheckAuth is now {0}.",
                ["Config.TeamAware.Set"] = "[MNCD] TeamAwareProtection is now {0}.",
                ["Config.Radius.Set"] = "[MNCD] EntityRadius (TC decay bubble) is now {0} meters.",
                ["Config.AutoDetect.Set"] = "[MNCD] AutoDetectWipeFromTags is now {0}. Active WipeMode: {1}.",
                ["Config.WipeMode.Set"] = "[MNCD] WipeModeOverride set to '{0}'. Active WipeMode is now {1}, AutoDetect disabled.",
                ["Config.WipeStartNow.Set"] = "[MNCD] WipeStartUnixTime set to now. WipeMode: {0}. WipeRemaining: {1}.",

                // TC loot messages (fallback if UI disabled)
                ["TcLoot.NoRemaining"] = "[MNCD] This TC's radius is fully protected from decay by ModernNoCupboardDecay.\nWipe schedule: {0}. Wipe end not calculated (manual or missing data).",
                ["TcLoot.WithRemaining"] = "[MNCD] This TC's radius is fully protected from decay by ModernNoCupboardDecay.\nWipe schedule: {0}. Wipe ends in: {1}.",

                // UI text
                ["UI.WipeTitle"] = "ModernNoCupboardDecay",
                ["UI.WipeLine"]  = "Wipe: {0} – {1} remaining",
                ["UI.WipeExtra"] = "Decay disabled within this Tool Cupboard radius.",

                // UI anchor / layout commands
                ["UIAnchor.Usage.Chat"] = "[MNCD] Usage: /mncdui <minX> <minY> <maxX> <maxY>",
                ["UIAnchor.Usage.Console"] = "[MNCD] Usage: mncd.ui <minX> <minY> <maxX> <maxY>",
                ["UIAnchor.Set"] = "[MNCD] UI Anchor set: Min({0}, {1})  Max({2}, {3})",
                ["UIAnchor.Error.Numeric"] = "[MNCD] All 4 values must be numeric.",
                ["UIAnchor.Error.Order"] = "[MNCD] minX < maxX and minY < maxY is required."
            }, this);
        }

        private string Msg(string key, string userId = null, params object[] args)
        {
            var msg = lang.GetMessage(key, this, userId);
            return args == null || args.Length == 0 ? msg : string.Format(msg, args);
        }

        #endregion

        // ----------------
        //  Plugin Lifecycle
        // ----------------

        private void Init()
        {
            pluginInitialized = false;
            lastStatusMessage = "Initializing ModernNoCupboardDecay...";
            permission.RegisterPermission(permAdmin, this);
            LoadDefaultMessages();
        }

        private void OnServerInitialized()
        {
            try
            {
                LoadVariables();

                DetectWipeModeFromTagsOrConfig();

                // Initialize wipe start time if not already set
                if (configData.WipeStartUnixTime <= 0)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    configData.WipeStartUnixTime = now;
                    SaveConfig(configData);
                    lastStatusMessage = $"Wipe start time initialized at {DateTimeOffset.FromUnixTimeSeconds(now):u}.";
                }

                pluginInitialized = true;

                if (string.IsNullOrEmpty(lastStatusMessage) ||
                    lastStatusMessage.StartsWith("Initializing", StringComparison.OrdinalIgnoreCase))
                {
                    lastStatusMessage = "ModernNoCupboardDecay initialized successfully.";
                }

                PrintDebugSummary();
            }
            catch (Exception ex)
            {
                pluginInitialized = false;
                lastStatusMessage = $"Initialization error: {ex.Message}";
                PrintError(lastStatusMessage);
            }
        }

        /// <summary>
        /// Called when the server creates a new save file (typically on map/wipe).
        /// We treat this as "wipe started now".
        /// </summary>
        private void OnNewSave(string filename)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            configData.WipeStartUnixTime = now;
            SaveConfig(configData);
            lastStatusMessage = $"Detected new save '{filename}', wipe start reset to {DateTimeOffset.FromUnixTimeSeconds(now):u}.";
        }

        // --------------------------
        //  Wipe Mode Detection / Time
        // --------------------------

        private void DetectWipeModeFromTagsOrConfig()
        {
            WipeMode detected = WipeMode.Manual;

            if (configData.AutoDetectWipeFromTags)
            {
                try
                {
                    string tags = ConVar.Server.tags ?? string.Empty;

                    if (!string.IsNullOrEmpty(tags))
                    {
                        if (tags.IndexOf("weekly", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            detected = WipeMode.Weekly;
                        }
                        else if (tags.IndexOf("biweekly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 tags.IndexOf("bi-weekly", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            detected = WipeMode.BiWeekly;
                        }
                        else if (tags.IndexOf("monthly", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            detected = WipeMode.Monthly;
                        }
                    }
                }
                catch (Exception e)
                {
                    PrintWarning($"Error reading server tags for wipe mode detection: {e.Message}");
                }
            }

            if (detected != WipeMode.Manual)
            {
                currentWipeMode = detected;
                lastStatusMessage = $"Wipe mode auto-detected from server.tags: {currentWipeMode}.";
                return;
            }

            currentWipeMode = ParseWipeMode(configData.WipeModeOverride);
            lastStatusMessage = $"Wipe mode set from config override: {currentWipeMode}.";
        }

        private WipeMode ParseWipeMode(string mode)
        {
            if (string.IsNullOrEmpty(mode))
                return WipeMode.Manual;

            switch (mode.Trim().ToLowerInvariant())
            {
                case "weekly":
                    return WipeMode.Weekly;
                case "biweekly":
                case "bi-weekly":
                    return WipeMode.BiWeekly;
                case "monthly":
                    return WipeMode.Monthly;
                default:
                    return WipeMode.Manual;
            }
        }

        private TimeSpan GetWipeDuration()
        {
            switch (currentWipeMode)
            {
                case WipeMode.Weekly:
                    return TimeSpan.FromDays(7);
                case WipeMode.BiWeekly:
                    return TimeSpan.FromDays(14);
                case WipeMode.Monthly:
                    return TimeSpan.FromDays(30);
                default:
                    return TimeSpan.Zero;
            }
        }

        private long GetWipeEndUnixTime()
        {
            if (configData.WipeStartUnixTime <= 0)
                return 0;

            TimeSpan dur = GetWipeDuration();
            if (dur == TimeSpan.Zero)
                return 0;

            return configData.WipeStartUnixTime + (long)dur.TotalSeconds;
        }

        private TimeSpan? GetWipeTimeRemaining()
        {
            long wipeEnd = GetWipeEndUnixTime();
            if (wipeEnd <= 0)
                return null;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long delta = wipeEnd - now;

            if (delta <= 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds(delta);
        }

        private string FormatTimeSpan(TimeSpan ts)
        {
            return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m";
        }

        private void PrintDebugSummary()
        {
            Puts("[ModernNoCupboardDecay] Config & wipe state loaded:");
            Puts($" - Version: {Version}");
            Puts($" - CheckAuth: {configData.CheckAuth}");
            Puts($" - TeamAwareProtection: {configData.TeamAwareProtection}");
            Puts($" - EntityRadius: {configData.EntityRadius}");
            Puts($" - AutoDetectWipeFromTags: {configData.AutoDetectWipeFromTags}");
            Puts($" - WipeModeOverride: {configData.WipeModeOverride}");
            Puts($" - WipeMode (active): {currentWipeMode}");
            Puts($" - WipeStartUnixTime: {configData.WipeStartUnixTime} ({(configData.WipeStartUnixTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(configData.WipeStartUnixTime).ToString("u") : "unset")})");

            var remaining = GetWipeTimeRemaining();
            if (remaining != null)
                Puts($" - Wipe ends in: {FormatTimeSpan(remaining.Value)}");
            else
                Puts(" - Wipe end: N/A (manual or missing data).");
        }

        // ---------------
        //  Core Decay Logic
        // ---------------

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null)
                return null;

            if (!info.damageTypes.Has(DamageType.Decay))
                return null;

            if (IsEntityWithinCupboardProtection(entity, info))
            {
                info.damageTypes.Scale(DamageType.Decay, 0f);
            }

            return null;
        }

        // -------------------
        //  Cupboard Logic
        // -------------------

        private static ulong GetOwnerId(BaseCombatEntity entity, HitInfo info)
        {
            if (entity != null && entity.OwnerID != 0)
                return entity.OwnerID;

            if (info?.HitEntity != null && info.HitEntity.OwnerID != 0)
                return info.HitEntity.OwnerID;

            return 0;
        }

        private bool IsEntityWithinCupboardProtection(BaseEntity entity, HitInfo info)
        {
            if (entity == null)
                return false;

            int mask = LayerMask.GetMask("Construction", "Construction Trigger", "Trigger", "Deployed");
            float radius = configData.EntityRadius;

            var colliders = Physics.OverlapSphere(entity.transform.position, radius, mask);

            if (colliders == null || colliders.Length == 0)
                return false;

            if (!configData.CheckAuth)
            {
                foreach (var col in colliders)
                {
                    var anyPriv = col.GetComponentInParent<BuildingPrivlidge>();
                    if (anyPriv != null)
                        return true;
                }

                return false;
            }

            ulong ownerId = GetOwnerId(entity as BaseCombatEntity, info);
            if (ownerId == 0)
                return false;

            foreach (var col in colliders)
            {
                var priv = col.GetComponentInParent<BuildingPrivlidge>();
                if (priv == null)
                    continue;

                if (IsOwnerAuthorizedOrTeammate(priv, ownerId))
                    return true;
            }

            return false;
        }

        private bool IsOwnerAuthorizedOrTeammate(BuildingPrivlidge priv, ulong ownerId)
        {
            if (priv == null || ownerId == 0)
                return false;

            if (CupboardAuthCheck(priv, ownerId))
                return true;

            if (!configData.TeamAwareProtection)
                return false;

            var rm = RelationshipManager.ServerInstance;
            if (rm == null)
                return false;

            var ownerTeam = rm.FindPlayersTeam(ownerId);
            if (ownerTeam == null)
                return false;

            if (ownerTeam.members.Contains(priv.OwnerID))
                return true;

            var authList = priv.authorizedPlayers;
            if (authList != null)
            {
                foreach (var auth in authList)
                {
                    if (ownerTeam.members.Contains(auth.userid))
                        return true;
                }
            }

            return false;
        }

        private bool CupboardAuthCheck(BuildingPrivlidge priv, ulong ownerId)
        {
            if (priv == null || ownerId == 0)
                return false;

            var authList = priv.authorizedPlayers;
            if (authList == null || authList.Count == 0)
                return false;

            foreach (var auth in authList)
            {
                if (auth.userid == ownerId)
                    return true;
            }

            return false;
        }

        // ----------------------
        //  Permissions & helpers
        // ----------------------

        private bool HasAdminPerm(BasePlayer player)
        {
            if (player == null)
                return false;

            if (player.IsAdmin)
                return true;

            return permission.UserHasPermission(player.UserIDString, permAdmin);
        }

        private bool TryParseBool(string value, out bool result)
        {
            result = false;
            if (string.IsNullOrEmpty(value))
                return false;

            string v = value.Trim().ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes" || v == "on")
            {
                result = true;
                return true;
            }

            if (v == "false" || v == "0" || v == "no" || v == "off")
            {
                result = false;
                return true;
            }

            return false;
        }

        private bool TryParseFloat(string s, out float f)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
        }

        // ----------------------
        //  Status Commands
        // ----------------------

        [ChatCommand("mncd")]
        private void ChatCommandMncd(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            var report = GetStatusReport(player.UserIDString);
            SendReply(player, report);
        }

        [ConsoleCommand("mncd")]
        private void ConsoleCommandMncd(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var userId = player?.UserIDString;
            var report = GetStatusReport(userId);

            if (player != null)
                SendReply(player, report);
            else
                Puts(report);
        }

        private string GetStatusReport(string userId = null)
        {
            string name = Title;
            string version = Version.ToString();

            string initState = pluginInitialized ? "ENABLED" : "NOT FULLY INITIALIZED";
            string wipeModeStr = currentWipeMode.ToString();
            string wipeRemainStr = "N/A";

            var remaining = GetWipeTimeRemaining();
            if (remaining != null)
                wipeRemainStr = FormatTimeSpan(remaining.Value);

            return Msg("Status.Report", userId,
                name,
                version,
                initState,
                configData.EntityRadius,
                configData.CheckAuth,
                configData.TeamAwareProtection,
                configData.AutoDetectWipeFromTags,
                wipeModeStr,
                configData.WipeModeOverride,
                wipeRemainStr,
                lastStatusMessage
            );
        }

        // ----------------------
        //  Config Commands
        // ----------------------

        [ChatCommand("mncdset")]
        private void ChatCommandMncdSet(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }

            if (args == null || args.Length == 0)
            {
                SendReply(player, Msg("Usage.MncdSet.Chat", player.UserIDString));
                return;
            }

            string option = args[0];
            string value = args.Length > 1 ? args[1] : null;

            string result = ApplyConfigChange(option, value, player.UserIDString);
            SendReply(player, result);
        }

        [ConsoleCommand("mncd.set")]
        private void ConsoleCommandMncdSet(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var userId = player?.UserIDString;

            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", userId));
                return;
            }

            if (arg.Args == null || arg.Args.Length == 0)
            {
                string usage = Msg("Usage.MncdSet.Console", userId);
                if (player != null)
                    SendReply(player, usage);
                else
                    Puts(usage);
                return;
            }

            string option = arg.Args[0];
            string value = arg.Args.Length > 1 ? arg.Args[1] : null;

            string result = ApplyConfigChange(option, value, userId);

            if (player != null)
                SendReply(player, result);
            else
                Puts(result);
        }

        private string ApplyConfigChange(string option, string value, string userId)
        {
            if (string.IsNullOrEmpty(option))
                return Msg("Error.ConfigOption", userId);

            string opt = option.Trim().ToLowerInvariant();

            try
            {
                switch (opt)
                {
                    case "checkauth":
                        {
                            if (!TryParseBool(value, out bool b))
                                return Msg("Error.CheckAuthValue", userId);

                            configData.CheckAuth = b;
                            SaveConfig(configData);
                            lastStatusMessage = $"CheckAuth set to {b}.";
                            return Msg("Config.CheckAuth.Set", userId, b);
                        }

                    case "teamaware":
                    case "teamauth":
                    case "team":
                        {
                            if (!TryParseBool(value, out bool b))
                                return Msg("Error.TeamAwareValue", userId);

                            configData.TeamAwareProtection = b;
                            SaveConfig(configData);
                            lastStatusMessage = $"TeamAwareProtection set to {b}.";
                            return Msg("Config.TeamAware.Set", userId, b);
                        }

                    case "radius":
                    case "entityradius":
                        {
                            if (string.IsNullOrEmpty(value))
                                return Msg("Error.RadiusValue", userId);

                            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) || r <= 0f)
                                return Msg("Error.RadiusValue", userId);

                            configData.EntityRadius = r;
                            SaveConfig(configData);
                            lastStatusMessage = $"EntityRadius set to {r}.";
                            return Msg("Config.Radius.Set", userId, r);
                        }

                    case "autodetect":
                    case "autotags":
                        {
                            if (!TryParseBool(value, out bool b))
                                return Msg("Error.AutoDetectValue", userId);

                            configData.AutoDetectWipeFromTags = b;
                            SaveConfig(configData);

                            DetectWipeModeFromTagsOrConfig();
                            return Msg("Config.AutoDetect.Set", userId, b, currentWipeMode);
                        }

                    case "wipemode":
                    case "wipeoverride":
                        {
                            if (string.IsNullOrEmpty(value))
                                return Msg("Error.WipeModeValue", userId);

                            configData.WipeModeOverride = value;
                            configData.AutoDetectWipeFromTags = false;
                            SaveConfig(configData);

                            DetectWipeModeFromTagsOrConfig();
                            return Msg("Config.WipeMode.Set", userId, value, currentWipeMode);
                        }

                    case "wipestartnow":
                    case "resetwipe":
                    case "wipe-reset":
                        {
                            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            configData.WipeStartUnixTime = now;
                            SaveConfig(configData);
                            lastStatusMessage = $"WipeStartUnixTime reset to now: {DateTimeOffset.FromUnixTimeSeconds(now):u}.";

                            var remaining = GetWipeTimeRemaining();
                            string remainStr = remaining != null ? FormatTimeSpan(remaining.Value) : "N/A";

                            return Msg("Config.WipeStartNow.Set", userId, currentWipeMode, remainStr);
                        }

                    default:
                        return Msg("Error.ConfigOption", userId);
                }
            }
            catch (Exception e)
            {
                lastStatusMessage = $"Config change error on '{opt}': {e.Message}";
                return Msg("Error.ConfigApply", userId, opt, e.Message);
            }
        }

        // ----------------------
        //  UI Anchor Commands
        // ----------------------

        [ChatCommand("mncdui")]
        private void ChatCommandMncdUi(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }

            if (args.Length != 4)
            {
                SendReply(player, Msg("UIAnchor.Usage.Chat", player.UserIDString));
                return;
            }

            if (!TryParseFloat(args[0], out float minX) ||
                !TryParseFloat(args[1], out float minY) ||
                !TryParseFloat(args[2], out float maxX) ||
                !TryParseFloat(args[3], out float maxY))
            {
                SendReply(player, Msg("UIAnchor.Error.Numeric", player.UserIDString));
                return;
            }

            if (minX >= maxX || minY >= maxY)
            {
                SendReply(player, Msg("UIAnchor.Error.Order", player.UserIDString));
                return;
            }

            configData.UiAnchorMin = $"{minX} {minY}";
            configData.UiAnchorMax = $"{maxX} {maxY}";
            SaveConfig(configData);

            SendReply(player, Msg("UIAnchor.Set", player.UserIDString, minX, minY, maxX, maxY));

            DestroyWipeTimerUI(player);
            SendReply(player, "Reopen a Tool Cupboard to see the new UI position.");
        }

        [ConsoleCommand("mncd.ui")]
        private void ConsoleCommandMncdUi(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var userId = player?.UserIDString;

            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", userId));
                return;
            }

            if (arg.Args == null || arg.Args.Length != 4)
            {
                string usage = Msg("UIAnchor.Usage.Console", userId);
                if (player != null) SendReply(player, usage);
                else Puts(usage);
                return;
            }

            if (!TryParseFloat(arg.Args[0], out float minX) ||
                !TryParseFloat(arg.Args[1], out float minY) ||
                !TryParseFloat(arg.Args[2], out float maxX) ||
                !TryParseFloat(arg.Args[3], out float maxY))
            {
                string msg = Msg("UIAnchor.Error.Numeric", userId);
                if (player != null) SendReply(player, msg);
                else Puts(msg);
                return;
            }

            if (minX >= maxX || minY >= maxY)
            {
                string msg = Msg("UIAnchor.Error.Order", userId);
                if (player != null) SendReply(player, msg);
                else Puts(msg);
                return;
            }

            configData.UiAnchorMin = $"{minX} {minY}";
            configData.UiAnchorMax = $"{maxX} {maxY}";
            SaveConfig(configData);

            string confirm = Msg("UIAnchor.Set", userId, minX, minY, maxX, maxY);
            if (player != null)
            {
                SendReply(player, confirm);
                DestroyWipeTimerUI(player);
                SendReply(player, "Reopen a Tool Cupboard to see the new UI position.");
            }
            else
            {
                Puts(confirm);
            }
        }

        // ----------------------
        //  TC Loot Hook + Wipe UI
        // ----------------------

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null)
                return;

            var priv = entity as BuildingPrivlidge;
            if (priv == null)
                return;

            DestroyWipeTimerUI(player);

            if (!configData.EnableTcWipeUI)
            {
                var remaining = GetWipeTimeRemaining();
                string wipeModeStr = currentWipeMode.ToString();
                var userId = player.UserIDString;

                if (remaining == null)
                    SendReply(player, Msg("TcLoot.NoRemaining", userId, wipeModeStr));
                else
                    SendReply(player, Msg("TcLoot.WithRemaining", userId, wipeModeStr, FormatTimeSpan(remaining.Value)));

                return;
            }

            var wipeRemaining = GetWipeTimeRemaining();
            string mode = currentWipeMode.ToString();
            string remainText = wipeRemaining != null ? FormatTimeSpan(wipeRemaining.Value) : "N/A";

            ShowWipeTimerUI(player, mode, remainText);
        }

        private void OnLootEntityEnd(BasePlayer player, BaseEntity entity)
        {
            if (player == null)
                return;

            DestroyWipeTimerUI(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
                return;

            DestroyWipeTimerUI(player);
        }

        private void ShowWipeTimerUI(BasePlayer player, string wipeMode, string remaining)
        {
            if (player == null)
                return;

            var userId = player.UserIDString;

            var container = new CuiElementContainer();

            var panel = new CuiElement
            {
                Name = WipeUiName,
                Parent = "Overlay",
                Components =
                {
                    new CuiImageComponent
                    {
                        Color = configData.UiBackgroundColor
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = configData.UiAnchorMin,
                        AnchorMax = configData.UiAnchorMax
                    }
                }
            };
            container.Add(panel);

            // Title
            container.Add(new CuiElement
            {
                Parent = WipeUiName,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg("UI.WipeTitle", userId),
                        FontSize = 14,
                        Align = TextAnchor.UpperCenter,
                        Color = configData.UiTextColor
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.05 0.5",
                        AnchorMax = "0.95 0.95"
                    }
                }
            });

            // Main line: wipe mode + remaining
            container.Add(new CuiElement
            {
                Parent = WipeUiName,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg("UI.WipeLine", userId, wipeMode, remaining),
                        FontSize = 12,
                        Align = TextAnchor.MiddleCenter,
                        Color = configData.UiTextColor
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.05 0.15",
                        AnchorMax = "0.95 0.5"
                    }
                }
            });

            // Extra line: decay note
            container.Add(new CuiElement
            {
                Parent = WipeUiName,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg("UI.WipeExtra", userId),
                        FontSize = 11,
                        Align = TextAnchor.LowerCenter,
                        Color = configData.UiTextColor
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.05 0.02",
                        AnchorMax = "0.95 0.2"
                    }
                }
            });

            CuiHelper.AddUi(player, container);
        }

        private void DestroyWipeTimerUI(BasePlayer player)
        {
            if (player == null)
                return;

            CuiHelper.DestroyUi(player, WipeUiName);
        }
    }
}
