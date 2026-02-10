# ModernNoCupboardDecay — Changelog
All notable changes to this project are documented here.

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
  `Collider[]` per call) with `Physics.OverlapSphereNonAlloc` and a static 512-element
  buffer. Layer mask cached once in `OnServerInitialized` instead of recomputed per tick.
- **Consolidated protection logic** — `IsEntityWithinCupboardProtection` and
  `IsPlayerInProtectedZone` (duplicated ~80 lines) merged into single
  `IsPositionProtected(Vector3, ulong)` method used by both decay prevention
  and debug overlay.
- **TC preview allocation** — Replaced LINQ `.OfType<BuildingPrivlidge>().ToList()`
  with manual `foreach`/`as` iteration over `serverEntities`. No list allocation.
- **Auth list iteration** — Replaced `foreach` with indexed `for` loops on
  `authorizedPlayers` to avoid enumerator allocation.
- **Init guard on decay hook** — `OnEntityTakeDamage` returns immediately if
  plugin has not finished initialization, preventing early null config access.
- **Removed unused `System.Linq` import** and trailing comment on using statements.
- **Consistent field naming** — Private fields use underscore prefix convention.
- **Leaner console command handlers** — Shared `Reply()` method and null-safe
  `arg?.Args` patterns throughout.

### Compatibility
- Verified against Oxide/uMod v2.0.7022 API surface.
- All hook signatures match current Oxide expectations.
- Naval update entities (boats, submarines, floating structures) are protected
  by the existing radius-based `OverlapSphereNonAlloc` approach — no entity
  type filtering means all decayable objects within TC range are covered.

---

## [4.0.0] – The Feature Expansion Update
ModernNoCupboardDecay has undergone a major evolution. This version introduces powerful new admin tools, player utilities, UI improvements, wipe intelligence, visuals, and multilingual support.

### Added
- **TC Protection Preview (Hologram Rings)**  
  Players can now visualize the decay protection radius with `/mncdpreview`.  
  Uses Rust’s client-side ddraw system. Toggleable and permission-aware.

- **Draggable / Adjustable Wipe UI Panel**  
  Fully movable wipe timer using:  
  `/mncdui`, `/mncduiadd`, `/mncdresetui`  

- **Real-Time Debug Overlay**  
  Players may toggle a status indicator (`MNCD: Protected / Not Protected`) using `/mncddebug`.

- **Full Help System**  
  New command `/mncdhelp`, along with topic-based help pages:  
  `ui`, `set`, `debug`, `preview`, `wipe`

- **Automatic Wipe Detection**  
  Reads server tags to determine:  
  - weekly  
  - biweekly  
  - monthly  
  - ANY custom N-day wipe (e.g., 5d, 10d, 3d)  
  Falls back to manual mode if overridden.

- **New Runtime Config Commands**  
  `/mncdset checkauth <bool>`  
  `/mncdset teamaware <bool>`  
  `/mncdset radius <meters>`  
  `/mncdset autodetect <bool>`  
  `/mncdset wipemode <mode>`  
  `/mncdset wipestartnow`

- **Localization Support**  
  Full translations added for:  
  - English  
  - Spanish  
  - Russian  
  - Simplified Chinese  

- **Better Logging, More Documentation, Cleaner Structure**  
  Internal code rewritten for readability, maintainability, and uMod compliance.

---

## [3.x] – Modernization & Internal Overhaul
### Added
- Rebuilt decay protection logic to match new Rust entity behaviors  
- Team-aware protection originally introduced  
- Wipe UI panel created  
- Initial configuration restructuring  
- Updated decay handling for newly added Rust deployables (e.g., wallpaper)

### Improved
- Overall performance & hook stability  
- Cleaner error handling  
- Better config persistence

---

## [2.x] – Compatibility Restoration
### Added
- Updated entity lists to include items introduced by recent Rust patches  
- Improved checks for TC authorization  
- More reliable decay-prevention logic

### Fixed
- Several decay exceptions caused by new deployable classes  
- Plugins breaking after Facepunch API changes

---

## [1.x] – Initial Release
### Added
- Simple “no decay within TC radius” effect  
- Basic configuration  
- Minimal logging  

This was the foundation for everything that followed.

---

## Summary
ModernNoCupboardDecay has grown into a **full control suite** for Rust decay behavior, wipe awareness, visual debugging, UI customization, and transparent gameplay communication.
Version 5.0.0 refactors the core for Oxide v2.0.7022+ compatibility, fixes critical auth bugs, and optimizes the decay hot path for high-population servers.
