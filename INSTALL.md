# Installation Guide

Follow these steps to install **Modern No Cupboard Decay v5.3.3** on your Rust server with uMod (Oxide).

---

## 1. Requirements

| Requirement | Version |
|---|---|
| Oxide / uMod | 2.0.7022 or newer (verified against 2.0.7338, May 2026) |
| Rust | Naval Update or newer |

No other plugins are required. Modern No Cupboard Decay is standalone.

## 2. Install the plugin

Copy `ModernNoCupboardDecay.cs` into your server's `oxide/plugins/` directory.

Oxide will compile and load it automatically. Watch the console for:

```
[ModernNoCupboardDecay] v5.3.3 initialized. Radius: 30m | Mode: Weekly
```

If you see a hard-fail message instead, check that Oxide loaded correctly and
that the server has finished initializing before the plugin was placed.

## 3. Grant permissions

Three permission nodes are available. Grant them to roles or players as needed:

```
oxide.grant group admin modernnocupboarddecay.admin
oxide.grant group admin modernnocupboarddecay.debug
oxide.grant group moderator modernnocupboarddecay.preview
```

| Node | Grants access to |
|---|---|
| `modernnocupboarddecay.admin` | `/mncdset`, `/mncdui`, `/mncduiadd`, `/mncdresetui` |
| `modernnocupboarddecay.debug` | `/mncddebug` protection-status overlay |
| `modernnocupboarddecay.preview` | `/mncdpreview` TC radius rings (when `PreviewRequiresPermission = true`) |

## 4. Configure

The configuration file is created at `oxide/config/ModernNoCupboardDecay.json` on first run.
Edit it and reload, or use the live `/mncdset` command in-game or via RCON.

Key settings:

| Key | Default | Purpose |
|---|---|---|
| `CheckAuth` | `false` | Restrict protection to TC-authorized players only |
| `TeamAwareProtection` | `true` | Extend protection to Rust team members of authorized players |
| `EntityRadius` | `30.0` | TC protection radius in metres |
| `AutoDetectWipeFromTags` | `true` | Detect wipe schedule from server.tags |
| `WipeModeOverride` | `Manual` | Override wipe mode: Weekly, Biweekly, Monthly, Custom, Manual |
| `EnableTcWipeUI` | `true` | Show wipe-timer panel when players open a TC |
| `PreviewRequiresPermission` | `false` | Require `modernnocupboarddecay.preview` for /mncdpreview |
| `PreviewCooldownSeconds` | `15.0` | Per-player cooldown for /mncdpreview (5-300 s) |

Full config reference is in README.md.

## 5. Set the wipe start time

After each wipe, set the wipe start time so the UI panel shows correct time remaining:

```
/mncdset wipestartnow
```

Or set it manually with a Unix timestamp:

```
/mncdset WipeStartUnixTime 1700000000
```

The plugin also detects wipes automatically via the `OnNewSave` hook when the map is wiped.

## 6. Verify

Run `/mncd` to confirm the plugin is active and to see the current state, radius,
wipe mode, and remaining wipe time.  `/mncdhelp` lists every command and topic.

Use `/mncdpreview` in-game to see the TC protection radius as a hologram ring around any
nearby Tool Cupboard.

## Updating

Replace `ModernNoCupboardDecay.cs` and reload:

```
oxide.reload ModernNoCupboardDecay
```

Configuration is preserved across updates.

## Language files

Five locales are included. Place them in the correct per-locale directory if not already present:

```
oxide/lang/en/ModernNoCupboardDecay.json
oxide/lang/es/ModernNoCupboardDecay.json
oxide/lang/ru/ModernNoCupboardDecay.json
oxide/lang/zh-CN/ModernNoCupboardDecay.json
oxide/lang/la/ModernNoCupboardDecay.json
```
