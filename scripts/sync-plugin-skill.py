#!/usr/bin/env python3
"""Generate the plugin skill from SkillTemplates~ and keep plugin versions aligned.

SkillTemplates~ is the user-facing source (it still contains {{PACKAGE_VERSION}},
which the Unity CLI Manager replaces per project). The plugin skill is shared
across projects, so this script stamps the package version and replaces the
"reinstall from CLI Manager" sentence with a version-mismatch check.

Symlinks are not used. Codex rejects a symlinked root plugin.json, and a
Windows checkout does not reliably materialize git symlinks. The plugin tree
is a generated copy. Do not edit it by hand.

  scripts/sync-plugin-skill.sh --write
  scripts/sync-plugin-skill.sh --check
"""

from __future__ import annotations

import argparse
import difflib
import json
import shutil
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
PACKAGE_JSON = REPO_ROOT / "unity-package" / "com.yhc509.unity-cli-bridge" / "package.json"
TEMPLATE_ROOT = REPO_ROOT / "unity-package" / "com.yhc509.unity-cli-bridge" / "SkillTemplates~"
PLUGIN_SKILL_ROOT = REPO_ROOT / "plugins" / "unity-cli-bridge" / "skills" / "unity-cli-bridge"
VERSIONED_MANIFESTS = (
    REPO_ROOT / "plugins" / "unity-cli-bridge" / "plugin.json",
    REPO_ROOT / "plugins" / "unity-cli-bridge" / ".claude-plugin" / "plugin.json",
)

VERSION_MARKER = "<!-- unity-cli-bridge-package-version: {{PACKAGE_VERSION}} -->"
INSTALLED_SENTENCE = (
    "This skill was installed with Unity package `com.yhc509.unity-cli-bridge` "
    "v{{PACKAGE_VERSION}}. After a package update, reinstall the skill from "
    "Unity's `Window > Unity CLI Manager`."
)


def package_version() -> str:
    data = json.loads(PACKAGE_JSON.read_text(encoding="utf-8"))
    version = str(data.get("version", "")).strip()
    if not version:
        raise SystemExit(f"{PACKAGE_JSON}: missing version")
    return version


def plugin_paragraph(version: str) -> str:
    return (
        f"This copy is the shared plugin skill for Unity package "
        f"`com.yhc509.unity-cli-bridge` {version}. It is one skill for every "
        f"project, so the open project may be on a different package version. "
        f"Warn the user when the versions differ, then keep going.\n\n"
        f"`unity-cli status` does not include the package version. Use it to "
        f"confirm the live editor (`projectName`, `projectRoot`, `unityVersion`, "
        f"and the play/compile flags). Read `version` from the resolved package "
        f"file: `Packages/com.yhc509.unity-cli-bridge/package.json` when the "
        f"package is embedded, otherwise "
        f"`Library/PackageCache/com.yhc509.unity-cli-bridge@*/package.json`. "
        f"Ignore `Packages/packages-lock.json` for this check when the dependency "
        f"is `git` or `file:` — that `version` is a URL or path.\n\n"
        f"`UNKNOWN_COMMAND`, or a `PROTOCOL_MISMATCH` the CLI could not re-exec, "
        f"means this project's package and this skill do not describe the same "
        f"commands. Ask the user to install the matching CLI from that project's "
        f"`Window > Unity CLI Manager`, and to refresh this plugin "
        f"(`/plugin marketplace update unity-cli-bridge`, "
        f"`codex plugin marketplace upgrade unity-cli-bridge`, or "
        f"`grok plugin marketplace update unity-cli-bridge`). Loaded from the "
        f"plugin, the slash command is `/unity-cli-bridge:unity-cli-bridge` "
        f"(bare `/unity-cli-bridge` when the name is free)."
    )


def render_skill_markdown(template_text: str, version: str) -> str:
    if template_text.count(INSTALLED_SENTENCE) != 1:
        raise SystemExit(
            "SkillTemplates~/SKILL.md no longer contains the install sentence "
            "this sync replaces. Update INSTALLED_SENTENCE in "
            "scripts/sync-plugin-skill.py in the same change."
        )
    if template_text.count(VERSION_MARKER) != 1:
        raise SystemExit(
            "SkillTemplates~/SKILL.md no longer contains the package-version marker."
        )
    text = template_text.replace(INSTALLED_SENTENCE, plugin_paragraph(version), 1)
    stamped = (
        f"<!-- unity-cli-bridge-package-version: {version} -->\n"
        "<!-- unity-cli-bridge-skill-channel: plugin -->"
    )
    text = text.replace(VERSION_MARKER, stamped, 1)
    if "{{PACKAGE_VERSION}}" in text:
        raise SystemExit("plugin SKILL.md still contains {{PACKAGE_VERSION}}")
    return text


def iter_template_files(root: Path):
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        if path.name.endswith(".meta") or path.name == ".DS_Store":
            continue
        yield path


def build_skill_tree(destination: Path, version: str) -> None:
    if destination.exists():
        shutil.rmtree(destination)
    destination.mkdir(parents=True)
    for source in iter_template_files(TEMPLATE_ROOT):
        relative = source.relative_to(TEMPLATE_ROOT)
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        if source.name == "SKILL.md":
            rendered = render_skill_markdown(source.read_text(encoding="utf-8"), version)
            target.write_text(rendered, encoding="utf-8", newline="\n")
        else:
            target.write_bytes(source.read_bytes())


def manifest_version(path: Path) -> str:
    if not path.is_file():
        raise SystemExit(f"missing plugin manifest: {path}")
    data = json.loads(path.read_text(encoding="utf-8"))
    version = str(data.get("version", "")).strip()
    if not version:
        raise SystemExit(f"{path}: missing version")
    return version


def write_manifest_version(path: Path, version: str) -> None:
    text = path.read_text(encoding="utf-8")
    data = json.loads(text)
    current = str(data.get("version", ""))
    if current == version:
        return
    needle = '"version": "' + current + '"'
    replacement = '"version": "' + version + '"'
    if needle not in text or text.count(needle) != 1:
        raise SystemExit(f"{path}: could not replace the version field in place")
    path.write_text(text.replace(needle, replacement, 1), encoding="utf-8", newline="\n")


def relative_files(root: Path) -> dict[str, bytes]:
    files: dict[str, bytes] = {}
    if not root.is_dir():
        return files
    for path in sorted(root.rglob("*")):
        if path.is_file():
            files[path.relative_to(root).as_posix()] = path.read_bytes()
    return files


def canonical_bytes(data: bytes) -> bytes:
    # Windows checkouts rewrite LF as CRLF. Compare the text, not the checkout ending.
    return data.replace(b"\r\n", b"\n")


def report_tree_diff(expected: dict[str, bytes], actual: dict[str, bytes]) -> str:
    lines: list[str] = []
    expected_names = set(expected)
    actual_names = set(actual)
    for name in sorted(expected_names - actual_names):
        lines.append(f"missing {name}")
    for name in sorted(actual_names - expected_names):
        lines.append(f"unexpected {name}")
    for name in sorted(expected_names & actual_names):
        if canonical_bytes(expected[name]) == canonical_bytes(actual[name]):
            continue
        lines.append(f"differs {name}")
        if name.endswith((".md", ".yaml", ".yml", ".json")):
            diff = difflib.unified_diff(
                actual[name].decode("utf-8", errors="replace").splitlines(),
                expected[name].decode("utf-8", errors="replace").splitlines(),
                fromfile=f"plugins/{name}",
                tofile=f"generated/{name}",
                lineterm="",
            )
            preview = list(diff)
            lines.extend(preview[:200])
            if len(preview) > 200:
                lines.append("... diff truncated ...")
    return "\n".join(lines)


def check(version: str) -> int:
    errors: list[str] = []
    for manifest in VERSIONED_MANIFESTS:
        try:
            found = manifest_version(manifest)
        except SystemExit as error:
            errors.append(str(error))
            continue
        if found != version:
            errors.append(
                f"{manifest.relative_to(REPO_ROOT)} version is {found}, "
                f"package.json is {version}"
            )

    with tempfile.TemporaryDirectory(prefix="unity-cli-plugin-skill-") as temp:
        expected_root = Path(temp) / "skill"
        build_skill_tree(expected_root, version)
        diff = report_tree_diff(relative_files(expected_root), relative_files(PLUGIN_SKILL_ROOT))
        if diff:
            errors.append(diff)

    if errors:
        print("plugin skill is out of sync with SkillTemplates~ and package.json:", file=sys.stderr)
        print("\n".join(errors), file=sys.stderr)
        print("Run scripts/sync-plugin-skill.sh --write and commit the result.", file=sys.stderr)
        return 1

    print(f"plugin skill is in sync at package version {version}")
    return 0


def write(version: str) -> int:
    for manifest in VERSIONED_MANIFESTS:
        write_manifest_version(manifest, version)
    build_skill_tree(PLUGIN_SKILL_ROOT, version)
    print(f"wrote plugin skill and manifest versions for {version}")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true", help="fail if the plugin copy or versions drift")
    mode.add_argument("--write", action="store_true", help="regenerate the plugin skill and stamp versions")
    args = parser.parse_args(argv)
    version = package_version()
    if args.write:
        return write(version)
    return check(version)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
