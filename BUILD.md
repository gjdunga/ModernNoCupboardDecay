# Building / Compiling ModernNoCupboardDecay

ModernNoCupboardDecay ships as a single source file, `oxide/plugins/ModernNoCupboardDecay.cs`,
which the Oxide runtime compiles **on the Rust server** when the plugin loads.
You do not need to build anything to deploy it — copy the `.cs` file and reload.

This document describes the optional **compile-validation chain**: a way to
type-check the plugin against the *real* Oxide, Rust and Unity assemblies on your
own machine (or in CI) so API breaks are caught before they reach a live server.
For example, Facepunch's periodic Rust API changes — such as the "Built Different"
update retyping `ConsoleSystem.Arg.Args` from `string[]` to `Facepunch.StringView[]`
— are exactly the class of break this chain catches at build time.

> The DLL produced by this build is a throwaway. The shipped artifact is always
> the raw `.cs` file. This project exists only to make the compiler tell you
> whether that `.cs` file still binds against the current game API.

---

## What's in the chain

| Path | Purpose |
| --- | --- |
| `build/ModernNoCupboardDecay.csproj` | SDK-style project that compiles the plugin against the reference assemblies. Targets `net48`. Excludes the bundled `Oxide.References` facade to avoid a `Newtonsoft.Json` `JsonPropertyAttribute` collision (CS0433). |
| `tools/fetch-references.sh` | Linux/macOS: downloads a Rust dedicated server + Oxide and stages the reference DLLs under `references/`. |
| `tools/fetch-references.ps1` | Windows (PowerShell 5.1+): same as above. |
| `Makefile` | Convenience targets (`make references-managed`, `make build`). |
| `.github/workflows/compile.yml` | CI: fetches references (weekly-cached) and compiles on every push / PR. |

`references/` and `.steamcmd/` are git-ignored — the game DLLs are proprietary
and are fetched per-machine, never committed.

---

## Prerequisites

- **.NET SDK 8.0+** (`dotnet --version`). The `net48` reference assemblies are
  pulled automatically from NuGet (`Microsoft.NETFramework.ReferenceAssemblies`),
  so **no Mono / .NET Framework install is required**, even on Linux.
- **curl, tar, unzip** (Linux/macOS) for the fetch script.
- On Linux, SteamCMD needs 32-bit runtime libs:
  ```bash
  sudo dpkg --add-architecture i386
  sudo apt-get update
  sudo apt-get install -y lib32gcc-s1 ca-certificates
  ```
- Disk: the Rust dedicated server download is several GB. Use `--managed-only`
  to keep only the small `Managed/` folder afterward.

---

## One-time: fetch the reference assemblies

```bash
# Linux/macOS — keep only RustDedicated_Data/Managed (~tens of MB):
make references-managed
```

```powershell
# Windows (PowerShell)
tools\fetch-references.ps1 -ManagedOnly
```

### Already have a server?

Point the build at an existing install's `Managed` folder instead of downloading:

```bash
export RUST_MANAGED="/path/to/server/RustDedicated_Data/Managed"
dotnet build build/ModernNoCupboardDecay.csproj -c Release
# or per-build (highest precedence):
dotnet build build/ModernNoCupboardDecay.csproj -c Release -p:ManagedDir="/path/to/.../Managed"
```

---

## Compile

```bash
make build
# equivalently:
dotnet build build/ModernNoCupboardDecay.csproj -c Release
```

A clean run ends with `Build succeeded` and `0 Error(s)`. **Errors mean the plugin
will not load on a server** with the matching Oxide/Rust build — fix them before
opening a PR or releasing. Two `CS0649` warnings about the `Duel` / `DuelsManager`
fields are expected and harmless: they are `[PluginReference]` fields that Oxide
assigns at runtime.

---

## How CI uses it

`.github/workflows/compile.yml` runs on every push to `main` and every PR that
touches the plugin or the build chain. It restores the `Managed/` folder from a
cache keyed by ISO **year-week**, fetches it on a miss (once a week), and runs
`dotnet build`. The weekly key means the plugin is re-validated against the latest
Rust/Oxide build roughly in step with Rust's patch cadence.

---

## Notes

- Validates **compilation only** — it does not run the plugin. See
  [`CONTRIBUTING.md`](CONTRIBUTING.md) for manual testing guidance.
- Target framework is `net48` because Rust's server scripting backend is Mono /
  .NET Framework 4.x flavoured. `LangVersion` is pinned to `9.0`.
- The build adds no third-party references beyond the stock Oxide/Rust `Managed/`
  assemblies.
