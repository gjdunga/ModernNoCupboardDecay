// ============================================================================
// ModernNoCupboardDecay  v5.2.0
// Author  : Gabriel (gjdunga)
// License : MIT  –  see LICENSE.MD
//
// What this plugin does
// ---------------------
// Prevents decay damage for all building blocks and deployables that fall
// within the configured radius of any Tool Cupboard on the server.
//
// Optional CheckAuth mode (default OFF) restricts protection to entities
// whose OwnerID is directly authorized on a nearby TC, or is a Rust-team
// member of such an authorized player (TeamAwareProtection, default ON).
//
// A wipe-timer UI panel is shown to players who open a TC.  The panel
// displays the active wipe schedule and time remaining.  Wipe mode can be
// detected automatically from ConVar.Server.tags or set manually.
//
// Permissions
// -----------
//   modernnocupboarddecay.admin    – /mncdset, /mncdui, /mncduiadd, /mncdresetui
//   modernnocupboarddecay.debug    – /mncddebug  (protection-status overlay)
//   modernnocupboarddecay.preview  – /mncdpreview (TC radius rings)
//                                    (only enforced when PreviewRequiresPermission = true)
//
// Compatibility
// -------------
//   Oxide / uMod  2.0.7022+
//   Rust Naval Update  (ProtoBuf.PlayerNameID.userid auth list)
//
// Security notes (v5.1.0 / v5.2.0)
// --------------------------------
//   C1  HitBuffer is now an instance field.  The static field was shared
//       across all concurrent Physics.OverlapSphereNonAlloc calls; under
//       Oxide's timer + hook execution model, two overlapping calls would
//       corrupt each other's result counts.
//
//   C2  mncd.set from RCON / server console runs without a BasePlayer.
//       The permission gate is intentionally skipped in that case because
//       RCON access implies server-operator trust; this is now documented
//       with explicit logging so the action is auditable.
//
//   C3  /mncdpreview iterates every entity in serverEntities.  A per-player
//       cooldown (PreviewCooldownSeconds, default 15 s) prevents repeated
//       full-server scans from unprivileged players.
//
//   H1  SetUiAnchors now clamps all four anchor floats to [0, 1] before
//       writing them to config.
//
//   H2  ApplyUiOffset now enforces a minimum panel dimension of 0.05
//       units, preventing a degenerate zero-width/zero-height panel at
//       the edge of the 0–1 range.
//
//   H3  WipeModeOverride is sanitised (printable ASCII, max 64 chars) and
//       parsed before the config is mutated; a bad value returns an error
//       without dirtying the saved config.
//
//   H4  Debug overlay timers no longer close over a BasePlayer reference.
//       They capture only the ulong userID and resolve the live player via
//       BasePlayer.FindByID() on each tick, preventing stale-reference
//       bugs from Rust's object pooling.
//
//   M1  UiBackgroundColor and UiTextColor are validated as four space-
//       separated floats in [0, 1] before being written to CUI JSON.
//
//   M2  HitBuffer size increased to 1024; a server console warning is
//       emitted when the result count reaches the buffer limit, indicating
//       that the limit may be too small for the current base density.
//
//   M3  IsPositionProtected guards against a null _config explicitly.
//
//   M4  Tag detection now checks "biweekly" / "bi-weekly" BEFORE "weekly",
//       so a server tagged "biweekly" is no longer misidentified as Weekly.
//
//   M5  /mncdset wipemode parses and validates the new value before saving
//       or modifying any config fields.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ModernNoCupboardDecay", "Gabriel", "5.2.0")]
    [Description("Prevents decay within Tool Cupboard radius. Wipe-aware UI, team auth, debug tools. Oxide 2.0.7022+ / Naval Update compatible.")]
    public class ModernNoCupboardDecay : RustPlugin
    {
        // ====================================================================
        // #region Fields
        // ====================================================================
        #region Fields

        /// <summary>Loaded and validated configuration. Never null after OnServerInitialized.</summary>
        private ConfigData _config;

        /// <summary>True only after OnServerInitialized completes without error.</summary>
        private bool _initialized;

        /// <summary>Human-readable status shown by /mncd.</summary>
        private string _statusMsg = "Plugin not yet initialized.";

        /// <summary>Active wipe mode, set by DetectWipeModeFromTagsOrConfig.</summary>
        private WipeMode _wipeMode = WipeMode.Manual;

        /// <summary>Unity physics layer mask used for the overlap sphere query.</summary>
        private int _protectionMask;

        // --- Permission node constants ---------------------------------------
        private const string PermAdmin   = "modernnocupboarddecay.admin";
        private const string PermDebug   = "modernnocupboarddecay.debug";
        private const string PermPreview = "modernnocupboarddecay.preview";

        // --- CUI element name constants -------------------------------------
        // These names must be unique per plugin instance.  Two simultaneously
        // loaded forks would collide; rename these if you fork this plugin.
        private const string UiWipe  = "MNCD_WipeTimer";
        private const string UiDebug = "MNCD_DebugOverlay";

        // --- Physics overlap buffer -----------------------------------------
        // SECURITY FIX (C1): Instance field instead of static.  A static
        // buffer is shared across all calls on the same AppDomain; concurrent
        // decay hooks and debug-overlay timers in the same Oxide frame would
        // read each other's results.  An instance field is private to this
        // plugin instance.
        //
        // Size 1024: Physics.OverlapSphereNonAlloc silently truncates results
        // at the buffer length.  1024 covers dense base layouts; if the server
        // log shows "HitBuffer capacity reached" consistently, increase this.
        private readonly Collider[] _hitBuffer = new Collider[1024];

        /// <summary>Set of userIDs that currently have the debug overlay active.</summary>
        private readonly HashSet<ulong>           _debugUsers  = new HashSet<ulong>();

        /// <summary>Per-player Oxide timers driving the debug overlay refresh.</summary>
        private readonly Dictionary<ulong, Timer> _debugTimers = new Dictionary<ulong, Timer>();

        /// <summary>
        /// Per-player Unix timestamp (seconds) of the last /mncdpreview call.
        /// Used to enforce PreviewCooldownSeconds per player.  (Security fix C3.)
        /// </summary>
        private readonly Dictionary<ulong, long>  _previewLastUsed = new Dictionary<ulong, long>();

        /// <summary>Wipe schedule modes supported by the plugin.</summary>
        private enum WipeMode { Manual, Weekly, BiWeekly, Monthly, CustomDays }

        #endregion


        // ====================================================================
        // #region Configuration
        // ====================================================================
        #region Configuration

        /// <summary>
        /// Strongly-typed configuration object.  Oxide serialises this to/from
        /// oxide/config/ModernNoCupboardDecay.json.
        ///
        /// All fields have safe defaults; ValidateConfig() clamps numeric ranges
        /// after deserialization to prevent hand-edited extremes.
        /// </summary>
        private class ConfigData
        {
            // --- Protection behaviour ---------------------------------------

            /// <summary>
            /// When false (default), any entity near ANY Tool Cupboard is protected.
            /// When true, an entity's OwnerID must be authorized on a nearby TC
            /// (or be a team member of an authed player, if TeamAwareProtection = true).
            /// </summary>
            public bool CheckAuth { get; set; } = false;

            /// <summary>
            /// Only meaningful when CheckAuth = true.
            /// When true, teammates of a TC-authorized player are also protected.
            /// Uses Rust's built-in RelationshipManager team membership.
            /// </summary>
            public bool TeamAwareProtection { get; set; } = true;

            /// <summary>
            /// Radius (meters) around each TC that defines the protection bubble.
            /// Clamped to [1, 500] by ValidateConfig.
            /// Default: 30 m (matches Rust's default TC upkeep radius).
            /// </summary>
            public float EntityRadius { get; set; } = 30f;

            // --- Wipe schedule ----------------------------------------------

            /// <summary>
            /// When true, attempt to read wipe mode from ConVar.Server.tags
            /// ("weekly", "biweekly", "monthly", or "Nd" patterns).
            /// WipeModeOverride is ignored when this is true and a tag is found.
            /// </summary>
            public bool AutoDetectWipeFromTags { get; set; } = true;

            /// <summary>
            /// Fallback wipe mode used when AutoDetectWipeFromTags = false or no
            /// matching tag is found.  Valid values: Manual, Weekly, BiWeekly,
            /// Monthly, or a day-count string like "5d".
            /// SECURITY: Sanitised to printable ASCII, max 64 chars, before storage.
            /// </summary>
            public string WipeModeOverride { get; set; } = "Manual";

            /// <summary>
            /// Day count used when WipeMode resolves to CustomDays.
            /// Set automatically by tag detection or /mncdset wipemode.
            /// Clamped to [0, 365] by ValidateConfig.
            /// </summary>
            public int CustomWipeDays { get; set; } = 0;

            /// <summary>
            /// Unix timestamp (UTC seconds) when the current wipe started.
            /// Set automatically by OnNewSave; can be reset with /mncdset wipestartnow.
            /// </summary>
            public long WipeStartUnixTime { get; set; } = 0;

            // --- Wipe-timer UI panel ----------------------------------------

            /// <summary>When true, shows the wipe-timer CUI panel when a player opens a TC.</summary>
            public bool EnableTcWipeUI { get; set; } = true;

            /// <summary>
            /// CUI background color in "R G B A" format (floats 0–1).
            /// SECURITY: Validated as four space-separated floats in ValidateConfig.
            /// </summary>
            public string UiBackgroundColor { get; set; } = "0.05 0.05 0.05 0.85";

            /// <summary>
            /// CUI text color in "R G B A" format (floats 0–1).
            /// SECURITY: Validated as four space-separated floats in ValidateConfig.
            /// </summary>
            public string UiTextColor { get; set; } = "0.9 0.9 0.9 1.0";

            /// <summary>
            /// Normalized screen-space anchor min for the wipe-timer panel.
            /// Format: "X Y" with both values in [0, 1].
            /// </summary>
            public string UiAnchorMin { get; set; } = "0.4 0.92";

            /// <summary>
            /// Normalized screen-space anchor max for the wipe-timer panel.
            /// Format: "X Y" with both values in [0, 1].
            /// </summary>
            public string UiAnchorMax { get; set; } = "0.6 0.98";

            // --- Preview command --------------------------------------------

            /// <summary>
            /// When false (default), every player can use /mncdpreview.
            /// When true, the modernnocupboarddecay.preview permission (or IsAdmin) is required.
            /// </summary>
            public bool PreviewRequiresPermission { get; set; } = false;

            /// <summary>
            /// Minimum seconds a player must wait between /mncdpreview calls.
            /// Prevents repeated full serverEntities scans by any player.
            /// (Security fix C3.) Clamped to [5, 300] by ValidateConfig.
            /// </summary>
            public float PreviewCooldownSeconds { get; set; } = 15f;

            /// <summary>How long (seconds) the preview spheres are visible.  Clamped to [1, 300].</summary>
            public float PreviewRingDuration { get; set; } = 30f;

            /// <summary>Multiplies EntityRadius for the visual preview ring only.  Clamped to [0.1, 10].</summary>
            public float PreviewRingRadiusMultiplier { get; set; } = 1.0f;
        }

        // --- Config lifecycle -----------------------------------------------

        /// <summary>
        /// Called by Oxide when no config file exists.
        /// Writes a new file populated with ConfigData defaults.
        /// </summary>
        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating new configuration file with default values.");
            _config = new ConfigData();
        }

        /// <summary>
        /// Reads the config file, falls back to defaults on failure, validates
        /// all fields, and writes the (possibly corrected) config back to disk.
        /// </summary>
        private void LoadConfigData()
        {
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null)
                    throw new Exception("Deserialized to null.");
            }
            catch (Exception e)
            {
                PrintWarning($"Config load error – using defaults: {e.Message}");
                _config = new ConfigData();
            }

            ValidateConfig();
            SaveConfig(_config);
            _statusMsg = "Configuration loaded.";
        }

        /// <summary>
        /// Clamps numeric ranges and validates string fields that are later
        /// used in CUI JSON to prevent injection through a hand-edited config.
        ///
        /// SECURITY (M1): UiBackgroundColor and UiTextColor are validated as
        /// four space-separated floats in [0, 1].  Invalid values are replaced
        /// with safe defaults so CUI JSON is never constructed from arbitrary text.
        ///
        /// SECURITY (H3): WipeModeOverride is sanitised before storage.
        /// </summary>
        private void ValidateConfig()
        {
            // Numeric range clamps
            _config.EntityRadius              = Mathf.Clamp(_config.EntityRadius, 1f, 500f);
            _config.CustomWipeDays            = Mathf.Clamp(_config.CustomWipeDays, 0, 365);
            _config.PreviewRingDuration       = Mathf.Clamp(_config.PreviewRingDuration, 1f, 300f);
            _config.PreviewRingRadiusMultiplier = Mathf.Clamp(_config.PreviewRingRadiusMultiplier, 0.1f, 10f);
            _config.PreviewCooldownSeconds    = Mathf.Clamp(_config.PreviewCooldownSeconds, 5f, 300f);

            // Color string validation  (SECURITY M1)
            if (!IsValidCuiColor(_config.UiBackgroundColor))
            {
                PrintWarning($"UiBackgroundColor '{_config.UiBackgroundColor}' is invalid; resetting to default.");
                _config.UiBackgroundColor = "0.05 0.05 0.05 0.85";
            }
            if (!IsValidCuiColor(_config.UiTextColor))
            {
                PrintWarning($"UiTextColor '{_config.UiTextColor}' is invalid; resetting to default.");
                _config.UiTextColor = "0.9 0.9 0.9 1.0";
            }

            // WipeModeOverride sanitisation  (SECURITY H3)
            _config.WipeModeOverride = SanitiseWipeModeString(_config.WipeModeOverride);

            // Anchor sanity – parse and re-serialize through our formatter so
            // values stored on disk are always the canonical "X Y" representation.
            if (!TryParseAnchor(_config.UiAnchorMin, out float minX, out float minY))
            { minX = 0.4f; minY = 0.92f; }
            if (!TryParseAnchor(_config.UiAnchorMax, out float maxX, out float maxY))
            { maxX = 0.6f; maxY = 0.98f; }

            // Rewrite anchors through the clamp path to guarantee stored values are in [0,1]
            WriteAnchorsSafe(minX, minY, maxX, maxY);
        }

        /// <summary>Writes config to disk.</summary>
        private void SaveConfig(ConfigData cfg) => Config.WriteObject(cfg, true);

        #endregion


        // ====================================================================
        // #region Localization
        // ====================================================================
        #region Localization

        /// <summary>
        /// Registers all English (fallback) localization keys.
        /// Translators add their own locale files under oxide/lang/{locale}/.
        /// </summary>
        private void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                // Status
                ["Status.Report"] =
                    "[{0}] v{1} | State: {2} | Radius: {3}m | CheckAuth: {4} | TeamAware: {5} | " +
                    "AutoDetect: {6} | WipeMode: {7} (Override: {8}) | WipeRemaining: {9} | Status: {10}",

                // Errors
                ["Error.NoPermission"]    = "[MNCD] You do not have permission to change settings.",
                ["Error.NoDebugPermission"] = "[MNCD] You do not have permission to use the debug overlay.",
                ["Error.ConfigOption"]    = "[MNCD] Unknown option. Valid options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Error.ConfigApply"]     = "[MNCD] Error applying '{0}': {1}",
                ["Error.CheckAuthValue"]  = "[MNCD] checkauth requires true/false.",
                ["Error.TeamAwareValue"]  = "[MNCD] teamaware requires true/false.",
                ["Error.AutoDetectValue"] = "[MNCD] autodetect requires true/false.",
                ["Error.RadiusValue"]     = "[MNCD] radius requires a positive number in meters (1–500).",
                ["Error.WipeModeValue"]   = "[MNCD] wipemode requires: Manual, Weekly, BiWeekly, Monthly, or a day count like 5d.",
                ["Error.BoolExpected"]    = "[MNCD] {0} requires true or false.",

                // Cooldown
                ["Preview.Cooldown"] = "[MNCD] Please wait {0} more seconds before using /mncdpreview again.",

                // Usage
                ["Usage.MncdSet.Chat"]    = "[MNCD] Usage: /mncdset <option> <value>.  Options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Usage.MncdSet.Console"] = "[MNCD] Usage: mncd.set <option> <value>.  Options: checkauth, teamaware, radius, autodetect, wipemode, wipestartnow.",
                ["Usage.UIAdd.Chat"]      = "[MNCD] Usage: /mncduiadd <deltaX> <deltaY>  (normalized 0..1 offsets).",
                ["Usage.UIAdd.Console"]   = "[MNCD] Usage: mncd.uiadd <deltaX> <deltaY>  (normalized 0..1 offsets).",

                // Config confirmations
                ["Config.CheckAuth.Set"]    = "[MNCD] CheckAuth is now {0}.",
                ["Config.TeamAware.Set"]    = "[MNCD] TeamAwareProtection is now {0}.",
                ["Config.Radius.Set"]       = "[MNCD] EntityRadius (TC decay bubble) is now {0} meters.",
                ["Config.AutoDetect.Set"]   = "[MNCD] AutoDetectWipeFromTags is now {0}.  Active WipeMode: {1}.",
                ["Config.WipeMode.Set"]     = "[MNCD] WipeModeOverride set to '{0}'.  Active WipeMode: {1}.  AutoDetect disabled.",
                ["Config.WipeStartNow.Set"] = "[MNCD] WipeStartUnixTime set to now.  WipeMode: {0}.  Remaining: {1}.",
                ["Config.UIReset.Set"]      = "[MNCD] UI anchors reset to default.  Reopen a TC to see the new position.",
                ["Config.UIAdd.Set"]        = "[MNCD] UI anchors shifted by ({0}, {1}).  New Min({2}, {3}) Max({4}, {5}).",

                // TC loot messages (shown when UI is disabled)
                ["TcLoot.NoRemaining"]   = "[MNCD] This TC's radius is fully protected from decay.\nWipe schedule: {0}.  Wipe end not calculated (manual or missing data).",
                ["TcLoot.WithRemaining"] = "[MNCD] This TC's radius is fully protected from decay.\nWipe schedule: {0}.  Wipe ends in: {1}.",

                // Wipe-timer UI text
                ["UI.WipeTitle"] = "ModernNoCupboardDecay",
                ["UI.WipeLine"]  = "Wipe: {0}  –  {1} remaining",
                ["UI.WipeExtra"] = "Decay disabled within this Tool Cupboard radius.",

                // UI Anchor commands
                ["UIAnchor.Usage.Chat"]    = "[MNCD] Usage: /mncdui <minX> <minY> <maxX> <maxY>",
                ["UIAnchor.Usage.Console"] = "[MNCD] Usage: mncd.ui <minX> <minY> <maxX> <maxY>",
                ["UIAnchor.Set"]           = "[MNCD] UI Anchor set: Min({0}, {1})  Max({2}, {3})",
                ["UIAnchor.Error.Numeric"] = "[MNCD] All 4 values must be numeric.",
                ["UIAnchor.Error.Order"]   = "[MNCD] minX < maxX and minY < maxY is required.",

                // Debug overlay
                ["Debug.Enabled"]        = "[MNCD] Debug overlay enabled.  Shows whether you are inside a protection zone.",
                ["Debug.Disabled"]       = "[MNCD] Debug overlay disabled.",
                ["Debug.UI.Protected"]   = "MNCD: Protected",
                ["Debug.UI.NotProtected"] = "MNCD: Not Protected",

                // Preview
                ["Preview.NoTC"]   = "[MNCD] No Tool Cupboards found.  Nothing to preview.",
                ["Preview.Drawn"]  = "[MNCD] Drawing TC protection rings for {0} cupboards for {1}s (radius {2}m).",
                ["Preview.NoPerm"] = "[MNCD] You do not have permission to use /mncdpreview.",

                // Help
                ["Help.Header"]  = "[MNCD] ModernNoCupboardDecay v{0}",
                ["Help.General"] =
                    "ModernNoCupboardDecay prevents decay for entities within TC radius and shows wipe info.\n" +
                    "Basic commands:\n" +
                    "  /mncd               – Show plugin status.\n" +
                    "  /mncdhelp [topic]   – Help topics: basic, ui, set, debug, preview, wipe.\n" +
                    "  /mncdpreview        – Draw TC protection rings.\n" +
                    "  /mncddebug          – Toggle protection-status overlay.\n" +
                    "Admin commands:\n" +
                    "  /mncdset            – Live config changes.\n" +
                    "  /mncdui, /mncduiadd – Move the wipe UI panel.\n" +
                    "  /mncdresetui        – Reset wipe UI to default position.",
                ["Help.UnknownTopic"] = "[MNCD] Unknown topic '{0}'.  Valid topics: basic, ui, set, debug, preview, wipe.",
                ["Help.Topic.basic"] =
                    "MNCD basics:\n" +
                    "  All entities within the configured radius of any TC are protected from decay.\n" +
                    "  Optional CheckAuth (default false): only entities owned by a TC-authed player\n" +
                    "    (or their Rust team, if TeamAwareProtection = true) are protected.\n" +
                    "  The TC upkeep display shows time-until-wipe in the MNCD panel.",
                ["Help.Topic.ui"] =
                    "UI positioning (admin only):\n" +
                    "  /mncdui <minX> <minY> <maxX> <maxY>  – Set panel anchors (0..1 each).\n" +
                    "  /mncduiadd <dx> <dy>                  – Nudge panel position.\n" +
                    "  /mncdresetui                          – Reset to default top-center.\n" +
                    "Reopen the TC after changing the panel position.",
                ["Help.Topic.set"] =
                    "Live configuration (/mncdset or mncd.set):\n" +
                    "  checkauth  <true|false>  – Restrict to TC-authed owners/teams.\n" +
                    "  teamaware  <true|false>  – Include Rust team members (needs checkauth = true).\n" +
                    "  radius     <meters>      – TC protection radius (1–500).\n" +
                    "  autodetect <true|false>  – Auto-read wipe mode from server.tags.\n" +
                    "  wipemode   <mode>        – Manual|Weekly|BiWeekly|Monthly|Nd (e.g. 5d).\n" +
                    "  wipestartnow             – Reset wipe start time to now.",
                ["Help.Topic.debug"] =
                    "Debug overlay (/mncddebug or mncd.debug):\n" +
                    "  Toggles a small 'MNCD: Protected / Not Protected' banner.\n" +
                    "  Updates every 0.5 s.  Requires admin or modernnocupboarddecay.debug perm.\n" +
                    "  Automatically stops on disconnect.",
                ["Help.Topic.preview"] =
                    "TC preview rings (/mncdpreview or mncd.preview):\n" +
                    "  Draws client-side spheres showing every TC's protection bubble.\n" +
                    "  Requires PreviewRequiresPermission = false (default) or the preview perm.\n" +
                    "  Subject to a per-player cooldown (PreviewCooldownSeconds, default 15 s).\n" +
                    "Config keys: PreviewRequiresPermission, PreviewCooldownSeconds,\n" +
                    "             PreviewRingDuration, PreviewRingRadiusMultiplier.",
                ["Help.Topic.wipe"] =
                    "Wipe modes:\n" +
                    "  AutoDetectWipeFromTags = true: reads server.tags for weekly/biweekly/monthly/Nd.\n" +
                    "  AutoDetectWipeFromTags = false: uses WipeModeOverride.\n" +
                    "  Modes: Manual (no timer), Weekly (7d), BiWeekly (14d), Monthly (30d), CustomDays.\n" +
                    "  WipeStartUnixTime is set by OnNewSave or /mncdset wipestartnow.",
            }, this);
        }

        /// <summary>
        /// Retrieves a localised message, formats it with <paramref name="args"/>,
        /// and returns the raw key on FormatException so a bad translation never
        /// crashes the plugin.
        /// </summary>
        /// <param name="key">Lang dictionary key.</param>
        /// <param name="userId">Steam64 ID string of the recipient, for per-player locale.</param>
        /// <param name="args">Optional format arguments.</param>
        private string Msg(string key, string userId = null, params object[] args)
        {
            string msg = lang.GetMessage(key, this, userId);
            if (args == null || args.Length == 0)
                return msg;
            try   { return string.Format(msg, args); }
            catch (FormatException) { return msg; }
        }

        #endregion


        // ====================================================================
        // #region Lifecycle
        // ====================================================================
        #region Lifecycle

        /// <summary>
        /// Called by Oxide before OnServerInitialized.
        /// Registers permissions and language strings; does not access game state.
        /// </summary>
        private void Init()
        {
            _initialized = false;
            _statusMsg   = "Initializing...";

            permission.RegisterPermission(PermAdmin,   this);
            permission.RegisterPermission(PermDebug,   this);
            permission.RegisterPermission(PermPreview, this);

            LoadDefaultMessages();
        }

        /// <summary>
        /// Called by Oxide after the server has fully started.
        /// Loads config, detects wipe mode, and sets _initialized = true.
        /// Any exception here leaves the plugin in a safe disabled state that
        /// returns null from OnEntityTakeDamage (no modification to vanilla decay).
        /// </summary>
        private void OnServerInitialized()
        {
            try
            {
                // Build the layer mask once; LayerMask.GetMask is moderately expensive.
                _protectionMask = LayerMask.GetMask("Construction", "Construction Trigger", "Trigger", "Deployed");

                LoadConfigData();
                DetectWipeModeFromTagsOrConfig();

                // Seed wipe-start time if this is a fresh install or the value was cleared.
                if (_config.WipeStartUnixTime <= 0)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _config.WipeStartUnixTime = now;
                    SaveConfig(_config);
                    _statusMsg = $"Wipe start seeded to {DateTimeOffset.FromUnixTimeSeconds(now):u}.";
                }

                _initialized = true;

                if (_statusMsg.StartsWith("Initializing", StringComparison.OrdinalIgnoreCase))
                    _statusMsg = "Initialized successfully.";

                PrintStartupSummary();
            }
            catch (Exception ex)
            {
                _initialized = false;
                _statusMsg   = $"Init failed: {ex.Message}";
                PrintError(_statusMsg);
            }
        }

        /// <summary>
        /// Called by Oxide when the plugin is unloaded (server shutdown or hot-reload).
        /// Destroys all timers and removes all CUI panels for connected players so
        /// stale panels are not left on screen.
        /// </summary>
        private void Unload()
        {
            // Stop all debug overlay timers.
            foreach (var kvp in _debugTimers)
                kvp.Value?.Destroy();
            _debugTimers.Clear();
            _debugUsers.Clear();

            // Destroy UI panels for every connected player.
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;
                CuiHelper.DestroyUi(player, UiWipe);
                CuiHelper.DestroyUi(player, UiDebug);
            }
        }

        /// <summary>
        /// Called by Oxide when the server saves a new map (wipe event).
        /// Resets WipeStartUnixTime to now so the wipe-timer UI counts down
        /// from the correct baseline after every forced wipe.
        /// </summary>
        /// <param name="filename">Map filename; logged for auditing.</param>
        private void OnNewSave(string filename)
        {
            if (_config == null) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _config.WipeStartUnixTime = now;
            SaveConfig(_config);
            _statusMsg = $"Wipe detected ('{filename}'); start time reset to {DateTimeOffset.FromUnixTimeSeconds(now):u}.";
        }

        #endregion


        // ====================================================================
        // #region Wipe Detection
        // ====================================================================
        #region Wipe Detection

        /// <summary>
        /// Sets _wipeMode by reading ConVar.Server.tags (when AutoDetectWipeFromTags
        /// is true) or falling back to ParseWipeMode(_config.WipeModeOverride).
        ///
        /// SECURITY FIX (M4): "biweekly" / "bi-weekly" are tested BEFORE "weekly"
        /// so the longer pattern cannot be shadowed by the shorter one.
        /// </summary>
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

                        // SECURITY M4: check more-specific pattern first.
                        if (lower.Contains("biweekly") || lower.Contains("bi-weekly"))
                            detected = WipeMode.BiWeekly;
                        else if (lower.Contains("weekly"))
                            detected = WipeMode.Weekly;
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
                    PrintWarning($"Could not read server tags: {e.Message}");
                }
            }

            if (detected != WipeMode.Manual)
            {
                _wipeMode  = detected;
                _statusMsg = $"Wipe mode auto-detected: {_wipeMode}" +
                             (_wipeMode == WipeMode.CustomDays && _config.CustomWipeDays > 0
                                 ? $" ({_config.CustomWipeDays}d)." : ".");
                return;
            }

            // Fall back to config override.
            _wipeMode  = ParseWipeMode(_config.WipeModeOverride);
            _statusMsg = $"Wipe mode from config override: {_wipeMode}" +
                         (_wipeMode == WipeMode.CustomDays && _config.CustomWipeDays > 0
                             ? $" ({_config.CustomWipeDays}d)." : ".");
        }

        /// <summary>
        /// Scans comma-separated tag tokens for a pattern like "5d", "5day", or
        /// "5days" and returns the day count if it is in [2, 60], else 0.
        ///
        /// Accepts formats: "5d", "5day", "5days"; also space/dash/underscore
        /// separated tokens where a number is adjacent to a day suffix.
        /// </summary>
        private int TryExtractDaysFromTags(string lowerTags)
        {
            if (string.IsNullOrEmpty(lowerTags)) return 0;

            foreach (string piece in lowerTags.Split(','))
            {
                foreach (string token in piece.Trim().Split(' ', '-', '_'))
                {
                    string t = token.Trim();
                    if (string.IsNullOrEmpty(t)) continue;

                    // Strip trailing day suffix to isolate the numeric part.
                    if      (t.EndsWith("days")) t = t.Substring(0, t.Length - 4);
                    else if (t.EndsWith("day"))  t = t.Substring(0, t.Length - 3);
                    else if (t.EndsWith("d"))    t = t.Substring(0, t.Length - 1);

                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days)
                        && days >= 2 && days <= 60)
                        return days;
                }
            }
            return 0;
        }

        /// <summary>
        /// Converts a wipe-mode string to a WipeMode enum value.
        /// Also sets _config.CustomWipeDays when a numeric day-count is parsed.
        ///
        /// Accepted strings (case-insensitive):
        ///   "Manual", "Weekly", "BiWeekly", "bi-weekly", "Monthly",
        ///   "5d", "5day", "5days"  (any positive integer)
        ///
        /// Returns WipeMode.Manual for unrecognised input.
        /// </summary>
        private WipeMode ParseWipeMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return WipeMode.Manual;

            string lower = mode.Trim().ToLowerInvariant();

            // Try day-count pattern first.
            string numPart = lower;
            if      (numPart.EndsWith("days")) numPart = numPart.Substring(0, numPart.Length - 4);
            else if (numPart.EndsWith("day"))  numPart = numPart.Substring(0, numPart.Length - 3);
            else if (numPart.EndsWith("d"))    numPart = numPart.Substring(0, numPart.Length - 1);

            if (int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days > 0)
            {
                _config.CustomWipeDays = Mathf.Clamp(days, 1, 365);
                return WipeMode.CustomDays;
            }

            switch (lower)
            {
                case "weekly":               return WipeMode.Weekly;
                case "biweekly":
                case "bi-weekly":            return WipeMode.BiWeekly;
                case "monthly":              return WipeMode.Monthly;
                default:                     return WipeMode.Manual;
            }
        }

        // --- Time helpers ---------------------------------------------------

        /// <summary>Returns the wipe duration for the active mode, or TimeSpan.Zero for Manual.</summary>
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

        /// <summary>
        /// Returns the Unix timestamp when the current wipe ends, or 0 if
        /// the mode is Manual or WipeStartUnixTime has not been set.
        /// </summary>
        private long GetWipeEndUnixTime()
        {
            if (_config.WipeStartUnixTime <= 0) return 0;
            TimeSpan dur = GetWipeDuration();
            return dur == TimeSpan.Zero ? 0 : _config.WipeStartUnixTime + (long)dur.TotalSeconds;
        }

        /// <summary>
        /// Returns time remaining until wipe end, or null if indeterminate.
        /// Returns TimeSpan.Zero if the wipe end has already passed.
        /// </summary>
        private TimeSpan? GetWipeTimeRemaining()
        {
            long wipeEnd = GetWipeEndUnixTime();
            if (wipeEnd <= 0) return null;
            long delta = wipeEnd - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return delta <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(delta);
        }

        /// <summary>Formats a TimeSpan as "Xd Xh Xm" without seconds.</summary>
        private static string FormatTimeSpan(TimeSpan ts)
            => $"{ts.Days}d {ts.Hours}h {ts.Minutes}m";

        /// <summary>Returns a human-readable wipe mode string including the day count for CustomDays.</summary>
        private string GetWipeModeDisplayString()
        {
            string mode = _wipeMode.ToString();
            if (_wipeMode == WipeMode.CustomDays && _config.CustomWipeDays > 0)
                mode += $" ({_config.CustomWipeDays}d)";
            return mode;
        }

        /// <summary>Prints a startup summary to the server console for operators.</summary>
        private void PrintStartupSummary()
        {
            Puts("[ModernNoCupboardDecay] Startup summary:");
            Puts($"  Version: {Version}");
            Puts($"  CheckAuth: {_config.CheckAuth}  |  TeamAware: {_config.TeamAwareProtection}");
            Puts($"  EntityRadius: {_config.EntityRadius}m");
            Puts($"  AutoDetect: {_config.AutoDetectWipeFromTags}  |  WipeMode: {_wipeMode}");
            Puts($"  WipeStart: {(_config.WipeStartUnixTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(_config.WipeStartUnixTime).ToString("u") : "unset")}");

            var remaining = GetWipeTimeRemaining();
            Puts(remaining.HasValue
                ? $"  Wipe ends in: {FormatTimeSpan(remaining.Value)}"
                : "  Wipe end: N/A (manual mode or missing data).");
        }

        #endregion


        // ====================================================================
        // #region Core Decay Logic
        // ====================================================================
        #region Core Decay Logic

        /// <summary>
        /// Oxide hook: called whenever any entity takes any damage.
        /// We intercept Decay-type damage only.  If the entity's position is
        /// within range of a qualifying TC, we zero the decay damage scale,
        /// which Rust treats as "no damage applied" for that tick.
        ///
        /// Returns null in all cases to let other Oxide plugins continue
        /// processing the hit.  We modify info in-place instead of returning
        /// a non-null value, which avoids blocking the damage pipeline.
        /// </summary>
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // Guard: plugin not ready, or arguments invalid.
            // SECURITY (M3): also guards against _config being null.
            if (!_initialized || _config == null || entity == null || info == null)
                return null;

            // Fast-exit: only process decay damage.
            if (!info.damageTypes.Has(DamageType.Decay))
                return null;

            if (IsPositionProtected(entity.transform.position, GetOwnerId(entity)))
                info.damageTypes.Scale(DamageType.Decay, 0f);

            return null;
        }

        #endregion


        // ====================================================================
        // #region Protection Logic
        // ====================================================================
        #region Protection Logic

        /// <summary>
        /// Returns the OwnerID of the entity.  Falls back to 0 if the entity
        /// has no owner (e.g. server-spawned entities).
        ///
        /// Note: the original code also checked info.HitEntity.OwnerID as a
        /// fallback, but for decay hits HitEntity == entity, so that read was
        /// a no-op.  Removed for clarity.
        /// </summary>
        private static ulong GetOwnerId(BaseCombatEntity entity)
            => entity?.OwnerID ?? 0;

        /// <summary>
        /// Core protection query.  Uses Physics.OverlapSphereNonAlloc with a
        /// pre-allocated instance-level buffer (zero GC allocation per call).
        ///
        /// SECURITY FIX (C1): Buffer is an instance field, not static.
        /// SECURITY FIX (M2): Warns when result count == buffer capacity.
        /// SECURITY FIX (M3): Returns false immediately if _config is null.
        ///
        /// Algorithm:
        ///   1. Find all colliders within _config.EntityRadius of the position.
        ///   2. For each collider, walk up the GameObject hierarchy looking for a
        ///      BuildingPrivlidge component (the TC).
        ///   3a. If CheckAuth = false: any TC found means protected.
        ///   3b. If CheckAuth = true:  the ownerId must be directly authorized on
        ///       the TC, or a team member of an authorized player (when TeamAwareProtection = true).
        /// </summary>
        /// <param name="position">World position to test.</param>
        /// <param name="ownerId">Steam64 ID of the entity's owner (0 = no owner).</param>
        private bool IsPositionProtected(Vector3 position, ulong ownerId)
        {
            if (_config == null) return false;

            int count = Physics.OverlapSphereNonAlloc(
                position, _config.EntityRadius, _hitBuffer, _protectionMask);

            // SECURITY M2: warn if results were truncated.
            if (count == _hitBuffer.Length)
                PrintWarning(
                    $"[MNCD] HitBuffer capacity ({_hitBuffer.Length}) reached near {position}. " +
                    "Some TCs may have been missed. Consider increasing the buffer size in the source.");

            if (!_config.CheckAuth)
            {
                // Fast path: any TC within range = protected.
                for (int i = 0; i < count; i++)
                {
                    if (_hitBuffer[i].GetComponentInParent<BuildingPrivlidge>() != null)
                        return true;
                }
                return false;
            }

            // Auth path: entity must be owned by someone who is authorized on a nearby TC.
            if (ownerId == 0) return false;

            for (int i = 0; i < count; i++)
            {
                var priv = _hitBuffer[i].GetComponentInParent<BuildingPrivlidge>();
                if (priv != null && IsOwnerAuthorizedOrTeammate(priv, ownerId))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when ownerId is directly authorized on the TC, or is a
        /// Rust team member of any authorized player (when TeamAwareProtection = true).
        ///
        /// Uses ProtoBuf.PlayerNameID.userid, which is the correct access pattern
        /// for Oxide v2.0.7022+ / Naval Update.
        /// </summary>
        private bool IsOwnerAuthorizedOrTeammate(BuildingPrivlidge priv, ulong ownerId)
        {
            if (priv == null || ownerId == 0) return false;

            // Direct auth check.
            if (IsAuthorizedOnCupboard(priv, ownerId)) return true;
            if (!_config.TeamAwareProtection) return false;

            // Team-aware check: find the entity owner's Rust team, then see if
            // the TC owner or any TC-authed player is on that same team.
            var rm = RelationshipManager.ServerInstance;
            if (rm == null) return false;

            var ownerTeam = rm.FindPlayersTeam(ownerId);
            if (ownerTeam == null) return false;

            // Is the TC's building owner on the entity owner's team?
            if (priv.OwnerID != 0 && ownerTeam.members.Contains(priv.OwnerID))
                return true;

            // Is any directly-authorized player on the entity owner's team?
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
        /// Iterates the TC's authorizedPlayers list and returns true if ownerId
        /// is present.  Uses a plain for-loop to avoid LINQ allocation.
        /// </summary>
        private static bool IsAuthorizedOnCupboard(BuildingPrivlidge priv, ulong ownerId)
        {
            var authList = priv.authorizedPlayers;
            if (authList == null || authList.Count == 0) return false;

            for (int i = 0; i < authList.Count; i++)
            {
                if (authList[i].userid == ownerId) return true;
            }
            return false;
        }

        #endregion


        // ====================================================================
        // #region Permissions & Shared Helpers
        // ====================================================================
        #region Permissions & Shared Helpers

        /// <summary>True when the player is a server admin or has the admin permission node.</summary>
        private bool HasAdminPerm(BasePlayer player)
            => player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));

        /// <summary>True when the player is a server admin or has the debug permission node.</summary>
        private bool HasDebugPerm(BasePlayer player)
            => player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermDebug));

        /// <summary>
        /// True when the player can use /mncdpreview.
        /// When PreviewRequiresPermission = false, all players pass.
        /// When true, admin or the preview permission node is required.
        /// </summary>
        private bool HasPreviewPerm(BasePlayer player)
        {
            if (player == null) return false;
            if (!_config.PreviewRequiresPermission) return true;
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermPreview);
        }

        /// <summary>
        /// Parses common boolean representations.
        /// Accepted truthy:  "true", "1", "yes", "on"
        /// Accepted falsy:   "false", "0", "no", "off"
        /// Returns false (and sets result = false) for anything else.
        /// </summary>
        private static bool TryParseBool(string value, out bool result)
        {
            result = false;
            if (string.IsNullOrEmpty(value)) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on":  result = true;  return true;
                case "false": case "0": case "no": case "off": result = false; return true;
                default: return false;
            }
        }

        /// <summary>Culture-invariant float parser; returns false on failure.</summary>
        private static bool TryParseFloat(string s, out float f)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

        /// <summary>
        /// Parses a CUI anchor string of the form "X Y" into two floats.
        /// Returns false if the format is wrong or either value is non-numeric.
        /// </summary>
        private static bool TryParseAnchor(string anchor, out float x, out float y)
        {
            x = y = 0f;
            if (string.IsNullOrEmpty(anchor)) return false;
            var parts = anchor.Trim().Split(' ');
            return parts.Length == 2
                && TryParseFloat(parts[0], out x)
                && TryParseFloat(parts[1], out y);
        }

        /// <summary>
        /// Returns the canonical "X Y" anchor string using invariant culture and
        /// exactly three decimal places, ensuring deterministic serialization.
        /// </summary>
        private static string ToAnchorString(float x, float y)
            => $"{x.ToString("0.###", CultureInfo.InvariantCulture)} {y.ToString("0.###", CultureInfo.InvariantCulture)}";

        /// <summary>
        /// Validates a CUI color string: must be exactly four space-separated
        /// floats each in [0, 1].  Rust's CUI JSON uses this exact format.
        ///
        /// SECURITY (M1): Called before any color string is written to a CUI
        /// element so a malformed config cannot inject JSON characters.
        /// </summary>
        private static bool IsValidCuiColor(string color)
        {
            if (string.IsNullOrEmpty(color)) return false;
            var parts = color.Trim().Split(' ');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    return false;
                if (f < 0f || f > 1f) return false;
            }
            return true;
        }

        /// <summary>
        /// Sanitises a wipe-mode override string:
        ///   – strips non-printable characters
        ///   – trims whitespace
        ///   – truncates to 64 characters
        ///
        /// SECURITY (H3): Prevents overlong or control-character-laden strings
        /// from being stored in config and echoed back into chat.
        /// Returns "Manual" if the result is empty.
        /// </summary>
        private static string SanitiseWipeModeString(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Manual";

            var sb = new System.Text.StringBuilder(64);
            foreach (char c in raw)
            {
                if (c >= 0x20 && c < 0x7F)  // printable ASCII only
                    sb.Append(c);
                if (sb.Length >= 64) break;
            }
            string result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "Manual" : result;
        }

        /// <summary>
        /// Sends a message to an in-game player or logs it to the console,
        /// depending on which context the command was invoked from.
        /// </summary>
        private void Reply(BasePlayer player, ConsoleSystem.Arg arg, string message)
        {
            if (player != null) SendReply(player, message);
            else if (arg != null) Puts(message);
        }

        #endregion


        // ====================================================================
        // #region Status Commands
        // ====================================================================
        #region Status Commands

        /// <summary>/mncd – Shows the current plugin status to any player.</summary>
        [ChatCommand("mncd")]
        private void CmdMncdChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            SendReply(player, GetStatusReport(player.UserIDString));
        }

        /// <summary>mncd – Console/RCON equivalent of /mncd.</summary>
        [ConsoleCommand("mncd")]
        private void CmdMncdConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            Reply(player, arg, GetStatusReport(player?.UserIDString));
        }

        /// <summary>Builds the status report string shown by /mncd.</summary>
        private string GetStatusReport(string userId = null)
        {
            string initState   = _initialized ? "ENABLED" : "NOT INITIALIZED";
            var    remaining   = GetWipeTimeRemaining();
            string wipeRemain  = remaining.HasValue ? FormatTimeSpan(remaining.Value) : "N/A";

            return Msg("Status.Report", userId,
                Title, Version,
                initState,
                _config?.EntityRadius ?? 0f,
                _config?.CheckAuth ?? false,
                _config?.TeamAwareProtection ?? false,
                _config?.AutoDetectWipeFromTags ?? false,
                GetWipeModeDisplayString(),
                _config?.WipeModeOverride ?? "N/A",
                wipeRemain,
                _statusMsg);
        }

        #endregion


        // ====================================================================
        // #region Help Commands
        // ====================================================================
        #region Help Commands

        /// <summary>/mncdhelp [topic] – Shows help text for the specified topic.</summary>
        [ChatCommand("mncdhelp")]
        private void CmdHelpChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            string topic = (args != null && args.Length > 0) ? args[0].ToLowerInvariant() : "general";
            SendReply(player, BuildHelpText(topic, player.UserIDString));
        }

        /// <summary>mncd.help [topic] – Console equivalent of /mncdhelp.</summary>
        [ConsoleCommand("mncd.help")]
        private void CmdHelpConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            string topic = (arg?.Args != null && arg.Args.Length > 0)
                ? arg.Args[0].ToLowerInvariant()
                : "general";
            Reply(player, arg, BuildHelpText(topic, player?.UserIDString));
        }

        /// <summary>Returns the help text for the given topic key.</summary>
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


        // ====================================================================
        // #region Configuration Commands  (/mncdset, mncd.set)
        // ====================================================================
        #region Configuration Commands

        /// <summary>
        /// /mncdset &lt;option&gt; &lt;value&gt;  – Live config editing (admin only).
        /// </summary>
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
            SendReply(player, ApplyConfigChange(args[0], args.Length > 1 ? args[1] : null, player.UserIDString));
        }

        /// <summary>
        /// mncd.set &lt;option&gt; &lt;value&gt;  – Console / RCON equivalent of /mncdset.
        ///
        /// SECURITY NOTE (C2): When arg.Player() returns null the caller is RCON or
        /// the server console, which is already a privileged context (no player-side
        /// permission check is possible or needed).  The action is logged to the
        /// server console so it remains auditable.
        /// </summary>
        [ConsoleCommand("mncd.set")]
        private void CmdSetConsole(ConsoleSystem.Arg arg)
        {
            var    player = arg?.Player();
            string userId = player?.UserIDString;

            // In-game player must have the admin permission.
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

            string option = arg.Args[0];
            string value  = arg.Args.Length > 1 ? arg.Args[1] : null;

            // SECURITY C2: Log RCON/console changes so they are auditable.
            if (player == null)
                Puts($"[MNCD] RCON/console config change: mncd.set {option} {value ?? "(no value)"}");

            Reply(player, arg, ApplyConfigChange(option, value, userId));
        }

        /// <summary>
        /// Applies a single live configuration change, saves to disk, and returns
        /// a localised confirmation or error message.
        ///
        /// SECURITY FIX (M5): wipemode now validates the value and parses it into a
        /// WipeMode enum BEFORE mutating or saving any config fields.  A bad value
        /// returns an error and leaves config unchanged.
        ///
        /// SECURITY FIX (H3): WipeModeOverride is sanitised through
        /// SanitiseWipeModeString before storage.
        /// </summary>
        /// <param name="option">Config key name (case-insensitive).</param>
        /// <param name="value">New value as a raw string; null for value-less actions.</param>
        /// <param name="userId">Caller's Steam64 ID string for localisation (null for console).</param>
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
                        if (string.IsNullOrEmpty(value)
                            || !TryParseFloat(value, out float r)
                            || r <= 0f)
                            return Msg("Error.RadiusValue", userId);

                        r = Mathf.Clamp(r, 1f, 500f);
                        _config.EntityRadius = r;
                        SaveConfig(_config);
                        _statusMsg = $"EntityRadius set to {r}m.";
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
                        // SECURITY M5 + H3: validate and parse BEFORE touching config.
                        if (string.IsNullOrEmpty(value))
                            return Msg("Error.WipeModeValue", userId);

                        string sanitised = SanitiseWipeModeString(value);
                        WipeMode parsed  = ParseWipeMode(sanitised);

                        // ParseWipeMode returns Manual for unrecognised strings,
                        // but we only accept Manual when the user explicitly typed it.
                        // Reject anything that parsed as Manual but wasn't typed as Manual.
                        bool explicitManual = sanitised.Trim().ToLowerInvariant() == "manual";
                        if (parsed == WipeMode.Manual && !explicitManual)
                            return Msg("Error.WipeModeValue", userId);

                        // All validation passed — now mutate config.
                        _config.WipeModeOverride          = sanitised;
                        _config.AutoDetectWipeFromTags     = false;
                        SaveConfig(_config);
                        DetectWipeModeFromTagsOrConfig();
                        return Msg("Config.WipeMode.Set", userId, sanitised, _wipeMode);
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
                        return Msg("Config.WipeStartNow.Set", userId, _wipeMode,
                            remaining.HasValue ? FormatTimeSpan(remaining.Value) : "N/A");
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


        // ====================================================================
        // #region UI Anchor Commands  (/mncdui, /mncduiadd, /mncdresetui)
        // ====================================================================
        #region UI Anchor Commands

        /// <summary>
        /// /mncdui &lt;minX&gt; &lt;minY&gt; &lt;maxX&gt; &lt;maxY&gt;
        /// Sets the wipe-timer panel anchors in normalized screen space (admin only).
        /// </summary>
        [ChatCommand("mncdui")]
        private void CmdUiChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }
            HandleUiAnchorChange(player, null, args);
        }

        /// <summary>mncd.ui – Console equivalent of /mncdui.</summary>
        [ConsoleCommand("mncd.ui")]
        private void CmdUiConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }
            HandleUiAnchorChange(player, arg, arg?.Args);
        }

        /// <summary>Shared implementation for chat and console UI anchor set commands.</summary>
        private void HandleUiAnchorChange(BasePlayer player, ConsoleSystem.Arg arg, string[] args)
        {
            string userId = player?.UserIDString;

            if (args == null || args.Length != 4)
            {
                Reply(player, arg, player != null
                    ? Msg("UIAnchor.Usage.Chat", userId)
                    : Msg("UIAnchor.Usage.Console", userId));
                return;
            }

            if (!TryParseFloat(args[0], out float minX) || !TryParseFloat(args[1], out float minY) ||
                !TryParseFloat(args[2], out float maxX) || !TryParseFloat(args[3], out float maxY))
            {
                Reply(player, arg, Msg("UIAnchor.Error.Numeric", userId));
                return;
            }

            if (minX >= maxX || minY >= maxY)
            {
                Reply(player, arg, Msg("UIAnchor.Error.Order", userId));
                return;
            }

            // WriteAnchorsSafe clamps each float to [0,1] and enforces minimum dimensions.
            WriteAnchorsSafe(minX, minY, maxX, maxY);
            Reply(player, arg, Msg("UIAnchor.Set", userId, minX, minY, maxX, maxY));
            if (player != null) DestroyWipeTimerUI(player);
        }

        /// <summary>
        /// /mncduiadd &lt;dx&gt; &lt;dy&gt;
        /// Nudges the wipe-timer panel by a normalized offset (admin only).
        /// </summary>
        [ChatCommand("mncduiadd")]
        private void CmdUiAddChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }
            HandleUiAddOffset(player, null, args);
        }

        /// <summary>mncd.uiadd – Console equivalent of /mncduiadd.</summary>
        [ConsoleCommand("mncd.uiadd")]
        private void CmdUiAddConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }
            HandleUiAddOffset(player, arg, arg?.Args);
        }

        /// <summary>Shared implementation for the UI nudge command.</summary>
        private void HandleUiAddOffset(BasePlayer player, ConsoleSystem.Arg arg, string[] args)
        {
            string userId = player?.UserIDString;

            if (args == null || args.Length != 2)
            {
                Reply(player, arg, player != null
                    ? Msg("Usage.UIAdd.Chat", userId)
                    : Msg("Usage.UIAdd.Console", userId));
                return;
            }

            if (!TryParseFloat(args[0], out float dx) || !TryParseFloat(args[1], out float dy))
            {
                Reply(player, arg, Msg("UIAnchor.Error.Numeric", userId));
                return;
            }

            ApplyUiOffset(dx, dy);

            GetCurrentAnchors(out float minX, out float minY, out float maxX, out float maxY);
            Reply(player, arg, Msg("Config.UIAdd.Set", userId, dx, dy, minX, minY, maxX, maxY));
            if (player != null) DestroyWipeTimerUI(player);
        }

        /// <summary>
        /// /mncdresetui  – Resets the wipe-timer panel to the default position (admin only).
        /// </summary>
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

        /// <summary>mncd.resetui – Console equivalent of /mncdresetui.</summary>
        [ConsoleCommand("mncd.resetui")]
        private void CmdResetUiConsole(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player != null && !HasAdminPerm(player))
            {
                SendReply(player, Msg("Error.NoPermission", player.UserIDString));
                return;
            }
            ResetUiAnchors();
            Reply(player, arg, Msg("Config.UIReset.Set", player?.UserIDString));
            if (player != null) DestroyWipeTimerUI(player);
        }

        // --- Anchor helpers -------------------------------------------------

        /// <summary>
        /// Writes anchor values to config with safety constraints applied:
        ///   1. All four floats are clamped to [0, 1]  (SECURITY H1).
        ///   2. A minimum panel dimension of 0.05 is enforced in both axes
        ///      (SECURITY H2).
        /// </summary>
        private void WriteAnchorsSafe(float minX, float minY, float maxX, float maxY)
        {
            const float minDim = 0.05f;

            minX = Mathf.Clamp01(minX);
            minY = Mathf.Clamp01(minY);
            maxX = Mathf.Clamp01(maxX);
            maxY = Mathf.Clamp01(maxY);

            // Enforce minimum width: if clamping collapsed the panel, push max outward.
            // If there is no room to push (minX is 1.0), pull minX back instead.
            if (maxX - minX < minDim)
            {
                maxX = Mathf.Clamp01(minX + minDim);
                if (maxX - minX < minDim)
                    minX = Mathf.Clamp01(maxX - minDim);
            }

            // Enforce minimum height.
            if (maxY - minY < minDim)
            {
                maxY = Mathf.Clamp01(minY + minDim);
                if (maxY - minY < minDim)
                    minY = Mathf.Clamp01(maxY - minDim);
            }

            _config.UiAnchorMin = ToAnchorString(minX, minY);
            _config.UiAnchorMax = ToAnchorString(maxX, maxY);
            SaveConfig(_config);
        }

        /// <summary>
        /// Applies a delta offset to the current anchors.
        /// The resulting anchors are passed through WriteAnchorsSafe so
        /// all range and minimum-dimension constraints are enforced.
        /// </summary>
        private void ApplyUiOffset(float dx, float dy)
        {
            GetCurrentAnchors(out float minX, out float minY, out float maxX, out float maxY);
            WriteAnchorsSafe(minX + dx, minY + dy, maxX + dx, maxY + dy);
        }

        /// <summary>
        /// Reads the current anchor values from config.
        /// Falls back to the defaults if parsing fails.
        /// </summary>
        private void GetCurrentAnchors(out float minX, out float minY, out float maxX, out float maxY)
        {
            if (!TryParseAnchor(_config.UiAnchorMin, out minX, out minY) ||
                !TryParseAnchor(_config.UiAnchorMax, out maxX, out maxY))
            {
                minX = 0.4f; minY = 0.92f;
                maxX = 0.6f; maxY = 0.98f;
            }
        }

        /// <summary>Resets panel anchors to the default top-center position.</summary>
        private void ResetUiAnchors()
            => WriteAnchorsSafe(0.4f, 0.92f, 0.6f, 0.98f);

        #endregion


        // ====================================================================
        // #region Debug Overlay  (/mncddebug)
        // ====================================================================
        #region Debug Overlay

        /// <summary>
        /// /mncddebug  – Toggles the per-player protection-status overlay.
        /// Requires admin or modernnocupboarddecay.debug permission.
        /// </summary>
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

        /// <summary>mncd.debug – Console equivalent of /mncddebug (in-game only).</summary>
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

        /// <summary>Enables or disables the debug overlay for the given player.</summary>
        private void ToggleDebugOverlay(BasePlayer player)
        {
            if (_debugUsers.Contains(player.userID))
            {
                DisableDebugOverlay(player.userID);
                SendReply(player, Msg("Debug.Disabled", player.UserIDString));
            }
            else
            {
                EnableDebugOverlay(player.userID);
                SendReply(player, Msg("Debug.Enabled", player.UserIDString));
            }
        }

        /// <summary>
        /// Starts a 0.5 s repeating timer that refreshes the debug overlay UI
        /// for the specified player.
        ///
        /// SECURITY FIX (H4): The timer closure captures only the ulong userID,
        /// NOT a BasePlayer reference.  On each tick it resolves the live player
        /// via BasePlayer.FindByID so stale references from Rust's object pooling
        /// are never used.
        /// </summary>
        private void EnableDebugOverlay(ulong userId)
        {
            _debugUsers.Add(userId);

            // Destroy any existing timer for this user first.
            if (_debugTimers.TryGetValue(userId, out var existing))
                existing?.Destroy();

            // Capture userID (value type) only — no reference to BasePlayer.
            _debugTimers[userId] = timer.Every(0.5f, () =>
            {
                // Resolve the player from the live active list on every tick.
                var p = BasePlayer.FindByID(userId);
                if (p == null || !p.IsConnected || p.IsDead())
                {
                    DisableDebugOverlay(userId);
                    return;
                }
                UpdateDebugOverlayUI(p);
            });
        }

        /// <summary>
        /// Stops the debug overlay timer, removes the user from the active set,
        /// and destroys the CUI panel.
        /// Accepts a ulong so it can be called from inside the timer closure
        /// without requiring a BasePlayer reference.
        /// </summary>
        private void DisableDebugOverlay(ulong userId)
        {
            _debugUsers.Remove(userId);

            if (_debugTimers.TryGetValue(userId, out var t))
            {
                t?.Destroy();
                _debugTimers.Remove(userId);
            }

            // Destroy the panel if the player is still online.
            var p = BasePlayer.FindByID(userId);
            if (p != null && p.IsConnected)
                CuiHelper.DestroyUi(p, UiDebug);
        }

        /// <summary>
        /// Overload accepting a BasePlayer for callers that already have the reference
        /// (e.g. OnPlayerDisconnected).
        /// </summary>
        private void DisableDebugOverlay(BasePlayer player)
        {
            if (player == null) return;
            DisableDebugOverlay(player.userID);
        }

        /// <summary>
        /// Rebuilds the "Protected / Not Protected" banner for one player.
        /// Destroys the previous panel before adding the new one to avoid stacking.
        /// </summary>
        private void UpdateDebugOverlayUI(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            bool   isProtected = IsPositionProtected(player.transform.position, player.userID);
            string text        = isProtected
                ? Msg("Debug.UI.Protected",    player.UserIDString)
                : Msg("Debug.UI.NotProtected", player.UserIDString);

            var container = new CuiElementContainer();

            container.Add(new CuiElement
            {
                Name   = UiDebug,
                Parent = "Overlay",
                Components =
                {
                    new CuiImageComponent         { Color = "0 0 0 0.4" },
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
                        Text      = text,
                        FontSize  = 12,
                        Align     = TextAnchor.MiddleCenter,
                        Color     = "1 1 1 1"
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });

            CuiHelper.DestroyUi(player, UiDebug);
            CuiHelper.AddUi(player, container);
        }

        #endregion


        // ====================================================================
        // #region TC Preview  (/mncdpreview)
        // ====================================================================
        #region TC Preview

        /// <summary>
        /// /mncdpreview  – Draws client-side spheres showing every TC's protection bubble.
        /// Subject to per-player cooldown and optional permission gate.
        /// </summary>
        [ChatCommand("mncdpreview")]
        private void CmdPreviewChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!HasPreviewPerm(player))
            {
                SendReply(player, Msg("Preview.NoPerm", player.UserIDString));
                return;
            }
            HandlePreview(player);
        }

        /// <summary>mncd.preview – Console / F1 equivalent of /mncdpreview.</summary>
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
            HandlePreview(player);
        }

        /// <summary>
        /// Shared implementation for the TC preview command.
        ///
        /// SECURITY FIX (C3): Enforces a per-player cooldown of
        /// PreviewCooldownSeconds (default 15 s) to prevent repeated
        /// full serverEntities scans from unprivileged players.
        ///
        /// Performance note: iterating BaseNetworkable.serverEntities is O(n) over
        /// the full entity list.  The cooldown is essential on servers with many
        /// entities.  Admins (IsAdmin) are not exempt from the cooldown; the scan
        /// cost is the same regardless of permission level.
        /// </summary>
        private void HandlePreview(BasePlayer player)
        {
            long now         = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long cooldownSec = (long)_config.PreviewCooldownSeconds;

            if (_previewLastUsed.TryGetValue(player.userID, out long lastUsed))
            {
                long elapsed   = now - lastUsed;
                long remaining = cooldownSec - elapsed;
                if (remaining > 0)
                {
                    SendReply(player, Msg("Preview.Cooldown", player.UserIDString, remaining));
                    return;
                }
            }

            _previewLastUsed[player.userID] = now;

            float radius   = _config.EntityRadius * Mathf.Max(0.1f, _config.PreviewRingRadiusMultiplier);
            float duration = _config.PreviewRingDuration;
            int   tcCount  = 0;

            // Manual iteration avoids LINQ .OfType<>().ToList() allocation.
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var tc = entity as BuildingPrivlidge;
                if (tc == null) continue;

                var pos = tc.transform.position;
                player.SendConsoleCommand(
                    "ddraw.sphere", duration, 0f, 1f, 0f, 1f,
                    pos.x, pos.y, pos.z, radius);
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


        // ====================================================================
        // #region TC Loot Hooks & Wipe-Timer UI
        // ====================================================================
        #region TC Loot Hooks & Wipe-Timer UI

        /// <summary>
        /// Oxide hook: called when a player opens any entity's loot panel.
        /// When the entity is a BuildingPrivlidge (TC), show the wipe-timer panel.
        /// </summary>
        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null)        return;
            if (!(entity is BuildingPrivlidge))          return;

            // Always destroy any stale panel from a previous TC interaction first.
            DestroyWipeTimerUI(player);

            string    modeStr   = GetWipeModeDisplayString();
            TimeSpan? remaining = GetWipeTimeRemaining();

            if (!_config.EnableTcWipeUI)
            {
                // UI disabled: send a plain text message instead.
                string userId = player.UserIDString;
                SendReply(player, remaining.HasValue
                    ? Msg("TcLoot.WithRemaining", userId, modeStr, FormatTimeSpan(remaining.Value))
                    : Msg("TcLoot.NoRemaining", userId, modeStr));
                return;
            }

            string remainText = remaining.HasValue ? FormatTimeSpan(remaining.Value) : "N/A";
            ShowWipeTimerUI(player, modeStr, remainText);
        }

        /// <summary>Oxide hook: called when a player closes a loot panel.  Removes the wipe-timer UI.</summary>
        private void OnLootEntityEnd(BasePlayer player, BaseEntity entity)
        {
            if (player != null) DestroyWipeTimerUI(player);
        }

        /// <summary>Oxide hook: called on player disconnect.  Cleans up both UI panels.</summary>
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            DestroyWipeTimerUI(player);
            DisableDebugOverlay(player);
        }

        /// <summary>
        /// Builds and sends the three-element wipe-timer CUI panel.
        ///
        /// SECURITY (M1): UiBackgroundColor and UiTextColor are guaranteed valid
        /// by ValidateConfig; no user-supplied string reaches CuiImageComponent.Color
        /// or CuiTextComponent.Color without passing IsValidCuiColor first.
        /// </summary>
        private void ShowWipeTimerUI(BasePlayer player, string wipeMode, string remaining)
        {
            if (player == null) return;

            string userId    = player.UserIDString;
            var    container = new CuiElementContainer();

            // Panel background
            container.Add(new CuiElement
            {
                Name   = UiWipe,
                Parent = "Overlay",
                Components =
                {
                    new CuiImageComponent         { Color = _config.UiBackgroundColor },
                    new CuiRectTransformComponent { AnchorMin = _config.UiAnchorMin, AnchorMax = _config.UiAnchorMax }
                }
            });

            // Title row
            container.Add(new CuiElement
            {
                Parent = UiWipe,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text     = Msg("UI.WipeTitle", userId),
                        FontSize = 14,
                        Align    = TextAnchor.UpperCenter,
                        Color    = _config.UiTextColor
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.05 0.5", AnchorMax = "0.95 0.95" }
                }
            });

            // Wipe-mode and remaining-time row
            container.Add(new CuiElement
            {
                Parent = UiWipe,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text     = Msg("UI.WipeLine", userId, wipeMode, remaining),
                        FontSize = 12,
                        Align    = TextAnchor.MiddleCenter,
                        Color    = _config.UiTextColor
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.05 0.15", AnchorMax = "0.95 0.5" }
                }
            });

            // Footer note
            container.Add(new CuiElement
            {
                Parent = UiWipe,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text     = Msg("UI.WipeExtra", userId),
                        FontSize = 11,
                        Align    = TextAnchor.LowerCenter,
                        Color    = _config.UiTextColor
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.05 0.02", AnchorMax = "0.95 0.2" }
                }
            });

            CuiHelper.AddUi(player, container);
        }

        /// <summary>Removes the wipe-timer CUI panel from the player's screen.</summary>
        private void DestroyWipeTimerUI(BasePlayer player)
        {
            if (player != null) CuiHelper.DestroyUi(player, UiWipe);
        }

        #endregion
    }
}
