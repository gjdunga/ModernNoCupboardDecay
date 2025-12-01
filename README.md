README.md
# ModernNoCupboardDecay

**Author:** Gabriel  
**Version:** 3.7.0
**Game:** Rust (uMod / Oxide plugin)

ModernNoCupboardDecay disables **decay** for **any entity** inside the radius of a **Tool Cupboard**, including newer decorative items (e.g., wallpaper, deployables, etc.).  

It also provides:

- Wipe-aware timer display (time until end of wipe) via a **clean CUI panel** when a TC is open.
- Optional **auth-based** and **team-aware** protection.
- Automatic wipe mode detection from `server.tags` (`weekly`, `biweekly`, `monthly`).
- Fully **localized** messages via uMod `Lang` API.
- Live configuration via **chat**, **console**, and **RCON**.

---

## Features

- **No decay for anything** in the TC bubble (default radius 30m).
- Optional **CheckAuth**:
  - If disabled: *any* entity inside radius is protected.
  - If enabled: only entities owned by TC-authorized players (and optionally their team).
- **Team-aware mode**:
  - If enabled, teammates of TC owner or authorized players are also protected.
- **Wipe-aware timer UI**:
  - Uses Oxide’s CUI to show “Wipe: Weekly – 3d 4h 12m remaining”.
  - Appears when a player opens a Tool Cupboard.
- **Server wipe handling**:
  - Automatically resets wipe start on `OnNewSave`.
  - Can be manually overridden via commands.
- **Admin tools**:
  - `/mncd` and `mncd` provide detailed status and diagnostics.
  - `/mncdset` and `mncd.set` allow adjusting settings live.
  - `/mncdui` and `mncd.ui` adjust UI position live.

---

## Installation

1. Download `ModernNoCupboardDecay.cs`.
2. Place it into your server at:

   ```text
   oxide/plugins/ModernNoCupboardDecay.cs


Restart the server or run:

oxide.reload ModernNoCupboardDecay


(Optional) Add permissions to your admins:

oxide.grant group admin modernnocupboarddecay.admin

Permissions
modernnocupboarddecay.admin


Allows usage of:

/mncdset

mncd.set

/mncdui

mncd.ui

Normal players can still use /mncd to see status (read-only).

Chat & Console Commands
Status
Chat
/mncd


Shows plugin version, state, radius, wipe mode, and remaining time.

Console / RCON
mncd


Same as /mncd, printed to console output.

Live Config (core options)
Chat (admin / perm)
/mncdset <option> <value>

Console / RCON
mncd.set <option> <value>


Options:

checkauth – true / false

Require TC auth/team for no-decay or not.

teamaware – true / false

When checkauth is true, also grant protection to teammates of TC owner/authorized users.

radius – <float>

No-decay radius around each TC in meters. Default: 30.

autodetect – true / false

Use server.tags to auto-detect wipe mode (weekly, biweekly, monthly).

wipemode – Manual / Weekly / BiWeekly / Monthly

Sets wipe mode and disables auto-detect.

wipestartnow

No value; sets wipe start to “now” (UTC).

Examples:

/mncdset checkauth true
/mncdset teamaware true
/mncdset radius 32.5
/mncdset autodetect false
/mncdset wipemode Monthly
/mncdset wipestartnow

mncd.set checkauth true
mncd.set radius 30
mncd.set wipemode Weekly
mncd.set wipestartnow

UI Anchor Commands

These adjust the position of the wipe timer CUI panel.

Chat (admin / perm)
/mncdui <minX> <minY> <maxX> <maxY>


Example:

/mncdui 0.38 0.90 0.62 0.97

Console / RCON
mncd.ui <minX> <minY> <maxX> <maxY>


Example:

mncd.ui 0.4 0.92 0.6 0.98


The plugin validates:

All 4 values are numeric.

minX < maxX and minY < maxY.

After changing anchors:

Config is saved.

Any active TC UI is destroyed; players just need to reopen a TC to see the new position.

Configuration

After first run, a config file like this will be generated:

{
  "CheckAuth": false,
  "TeamAwareProtection": true,
  "EntityRadius": 30.0,
  "AutoDetectWipeFromTags": true,
  "WipeModeOverride": "Manual",
  "WipeStartUnixTime": 0,
  "EnableTcWipeUI": true,
  "UiBackgroundColor": "0.05 0.05 0.05 0.85",
  "UiTextColor": "0.9 0.9 0.9 1.0",
  "UiAnchorMin": "0.4 0.92",
  "UiAnchorMax": "0.6 0.98"
}

Key Settings

CheckAuth

false (default): anything in radius = no decay.

true: only entities owned by TC-authorized players (and optionally their team).

TeamAwareProtection

Works only when CheckAuth is true.

If true: players on the same team as TC owner or any authorized player are also protected.

EntityRadius

Radius around each TC where decay is disabled.

AutoDetectWipeFromTags

Reads ConVar.Server.tags and looks for keywords:

weekly → Weekly

biweekly / bi-weekly → BiWeekly

monthly → Monthly

WipeModeOverride

Used when AutoDetectWipeFromTags is false or detection fails.

WipeStartUnixTime

Internal; automatically set via OnNewSave or wipestartnow.

EnableTcWipeUI

Toggles the wipe timer CUI panel when opening TCs.

UiBackgroundColor, UiTextColor

RGBA strings for the CUI panel.

UiAnchorMin, UiAnchorMax

Panel position, modified by /mncdui and mncd.ui.

Localization

All player-facing messages are routed through the Lang API.

Default English is registered in-code.
You can override by creating language files:

oxide/lang/ModernNoCupboardDecay.en.json
oxide/lang/ModernNoCupboardDecay.de.json
oxide/lang/ModernNoCupboardDecay.fr.json
...


Each file uses the same keys as defined in the plugin (e.g. "Status.Report", "UI.WipeLine", "Error.NoPermission", etc.).

Behavior Notes

Decay prevention:

Decay damage (DamageType.Decay) is intercepted in OnEntityTakeDamage.

If the entity is inside a TC bubble (and passes auth rules), decay is scaled to 0.

Auth & teams:

Uses BuildingPrivlidge.authorizedPlayers for auth.

Uses RelationshipManager.ServerInstance.FindPlayersTeam to check team membership.

Wipe time:

Wipe start is updated:

When a new save is detected (OnNewSave).

When an admin runs wipestartnow.

Wipe duration is based on:

Autodetected mode from server.tags, or

WipeModeOverride.

TC UI:

Uses CUI attached to "Overlay"; cleans up on:

OnLootEntityEnd

OnPlayerDisconnected

Position changes from UI commands.
