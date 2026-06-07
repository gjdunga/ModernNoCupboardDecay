# Installation &mdash; ModernNoCupboardDecay v5.3.4

A 5-minute install. Full command and config reference is in
[`README.md`](./README.md).

## Requirements

| | |
|---|---|
| Oxide / uMod | **2.0.7022** or newer (verified against **2.0.7338**, May 2026) |
| Rust | Naval Update or newer |
| Dependencies | None &mdash; the plugin is standalone. |

## 1. Drop in the plugin

Copy `oxide/plugins/ModernNoCupboardDecay.cs` into your server's
`oxide/plugins/` directory. Oxide compiles and loads it on the spot.

The server console should print a startup summary similar to:

```
[ModernNoCupboardDecay] Startup summary:
  Version: 5.3.4
  CheckAuth: False  |  TeamAware: True
  EntityRadius: 30m
  AutoDetect: True  |  WipeMode: Weekly
  WipeStart: 2026-05-17 00:00:00Z
  Wipe ends in: 6d 23h 59m
```

If the summary is missing or you see a `Init failed` line, the plugin is in
a safe disabled state &mdash; decay is unaffected, vanilla Rust behavior
resumes. Check the error and reload after fixing.

## 2. Grant permissions (optional)

Server admins inherit every node, so this step is only needed for
non-admin staff:

```
oxide.grant group admin     modernnocupboarddecay.admin
oxide.grant group admin     modernnocupboarddecay.debug
oxide.grant group moderator modernnocupboarddecay.preview
```

| Node | Purpose |
|---|---|
| `modernnocupboarddecay.admin` | `/mncdset`, `/mncdui`, `/mncduiadd`, `/mncdresetui` |
| `modernnocupboarddecay.debug` | `/mncddebug` overlay |
| `modernnocupboarddecay.preview` | `/mncdpreview` (only when `PreviewRequiresPermission = true`) |

## 3. Configure

The config file is created on first load at
`oxide/config/ModernNoCupboardDecay.json`. Edit it and `oxide.reload
ModernNoCupboardDecay`, or change settings live:

```
/mncdset checkauth   true
/mncdset teamaware   true
/mncdset radius      30
/mncdset autodetect  true
/mncdset wipemode    Weekly      # or BiWeekly / Monthly / Manual / 5d
/mncdset wipestartnow
```

The same options work over RCON / server console as `mncd.set ...`, and
every console change is echoed to the server log.

Full key-by-key reference: [`README.md` &rarr; Configuration](./README.md#configuration).

## 4. Wipe day

On a fresh map Oxide fires `OnNewSave` and the plugin resets the wipe start
automatically. If you change wipe schedule mid-cycle, resync the countdown:

```
/mncdset wipestartnow
```

To set the wipe start to a specific epoch, edit
`oxide/config/ModernNoCupboardDecay.json` (`WipeStartUnixTime`, UTC seconds)
and reload.

## 5. Verify

| Check | Command |
|---|---|
| Plugin is enabled and config is sane | `/mncd` |
| In-game help | `/mncdhelp` then `/mncdhelp <topic>` |
| Visualize every TC's protection radius | `/mncdpreview` |
| Live "am I protected?" banner | `/mncddebug` (note: **mncd**, not `mcd`) |

`/mncddebug` toggles a small overlay reading **MNCD: Protected** or
**MNCD: Not Protected**. It refreshes every 0.5 s, requires admin or
`modernnocupboarddecay.debug`, and self-cleans on disconnect. Console
equivalent: `mncd.debug`.

## 6. Translations

Five locales ship with the plugin:

```
oxide/lang/en/ModernNoCupboardDecay.json
oxide/lang/es/ModernNoCupboardDecay.json
oxide/lang/ru/ModernNoCupboardDecay.json
oxide/lang/zh-CN/ModernNoCupboardDecay.json
oxide/lang/la/ModernNoCupboardDecay.json
```

Place them under the same per-locale paths on your server. Adding a new
locale is documented in [`CONTRIBUTING.md`](./CONTRIBUTING.md).

## Updating

Drop in the new `.cs`, then:

```
oxide.reload ModernNoCupboardDecay
```

The config file is preserved across updates; new keys appear with their
defaults and out-of-range values are clamped on load.
