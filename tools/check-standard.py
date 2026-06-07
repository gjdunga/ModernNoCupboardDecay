#!/usr/bin/env python3
"""
check-standard.py — conformance checker for DunganSoft Oxide/Rust plugins.

Validates a plugin repository against the DunganSoft Plugin Standard (see
STANDARD.md). Run from a plugin repo root, or pass the repo path:

    python3 tools/check-standard.py [REPO_PATH]

Exit code 0 = conformant (no errors; warnings allowed), 1 = violations found.

Per-repo configuration lives in `.plugin-standard.json` at the repo root:

    {
      "umod": true,                 // false for commercial-marketplace plugins
      "author_handle": "gjdunga",   // required [Info] author when umod=true
      "locales": ["en","es","ru","la","zh-CN","de","fr","pt"],
      "config_required": true,      // require a sample oxide/config/<Plugin>.json
      "icon_required": false        // icon.png: warn (false) or error (true)
    }

Only the Python standard library plus PyYAML are required.
"""
import sys, os, re, json, glob

try:
    import yaml
except ImportError:
    print("ERROR: PyYAML required (pip install pyyaml)", file=sys.stderr)
    sys.exit(2)

DEFAULTS = {
    "umod": True,
    "author_handle": "gjdunga",
    "locales": ["en", "es", "ru", "la", "zh-CN", "de", "fr", "pt"],
    "config_required": True,
    "icon_required": False,
}

REQUIRED_FILES = [
    "README.md", "INSTALL.md", "CHANGELOG.md", "CONTRIBUTING.md",
    "SECURITY.md", "BUILD.md", "LICENSE", ".editorconfig",
    ".gitattributes", ".gitignore", "manifest.json", ".umod.yaml",
]
MANIFEST_REQUIRED = [
    "name", "title", "version", "author", "author_handle", "description",
    "license", "game", "language", "tags", "permissions", "commands",
    "compatibility", "links",
]
UMOD_REQUIRED = [
    "name", "title", "version", "author", "author_handle",
    "description", "game", "language", "license", "tags",
]

errors, warnings = [], []
def err(m): errors.append(m)
def warn(m): warnings.append(m)


def placeholders(s):
    """Return the multiset of format placeholders: {0}, {1:00}, {named}."""
    return sorted(re.findall(r"\{([A-Za-z0-9_]+)(?::[^}]*)?\}", s))


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main(repo):
    cfg = dict(DEFAULTS)
    cfgp = os.path.join(repo, ".plugin-standard.json")
    if os.path.exists(cfgp):
        try:
            cfg.update(load_json(cfgp))
        except Exception as e:
            err(f".plugin-standard.json is invalid JSON: {e}")
    else:
        warn(".plugin-standard.json missing — using defaults")

    # --- locate the plugin source --------------------------------------
    cs = sorted(glob.glob(os.path.join(repo, "oxide/plugins/*.cs")))
    if len(cs) != 1:
        err(f"expected exactly one oxide/plugins/*.cs, found {len(cs)}")
        return finish()
    cs_path = cs[0]
    plugin = os.path.splitext(os.path.basename(cs_path))[0]
    src = open(cs_path, encoding="utf-8", errors="replace").read()

    # --- required files & naming ---------------------------------------
    for rel in REQUIRED_FILES:
        if not os.path.exists(os.path.join(repo, rel)):
            err(f"missing required file: {rel}")
    # forbid the wrong-casing variants we have seen
    for variant in ("License.md", "LICENSE.md", "LICENSE.MD", "changelog.md", "Changelog.md"):
        p = os.path.join(repo, variant)
        if os.path.exists(p) and variant not in ("LICENSE",):
            warn(f"non-canonical filename present: {variant} (use LICENSE / CHANGELOG.md)")
    if not os.path.exists(os.path.join(repo, f"build/{plugin}.csproj")):
        err(f"missing build/{plugin}.csproj (compile chain)")
    if not os.path.exists(os.path.join(repo, "tools/check-standard.py")):
        warn("tools/check-standard.py not vendored into this repo")
    icon = os.path.exists(os.path.join(repo, "icon.png"))
    if not icon:
        (err if cfg["icon_required"] else warn)("icon.png missing (uMod/marketplace listing icon)")

    # --- [Info] / [Description] / class -------------------------------
    m = re.search(r'\[Info\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([0-9]+\.[0-9]+\.[0-9]+)"\s*\)\]', src)
    info_ver = info_author = None
    if not m:
        err("plugin is missing a well-formed [Info(\"Title\", \"author\", \"x.y.z\")] attribute")
    else:
        _, info_author, info_ver = m.group(1), m.group(2), m.group(3)
        if cfg["umod"] and info_author != cfg["author_handle"]:
            err(f'[Info] author is "{info_author}"; uMod requires the submitting username "{cfg["author_handle"]}"')
    if not re.search(r'\[Description\(\s*"', src):
        err("plugin is missing a [Description(\"...\")] attribute")
    if not re.search(rf'class\s+{re.escape(plugin)}\s*:', src):
        err(f"plugin class name does not match filename ({plugin})")
    if "namespace Oxide.Plugins" not in src:
        err("plugin is not in `namespace Oxide.Plugins`")

    # --- manifest.json -------------------------------------------------
    man = None
    mp = os.path.join(repo, "manifest.json")
    if os.path.exists(mp):
        try:
            man = load_json(mp)
            for k in MANIFEST_REQUIRED:
                if k not in man:
                    err(f"manifest.json missing key: {k}")
            if man.get("author_handle") and man["author_handle"] != cfg["author_handle"]:
                warn(f'manifest author_handle "{man.get("author_handle")}" != "{cfg["author_handle"]}"')
        except Exception as e:
            err(f"manifest.json invalid JSON: {e}")

    # --- .umod.yaml ----------------------------------------------------
    umod = None
    up = os.path.join(repo, ".umod.yaml")
    if os.path.exists(up):
        try:
            umod = yaml.safe_load(open(up, encoding="utf-8"))
            for k in UMOD_REQUIRED:
                if k not in (umod or {}):
                    err(f".umod.yaml missing key: {k}")
        except Exception as e:
            err(f".umod.yaml invalid YAML: {e}")

    # --- version sync --------------------------------------------------
    versions = {}
    if info_ver: versions["[Info]"] = info_ver
    if man and "version" in man: versions["manifest.json"] = str(man["version"])
    if umod and "version" in umod: versions[".umod.yaml"] = str(umod["version"])
    cl = os.path.join(repo, "CHANGELOG.md")
    if os.path.exists(cl):
        cm = re.search(r'^\s*##\s*\[?v?([0-9]+\.[0-9]+\.[0-9]+)\]?', open(cl, encoding="utf-8").read(), re.M)
        if cm: versions["CHANGELOG.md"] = cm.group(1)
        else: warn("could not find a version heading in CHANGELOG.md")
    distinct = set(versions.values())
    if len(distinct) > 1:
        err("version mismatch across files: " + ", ".join(f"{k}={v}" for k, v in versions.items()))

    # --- no Reflection (uMod only) ------------------------------------
    if cfg["umod"]:
        refl = re.findall(r'\b(System\.Reflection|\.GetField\(|\.GetProperty\(|\.GetMethod\(|BindingFlags|Activator\.CreateInstance|MethodInfo|FieldInfo|PropertyInfo)\b', src)
        if refl:
            err(f"uMod bans Reflection — found {sorted(set(refl))} (set umod=false if this is a marketplace plugin)")

    # --- config sample -------------------------------------------------
    cfgfiles = glob.glob(os.path.join(repo, "oxide/config/*.json"))
    if cfg["config_required"] and not cfgfiles:
        err(f"missing sample config oxide/config/{plugin}.json")
    if glob.glob(os.path.join(repo, "oxide/data/*.json")):
        warn("config/state committed under oxide/data — uMod convention is oxide/config")

    # --- translations --------------------------------------------------
    lang_root = os.path.join(repo, "oxide/lang")
    have = sorted(d for d in os.listdir(lang_root)) if os.path.isdir(lang_root) else []
    want = cfg["locales"]
    for loc in want:
        if loc not in have:
            err(f"missing locale: oxide/lang/{loc}")
    for loc in have:
        if loc not in want:
            warn(f"unexpected locale present: {loc} (not in standard set)")
    en_path = os.path.join(lang_root, "en", f"{plugin}.json")
    if not os.path.exists(en_path):
        err(f"missing oxide/lang/en/{plugin}.json (canonical key set)")
    else:
        try:
            en = load_json(en_path)
        except Exception as e:
            err(f"en lang file invalid JSON: {e}"); en = None
        if en is not None:
            enk = set(en)
            for loc in want:
                p = os.path.join(lang_root, loc, f"{plugin}.json")
                if not os.path.exists(p):
                    continue
                try:
                    d = load_json(p)
                except Exception as e:
                    err(f"{loc} lang file invalid JSON: {e}"); continue
                miss, extra = enk - set(d), set(d) - enk
                if miss: err(f"locale {loc}: missing {len(miss)} keys e.g. {sorted(miss)[:4]}")
                if extra: err(f"locale {loc}: orphan keys {sorted(extra)[:6]}")
                bad = [k for k in (enk & set(d)) if placeholders(str(d[k])) != placeholders(str(en[k]))]
                if bad: err(f"locale {loc}: placeholder mismatch in {bad[:4]}")

    return finish()


def finish():
    for w in warnings: print(f"  WARN  {w}")
    for e in errors: print(f"  FAIL  {e}")
    if errors:
        print(f"\nNON-CONFORMANT — {len(errors)} error(s), {len(warnings)} warning(s)")
        return 1
    print(f"\nCONFORMANT — 0 errors, {len(warnings)} warning(s)")
    return 0


if __name__ == "__main__":
    repo = sys.argv[1] if len(sys.argv) > 1 else "."
    plugin_files = glob.glob(os.path.join(repo, "oxide/plugins/*.cs"))
    name = os.path.basename(os.path.abspath(repo))
    print(f"== DunganSoft Plugin Standard check: {name} ==")
    sys.exit(main(repo))
