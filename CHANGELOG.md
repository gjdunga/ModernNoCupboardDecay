# ModernNoCupboardDecay -- Changelog
All notable changes to this project are documented here.

---

## [5.3.2] -- 2026-03-30

### Compatibility

- Verified compatible with Oxide 2.0.7182 (Rust Community Update 268). No hook
  signature changes were introduced between Oxide 2.0.7022 and 2.0.7182 that
  affect this plugin. All hooks (OnEntityTakeDamage, OnLootEntity, OnLootEntityEnd,
  OnPlayerDisconnected, OnNewSave, OnServerInitialized, Init, Unload) remain
  correct. The authorizedPlayers HashSet<ulong> pattern, DamageType.Decay via
  using Rust, BuildingPrivlidge.GetComponentInParent, and
  Physics.OverlapSphereNonAlloc signatures are all unchanged.
- Compatibility note added to plugin file header and README.

### Documentation

- README.md compatibility line updated to include Rust Community Update 268
  and Oxide 2.0.7182.
- INSTALL.md created (was absent from repository).

### No Code Changes

No functional, security, or performance changes. This is a documentation and
compatibility-verification release only.

---

All notable changes to this project are documented here.

---

## [5.3.1] – Rust API compatibility fixes and physics query hardening

### Fixed (compilation)
- **DamageType namespace:** Added `using Rust;` directive to resolve `DamageType.Decay`
  inside `namespace Oxide.Plugins`. Prior attempt used `global::DamageType` which pointed
  to the C# root namespace; the type lives under `namespace Rust` in the Facepunch assembly,
  not at root, so that made the error worse. Correct fix is the using directive.
- **authorizedPlayers iteration:** `BuildingPrivlidge.authorizedPlayers` is now
  `HashSet<ulong>` in current Rust (not `List<ProtoBuf.PlayerNameID>` as documented in the
  Naval Update notes). Removed indexed for-loops (HashSet has no indexer) and removed
  `.userid` field access (entries are raw Steam64 IDs). Both `IsOwnerAuthorizedOrTeammate`
  and `IsAuthorizedOnCupboard` now use `foreach (ulong authedId in authList)` with direct
  comparison.

### Fixed (runtime)
- **_protectionMask narrowed to "Deployed" only:** Previous mask included Construction,
  Construction Trigger, and Trigger layers. BuildingPrivlidge is on the Deployed layer
  exclusively. The broader mask caused OverlapSphereNonAlloc to fill the buffer with
  building block and trigger volume colliders before reaching any TCs, saturating the
  HitBuffer at 1024 on dense bases and producing false-miss warnings. Narrowing to
  Deployed eliminates irrelevant collider hits while retaining all TC matches.

### Changed
- **HitBuffer raised to 4086:** Appropriate for build/PVE servers with high deployable
  density. Previous values: 512 (original), 1024 (v5.1.0 M2 fix), 2048 (interim).
  4086 gives adequate headroom for large player builds within a 30m radius sphere on
  a low-wipe PVE server without meaningful memory cost.
- Version synced to 5.3.1 across `plugin.cs`, `manifest.json`, `README.md`, `.umod.yaml`.

---

## [5.3.0] – Security audit, Latin translation, structural fixes

### Fixed (security)
- **S1** `OnLootEntity` lacked an `_initialized` / `_config == null` guard.
  If a player opened a TC before `OnServerInitialized` completed (e.g. during
  a race at server start), `GetWipeModeDisplayString()` and
  `GetWipeTimeRemaining()` could throw a `NullReferenceException`.  Guard now
  matches `OnEntityTakeDamage`.
- **S2** `GetWipeModeDisplayString()` accessed `_config.CustomWipeDays` without
  a null check on `_config`.  Guard added; safe even if called before init.
- **S3** `Preview.Cooldown` localisation key was registered in
  `LoadDefaultMessages()` but absent from all four shipped lang JSON files.
  Players in non-English locales saw the raw key string instead of the
  translated message.  Added to all files (en, es, ru, zh-CN, la).

### Fixed (structure)
- **S4** Stale file `oxide/lang/ModernNoCupboardDecay.en.json` (wrong path,
  flat layout instead of per-locale subdirectory) removed.
- **S5** Sample config was at `oxide/oxide/config/ModernNoCupboardDecay.json`
  (doubly-nested, would create `/oxide/oxide/config/` on disk).  Moved to
  `oxide/config/ModernNoCupboardDecay.json`.

### Added
- **Latin (`la`) translation** — full ecclesiastical Latin for all UI strings,
  help topics, error messages, and command feedback.
- **Value-tuple policy note** in file header confirming zero C# value-tuple
  usage (required for uMod build server compatibility).

### Changed
- `manifest.json`: added `"languages"` array listing all five locales.
- `.umod.yaml`: description updated to list all five locales.
- `README.md`: version bump, Latin added to language file list.

---

## [5.2.0] – Version alignment and documentation pass

Bumps all support files (manifest.json, .umod.yaml, README.md) to match the
plugin source version.  No functional code changes from 5.1.0.

### Changed
- `manifest.json` version field: 5.0.0 -> 5.2.0.
- `.umod.yaml` version field: 4.0.0 -> 5.2.0; permissions table completed with
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
  clamp 5-300 s) enforced per-player via `_previewLastUsed` dictionary.
- **H1** `SetUiAnchors` did not clamp floats to `[0, 1]`.  Replaced with
  `WriteAnchorsSafe` which clamps all four inputs before writing to config.
- **H2** `ApplyUiOffset` could produce a zero-width/zero-height panel at the
  edge of the normalized range.  `WriteAnchorsSafe` now enforces a minimum
  panel dimension of 0.05 in both axes.
- **H3** `WipeModeOverride` stored raw user input with no sanitisation.
  `SanitiseWipeModeString` now filters to printable ASCII, truncates at 64
  characters, and falls back to "Manual" on empty input.
- **H4** Debug overlay timer closed over a `BasePlayer` reference.  Timer
  closure now captures only `ulong userID` and calls `BasePlayer.FindByID()`
  on each tick.
- **M1** `UiBackgroundColor` and `UiTextColor` were passed to CUI JSON without
  validation.  `IsValidCuiColor` validates both as four space-separated floats
  in `[0, 1]` during `ValidateConfig`.
- **M2** `HitBuffer` size raised from 512 to 1024.  A `PrintWarning` is
  emitted when the result count equals the buffer length.
- **M3** `IsPositionProtected` now explicitly guards `_config == null`.
- **M4** Tag detection checked `"weekly"` before `"biweekly"`.  Check order
  reversed; `"biweekly"` / `"bi-weekly"` tested first.
- **M5** `ApplyConfigChange` for `wipemode` mutated and saved config before
  validating the new value.  Now parses and validates first.

### Added
- `PreviewCooldownSeconds` config key (default 15 s).

---

## [5.0.0] – Oxide v2.0.7022 / Naval Update Compatibility

Comprehensive refactor targeting Oxide v2.0.7022 and the Facepunch Rust Naval Update.

### Fixed
- **Critical: `authorizedPlayers` access pattern** — now correctly uses `.userid`.
- **NRE in console help command** — `arg.Args` null check added.
- **FormatException guard** — `Msg()` now catches `FormatException`.

### Added
- `Unload()` hook — destroys all debug timers and CUI elements on unload.
- Config validation on load — clamps all numeric ranges.
- `Reply()` helper — unified console/chat response routing.
- `GetWipeModeDisplayString()` helper.

### Improved
- Zero-GC decay hot path via `OverlapSphereNonAlloc` with pre-allocated buffer.
- Consolidated protection logic into `IsPositionProtected(Vector3, ulong)`.
- TC preview uses manual `foreach`/`as` iteration; no LINQ allocation.
- Auth list iteration uses indexed `for` loops.

---

## [4.0.0] – The Feature Expansion Update

### Added
- TC Protection Preview (Hologram Rings)
- Draggable / Adjustable Wipe UI Panel
- Real-Time Debug Overlay
- Full Help System
- Automatic Wipe Detection from server.tags
- New Runtime Config Commands
- Localization for en, es, ru, zh-CN

---

## [3.x] – Modernization and Internal Overhaul

Rebuilt decay logic, team-aware protection, wipe UI panel, and config structure.

---

## [2.x] – Compatibility Restoration

Updated entity lists and TC authorization checks for Rust patches.

---

## [1.x] – Initial Release

Simple no-decay-within-TC-radius effect with basic configuration.
