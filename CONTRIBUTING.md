# Contributing &mdash; ModernNoCupboardDecay

Thanks for helping out. This plugin is maintained by
**Gabriel Dungan** &mdash; DunganSoft Technologies. It targets Oxide / uMod
**2.0.7022+** and is verified against the latest Oxide release (currently
**2.0.7338**, May 2026 Rust patch series).

## Ground rules

1. **No value tuples.** uMod's build server strips the `System.ValueTuple`
   shim, so `(T1, T2)` syntax will fail to compile. Use named private
   classes or `out` parameters for multi-value returns. The plugin file
   has a header note enforcing this; please keep it true.
2. **Zero allocations on the decay hot path.** `OnEntityTakeDamage` runs
   on every damage event. Early-return on non-decay damage. Use the
   instance `_hitBuffer` with `Physics.OverlapSphereNonAlloc` &mdash; do not
   allocate inside the loop, do not use LINQ, do not call `ToList()`.
3. **Defensive guards stay defensive.** Anything that touches `_config`
   from a hook or timer must guard `_config == null` and `_initialized`.
   If Oxide ever hot-reloads mid-tick we must fail safe and let vanilla
   decay resume.
4. **Security-relevant changes carry a tag.** Use `C#` / `H#` / `M#` /
   `S#` / `P#` markers in comments and the changelog (Critical, High,
   Medium, Security-misc, Performance). See existing markers in the
   source as a model.
5. **Don't break the config schema.** Add new keys with sane defaults
   and let `ValidateConfig` clamp them. Never remove or rename an
   existing key without a migration path.

## Project layout

```
oxide/plugins/ModernNoCupboardDecay.cs   the plugin
oxide/config/ModernNoCupboardDecay.json  sample config (defaults)
oxide/lang/<locale>/ModernNoCupboardDecay.json
manifest.json                            uMod metadata
.umod.yaml                               uMod package descriptor
README.md / INSTALL.md / CHANGELOG.md
LICENSE                               MIT
```

Keep versions aligned across `[Info(...)]`, `manifest.json`, `.umod.yaml`,
`README.md`, and `INSTALL.md`. The `oxide_verified` field in
`manifest.json` should track the Oxide release the maintainer has actually
tested against.

## Branching and commits

- Work in a feature branch off `main`. Name it after the change, e.g.
  `feature/wipe-tag-fjord`, `fix/buffer-overflow-warning`,
  `chore/bump-2-0-7400`.
- Commits should describe the **why**, not just the **what**. The history
  is referenced from `CHANGELOG.md`, so make it readable.
- Bump the version in **all five** locations when shipping a release:
  `[Info(...)]`, file-header comment, `manifest.json`, `.umod.yaml`,
  `README.md`. Add a `CHANGELOG.md` entry the same commit.
- Pull requests should target `main` and start as **draft** until the
  changelog entry, version bump, and any new locale strings land
  together.

## Adding a translation

1. Copy `oxide/lang/en/ModernNoCupboardDecay.json` to
   `oxide/lang/<your-locale>/ModernNoCupboardDecay.json` (locale code
   matches Oxide's: `en`, `de`, `fr`, `pt-BR`, ...).
2. Translate the **values** only. Never change the keys.
3. Every key registered in `LoadDefaultMessages()` must exist in every
   locale file. Missing keys fall back to English silently and become
   regressions only auditors notice &mdash; that's a v5.3.x lesson, don't
   repeat it.
4. If you add a brand-new key in code, add it to **all five** shipped
   locales in the same PR. Use a literal English translation in the
   non-English files if you don't speak the language &mdash; native speakers
   can refine later.
5. List the locale in `manifest.json` `languages`, `.umod.yaml`
   description, and `README.md`.

## Adding a config key

1. Add the property to `ConfigData` with a safe default and an XML
   summary.
2. Clamp / validate it in `ValidateConfig`. Numerics: `Mathf.Clamp`.
   Strings used in CUI JSON: a dedicated validator like `IsValidCuiColor`.
   Free-form text: sanitise to printable ASCII as `SanitiseWipeModeString`
   does.
3. Add a row to the **Configuration** table in `README.md`.
4. If it's tunable at runtime, add an `/mncdset` option in
   `ApplyConfigChange` and a localisation key + matching row in every
   locale file.

## Adding a command

1. Pair every `[ChatCommand]` with a matching `[ConsoleCommand]` so
   admins can drive it from RCON.
2. Admin-gated commands must call `HasAdminPerm` (or `HasDebugPerm` /
   `HasPreviewPerm`).
3. RCON callers reach the command with `arg.Player() == null`. Treat
   that as trusted but log every state change to the server console
   for auditability (the `mncd.set` console branch is the reference
   implementation).
4. Update the **Commands** table in `README.md` and add help text to
   `LoadDefaultMessages()` keyed under `Help.Topic.<name>`.

## Local testing checklist

Run through these on a dev server before opening a PR:

- [ ] Build a base inside a TC. Trigger decay with the admin damage
      tool. The block should take **0** decay damage; other damage
      types are unaffected.
- [ ] `/mncddebug` &mdash; banner flips Protected / Not Protected on TC
      boundary crossings. **The banner should not flicker** when the
      state hasn't changed (P1 / v5.3.3 regression watch).
- [ ] `/mncdpreview` &mdash; rings draw for every TC. Spam-call it: the
      second call within `PreviewCooldownSeconds` must return the
      cooldown message, not a fresh draw.
- [ ] Open a TC: the wipe-timer panel appears. Close it: the panel
      disappears. Disconnect: no orphan CUI elements remain on
      reconnect.
- [ ] `oxide.reload ModernNoCupboardDecay` mid-session: every timer
      and CUI must be torn down cleanly by `Unload`.
- [ ] `/mncdset wipemode 5d` &mdash; wipe mode becomes `CustomDays (5d)`,
      `AutoDetectWipeFromTags` becomes false, config file reflects both.
- [ ] Hand-edit `oxide/config/ModernNoCupboardDecay.json` with junk
      values (`EntityRadius: 99999`, `UiTextColor: "foo"`,
      `UiAnchorMin: "2 2"`). On next load the file should be silently
      sanitised and written back.

## Security review

Before merging anything that touches:

- the protection logic (`IsPositionProtected`, `IsOwnerAuthorizedOrTeammate`),
- a hook signature,
- CUI JSON construction,
- RCON command handlers,
- or any external string that reaches `Config.WriteObject`,

re-run a security pass and tag the result in `CHANGELOG.md` with the
appropriate `C# / H# / M# / S#` marker. A full historical audit table
lives at the top of `ModernNoCupboardDecay.cs`.

## Filing an issue

Useful issue reports include:
- Plugin version (`/mncd` output),
- Oxide build (`oxide.version`),
- Rust client / server version,
- Repro steps,
- Relevant `oxide/logs/` lines,
- A copy of `oxide/config/ModernNoCupboardDecay.json` if config-related.

Security-sensitive reports: please open a private security advisory on
the GitHub repo rather than a public issue.

## License

Contributions are accepted under the GPL-3.0 terms in
[`LICENSE`](./LICENSE). By opening a pull request you confirm you
have the right to license your contribution under those terms.
