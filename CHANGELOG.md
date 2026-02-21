# ModernNoCupboardDecay — Changelog
All notable changes to this project are documented here.

---

## [5.2.0] – Version alignment and documentation pass

Bumps all support files (manifest.json, .umod.yaml, README.md) to match the
plugin source version.  No functional code changes from 5.1.0.

### Changed
- `manifest.json` version field: 5.0.0 → 5.2.0.
- `.umod.yaml` version field: 4.0.0 → 5.2.0; permissions table completed with
  all three nodes (admin, debug, preview).
- `README.md` rewritten to reflect 5.x command set, config table updated with
  `PreviewCooldownSeconds` (new in 5.1.0), and RCON audit note added.

---

## [5.1.0] – Security hardening

Full security audit and refactor.  All issues from the code review addressed.

### Fixed (security)
- **C1** `HitBuffer` was a `static` field shared across all
  `Physics.OverlapSphereNonAlloc` calls.  Converted to an instance field;
  concurrent decay hooks and debug-overlay timer ticks no longer share the
  same buffer.
- **C2** `mncd.set` from RCON / server console bypassed permission checks
  silently.  Behaviour is unchanged (RCON is trusted), but every change is now
  logged to the server console for auditability.
- **C3** `/mncdpreview` iterated all of `BaseNetworkable.serverEntities` with
  no rate limit.  Added `PreviewCooldownSeconds` config key (default 15 s,
  clamp 5–300 s) enforced per-player via `_previewLastUsed` dictionary.
- **H1** `SetUiAnchors` did not clamp floats to `[0, 1]`.  Replaced with
  `WriteAnchorsSafe` which clamps all four inputs before writing to config.
- **H2** `ApplyUiOffset` could produce a zero-width/zero-height panel at the
  edge of the normalized range.  `WriteAnchorsSafe` now enforces a minimum
  panel dimension of 0.05 in both axes.
- **H3** `WipeModeOverride` stored raw user input with no sanitisation.
  `SanitiseWipeModeString` now filters to printable ASCII, truncates at 64
  characters, and falls back to "Manual" on empty input.  Called on config
  load and before every `wipemode` change.
- **H4** Debug overlay timer closed over a `BasePlayer` reference.  Rust uses
  object pooling; a disconnected player's object can be reused for a different
  connection without the reference going null.  Timer closure now captures only
  `ulong userID` and calls `BasePlayer.FindByID(userId)` on each tick.
- **M1** `UiBackgroundColor` and `UiTextColor` were passed to CUI JSON without
  validation.  `IsValidCuiColor` now validates both as four space-separated
  floats in `[0, 1]` during `ValidateConfig`; invalid values are replaced with
  safe defaults.
- **M2** `HitBuffer` size raised from 512 to 1024.  A `PrintWarning` is
  emitted when the result count equals the buffer length (silent truncation
  previously hid TC misses in dense bases).
- **M3** `IsPositionProtected` now explicitly guards `_config == null` instead
  of relying implicitly on the `_initialized` flag.
- **M4** Tag detection checked `"weekly"` before `"biweekly"`.  Because
  `"biweekly".Contains("weekly")` is true, servers tagged `"biweekly"` were
  misidentified as Weekly.  Check order reversed.
- **M5** `ApplyConfigChange` for `wipemode` mutated and saved config before
  validating the new value.  Now parses and validates first; a bad value
  returns an error without touching the saved config.

### Added
- `PreviewCooldownSeconds` config key (default 15 s).

### Refactored
- Duplicate chat/console command handlers collapsed into shared implementation
  methods (`HandleUiAnchorChange`, `HandleUiAddOffset`, `HandlePreview`).
- `DisableDebugOverlay` overloaded to accept either `ulong` or `BasePlayer`.
- Full XML `<summary>` doc coverage on every `private` method.
- File header updated with complete security change log.

---

## [5.0.0] – Oxide v2.0.7022 / Naval Update Compatibility

Comprehensive refactor targeting Oxide v2.0.7022 and the Facepunch Rust Naval Update.
Tighter code, critical bug fixes, and performance improvements.

### Fixed
- **Critical: `authorizedPlayers` access pattern** — TC auth list contains
  `ProtoBuf.PlayerNameID` objects; now correctly uses `.userid` instead of
  comparing the entry object directly with `ulong`. This fixes CheckAuth and
  TeamAware protection on Oxide v2.0.7022+.
- **NRE in console help command** — `arg.Args` null check added before access.
- **FormatException guard** — `Msg()` helper now catches `FormatException` from
  malformed lang strings instead of crashing.

### Added
- **`Unload()` hook** — Properly destroys all debug timers and CUI elements for
  every connected player on plugin unload/reload. Prevents orphaned UI panels.
- **Config validation on load** — Clamps EntityRadius (1-500), CustomWipeDays
  (0-365), PreviewRingDuration (1-300), PreviewRingRadiusMultiplier (0.1-10).
  Also enforces radius bounds on live `/mncdset radius` changes.
- **`Reply()` helper** — Unified console/chat response routing eliminates
  duplicated if/else blocks across all console commands.
- **`GetWipeModeDisplayString()` helper** — Eliminates repeated wipe mode
  formatting logic across status report and TC loot UI.

### Improved
- **Zero-GC decay hot path** — Replaced `Physics.OverlapSphere` (allocates a new
  `Collider[]` per call) with `Physics.OverlapSphereNonAlloc` and a pre-allocated
  buffer. Layer mask cached once in `OnServerInitialized` instead of recomputed per tick.
- **Consolidated protection logic** — Two near-duplicate protection methods merged
  into single `IsPositionProtected(Vector3, ulong)` used by both decay prevention
  and debug overlay.
- **TC preview allocation** — Replaced LINQ `.OfType<BuildingPrivlidge>().ToList()`
  with manual `foreach`/`as` iteration over `serverEntities`. No list allocation.
- **Auth list iteration** — Replaced `foreach` with indexed `for` loops on
  `authorizedPlayers` to avoid enumerator allocation.
- **Init guard on decay hook** — `OnEntityTakeDamage` returns immediately if
  plugin has not finished initialization, preventing early null config access.
- **Removed unused `System.Linq` import** and trailing comment on using statements.
- **Consistent field naming** — Private fields use underscore prefix convention.

### Compatibility
- Verified against Oxide/uMod v2.0.7022 API surface.
- Naval update entities (boats, submarines, floating structures) are protected
  by the existing radius-based `OverlapSphereNonAlloc` approach.

---

## [4.0.0] – The Feature Expansion Update

### Added
- TC Protection Preview (Hologram Rings) — `/mncdpreview`
- Draggable / Adjustable Wipe UI Panel — `/mncdui`, `/mncduiadd`, `/mncdresetui`
- Real-Time Debug Overlay — `/mncddebug`
- Full Help System — `/mncdhelp` with topic-based pages
- Automatic Wipe Detection from server.tags (weekly / biweekly / monthly / Nd)
- New Runtime Config Commands via `/mncdset`
- Localization for English, Spanish, Russian, Simplified Chinese

---

## [3.x] – Modernization and Internal Overhaul

Rebuilt decay logic, team-aware protection, wipe UI panel, and config structure.

---

## [2.x] – Compatibility Restoration

Updated entity lists and TC authorization checks for Rust patches.

---

## [1.x] – Initial Release

Simple no-decay-within-TC-radius effect with basic configuration.
