using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;                    // For LINQ (used in preview)
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Rust;
using UnityEngine;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    /// <summary>
    /// ModernNoCupboardDecay
    /// 
    /// High-level responsibilities:
    /// - Disable decay damage for entities inside a Tool Cupboard radius.
    /// - Optionally require TC auth + team membership for protection.
    /// - Track wipe start time and compute "time until wipe end".
    /// - Display a TC UI panel showing wipe mode + time remaining.
    /// - Provide commands for live configuration + UI positioning.
    /// - Provide debug tools: protection overlay, TC preview rings.
    /// </summary>
    [Info("ModernNoCupboardDecay", "Gabriel", "4.0.0")]
    [Description("Prevents decay for anything within a Tool Cupboard radius, with wipe-aware timer UI, team-aware auth, debug tools, and localization.")]
    public class ModernNoCupboardDecay : RustPlugin
    {
        // -----------------------
        //  Config & Diagnostics
        // -----------------------

        private ConfigData configData;

        /// <summary>
        /// True when plugin finished Init + OnServerInitialized successfully.
        /// Used mostly for status display.
        /// </summary>
        private bool pluginInitialized;

        /// <summary>
        /// Human-readable last status / error message, used in /mncd output.
        /// </summary>
        private string lastStatusMessage = "Plugin not yet initialized.";

        /// <summary>
        /// Internal enumeration of wipe modes.
        /// "CustomDays" is for arbitrary N-day wipes (e.g., 5d, 10d).
        /// </summary>
        private enum WipeMode
        {
            Manual = 0,
            Weekly = 1,
            BiWeekly = 2,
            Monthly = 3,
            CustomDays = 4
        }

        private WipeMode currentWipeMode = WipeMode.Manual;

        // Permissions
        private const string permAdmin   = "modernnocupboarddecay.admin";
        private const string permDebug   = "modernnocupboarddecay.debug";
        private const string permPreview = "modernnocupboarddecay.preview";

        // CUI element names
        private const string WipeUiName  = "MNCD_WipeTimer";
        private const string DebugUiName = "MNCD_DebugOverlay";

        /// <summary>
        /// Players who currently have the debug overlay enabled.
        /// </summary>
        private readonly HashSet<ulong> debugOverlayUsers = new HashSet<ulong>();

        /// <summary>
        /// Timers driving the debug overlay for each player (periodic update).
        /// </summary>
        private readonly Dictionary<ulong, Timer> debugOverlayTimers = new Dictionary<ulong, Timer>();

        #region Config

        /// <summary>
        /// Strongly-typed plugin config. Serialized to oxide/config/ModernNoCupboardDecay.json.
        /// </summary>
        private class ConfigData
        {
            public bool CheckAuth { get; set; } = false;
            public bool TeamAwareProtection { get; set; } = true;
            public float EntityRadius { get; set; } = 30f;

            public bool AutoDetectWipeFromTags { get; set; } = true;
            public string WipeModeOverride { get; set; } = "Manual";
            public int CustomWipeDays { get; set; } = 0;
            public long WipeStartUnixTime { get; set; } = 0;

            public bool EnableTcWipeUI { get; set; } = true;
            public string UiBackgroundColor { get; set; } = "0.05 0.05 0.05 0.85";
            public string UiTextColor { get; set; } = "0.9 0.9 0.9 1.0";
            public string UiAnchorMin { get; set; } = "0.4 0.92";
            public string UiAnchorMax { get; set; } = "0.6 0.98";

            public bool PreviewRequiresPermission { get; set; } = false;
            public float PreviewRingDuration { get; set; } = 30f;
            public float PreviewRingRadiusMultiplier { get; set; } = 1.0f;
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
                ["Error.NoDebugPermission"] = "[MNCD] You do not have permission to use the debug overlay.",
                ["Error.ConfigOption"] = "[MNCD] Unknown option. Valid options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Error.ConfigApply"] = "[MNCD] Error applying '{0}': {1}",
                ["Error.CheckAuthValue"] = "[MNCD] checkauth requires true/false.",
                ["Error.TeamAwareValue"] = "[MNCD] teamaware requires true/false.",
                ["Error.AutoDetectValue"] = "[MNCD] autodetect requires true/false.",
                ["Error.RadiusValue"] = "[MNCD] radius requires a positive numeric value in meters.",
                ["Error.WipeModeValue"] = "[MNCD] wipemode requires one of: Manual, Weekly, BiWeekly, Monthly, or a numeric day count like 5d.",
                ["Error.BoolExpected"] = "[MNCD] {0} requires true/false.",

                // Usage
                ["Usage.MncdSet.Chat"] = "[MNCD] Usage: /mncdset <option> <value>. Options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Usage.MncdSet.Console"] = "[MNCD] Usage: mncd.set <option> <value>. Options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Usage.UIAdd.Chat"] = "[MNCD] Usage: /mncduiadd <deltaX> <deltaY> (normalized 0..1 offsets).",
                ["Usage.UIAdd.Console"] = "[MNCD] Usage: mncd.uiadd <deltaX> <deltaY> (normalized 0..1 offsets).",
                ["Usage.UIReset.Chat"] = "[MNCD] Usage: /mncdresetui (resets to plugin default UI position).",
                ["Usage.UIReset.Console"] = "[MNCD] Usage: mncd.resetui (resets to plugin default UI position).",

                // Config changes
                ["Config.CheckAuth.Set"] = "[MNCD] CheckAuth is now {0}.",
                ["Config.TeamAware.Set"] = "[MNCD] TeamAwareProtection is now {0}.",
                ["Config.Radius.Set"] = "[MNCD] EntityRadius (TC decay bubble) is now {0} meters.",
                ["Config.AutoDetect.Set"] = "[MNCD] AutoDetectWipeFromTags is now {0}. Active WipeMode: {1}.",
                ["Config.WipeMode.Set"] = "[MNCD] WipeModeOverride set to '{0}'. Active WipeMode is now {1}, AutoDetect disabled.",
                ["Config.WipeStartNow.Set"] = "[MNCD] WipeStartUnixTime set to now. WipeMode: {0}. WipeRemaining: {1}.",
                ["Config.UIReset.Set"] = "[MNCD] UI anchors reset to default. Reopen a Tool Cupboard to see the new position.",
                ["Config.UIAdd.Set"] = "[MNCD] UI anchors shifted by Δ({0}, {1}). New Min({2}, {3}) Max({4}, {5}).",

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
                ["UIAnchor.Error.Order"] = "[MNCD] minX < maxX and minY < maxY is required.",

                // Debug overlay
                ["Debug.Enabled"]  = "[MNCD] Debug overlay enabled. It will show whether YOU are currently inside a MNCD protection zone.",
                ["Debug.Disabled"] = "[MNCD] Debug overlay disabled.",
                ["Debug.UI.Protected"]    = "MNCD: Protected",
                ["Debug.UI.NotProtected"] = "MNCD: Not Protected",

                // Preview
                ["Preview.NoTC"] = "[MNCD] No Tool Cupboards found nearby. Nothing to preview.",
                ["Preview.Drawn"] = "[MNCD] Drawing TC protection rings for {0} cupboards for {1} seconds (radius {2}m).",
                ["Preview.NoPerm"] = "[MNCD] You do not have permission to use /mncdpreview.",

                // Help system
                ["Help.Header"] = "[MNCD] ModernNoCupboardDecay v{0}",
                ["Help.General"] =
                    "ModernNoCupboardDecay prevents decay for entities within Tool Cupboard radius and shows wipe info.\n" +
                    "Basic commands:\n" +
                    "  /mncd               - Show plugin status.\n" +
                    "  /mncdhelp [topic]   - Show help. Topics: basic, ui, set, debug, preview, wipe.\n" +
                    "  /mncdpreview        - Draw TC protection rings (hologram).\n" +
                    "  /mncddebug          - Toggle your personal protection status overlay.\n" +
                    "Admin commands:\n" +
                    "  /mncdset            - Live config (radius, auth, wipe mode).\n" +
                    "  /mncdui, /mncduiadd - Move the wipe UI panel.\n" +
                    "  /mncdresetui        - Reset the wipe UI position.\n" +
                    "Use '/mncdhelp <topic>' for details. Example: /mncdhelp preview",

                ["Help.UnknownTopic"] = "[MNCD] Unknown help topic '{0}'. Valid topics: basic, ui, set, debug, preview, wipe.",

                ["Help.Topic.basic"] =
                    "MNCD basics:\n" +
                    "• All entities within the configured radius of a Tool Cupboard are protected from decay.\n" +
                    "• Optional CheckAuth: only entities owned by a TC authed player (or their team) are protected.\n" +
                    "• TeamAwareProtection: when enabled, TC owner's Rust team and authed teammates are also covered.\n" +
                    "• The TC upkeep 'time left' becomes 'time until wipe' in the MNCD UI.\n" +
                    "Useful commands:\n" +
                    "  /mncd           - Show current radius, wipe mode, and status.\n" +
                    "  /mncddebug      - See if you are currently inside a protected bubble.",

                ["Help.Topic.ui"] =
                    "UI positioning:\n" +
                    "  /mncdui <minX> <minY> <maxX> <maxY>\n" +
                    "    • Sets the TC wipe UI panel anchors in normalized screen coords (0..1).\n" +
                    "    • Example: /mncdui 0.70 0.27 0.95 0.49\n" +
                    "  /mncduiadd <dx> <dy>\n" +
                    "    • Nudges the UI panel by a small offset.\n" +
                    "    • Example: /mncduiadd -0.02 0.10 moves it 2% left, 10% up.\n" +
                    "  /mncdresetui\n" +
                    "    • Resets the UI to the default top-center position.\n" +
                    "Notes:\n" +
                    "  • Requires admin or 'modernnocupboarddecay.admin' permission.\n" +
                    "  • After changing UI, reopen the TC to refresh the panel.",

                ["Help.Topic.set"] =
                    "Live configuration: /mncdset and mncd.set\n\n" +
                    "Chat (admin):\n" +
                    "  /mncdset <option> <value>\n" +
                    "Console/RCON:\n" +
                    "  mncd.set <option> <value>\n\n" +
                    "Options:\n" +
                    "  checkauth <true|false>\n" +
                    "    • true  => only entities owned by TC-authed players (and optional teams) are protected.\n" +
                    "    • false => anything near a TC is protected.\n" +
                    "  teamaware <true|false>\n" +
                    "    • When checkauth is true, also protect Rust teammates of TC owner/authed.\n" +
                    "  radius <meters>\n" +
                    "    • Sets TC protection radius. Example: /mncdset radius 30\n" +
                    "  autodetect <true|false>\n" +
                    "    • true  => detect wipe schedule from server.tags (weekly, monthly, 5d, etc.).\n" +
                    "    • false => use WipeModeOverride.\n" +
                    "  wipemode <Manual|Weekly|BiWeekly|Monthly|Nd>\n" +
                    "    • Example: /mncdset wipemode 5d (for 5-day wipes).\n" +
                    "  wipestartnow\n" +
                    "    • Resets wipe start to now. Use if you change wipe schedule mid-wipe.\n",

                ["Help.Topic.debug"] =
                    "Debug overlay: /mncddebug and mncd.debug\n\n" +
                    "  /mncddebug\n" +
                    "    • Toggles a small text panel at the top of your screen:\n" +
                    "      'MNCD: Protected'    => you are inside a protected TC radius.\n" +
                    "      'MNCD: Not Protected' => you are outside MNCD protection.\n" +
                    "  mncd.debug (console)\n" +
                    "    • Same as /mncddebug, but from the F1 console for that player.\n\n" +
                    "Notes:\n" +
                    "  • Requires admin or 'modernnocupboarddecay.debug' permission.\n" +
                    "  • Updates every 0.5 seconds while enabled.\n" +
                    "  • Automatically stops and cleans up when you disconnect.",

                ["Help.Topic.preview"] =
                    "TC preview rings: /mncdpreview and mncd.preview\n\n" +
                    "  /mncdpreview\n" +
                    "    • Draws 'hologram' spheres around all Tool Cupboards.\n" +
                    "    • Uses the configured EntityRadius (optionally multiplied by PreviewRingRadiusMultiplier).\n" +
                    "    • Only you see the rings; they are client-side ddraw.\n\n" +
                    "  mncd.preview (console)\n" +
                    "    • Same effect, invoked from the F1 console.\n\n" +
                    "Config:\n" +
                    "  PreviewRequiresPermission\n" +
                    "    • false (default) => everyone can use /mncdpreview.\n" +
                    "    • true            => requires 'modernnocupboarddecay.preview' perm or admin.\n" +
                    "  PreviewRingDuration\n" +
                    "    • How long rings are visible (seconds).\n" +
                    "  PreviewRingRadiusMultiplier\n" +
                    "    • Multiplies EntityRadius for the visual ring only.\n",

                ["Help.Topic.wipe"] =
                    "Wipe modes and wipe timer:\n\n" +
                    "Detection:\n" +
                    "  • AutoDetectWipeFromTags = true:\n" +
                    "    - Reads ConVar.Server.tags for keywords: weekly, biweekly, monthly.\n" +
                    "    - Also supports patterns like '5d', '5day', '5days' for custom N-day wipes.\n" +
                    "  • AutoDetectWipeFromTags = false:\n" +
                    "    - Uses WipeModeOverride from config or /mncdset wipemode.\n\n" +
                    "Supported modes:\n" +
                    "  Manual      => no automatic wipe end time.\n" +
                    "  Weekly      => 7-day wipes.\n" +
                    "  BiWeekly    => 14-day wipes.\n" +
                    "  Monthly     => 30-day wipes (fixed length).\n" +
                    "  Nd (Custom) => e.g. '5d' => 5-day wipes.\n\n" +
                    "Wipe start:\n" +
                    "  • OnNewSave (map/wipe) automatically sets WipeStartUnixTime.\n" +
                    "  • /mncdset wipestartnow can manually reset the wipe start.\n\n" +
                    "TC UI:\n" +
                    "  • When you open a TC, MNCD shows 'Wipe: <mode> – <time remaining>'.\n" +
                    "  • If mode is Manual or time cannot be computed, it shows N/A instead.",

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
            permission.RegisterPermission(permDebug, this);
            permission.RegisterPermission(permPreview, this);

            LoadDefaultMessages();
        }

        private void OnServerInitialized()
        {
            try
            {
                LoadVariables();

                DetectWipeModeFromTagsOrConfig();

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
                        string lowerTags = tags.ToLowerInvariant();

                        if (lowerTags.Contains("weekly"))
                        {
                            detected = WipeMode.Weekly;
                        }
                        else if (lowerTags.Contains("biweekly") || lowerTags.Contains("bi-weekly"))
                        {
                            detected = WipeMode.BiWeekly;
                        }
                        else if (lowerTags.Contains("monthly"))
                        {
                            detected = WipeMode.Monthly;
                        }
                        else
                        {
                            int customDays = TryExtractDaysFromTags(lowerTags);
                            if (customDays > 0)
                            {
                                detected = WipeMode.CustomDays;
                                configData.CustomWipeDays = customDays;
                            }
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
                lastStatusMessage = $"Wipe mode auto-detected from server.tags: {currentWipeMode}" +
                    (currentWipeMode == WipeMode.CustomDays && configData.CustomWipeDays > 0
                        ? $" ({configData.CustomWipeDays} days)"
                        : ".");
                return;
            }

            currentWipeMode = ParseWipeMode(configData.WipeModeOverride);
            lastStatusMessage = $"Wipe mode set from config override: {currentWipeMode}" +
                (currentWipeMode == WipeMode.CustomDays && configData.CustomWipeDays > 0
                    ? $" ({configData.CustomWipeDays} days)"
                    : ".");
        }

        private int TryExtractDaysFromTags(string lowerTags)
        {
            if (string.IsNullOrEmpty(lowerTags))
                return 0;

            var pieces = lowerTags.Split(',');
            foreach (var piece in pieces)
            {
                string p = piece.Trim();
                if (string.IsNullOrEmpty(p))
                    continue;

                var tokens = p.Split(' ', '-', '_');
                foreach (var token in tokens)
                {
                    string t = token.Trim();
                    if (string.IsNullOrEmpty(t))
                        continue;

                    if (t.EndsWith("days"))
                        t = t.Substring(0, t.Length - "days".Length);
                    else if (t.EndsWith("day"))
                        t = t.Substring(0, t.Length - "day".Length);
                    else if (t.EndsWith("d"))
                        t = t.Substring(0, t.Length - 1);

                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days))
                    {
                        if (days >= 2 && days <= 60)
                            return days;
                    }
                }
            }

            return 0;
        }

        private WipeMode ParseWipeMode(string mode)
        {
            if (string.IsNullOrEmpty(mode))
                return WipeMode.Manual;

            string lower = mode.Trim().ToLowerInvariant();

            string numericCandidate = lower;
            if (numericCandidate.EndsWith("days"))
                numericCandidate = numericCandidate.Substring(0, numericCandidate.Length - "days".Length);
            else if (numericCandidate.EndsWith("day"))
                numericCandidate = numericCandidate.Substring(0, numericCandidate.Length - "day".Length);
            else if (numericCandidate.EndsWith("d"))
                numericCandidate = numericCandidate.Substring(0, numericCandidate.Length - 1);

            if (int.TryParse(numericCandidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days > 0)
            {
                configData.CustomWipeDays = days;
                return WipeMode.CustomDays;
            }

            switch (lower)
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
                case WipeMode.CustomDays:
                    if (configData.CustomWipeDays > 0)
                        return TimeSpan.FromDays(configData.CustomWipeDays);
                    return TimeSpan.Zero;
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
            Puts($" - CustomWipeDays: {configData.CustomWipeDays}");
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
        //  Cupboard / Auth Logic
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
                foreach (var authUserId in authList)
                {
                    if (ownerTeam.members.Contains(authUserId))
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

            foreach (var authUserId in authList)
            {
                if (authUserId == ownerId)
                    return true;
            }

            return false;
        }

        private bool IsPlayerInProtectedZone(BasePlayer player)
        {
            if (player == null || !player.IsAlive())
                return false;

            int mask = LayerMask.GetMask("Construction", "Construction Trigger", "Trigger", "Deployed");
            float radius = configData.EntityRadius;

            var colliders = Physics.OverlapSphere(player.transform.position, radius, mask);
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

            ulong userId = player.userID;
            if (userId == 0)
                return false;

            foreach (var col in colliders)
            {
                var priv = col.GetComponentInParent<BuildingPrivlidge>();
                if (priv == null)
                    continue;

                if (IsOwnerAuthorizedOrTeammate(priv, userId))
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

        private bool HasDebugPerm(BasePlayer player)
        {
            if (player == null)
                return false;

            if (player.IsAdmin)
                return true;

            return permission.UserHasPermission(player.UserIDString, permDebug);
        }

        private bool HasPreviewPerm(BasePlayer player)
        {
            if (player == null)
                return false;

            if (!configData.PreviewRequiresPermission)
                return true;

            if (player.IsAdmin)
                return true;

            return permission.UserHasPermission(player.UserIDString, permPreview);
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

        private bool TryParseAnchor(string anchor, out float x, out float y)
        {
            x = y = 0f;
            if (string.IsNullOrEmpty(anchor))
                return false;

            var parts = anchor.Trim().Split(' ');
            if (parts.Length != 2)
                return false;

            return float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                   && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        private string ToAnchorString(float x, float y)
        {
            return $"{x.ToString("0.###", CultureInfo.InvariantCulture)} {y.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        private float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
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
            if (currentWipeMode == WipeMode.CustomDays && configData.CustomWipeDays > 0)
                wipeModeStr += $" ({configData.CustomWipeDays}d)";

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
        //  HELP COMMANDS
        // ----------------------

        [ChatCommand("mncdhelp")]
        private void ChatCommandMncdHelp(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            string userId = player.UserIDString;
            string topic = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "general";

            var text = BuildHelpText(topic, userId);
            SendReply(player, text);
        }

        [ConsoleCommand("mncd.help")]
        private void ConsoleCommandMncdHelp(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            string userId = player?.UserIDString;

            string topic = (arg.Args != null && arg.Args.Length > 0)
                ? arg.Args[0].ToLowerInvariant()
                : "general";

            var text = BuildHelpText(topic, userId);

            if (player != null)
                SendReply(player, text);
            else
                Puts(text);
        }

        private string BuildHelpText(string topic, string userId)
        {
            string header = string.Format(Msg("Help.Header", userId), Version.ToString());

            switch (topic)
            {
                case "general":
                case "main":
                case "help":
                    return header + "\n" + Msg("Help.General", userId);

                case "basic":
                    return header + "\n" + Msg("Help.Topic.basic", userId);

                case "ui":
                case "panel":
                    return header + "\n" + Msg("Help.Topic.ui", userId);

                case "set":
                case "config":
                case "settings":
                    return header + "\n" + Msg("Help.Topic.set", userId);

                case "debug":
                    return header + "\n" + Msg("Help.Topic.debug", userId);

                case "preview":
                case "ring":
                case "radius":
                    return header + "\n" + Msg("Help.Topic.preview", userId);

                case "wipe":
                case "wipemode":
                case "wipes":
                    return header + "\n" + Msg("Help.Topic.wipe", userId);

                default:
                    return header + "\n" + Msg("Help.UnknownTopic", userId, topic);
            }
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
        //  UI Anchor Commands (Absolute)
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

            configData.UiAnchorMin = ToAnchorString(minX, minY);
            configData.UiAnchorMax = ToAnchorString(maxX, maxY);
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

            configData.UiAnchorMin = ToAnchorString(minX, minY);
            configData.UiAnchorMax = ToAnchorString(maxX, maxY);
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
        //  UI Anchor Commands (Incremental / "Live Drag")
        // ----------------------

        [ChatCommand("mncduiadd")]
        private void ChatCommandMncdUiAdd(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }

            if (args.Length != 2)
            {
                SendReply(player, Msg("Usage.UIAdd.Chat", player.UserIDString));
                return;
            }

            if (!TryParseFloat(args[0], out float dx) || !TryParseFloat(args[1], out float dy))
            {
                SendReply(player, Msg("UIAnchor.Error.Numeric", player.UserIDString));
                return;
            }

            ApplyUiOffset(player, dx, dy, true);
        }

        [ConsoleCommand("mncd.uiadd")]
        private void ConsoleCommandMncdUiAdd(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var userId = player?.UserIDString;

            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", userId));
                return;
            }

            if (arg.Args == null || arg.Args.Length != 2)
            {
                string usage = Msg("Usage.UIAdd.Console", userId);
                if (player != null) SendReply(player, usage);
                else Puts(usage);
                return;
            }

            if (!TryParseFloat(arg.Args[0], out float dx) || !TryParseFloat(arg.Args[1], out float dy))
            {
                string msg = Msg("UIAnchor.Error.Numeric", userId);
                if (player != null) SendReply(player, msg);
                else Puts(msg);
                return;
            }

            ApplyUiOffset(player, dx, dy, false);
        }

        [ChatCommand("mncdresetui")]
        private void ChatCommandMncdResetUi(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }

            ResetUiAnchors(player, true);
        }

        [ConsoleCommand("mncd.resetui")]
        private void ConsoleCommandMncdResetUi(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var userId = player?.UserIDString;

            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", userId));
                return;
            }

            ResetUiAnchors(player, false);
        }

        private void ApplyUiOffset(BasePlayer player, float dx, float dy, bool notifyPlayer)
        {
            float minX, minY, maxX, maxY;
            if (!TryParseAnchor(configData.UiAnchorMin, out minX, out minY) ||
                !TryParseAnchor(configData.UiAnchorMax, out maxX, out maxY))
            {
                minX = 0.4f; minY = 0.92f;
                maxX = 0.6f; maxY = 0.98f;
            }

            minX = Clamp01(minX + dx);
            maxX = Clamp01(maxX + dx);
            minY = Clamp01(minY + dy);
            maxY = Clamp01(maxY + dy);

            if (minX >= maxX)
                maxX = Clamp01(minX + 0.05f);
            if (minY >= maxY)
                maxY = Clamp01(minY + 0.05f);

            configData.UiAnchorMin = ToAnchorString(minX, minY);
            configData.UiAnchorMax = ToAnchorString(maxX, maxY);
            SaveConfig(configData);

            if (player != null)
            {
                DestroyWipeTimerUI(player);
                if (notifyPlayer)
                {
                    SendReply(player, Msg("Config.UIAdd.Set", player.UserIDString,
                        dx, dy, minX, minY, maxX, maxY));
                    SendReply(player, "Reopen a Tool Cupboard to see the new UI position.");
                }
            }
        }

        private void ResetUiAnchors(BasePlayer player, bool notifyPlayer)
        {
            configData.UiAnchorMin = "0.4 0.92";
            configData.UiAnchorMax = "0.6 0.98";
            SaveConfig(configData);

            if (player != null)
            {
                DestroyWipeTimerUI(player);
                if (notifyPlayer)
                {
                    SendReply(player, Msg("Config.UIReset.Set", player.UserIDString));
                }
            }
            else
            {
                Puts("ModernNoCupboardDecay: UI anchors reset to default.");
            }
        }

        // ----------------------
        //  Debug Overlay Commands
        // ----------------------

        [ChatCommand("mncddebug")]
        private void ChatCommandMncdDebug(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!HasDebugPerm(player))
            {
                SendReply(player, Msg("Error.NoDebugPermission", player.UserIDString));
                return;
            }

            ToggleDebugOverlay(player);
        }

        [ConsoleCommand("mncd.debug")]
        private void ConsoleCommandMncdDebug(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null)
            {
                Puts("mncd.debug can only be used in-game by a player.");
                return;
            }

            if (!HasDebugPerm(player))
            {
                SendReply(player, Msg("Error.NoDebugPermission", player.UserIDString));
                return;
            }

            ToggleDebugOverlay(player);
        }

        private void ToggleDebugOverlay(BasePlayer player)
        {
            ulong id = player.userID;
            if (debugOverlayUsers.Contains(id))
            {
                DisableDebugOverlay(player);
                SendReply(player, Msg("Debug.Disabled", player.UserIDString));
            }
            else
            {
                EnableDebugOverlay(player);
                SendReply(player, Msg("Debug.Enabled", player.UserIDString));
            }
        }

        private void EnableDebugOverlay(BasePlayer player)
        {
            ulong id = player.userID;

            debugOverlayUsers.Add(id);

            if (debugOverlayTimers.TryGetValue(id, out var existing))
            {
                existing.Destroy();
            }

            debugOverlayTimers[id] = timer.Every(0.5f, () =>
            {
                if (player == null || !player.IsConnected || player.IsDead())
                {
                    DisableDebugOverlay(player);
                    return;
                }

                UpdateDebugOverlayUI(player);
            });
        }

        private void DisableDebugOverlay(BasePlayer player)
        {
            if (player == null)
                return;

            ulong id = player.userID;

            debugOverlayUsers.Remove(id);

            if (debugOverlayTimers.TryGetValue(id, out var t))
            {
                t.Destroy();
                debugOverlayTimers.Remove(id);
            }

            DestroyDebugOverlayUI(player);
        }

        private void UpdateDebugOverlayUI(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            bool isProtected = IsPlayerInProtectedZone(player);
            string text = isProtected ? Msg("Debug.UI.Protected", player.UserIDString) : Msg("Debug.UI.NotProtected", player.UserIDString);

            var container = new CuiElementContainer();

            var panel = new CuiElement
            {
                Name = DebugUiName,
                Parent = "Overlay",
                Components =
                {
                    new CuiImageComponent
                    {
                        Color = "0 0 0 0.4"
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.4 0.96",
                        AnchorMax = "0.6 0.99"
                    }
                }
            };
            container.Add(panel);

            container.Add(new CuiElement
            {
                Parent = DebugUiName,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = text,
                        FontSize = 12,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1"
                    }
                }
            });

            DestroyDebugOverlayUI(player);
            CuiHelper.AddUi(player, container);
        }

        private void DestroyDebugOverlayUI(BasePlayer player)
        {
            if (player == null)
                return;

            CuiHelper.DestroyUi(player, DebugUiName);
        }

        // ----------------------
        //  TC Preview (ddraw sphere)
        // ----------------------

        [ChatCommand("mncdpreview")]
        private void ChatCommandMncdPreview(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!HasPreviewPerm(player))
            {
                SendReply(player, Msg("Preview.NoPerm", player.UserIDString));
                return;
            }

            DrawTcPreview(player);
        }

        [ConsoleCommand("mncd.preview")]
        private void ConsoleCommandMncdPreview(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null)
            {
                Puts("mncd.preview can only be used in-game by a player.");
                return;
            }

            if (!HasPreviewPerm(player))
            {
                SendReply(player, Msg("Preview.NoPerm", player.UserIDString));
                return;
            }

            DrawTcPreview(player);
        }

        private void DrawTcPreview(BasePlayer player)
        {
            float radius = configData.EntityRadius * Mathf.Max(0.1f, configData.PreviewRingRadiusMultiplier);
            float duration = Mathf.Max(1f, configData.PreviewRingDuration);

            var tcs = BaseNetworkable.serverEntities
                .OfType<BuildingPrivlidge>()
                .ToList();

            if (tcs.Count == 0)
            {
                SendReply(player, Msg("Preview.NoTC", player.UserIDString));
                return;
            }

            foreach (var tc in tcs)
            {
                var pos = tc.transform.position;
                player.SendConsoleCommand("ddraw.sphere",
                    duration,
                    0f, 1f, 0f, 1f,
                    pos.x, pos.y, pos.z,
                    radius);
            }

            SendReply(player, Msg("Preview.Drawn", player.UserIDString, tcs.Count, duration, radius));
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

                if (currentWipeMode == WipeMode.CustomDays && configData.CustomWipeDays > 0)
                    wipeModeStr += $" ({configData.CustomWipeDays}d)";

                if (remaining == null)
                    SendReply(player, Msg("TcLoot.NoRemaining", userId, wipeModeStr));
                else
                    SendReply(player, Msg("TcLoot.WithRemaining", userId, wipeModeStr, FormatTimeSpan(remaining.Value)));

                return;
            }

            var wipeRemaining = GetWipeTimeRemaining();
            string mode = currentWipeMode.ToString();
            if (currentWipeMode == WipeMode.CustomDays && configData.CustomWipeDays > 0)
                mode += $" ({configData.CustomWipeDays}d)";

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
            DisableDebugOverlay(player);
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
