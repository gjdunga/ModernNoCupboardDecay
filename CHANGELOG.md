# ModernNoCupboardDecay — Changelog
All notable changes to this project are documented here.

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
Version 4.0.0 transforms it from a simple utility plugin into a polished, admin-friendly, player-friendly tool.
