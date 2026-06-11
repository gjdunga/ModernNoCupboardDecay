# ModernNoCupboardDecay

**v6.0.1** &middot; Oxide / uMod plugin for Rust &middot; GPL-3.0 license

Maintained by **Gabriel Dungan** &mdash; DunganSoft Technologies.

Stops decay on every building block and deployable inside a Tool Cupboard's
protection radius. Ships with a wipe-timer panel, team-aware authorization,
admin debug overlay, TC preview holograms, live config editing, and translations
for English, Spanish, Russian, Latin, Simplified Chinese, German, French, and Portuguese.

| | |
|---|---|
| Requires | Oxide / uMod **2.0.7022+** |
| Verified | Oxide **2.0.7423** (Built Different, 2026-06) |
| Install | [`INSTALL.md`](./INSTALL.md) |
| Changes | [`CHANGELOG.md`](./CHANGELOG.md) |
| Contribute | [`CONTRIBUTING.md`](./CONTRIBUTING.md) |

---

## Commands

| Command | Console | Who | What it does |
|---|---|---|---|
| `/mncd` | `mncd` | anyone | Show plugin status. |
| `/mncdhelp [topic]` | `mncd.help` | anyone | Help. Topics: `basic`, `ui`, `set`, `debug`, `preview`, `wipe`. |
| `/mncdpreview` | `mncd.preview` | anyone* | Draw client-side rings around every TC. |
| `/mncddebug` | `mncd.debug` | admin / `.debug` | Toggle the **MNCD: Protected / Not Protected** banner. |
| `/mncdset <opt> <value>` | `mncd.set` | admin | Live config edit. See table below. |
| `/mncdui <minX> <minY> <maxX> <maxY>` | `mncd.ui` | admin | Set wipe-panel anchors (0..1 normalized). |
| `/mncduiadd <dx> <dy>` | `mncd.uiadd` | admin | Nudge wipe-panel position. |
| `/mncdresetui` | `mncd.resetui` | admin | Reset wipe panel to default top-center. |

*`/mncdpreview` is open to everyone by default but is rate-limited per player
(`PreviewCooldownSeconds`, default 15 s). Set `PreviewRequiresPermission = true`
to gate it on `modernnocupboarddecay.preview`.

### `/mncdset` options

| Option | Value | Effect |
|---|---|---|
| `checkauth` | true / false | Require TC authorization for protection. |
| `teamaware` | true / false | Also protect Rust-team members (needs `checkauth = true`). |
| `radius` | 1 .. 500 | Protection bubble radius in meters. |
| `autodetect` | true / false | Read wipe schedule from `server.tags`. |
| `wipemode` | `Manual` / `Weekly` / `BiWeekly` / `Monthly` / `Nd` (e.g. `5d`) | Wipe schedule. Disables `autodetect`. |
| `wipestartnow` | &mdash; | Reset wipe start to now. |

Console / RCON form: `mncd.set <opt> <value>`. Every RCON change is echoed to
the server log for auditability.

---

## Permissions

| Node | Grants |
|---|---|
| `modernnocupboarddecay.admin` | `/mncdset`, `/mncdui`, `/mncduiadd`, `/mncdresetui` |
| `modernnocupboarddecay.debug` | `/mncddebug` overlay |
| `modernnocupboarddecay.preview` | `/mncdpreview` (only when `PreviewRequiresPermission = true`) |

Server admins implicitly satisfy every node.

---

## Configuration

File: `oxide/config/ModernNoCupboardDecay.json` (created on first load).

| Key | Default | Notes |
|---|---|---|
| `CheckAuth` | `false` | When true, only entities owned by TC-authed players are protected. |
| `TeamAwareProtection` | `true` | Extends `CheckAuth` to the owner's Rust team. |
| `EntityRadius` | `30.0` | Bubble radius in meters. Clamped to `[1, 500]`. |
| `AutoDetectWipeFromTags` | `true` | Reads `server.tags` for `weekly` / `biweekly` / `monthly` / `Nd`. |
| `WipeModeOverride` | `"Manual"` | Used when auto-detect is off or finds nothing. |
| `CustomWipeDays` | `0` | Day count for `CustomDays` mode. Clamped to `[0, 365]`. |
| `WipeStartUnixTime` | `0` | UTC epoch of current wipe start. Auto-set on `OnNewSave`. |
| `EnableTcWipeUI` | `true` | Show the wipe-timer CUI panel when a TC is opened. |
| `UiBackgroundColor` | `"0.05 0.05 0.05 0.85"` | `R G B A`, each in `[0, 1]`. |
| `UiTextColor` | `"0.9 0.9 0.9 1.0"` | `R G B A`, each in `[0, 1]`. |
| `UiAnchorMin` | `"0.4 0.92"` | Panel bottom-left, normalized `[0, 1]`. |
| `UiAnchorMax` | `"0.6 0.98"` | Panel top-right, normalized `[0, 1]`. |
| `PreviewRequiresPermission` | `false` | Gate `/mncdpreview` on the permission node. |
| `PreviewCooldownSeconds` | `15.0` | Per-player cooldown. Clamped to `[5, 300]`. |
| `PreviewRingDuration` | `30.0` | Seconds the ring stays visible. Clamped to `[1, 300]`. |
| `PreviewRingRadiusMultiplier` | `1.0` | Visual-only multiplier. Clamped to `[0.1, 10]`. |

Out-of-range, malformed, or hand-edited values are clamped or reset on load
and the corrected file is rewritten to disk.

---

## Localization

Per-locale files live under `oxide/lang/<locale>/ModernNoCupboardDecay.json`:

`en`, `es`, `ru`, `la`, `zh-CN`, `de`, `fr`, `pt` (English, Spanish, Russian, Latin, Simplified Chinese, German, French, Portuguese).

Adding a locale: copy `en/ModernNoCupboardDecay.json`, translate the values,
and drop it under the appropriate folder. See `CONTRIBUTING.md`.

---

## Smoke test

1. Place a TC and authorize yourself.
2. Build a wall or deployable inside the radius.
3. `/mncddebug` &mdash; confirm the banner flips to **MNCD: Protected**.
4. `/mncdpreview` &mdash; confirm the ring shows the protection bubble.
5. Open the TC &mdash; the wipe-timer panel should appear.
6. `/mncduiadd 0 -0.05` &mdash; the panel slides down on the next TC open.

---

## Support

Issues and feature requests: <https://github.com/gjdunga/ModernNoCupboardDecay>
