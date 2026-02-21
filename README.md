# ModernNoCupboardDecay v5.3.0 — Administrator Guide

Prevents decay for all building blocks and deployables within any Tool Cupboard
radius.  Includes wipe-timer UI, team-aware auth, debug overlay, TC preview
holograms, live config editing, and multi-language support.

Requires Oxide / uMod 2.0.7022+  |  Compatible with the Rust Naval Update.

---

## Permissions

| Node | Purpose |
|---|---|
| `modernnocupboarddecay.admin` | `/mncdset`, `/mncdui`, `/mncduiadd`, `/mncdresetui` |
| `modernnocupboarddecay.debug` | `/mncddebug` protection-status overlay |
| `modernnocupboarddecay.preview` | `/mncdpreview` (when `PreviewRequiresPermission = true`) |

---

## Commands

### Status and help

```
/mncd                   Show plugin status (radius, wipe mode, state).
/mncdhelp [topic]       In-game help. Topics: basic, ui, set, debug, preview, wipe.
```

### Live configuration (admin)

```
/mncdset <option> <value>
```

| Option | Values | Description |
|---|---|---|
| `checkauth` | true/false | Require TC authorization to protect entities. |
| `teamaware` | true/false | Protect Rust-team members of authed players (needs checkauth). |
| `radius` | 1-500 | TC protection bubble radius in meters. |
| `autodetect` | true/false | Read wipe schedule from server.tags automatically. |
| `wipemode` | Manual/Weekly/BiWeekly/Monthly/Nd | Set wipe schedule (e.g. `5d`). |
| `wipestartnow` | — | Reset wipe start timestamp to now. |

Console / RCON equivalent: `mncd.set <option> <value>`
All RCON changes are logged to the server console for auditability.

### UI positioning (admin)

```
/mncdui <minX> <minY> <maxX> <maxY>   Set panel anchors (normalized 0-1).
/mncduiadd <dx> <dy>                  Nudge panel position.
/mncdresetui                          Reset to default top-center position.
```

Reopen a TC after changing position to refresh the panel.

### TC radius preview

```
/mncdpreview    Draw client-side spheres around every TC's protection bubble.
mncd.preview    Same, from the F1 console.
```

Subject to `PreviewCooldownSeconds` (default 15 s) per player.
Optional permission gate: set `PreviewRequiresPermission = true` in config.

### Protection-status debug overlay

```
/mncddebug    Toggle "MNCD: Protected / Not Protected" banner.
mncd.debug    Same, from the F1 console.
```

Updates every 0.5 s. Requires admin or `modernnocupboarddecay.debug`.
Automatically stops and cleans up on disconnect.

---

## Configuration keys (oxide/config/ModernNoCupboardDecay.json)

| Key | Default | Description |
|---|---|---|
| `CheckAuth` | false | Auth-mode protection. |
| `TeamAwareProtection` | true | Extend auth-mode to Rust team members. |
| `EntityRadius` | 30 | TC bubble radius (meters, 1-500). |
| `AutoDetectWipeFromTags` | true | Detect wipe schedule from server.tags. |
| `WipeModeOverride` | "Manual" | Fallback wipe mode. |
| `CustomWipeDays` | 0 | Days for custom wipe mode. |
| `WipeStartUnixTime` | 0 | UTC epoch of wipe start (auto-set on wipe). |
| `EnableTcWipeUI` | true | Show wipe-timer panel when opening a TC. |
| `UiBackgroundColor` | "0.05 0.05 0.05 0.85" | Panel background (R G B A). |
| `UiTextColor` | "0.9 0.9 0.9 1.0" | Panel text color (R G B A). |
| `UiAnchorMin` | "0.4 0.92" | Panel bottom-left anchor. |
| `UiAnchorMax` | "0.6 0.98" | Panel top-right anchor. |
| `PreviewRequiresPermission` | false | Gate /mncdpreview on permission node. |
| `PreviewCooldownSeconds` | 15 | Minimum seconds between /mncdpreview calls per player. |
| `PreviewRingDuration` | 30 | Seconds preview spheres remain visible. |
| `PreviewRingRadiusMultiplier` | 1.0 | Scale EntityRadius for visual ring only. |

---

## Language files

```
oxide/lang/en/ModernNoCupboardDecay.json      (English)
oxide/lang/es/ModernNoCupboardDecay.json      (Spanish)
oxide/lang/ru/ModernNoCupboardDecay.json      (Russian)
oxide/lang/zh-CN/ModernNoCupboardDecay.json   (Simplified Chinese)
oxide/lang/la/ModernNoCupboardDecay.json      (Latin)
```

---

## Testing procedure

1. Place a TC and authorize yourself.
2. Build within its radius.
3. Trigger decay (admin hurt tool).
4. Run `/mncddebug` and walk in and out of the TC radius.
5. Run `/mncdpreview` to visualize the bubble.
6. Adjust UI live with `/mncduiadd 0 -0.05`.

---

## Support / issues

https://github.com/gjdunga/ModernNoCupboardDecay
