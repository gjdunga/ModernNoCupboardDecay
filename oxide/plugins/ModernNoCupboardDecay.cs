using System;
using System.Collections.Generic;
using System.Globalization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ModernNoCupboardDecay", "Gabriel", "5.0.0")]
    [Description("Prevents decay within Tool Cupboard radius. Wipe-aware UI, team auth, debug tools. Oxide 2.0.7022+ / Naval Update compatible.")]
    public class ModernNoCupboardDecay : RustPlugin
    {
        #region Fields

        private ConfigData _config;
        private bool _initialized;
        private string _statusMsg = "Plugin not yet initialized.";
        private WipeMode _wipeMode = WipeMode.Manual;
        private int _protectionMask;

        private const string PermAdmin = "modernnocupboarddecay.admin";
        private const string PermDebug = "modernnocupboarddecay.debug";
        private const string PermPreview = "modernnocupboarddecay.preview";

        private const string UiWipe = "MNCD_WipeTimer";
        private const string UiDebug = "MNCD_DebugOverlay";

        // Pre-allocated buffer for Physics.OverlapSphereNonAlloc — zero GC in hot path
        private static readonly Collider[] HitBuffer = new Collider[512];

        private readonly HashSet<ulong> _debugUsers = new HashSet<ulong>();
        private readonly Dictionary<ulong, Timer> _debugTimers = new Dictionary<ulong, Timer>();

        private enum WipeMode { Manual, Weekly, BiWeekly, Monthly, CustomDays }

        #endregion

        #region Config

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
            _config = new ConfigData();
        }

        private void LoadConfigData()
        {
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null)
                    throw new Exception("Config deserialized to null.");
            }
            catch (Exception e)
            {
                PrintWarning($"Config load error, using defaults: {e.Message}");
                LoadDefaultConfig();
            }

            ValidateConfig();
            SaveConfig(_config);
            _statusMsg = "Configuration loaded.";
        }

        private void ValidateConfig()
        {
            _config.EntityRadius = Mathf.Clamp(_config.EntityRadius, 1f, 500f);
            _config.CustomWipeDays = Mathf.Clamp(_config.CustomWipeDays, 0, 365);
            _config.PreviewRingDuration = Mathf.Clamp(_config.PreviewRingDuration, 1f, 300f);
            _config.PreviewRingRadiusMultiplier = Mathf.Clamp(_config.PreviewRingRadiusMultiplier, 0.1f, 10f);
        }

        private void SaveConfig(ConfigData cfg) => Config.WriteObject(cfg, true);

        #endregion

        #region Localization

        private void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Status.Report"] = "[{0}] v{1} | State: {2} | Radius: {3} | CheckAuth: {4} | TeamAware: {5} | AutoDetect: {6} | WipeMode: {7} (Override: {8}) | WipeRemaining: {9} | Status: {10}",

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

                ["Usage.MncdSet.Chat"] = "[MNCD] Usage: /mncdset <option> <value>. Options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Usage.MncdSet.Console"] = "[MNCD] Usage: mncd.set <option> <value>. Options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Usage.UIAdd.Chat"] = "[MNCD] Usage: /mncduiadd <deltaX> <deltaY> (normalized 0..1 offsets).",
                ["Usage.UIAdd.Console"] = "[MNCD] Usage: mncd.uiadd <deltaX> <deltaY> (normalized 0..1 offsets).",
                ["Usage.UIReset.Chat"] = "[MNCD] Usage: /mncdresetui (resets to plugin default UI position).",
                ["Usage.UIReset.Console"] = "[MNCD] Usage: mncd.resetui (resets to plugin default UI position).",

                ["Config.CheckAuth.Set"] = "[MNCD] CheckAuth is now {0}.",
                ["Config.TeamAware.Set"] = "[MNCD] TeamAwareProtection is now {0}.",
                ["Config.Radius.Set"] = "[MNCD] EntityRadius (TC decay bubble) is now {0} meters.",
                ["Config.AutoDetect.Set"] = "[MNCD] AutoDetectWipeFromTags is now {0}. Active WipeMode: {1}.",
                ["Config.WipeMode.Set"] = "[MNCD] WipeModeOverride set to '{0}'. Active WipeMode is now {1}, AutoDetect disabled.",
                ["Config.WipeStartNow.Set"] = "[MNCD] WipeStartUnixTime set to now. WipeMode: {0}. WipeRemaining: {1}.",
                ["Config.UIReset.Set"] = "[MNCD] UI anchors reset to default. Reopen a Tool Cupboard to see the new position.",
                ["Config.UIAdd.Set"] = "[MNCD] UI anchors shifted by ({0}, {1}). New Min({2}, {3}) Max({4}, {5}).",

                ["TcLoot.NoRemaining"] = "[MNCD] This TC's radius is fully protected from decay.\nWipe schedule: {0}. Wipe end not calculated (manual or missing data).",
                ["TcLoot.WithRemaining"] = "[MNCD] This TC's radius is fully protected from decay.\nWipe schedule: {0}. Wipe ends in: {1}.",

                ["UI.WipeTitle"] = "ModernNoCupboardDecay",
                ["UI.WipeLine"] = "Wipe: {0} \u2013 {1} remaining",
                ["UI.WipeExtra"] = "Decay disabled within this Tool Cupboard radius.",

                ["UIAnchor.Usage.Chat"] = "[MNCD] Usage: /mncdui <minX> <minY> <maxX> <maxY>",
                ["UIAnchor.Usage.Console"] = "[MNCD] Usage: mncd.ui <minX> <minY> <maxX> <maxY>",
                ["UIAnchor.Set"] = "[MNCD] UI Anchor set: Min({0}, {1})  Max({2}, {3})",
                ["UIAnchor.Error.Numeric"] = "[MNCD] All 4 values must be numeric.",
                ["UIAnchor.Error.Order"] = "[MNCD] minX < maxX and minY < maxY is required.",

                ["Debug.Enabled"] = "[MNCD] Debug overlay enabled. Shows whether you are inside a protection zone.",
                ["Debug.Disabled"] = "[MNCD] Debug overlay disabled.",
                ["Debug.UI.Protected"] = "MNCD: Protected",
                ["Debug.UI.NotProtected"] = "MNCD: Not Protected",

                ["Preview.NoTC"] = "[MNCD] No Tool Cupboards found. Nothing to preview.",
                ["Preview.Drawn"] = "[MNCD] Drawing TC protection rings for {0} cupboards for {1} seconds (radius {2}m).",
                ["Preview.NoPerm"] = "[MNCD] You do not have permission to use /mncdpreview.",

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
                    "  All entities within the configured radius of a Tool Cupboard are protected from decay.\n" +
                    "  Optional CheckAuth: only entities owned by a TC authed player (or their team) are protected.\n" +
                    "  TeamAwareProtection: when enabled, TC owner's Rust team and authed teammates are also covered.\n" +
                    "  The TC upkeep 'time left' becomes 'time until wipe' in the MNCD UI.\n" +
                    "Useful commands:\n" +
                    "  /mncd           - Show current radius, wipe mode, and status.\n" +
                    "  /mncddebug      - See if you are currently inside a protected bubble.",

                ["Help.Topic.ui"] =
                    "UI positioning:\n" +
                    "  /mncdui <minX> <minY> <maxX> <maxY>\n" +
                    "    Sets the TC wipe UI panel anchors in normalized screen coords (0..1).\n" +
                    "    Example: /mncdui 0.70 0.27 0.95 0.49\n" +
                    "  /mncduiadd <dx> <dy>\n" +
                    "    Nudges the UI panel by a small offset.\n" +
                    "    Example: /mncduiadd -0.02 0.10 moves it 2% left, 10% up.\n" +
                    "  /mncdresetui\n" +
                    "    Resets the UI to the default top-center position.\n" +
                    "Notes:\n" +
                    "  Requires admin or 'modernnocupboarddecay.admin' permission.\n" +
                    "  After changing UI, reopen the TC to refresh the panel.",

                ["Help.Topic.set"] =
                    "Live configuration: /mncdset and mncd.set\n\n" +
                    "Chat (admin):\n" +
                    "  /mncdset <option> <value>\n" +
                    "Console/RCON:\n" +
                    "  mncd.set <option> <value>\n\n" +
                    "Options:\n" +
                    "  checkauth <true|false>\n" +
                    "    true  => only entities owned by TC-authed players (and optional teams) are protected.\n" +
                    "    false => anything near a TC is protected.\n" +
                    "  teamaware <true|false>\n" +
                    "    When checkauth is true, also protect Rust teammates of TC owner/authed.\n" +
                    "  radius <meters>\n" +
                    "    Sets TC protection radius. Example: /mncdset radius 30\n" +
                    "  autodetect <true|false>\n" +
                    "    true  => detect wipe schedule from server.tags (weekly, monthly, 5d, etc.).\n" +
                    "    false => use WipeModeOverride.\n" +
                    "  wipemode <Manual|Weekly|BiWeekly|Monthly|Nd>\n" +
                    "    Example: /mncdset wipemode 5d (for 5-day wipes).\n" +
                    "  wipestartnow\n" +
                    "    Resets wipe start to now. Use if you change wipe schedule mid-wipe.\n",

                ["Help.Topic.debug"] =
                    "Debug overlay: /mncddebug and mncd.debug\n\n" +
                    "  /mncddebug\n" +
                    "    Toggles a small text panel at the top of your screen:\n" +
                    "      'MNCD: Protected'    => you are inside a protected TC radius.\n" +
                    "      'MNCD: Not Protected' => you are outside MNCD protection.\n" +
                    "  mncd.debug (console)\n" +
                    "    Same as /mncddebug, but from the F1 console for that player.\n\n" +
                    "Notes:\n" +
                    "  Requires admin or 'modernnocupboarddecay.debug' permission.\n" +
                    "  Updates every 0.5 seconds while enabled.\n" +
                    "  Automatically stops and cleans up when you disconnect.",

                ["Help.Topic.preview"] =
                    "TC preview rings: /mncdpreview and mncd.preview\n\n" +
                    "  /mncdpreview\n" +
                    "    Draws 'hologram' spheres around all Tool Cupboards.\n" +
                    "    Uses the configured EntityRadius (optionally multiplied by PreviewRingRadiusMultiplier).\n" +
                    "    Only you see the rings; they are client-side ddraw.\n\n" +
                    "  mncd.preview (console)\n" +
                    "    Same effect, invoked from the F1 console.\n\n" +
                    "Config:\n" +
                    "  PreviewRequiresPermission\n" +
                    "    false (default) => everyone can use /mncdpreview.\n" +
                    "    true            => requires 'modernnocupboarddecay.preview' perm or admin.\n" +
                    "  PreviewRingDuration\n" +
                    "    How long rings are visible (seconds).\n" +
                    "  PreviewRingRadiusMultiplier\n" +
                    "    Multiplies EntityRadius for the visual ring only.\n",

                ["Help.Topic.wipe"] =
                    "Wipe modes and wipe timer:\n\n" +
                    "Detection:\n" +
                    "  AutoDetectWipeFromTags = true:\n" +
                    "    Reads ConVar.Server.tags for keywords: weekly, biweekly, monthly.\n" +
                    "    Also supports patterns like '5d', '5day', '5days' for custom N-day wipes.\n" +
                    "  AutoDetectWipeFromTags = false:\n" +
                    "    Uses WipeModeOverride from config or /mncdset wipemode.\n\n" +
                    "Supported modes:\n" +
                    "  Manual      => no automatic wipe end time.\n" +
                    "  Weekly      => 7-day wipes.\n" +
                    "  BiWeekly    => 14-day wipes.\n" +
                    "  Monthly     => 30-day wipes (fixed length).\n" +
                    "  Nd (Custom) => e.g. '5d' => 5-day wipes.\n\n" +
                    "Wipe start:\n" +
                    "  OnNewSave (map/wipe) automatically sets WipeStartUnixTime.\n" +
                    "  /mncdset wipestartnow can manually reset the wipe start.\n\n" +
                    "TC UI:\n" +
                    "  When you open a TC, MNCD shows 'Wipe: <mode> - <time remaining>'.\n" +
                    "  If mode is Manual or time cannot be computed, it shows N/A instead.",

            }, this);
        }

        private string Msg(string key, string userId = null, params object[] args)
        {
            string msg = lang.GetMessage(key, this, userId);
            if (args == null || args.Length == 0)
                return msg;

            try
            {
                return string.Format(msg, args);
            }
            catch (FormatException)
            {
                return msg;
            }
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            _initialized = false;
            _statusMsg = "Initializing...";

            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermDebug, this);
            permission.RegisterPermission(PermPreview, this);

            LoadDefaultMessages();
        }

        private void OnServerInitialized()
        {
            try
            {
                _protectionMask = LayerMask.GetMask("Construction", "Construction Trigger", "Trigger", "Deployed");

                LoadConfigData();
                DetectWipeModeFromTagsOrConfig();

                if (_config.WipeStartUnixTime <= 0)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _config.WipeStartUnixTime = now;
                    SaveConfig(_config);
                    _statusMsg = $"Wipe start initialized at {DateTimeOffset.FromUnixTimeSeconds(now):u}.";
                }

                _initialized = true;

                if (_statusMsg.StartsWith("Initializing", StringComparison.OrdinalIgnoreCase))
                    _statusMsg = "Initialized successfully.";

                PrintDebugSummary();
            }
            catch (Exception ex)
            {
                _initialized = false;
                _statusMsg = $"Init error: {ex.Message}";
                PrintError(_statusMsg);
            }
        }

        private void Unload()
        {
            // Destroy all active timers
            foreach (var kvp in _debugTimers)
                kvp.Value?.Destroy();

            _debugTimers.Clear();
            _debugUsers.Clear();

            // Clean up UI for all connected players
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;
                CuiHelper.DestroyUi(player, UiWipe);
                CuiHelper.DestroyUi(player, UiDebug);
            }
        }

        private void OnNewSave(string filename)
        {
            if (_config == null) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _config.WipeStartUnixTime = now;
            SaveConfig(_config);
            _statusMsg = $"New save '{filename}', wipe start reset to {DateTimeOffset.FromUnixTimeSeconds(now):u}.";
        }

        #endregion

        #region Wipe Detection

        private void DetectWipeModeFromTagsOrConfig()
        {
            WipeMode detected = WipeMode.Manual;

            if (_config.AutoDetectWipeFromTags)
            {
                try
                {
                    string tags = ConVar.Server.tags ?? string.Empty;

                    if (!string.IsNullOrEmpty(tags))
                    {
                        string lower = tags.ToLowerInvariant();

                        if (lower.Contains("weekly"))
                            detected = WipeMode.Weekly;
                        else if (lower.Contains("biweekly") || lower.Contains("bi-weekly"))
                            detected = WipeMode.BiWeekly;
                        else if (lower.Contains("monthly"))
                            detected = WipeMode.Monthly;
                        else
                        {
                            int customDays = TryExtractDaysFromTags(lower);
                            if (customDays > 0)
                            {
                                detected = WipeMode.CustomDays;
                                _config.CustomWipeDays = customDays;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    PrintWarning($"Error reading server tags: {e.Message}");
                }
            }

            if (detected != WipeMode.Manual)
            {
                _wipeMode = detected;
                _statusMsg = $"Wipe mode auto-detected: {_wipeMode}" +
                    (_wipeMode == WipeMode.CustomDays && _config.CustomWipeDays > 0
                        ? $" ({_config.CustomWipeDays} days)"
                        : ".");
                return;
            }

            _wipeMode = ParseWipeMode(_config.WipeModeOverride);
            _statusMsg = $"Wipe mode from config: {_wipeMode}" +
                (_wipeMode == WipeMode.CustomDays && _config.CustomWipeDays > 0
                    ? $" ({_config.CustomWipeDays} days)"
                    : ".");
        }

        private int TryExtractDaysFromTags(string lowerTags)
        {
            if (string.IsNullOrEmpty(lowerTags))
                return 0;

            var pieces = lowerTags.Split(',');
            for (int i = 0; i < pieces.Length; i++)
            {
                string p = pieces[i].Trim();
                if (string.IsNullOrEmpty(p))
                    continue;

                var tokens = p.Split(' ', '-', '_');
                for (int j = 0; j < tokens.Length; j++)
                {
                    string t = tokens[j].Trim();
                    if (string.IsNullOrEmpty(t))
                        continue;

                    if (t.EndsWith("days"))
                        t = t.Substring(0, t.Length - 4);
                    else if (t.EndsWith("day"))
                        t = t.Substring(0, t.Length - 3);
                    else if (t.EndsWith("d"))
                        t = t.Substring(0, t.Length - 1);

                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days)
                        && days >= 2 && days <= 60)
                        return days;
                }
            }

            return 0;
        }

        private WipeMode ParseWipeMode(string mode)
        {
            if (string.IsNullOrEmpty(mode))
                return WipeMode.Manual;

            string lower = mode.Trim().ToLowerInvariant();

            string numPart = lower;
            if (numPart.EndsWith("days"))
                numPart = numPart.Substring(0, numPart.Length - 4);
            else if (numPart.EndsWith("day"))
                numPart = numPart.Substring(0, numPart.Length - 3);
            else if (numPart.EndsWith("d"))
                numPart = numPart.Substring(0, numPart.Length - 1);

            if (int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days > 0)
            {
                _config.CustomWipeDays = days;
                return WipeMode.CustomDays;
            }

            switch (lower)
            {
                case "weekly":     return WipeMode.Weekly;
                case "biweekly":
                case "bi-weekly":  return WipeMode.BiWeekly;
                case "monthly":    return WipeMode.Monthly;
                default:           return WipeMode.Manual;
            }
        }

        private TimeSpan GetWipeDuration()
        {
            switch (_wipeMode)
            {
                case WipeMode.Weekly:     return TimeSpan.FromDays(7);
                case WipeMode.BiWeekly:   return TimeSpan.FromDays(14);
                case WipeMode.Monthly:    return TimeSpan.FromDays(30);
                case WipeMode.CustomDays:
                    return _config.CustomWipeDays > 0
                        ? TimeSpan.FromDays(_config.CustomWipeDays)
                        : TimeSpan.Zero;
                default: return TimeSpan.Zero;
            }
        }

        private long GetWipeEndUnixTime()
        {
            if (_config.WipeStartUnixTime <= 0)
                return 0;

            TimeSpan dur = GetWipeDuration();
            if (dur == TimeSpan.Zero)
                return 0;

            return _config.WipeStartUnixTime + (long)dur.TotalSeconds;
        }

        private TimeSpan? GetWipeTimeRemaining()
        {
            long wipeEnd = GetWipeEndUnixTime();
            if (wipeEnd <= 0)
                return null;

            long delta = wipeEnd - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return delta <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(delta);
        }

        private string FormatTimeSpan(TimeSpan ts)
        {
            return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m";
        }

        private string GetWipeModeDisplayString()
        {
            string mode = _wipeMode.ToString();
            if (_wipeMode == WipeMode.CustomDays && _config.CustomWipeDays > 0)
                mode += $" ({_config.CustomWipeDays}d)";
            return mode;
        }

        private void PrintDebugSummary()
        {
            Puts("[ModernNoCupboardDecay] Config loaded:");
            Puts($"  Version: {Version}");
            Puts($"  CheckAuth: {_config.CheckAuth} | TeamAware: {_config.TeamAwareProtection}");
            Puts($"  EntityRadius: {_config.EntityRadius}");
            Puts($"  AutoDetect: {_config.AutoDetectWipeFromTags} | WipeMode: {_wipeMode}");
            Puts($"  WipeStart: {(_config.WipeStartUnixTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(_config.WipeStartUnixTime).ToString("u") : "unset")}");

            var remaining = GetWipeTimeRemaining();
            Puts(remaining != null
                ? $"  Wipe ends in: {FormatTimeSpan(remaining.Value)}"
                : "  Wipe end: N/A (manual or missing data).");
        }

        #endregion

        #region Core Decay Logic

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_initialized || entity == null || info == null)
                return null;

            if (!info.damageTypes.Has(DamageType.Decay))
                return null;

            if (IsPositionProtected(entity.transform.position, GetOwnerId(entity, info)))
                info.damageTypes.Scale(DamageType.Decay, 0f);

            return null;
        }

        #endregion

        #region Cupboard Protection

        private static ulong GetOwnerId(BaseCombatEntity entity, HitInfo info)
        {
            if (entity != null && entity.OwnerID != 0)
                return entity.OwnerID;

            if (info?.HitEntity != null && info.HitEntity.OwnerID != 0)
                return info.HitEntity.OwnerID;

            return 0;
        }

        /// <summary>
        /// Unified protection check used by both decay prevention and debug overlay.
        /// Searches for BuildingPrivlidge within the configured radius using a
        /// pre-allocated collider buffer (zero GC allocation).
        /// </summary>
        private bool IsPositionProtected(Vector3 position, ulong ownerId)
        {
            int count = Physics.OverlapSphereNonAlloc(position, _config.EntityRadius, HitBuffer, _protectionMask);

            if (!_config.CheckAuth)
            {
                // Fast path: any TC in range means protected
                for (int i = 0; i < count; i++)
                {
                    if (HitBuffer[i].GetComponentInParent<BuildingPrivlidge>() != null)
                        return true;
                }
                return false;
            }

            // Auth path: entity owner must be authed or on the team of an authed player
            if (ownerId == 0)
                return false;

            for (int i = 0; i < count; i++)
            {
                var priv = HitBuffer[i].GetComponentInParent<BuildingPrivlidge>();
                if (priv != null && IsOwnerAuthorizedOrTeammate(priv, ownerId))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if ownerId is directly authorized on the TC, or is a Rust team
        /// member of someone who is authorized.
        /// Uses ProtoBuf.PlayerNameID.userid for Oxide v2.0.7022+ compatibility.
        /// </summary>
        private bool IsOwnerAuthorizedOrTeammate(BuildingPrivlidge priv, ulong ownerId)
        {
            if (priv == null || ownerId == 0)
                return false;

            // Direct auth check
            if (IsAuthorizedOnCupboard(priv, ownerId))
                return true;

            if (!_config.TeamAwareProtection)
                return false;

            // Team-aware check
            var rm = RelationshipManager.ServerInstance;
            if (rm == null)
                return false;

            var ownerTeam = rm.FindPlayersTeam(ownerId);
            if (ownerTeam == null)
                return false;

            // Check if TC owner is on the entity owner's team
            if (priv.OwnerID != 0 && ownerTeam.members.Contains(priv.OwnerID))
                return true;

            // Check if any authed player is on the entity owner's team
            var authList = priv.authorizedPlayers;
            if (authList != null)
            {
                for (int i = 0; i < authList.Count; i++)
                {
                    if (ownerTeam.members.Contains(authList[i].userid))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks the TC authorized player list using PlayerNameID.userid.
        /// This is the correct access pattern for Oxide v2.0.7022+ where
        /// authorizedPlayers is List&lt;ProtoBuf.PlayerNameID&gt;.
        /// </summary>
        private bool IsAuthorizedOnCupboard(BuildingPrivlidge priv, ulong ownerId)
        {
            var authList = priv.authorizedPlayers;
            if (authList == null || authList.Count == 0)
                return false;

            for (int i = 0; i < authList.Count; i++)
            {
                if (authList[i].userid == ownerId)
                    return true;
            }

            return false;
        }

        #endregion

        #region Permissions & Helpers

        private bool HasAdminPerm(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));
        }

        private bool HasDebugPerm(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermDebug));
        }

        private bool HasPreviewPerm(BasePlayer player)
        {
            if (player == null) return false;
            if (!_config.PreviewRequiresPermission) return true;
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermPreview);
        }

        private bool TryParseBool(string value, out bool result)
        {
            result = false;
            if (string.IsNullOrEmpty(value))
                return false;

            switch (value.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on":
                    result = true;
                    return true;
                case "false": case "0": case "no": case "off":
                    result = false;
                    return true;
                default:
                    return false;
            }
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

        private void Reply(BasePlayer player, ConsoleSystem.Arg arg, string message)
        {
            if (player != null)
                SendReply(player, message);
            else if (arg != null)
                Puts(message);
        }

        #endregion

        #region Status Commands

        [ChatCommand("mncd")]
        private void CmdMncdChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            SendReply(player, GetStatusReport(player.UserIDString));
        }

        [ConsoleCommand("mncd")]
        private void CmdMncdConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var report = GetStatusReport(player?.UserIDString);
            Reply(player, arg, report);
        }

        private string GetStatusReport(string userId = null)
        {
            string initState = _initialized ? "ENABLED" : "NOT INITIALIZED";
            string wipeRemain = "N/A";
            var remaining = GetWipeTimeRemaining();
            if (remaining != null)
                wipeRemain = FormatTimeSpan(remaining.Value);

            return Msg("Status.Report", userId,
                Title, Version,
                initState, _config.EntityRadius,
                _config.CheckAuth, _config.TeamAwareProtection,
                _config.AutoDetectWipeFromTags, GetWipeModeDisplayString(),
                _config.WipeModeOverride, wipeRemain, _statusMsg);
        }

        #endregion

        #region Help Commands

        [ChatCommand("mncdhelp")]
        private void CmdHelpChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            string topic = (args != null && args.Length > 0) ? args[0].ToLowerInvariant() : "general";
            SendReply(player, BuildHelpText(topic, player.UserIDString));
        }

        [ConsoleCommand("mncd.help")]
        private void CmdHelpConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            string topic = (arg?.Args != null && arg.Args.Length > 0)
                ? arg.Args[0].ToLowerInvariant()
                : "general";

            Reply(player, arg, BuildHelpText(topic, player?.UserIDString));
        }

        private string BuildHelpText(string topic, string userId)
        {
            string header = Msg("Help.Header", userId, Version.ToString());

            switch (topic)
            {
                case "general": case "main": case "help":
                    return header + "\n" + Msg("Help.General", userId);
                case "basic":
                    return header + "\n" + Msg("Help.Topic.basic", userId);
                case "ui": case "panel":
                    return header + "\n" + Msg("Help.Topic.ui", userId);
                case "set": case "config": case "settings":
                    return header + "\n" + Msg("Help.Topic.set", userId);
                case "debug":
                    return header + "\n" + Msg("Help.Topic.debug", userId);
                case "preview": case "ring": case "radius":
                    return header + "\n" + Msg("Help.Topic.preview", userId);
                case "wipe": case "wipemode": case "wipes":
                    return header + "\n" + Msg("Help.Topic.wipe", userId);
                default:
                    return header + "\n" + Msg("Help.UnknownTopic", userId, topic);
            }
        }

        #endregion

        #region Config Commands

        [ChatCommand("mncdset")]
        private void CmdSetChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

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

            string result = ApplyConfigChange(args[0], args.Length > 1 ? args[1] : null, player.UserIDString);
            SendReply(player, result);
        }

        [ConsoleCommand("mncd.set")]
        private void CmdSetConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var userId = player?.UserIDString;

            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", userId));
                return;
            }

            if (arg?.Args == null || arg.Args.Length == 0)
            {
                Reply(player, arg, Msg("Usage.MncdSet.Console", userId));
                return;
            }

            string result = ApplyConfigChange(arg.Args[0], arg.Args.Length > 1 ? arg.Args[1] : null, userId);
            Reply(player, arg, result);
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
                        _config.CheckAuth = b;
                        SaveConfig(_config);
                        _statusMsg = $"CheckAuth set to {b}.";
                        return Msg("Config.CheckAuth.Set", userId, b);
                    }

                    case "teamaware":
                    case "teamauth":
                    case "team":
                    {
                        if (!TryParseBool(value, out bool b))
                            return Msg("Error.TeamAwareValue", userId);
                        _config.TeamAwareProtection = b;
                        SaveConfig(_config);
                        _statusMsg = $"TeamAwareProtection set to {b}.";
                        return Msg("Config.TeamAware.Set", userId, b);
                    }

                    case "radius":
                    case "entityradius":
                    {
                        if (string.IsNullOrEmpty(value) ||
                            !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ||
                            r <= 0f)
                            return Msg("Error.RadiusValue", userId);

                        r = Mathf.Clamp(r, 1f, 500f);
                        _config.EntityRadius = r;
                        SaveConfig(_config);
                        _statusMsg = $"EntityRadius set to {r}.";
                        return Msg("Config.Radius.Set", userId, r);
                    }

                    case "autodetect":
                    case "autotags":
                    {
                        if (!TryParseBool(value, out bool b))
                            return Msg("Error.AutoDetectValue", userId);
                        _config.AutoDetectWipeFromTags = b;
                        SaveConfig(_config);
                        DetectWipeModeFromTagsOrConfig();
                        return Msg("Config.AutoDetect.Set", userId, b, _wipeMode);
                    }

                    case "wipemode":
                    case "wipeoverride":
                    {
                        if (string.IsNullOrEmpty(value))
                            return Msg("Error.WipeModeValue", userId);
                        _config.WipeModeOverride = value;
                        _config.AutoDetectWipeFromTags = false;
                        SaveConfig(_config);
                        DetectWipeModeFromTagsOrConfig();
                        return Msg("Config.WipeMode.Set", userId, value, _wipeMode);
                    }

                    case "wipestartnow":
                    case "resetwipe":
                    case "wipe-reset":
                    {
                        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        _config.WipeStartUnixTime = now;
                        SaveConfig(_config);
                        _statusMsg = $"WipeStartUnixTime reset to {DateTimeOffset.FromUnixTimeSeconds(now):u}.";
                        var remaining = GetWipeTimeRemaining();
                        string remainStr = remaining != null ? FormatTimeSpan(remaining.Value) : "N/A";
                        return Msg("Config.WipeStartNow.Set", userId, _wipeMode, remainStr);
                    }

                    default:
                        return Msg("Error.ConfigOption", userId);
                }
            }
            catch (Exception e)
            {
                _statusMsg = $"Config error on '{opt}': {e.Message}";
                return Msg("Error.ConfigApply", userId, opt, e.Message);
            }
        }

        #endregion

        #region UI Anchor Commands

        [ChatCommand("mncdui")]
        private void CmdUiChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

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

            if (!TryParseFloat(args[0], out float minX) || !TryParseFloat(args[1], out float minY) ||
                !TryParseFloat(args[2], out float maxX) || !TryParseFloat(args[3], out float maxY))
            {
                SendReply(player, Msg("UIAnchor.Error.Numeric", player.UserIDString));
                return;
            }

            if (minX >= maxX || minY >= maxY)
            {
                SendReply(player, Msg("UIAnchor.Error.Order", player.UserIDString));
                return;
            }

            SetUiAnchors(minX, minY, maxX, maxY);
            SendReply(player, Msg("UIAnchor.Set", player.UserIDString, minX, minY, maxX, maxY));
            DestroyWipeTimerUI(player);
        }

        [ConsoleCommand("mncd.ui")]
        private void CmdUiConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var userId = player?.UserIDString;

            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", userId));
                return;
            }

            if (arg?.Args == null || arg.Args.Length != 4)
            {
                Reply(player, arg, Msg("UIAnchor.Usage.Console", userId));
                return;
            }

            if (!TryParseFloat(arg.Args[0], out float minX) || !TryParseFloat(arg.Args[1], out float minY) ||
                !TryParseFloat(arg.Args[2], out float maxX) || !TryParseFloat(arg.Args[3], out float maxY))
            {
                Reply(player, arg, Msg("UIAnchor.Error.Numeric", userId));
                return;
            }

            if (minX >= maxX || minY >= maxY)
            {
                Reply(player, arg, Msg("UIAnchor.Error.Order", userId));
                return;
            }

            SetUiAnchors(minX, minY, maxX, maxY);
            string confirm = Msg("UIAnchor.Set", userId, minX, minY, maxX, maxY);
            Reply(player, arg, confirm);

            if (player != null)
                DestroyWipeTimerUI(player);
        }

        [ChatCommand("mncduiadd")]
        private void CmdUiAddChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

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

            ApplyUiOffset(dx, dy);
            float minX, minY, maxX, maxY;
            GetCurrentAnchors(out minX, out minY, out maxX, out maxY);
            SendReply(player, Msg("Config.UIAdd.Set", player.UserIDString, dx, dy, minX, minY, maxX, maxY));
            DestroyWipeTimerUI(player);
        }

        [ConsoleCommand("mncd.uiadd")]
        private void CmdUiAddConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var userId = player?.UserIDString;

            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", userId));
                return;
            }

            if (arg?.Args == null || arg.Args.Length != 2)
            {
                Reply(player, arg, Msg("Usage.UIAdd.Console", userId));
                return;
            }

            if (!TryParseFloat(arg.Args[0], out float dx) || !TryParseFloat(arg.Args[1], out float dy))
            {
                Reply(player, arg, Msg("UIAnchor.Error.Numeric", userId));
                return;
            }

            ApplyUiOffset(dx, dy);
            float minX, minY, maxX, maxY;
            GetCurrentAnchors(out minX, out minY, out maxX, out maxY);
            Reply(player, arg, Msg("Config.UIAdd.Set", userId, dx, dy, minX, minY, maxX, maxY));

            if (player != null)
                DestroyWipeTimerUI(player);
        }

        [ChatCommand("mncdresetui")]
        private void CmdResetUiChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }

            ResetUiAnchors();
            SendReply(player, Msg("Config.UIReset.Set", player.UserIDString));
            DestroyWipeTimerUI(player);
        }

        [ConsoleCommand("mncd.resetui")]
        private void CmdResetUiConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            var userId = player?.UserIDString;

            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", userId));
                return;
            }

            ResetUiAnchors();
            Reply(player, arg, Msg("Config.UIReset.Set", userId));

            if (player != null)
                DestroyWipeTimerUI(player);
        }

        private void SetUiAnchors(float minX, float minY, float maxX, float maxY)
        {
            _config.UiAnchorMin = ToAnchorString(minX, minY);
            _config.UiAnchorMax = ToAnchorString(maxX, maxY);
            SaveConfig(_config);
        }

        private void GetCurrentAnchors(out float minX, out float minY, out float maxX, out float maxY)
        {
            if (!TryParseAnchor(_config.UiAnchorMin, out minX, out minY) ||
                !TryParseAnchor(_config.UiAnchorMax, out maxX, out maxY))
            {
                minX = 0.4f; minY = 0.92f;
                maxX = 0.6f; maxY = 0.98f;
            }
        }

        private void ApplyUiOffset(float dx, float dy)
        {
            float minX, minY, maxX, maxY;
            GetCurrentAnchors(out minX, out minY, out maxX, out maxY);

            minX = Mathf.Clamp01(minX + dx);
            maxX = Mathf.Clamp01(maxX + dx);
            minY = Mathf.Clamp01(minY + dy);
            maxY = Mathf.Clamp01(maxY + dy);

            if (minX >= maxX) maxX = Mathf.Clamp01(minX + 0.05f);
            if (minY >= maxY) maxY = Mathf.Clamp01(minY + 0.05f);

            SetUiAnchors(minX, minY, maxX, maxY);
        }

        private void ResetUiAnchors()
        {
            _config.UiAnchorMin = "0.4 0.92";
            _config.UiAnchorMax = "0.6 0.98";
            SaveConfig(_config);
        }

        #endregion

        #region Debug Overlay

        [ChatCommand("mncddebug")]
        private void CmdDebugChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!HasDebugPerm(player))
            {
                SendReply(player, Msg("Error.NoDebugPermission", player.UserIDString));
                return;
            }

            ToggleDebugOverlay(player);
        }

        [ConsoleCommand("mncd.debug")]
        private void CmdDebugConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null)
            {
                Puts("mncd.debug can only be used in-game.");
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
            if (_debugUsers.Contains(id))
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
            _debugUsers.Add(id);

            if (_debugTimers.TryGetValue(id, out var existing))
                existing?.Destroy();

            _debugTimers[id] = timer.Every(0.5f, () =>
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
            if (player == null) return;

            ulong id = player.userID;
            _debugUsers.Remove(id);

            if (_debugTimers.TryGetValue(id, out var t))
            {
                t?.Destroy();
                _debugTimers.Remove(id);
            }

            CuiHelper.DestroyUi(player, UiDebug);
        }

        private void UpdateDebugOverlayUI(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            bool isProtected = IsPositionProtected(player.transform.position, player.userID);
            string text = isProtected
                ? Msg("Debug.UI.Protected", player.UserIDString)
                : Msg("Debug.UI.NotProtected", player.UserIDString);

            var container = new CuiElementContainer();

            container.Add(new CuiElement
            {
                Name = UiDebug,
                Parent = "Overlay",
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0.4" },
                    new CuiRectTransformComponent { AnchorMin = "0.4 0.96", AnchorMax = "0.6 0.99" }
                }
            });

            container.Add(new CuiElement
            {
                Parent = UiDebug,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = text,
                        FontSize = 12,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });

            CuiHelper.DestroyUi(player, UiDebug);
            CuiHelper.AddUi(player, container);
        }

        #endregion

        #region TC Preview

        [ChatCommand("mncdpreview")]
        private void CmdPreviewChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!HasPreviewPerm(player))
            {
                SendReply(player, Msg("Preview.NoPerm", player.UserIDString));
                return;
            }

            DrawTcPreview(player);
        }

        [ConsoleCommand("mncd.preview")]
        private void CmdPreviewConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null)
            {
                Puts("mncd.preview can only be used in-game.");
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
            float radius = _config.EntityRadius * Mathf.Max(0.1f, _config.PreviewRingRadiusMultiplier);
            float duration = _config.PreviewRingDuration;
            int tcCount = 0;

            // Manual iteration avoids LINQ .OfType<>().ToList() allocation
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var tc = entity as BuildingPrivlidge;
                if (tc == null) continue;

                var pos = tc.transform.position;
                player.SendConsoleCommand("ddraw.sphere", duration, 0f, 1f, 0f, 1f, pos.x, pos.y, pos.z, radius);
                tcCount++;
            }

            if (tcCount == 0)
            {
                SendReply(player, Msg("Preview.NoTC", player.UserIDString));
                return;
            }

            SendReply(player, Msg("Preview.Drawn", player.UserIDString, tcCount, duration, radius));
        }

        #endregion

        #region TC Loot Hooks & Wipe UI

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return;
            if (!(entity is BuildingPrivlidge)) return;

            DestroyWipeTimerUI(player);

            string modeStr = GetWipeModeDisplayString();
            var remaining = GetWipeTimeRemaining();

            if (!_config.EnableTcWipeUI)
            {
                string userId = player.UserIDString;
                if (remaining == null)
                    SendReply(player, Msg("TcLoot.NoRemaining", userId, modeStr));
                else
                    SendReply(player, Msg("TcLoot.WithRemaining", userId, modeStr, FormatTimeSpan(remaining.Value)));
                return;
            }

            string remainText = remaining != null ? FormatTimeSpan(remaining.Value) : "N/A";
            ShowWipeTimerUI(player, modeStr, remainText);
        }

        private void OnLootEntityEnd(BasePlayer player, BaseEntity entity)
        {
            if (player != null)
                DestroyWipeTimerUI(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            DestroyWipeTimerUI(player);
            DisableDebugOverlay(player);
        }

        private void ShowWipeTimerUI(BasePlayer player, string wipeMode, string remaining)
        {
            if (player == null) return;

            var userId = player.UserIDString;
            var container = new CuiElementContainer();

            container.Add(new CuiElement
            {
                Name = UiWipe,
                Parent = "Overlay",
                Components =
                {
                    new CuiImageComponent { Color = _config.UiBackgroundColor },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = _config.UiAnchorMin,
                        AnchorMax = _config.UiAnchorMax
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = UiWipe,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg("UI.WipeTitle", userId),
                        FontSize = 14,
                        Align = TextAnchor.UpperCenter,
                        Color = _config.UiTextColor
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.05 0.5", AnchorMax = "0.95 0.95" }
                }
            });

            container.Add(new CuiElement
            {
                Parent = UiWipe,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg("UI.WipeLine", userId, wipeMode, remaining),
                        FontSize = 12,
                        Align = TextAnchor.MiddleCenter,
                        Color = _config.UiTextColor
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.05 0.15", AnchorMax = "0.95 0.5" }
                }
            });

            container.Add(new CuiElement
            {
                Parent = UiWipe,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg("UI.WipeExtra", userId),
                        FontSize = 11,
                        Align = TextAnchor.LowerCenter,
                        Color = _config.UiTextColor
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.05 0.02", AnchorMax = "0.95 0.2" }
                }
            });

            CuiHelper.AddUi(player, container);
        }

        private void DestroyWipeTimerUI(BasePlayer player)
        {
            if (player != null)
                CuiHelper.DestroyUi(player, UiWipe);
        }

        #endregion
    }
}
