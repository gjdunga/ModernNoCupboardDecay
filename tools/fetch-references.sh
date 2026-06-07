#!/usr/bin/env bash
# Fetch the Oxide/Rust/Unity reference assemblies the build chain compiles
# against. Installs a Rust dedicated server (Steam app 258550 — free, anonymous)
# and overlays the latest Oxide.Rust release, leaving the assemblies under
# references/RustDedicated_Data/Managed.
#
# These are proprietary game files. They are NOT committed to the repository
# (see .gitignore); every developer / CI run fetches its own copy.
#
# Usage:
#   tools/fetch-references.sh [--managed-only] [--dir DIR]
#
#   --managed-only   After installing, delete everything except
#                    RustDedicated_Data/Managed. Shrinks ~8GB to a few dozen MB
#                    so it fits a CI cache. The full server is not needed to compile.
#   --dir DIR        Install location (default: <repo>/references).
#   -h, --help       Show this help.
#
# Requirements: curl, tar, unzip — plus 32-bit runtime libs for steamcmd
#   Debian/Ubuntu: sudo apt-get install -y lib32gcc-s1 ca-certificates
set -euo pipefail

STEAM_APP_ID=258550
OXIDE_URL="https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust-linux.zip"
STEAMCMD_URL="https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz"

managed_only=0
install_dir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --managed-only) managed_only=1; shift ;;
    --dir)          install_dir="${2:?--dir requires a path}"; shift 2 ;;
    -h|--help)      sed -n '2,/^set -euo/p' "$0" | sed 's/^# \{0,1\}//; /^set -euo/d'; exit 0 ;;
    *)              echo "error: unknown argument '$1'" >&2; exit 2 ;;
  esac
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
install_dir="${install_dir:-$repo_root/references}"
steam_dir="$repo_root/.steamcmd"
managed_dir="$install_dir/RustDedicated_Data/Managed"

for tool in curl tar unzip; do
  command -v "$tool" >/dev/null 2>&1 || { echo "error: '$tool' is required but not installed." >&2; exit 1; }
done

echo "==> Installing SteamCMD into $steam_dir"
mkdir -p "$steam_dir"
if [[ ! -x "$steam_dir/steamcmd.sh" ]]; then
  curl -sSL "$STEAMCMD_URL" | tar -xz -C "$steam_dir"
fi

echo "==> Downloading / updating Rust dedicated server (app $STEAM_APP_ID)"
echo "    into $install_dir (this is large on first run)"
mkdir -p "$install_dir"
# force_install_dir must precede login per SteamCMD's argument ordering.
"$steam_dir/steamcmd.sh" \
  +force_install_dir "$install_dir" \
  +login anonymous \
  +app_update "$STEAM_APP_ID" validate \
  +quit

echo "==> Overlaying latest Oxide.Rust"
tmp_zip="$(mktemp --suffix=.zip)"
trap 'rm -f "$tmp_zip"' EXIT
curl -sSL "$OXIDE_URL" -o "$tmp_zip"
unzip -oq "$tmp_zip" -d "$install_dir"

if [[ ! -d "$managed_dir" ]]; then
  echo "error: expected managed directory not found at $managed_dir" >&2
  echo "       The Steam download or Oxide overlay did not complete." >&2
  exit 1
fi

if [[ "$managed_only" -eq 1 ]]; then
  echo "==> Pruning everything except RustDedicated_Data/Managed"
  find "$install_dir" -mindepth 1 -maxdepth 1 ! -name RustDedicated_Data -exec rm -rf {} +
  find "$install_dir/RustDedicated_Data" -mindepth 1 -maxdepth 1 ! -name Managed -exec rm -rf {} +
fi

echo
echo "Done. Reference assemblies are in:"
echo "  $managed_dir"
echo
echo "Build with:  dotnet build build/ModernNoCupboardDecay.csproj -c Release"
echo "        or:  make build"
