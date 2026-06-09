# CLAUDE.md — ModernNoCupboardDecay

This repository is part of the **DunganSoft Oxide/Rust plugin portfolio** and follows
a shared **DunganSoft Plugin Standard**. There is a single source-of-truth repo holding
the standard, the conformance checker, doc/CI templates, the licensing policy, the
wolf-paw listing icon, and the Codefling listing generator:

→ **https://github.com/gjdunga/rust-plugin-standard** (read its `STANDARD.md`)

## Working rules for this repo
- **Conformance:** `python3 tools/check-standard.py .` must report `0 errors`
  (CI: `.github/workflows/standards.yml`). `tools/check-standard.py` is **vendored**
  from the standard repo — change it there and re-sync; don't fork it here.
- **Compile-check:** `make references-managed` (one-time) then `make build` — type-checks
  the single `oxide/plugins/ModernNoCupboardDecay.cs` against the real Oxide/Rust/Unity assemblies
  (net48). See `BUILD.md`. The shipped artifact is the raw `.cs`; the build DLL is throwaway.
- **Versioning:** one SemVer string, in lockstep across `[Info]`, `manifest.json`,
  `.umod.yaml`, `README.md`, and the top `CHANGELOG.md` heading. Bump + add a changelog
  entry on any user-visible change.
- **Translations:** exactly 8 locales — `en, es, ru, la, zh-CN, de, fr, pt` — each
  key-matched to `en` with identical placeholders. Update all 8 when a message changes.
- **License:** GPL-3.0 (free; uMod-eligible). Per-repo config lives in
  `.plugin-standard.json` (`umod` true/false). Paid plugins use the DunganSoft EULA
  + a private repo (see the standard); these stay free/GPL.
- **Listings:** regenerate `CODEFLING.md` via the vendored `tools/make-listing.py`
  on each release.
- **Release:** tag `vX.Y.Z` → the release workflow drafts a GitHub release; publish it
  and confirm it is marked **Latest**.

When in doubt, the standard repo's `STANDARD.md` is authoritative.
