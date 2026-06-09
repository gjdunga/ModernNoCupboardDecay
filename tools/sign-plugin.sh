#!/usr/bin/env bash
#
# sign-plugin.sh — produce a detached, armored OpenPGP signature for the shipped
# plugin source, per the DunganSoft Plugin Standard (§11, Code signing).
#
#     oxide/plugins/<Plugin>.cs  ->  oxide/plugins/<Plugin>.cs.asc
#
# The signature is detached (the .cs stays byte-identical, as Oxide requires) and
# armored. Signs with the DunganSoft release key; the matching public key ships at
# keys/gjdunga.asc and CI verifies against it (see tools/check-standard.py).
#
# Run from the plugin repo root, or pass the repo path:
#     tools/sign-plugin.sh [REPO_PATH]
#
# Requires gpg with the release secret key available (gpg-agent passphrase cached,
# or run interactively). Override the key with $PLUGIN_SIGNING_KEY.
set -euo pipefail

KEY="${PLUGIN_SIGNING_KEY:-EAC0A2AE65CC6C9762DD6AF06877843761D5C6E6}"
ROOT="${1:-.}"

shopt -s nullglob
cs=( "$ROOT"/oxide/plugins/*.cs )
if [[ ${#cs[@]} -ne 1 ]]; then
  echo "error: expected exactly one oxide/plugins/*.cs, found ${#cs[@]}" >&2
  exit 1
fi
src="${cs[0]}"
sig="${src}.asc"

gpg --batch --yes --armor --local-user "$KEY" --detach-sign -o "$sig" "$src"
echo "signed: $sig"

# Self-check against the local keyring so a bad sign fails loudly.
gpg --verify "$sig" "$src"
