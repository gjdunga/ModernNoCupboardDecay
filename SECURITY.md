# Security Policy

## Reporting a vulnerability

Please report security issues **privately**, not as a public issue:

- Open a GitHub **security advisory** (Security tab → "Report a vulnerability"), or
- Contact the maintainer via the email on the [gjdunga](https://github.com/gjdunga) profile.

Include the plugin version, Oxide/uMod build, Rust server build, reproduction
steps, and impact. You will receive an acknowledgement within a few days.

## Scope

This plugin runs inside the Rust server process via Oxide/uMod. Relevant concerns
include command-permission bypass, unsanitised player input reaching logs or
config, and any path that lets a non-admin change protected state. Please do not
run denial-of-service tests against servers you do not own.

## Supported versions

The latest released version receives fixes. Older versions are not maintained;
please update before reporting.
