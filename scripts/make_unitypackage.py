#!/usr/bin/env python3
"""Build a Unity .unitypackage from a UPM folder without the Unity Editor.

A .unitypackage is a gzip-compressed tar. Each imported file or folder is one
GUID directory:

  <guid>/pathname     project path, UTF-8, with a trailing newline
  <guid>/asset.meta   the .meta file from the package, unchanged
  <guid>/asset        file bytes (omitted for folders)

Paths are ``Packages/<name>/...``. ``Tests~`` and any other ``~``-suffixed or
hidden folder is left out, matching what Unity imports from a UPM package.
GUIDs come from the existing ``.meta`` files; this script does not mint them.

The tar is ustar. Python's default pax format adds extended headers that
Unity's importer does not read.
"""

from __future__ import annotations

import argparse
import gzip
import io
import json
import os
import re
import sys
import tarfile
import tempfile
from dataclasses import dataclass
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_PACKAGE = REPO_ROOT / "unity-package" / "com.yhc509.unity-cli-bridge"
GUID_RE = re.compile(rb"(?m)^guid:[ \t]+([0-9a-f]{32})[ \t]*$")
MEMBER_RE = re.compile(r"^([0-9a-f]{32})/(pathname|asset|asset\.meta)$")


@dataclass(frozen=True)
class AssetEntry:
    guid: str
    relative_path: str
    project_path: str
    is_dir: bool
    meta: bytes
    body: bytes | None


def _excluded_dir(name: str) -> bool:
    return name.startswith(".") or name.endswith("~")


def _read_guid(meta_path: Path, meta: bytes) -> str:
    found = GUID_RE.findall(meta)
    if len(found) != 1:
        raise SystemExit(f"{meta_path}: expected one lowercase guid, found {len(found)}")
    return found[0].decode("ascii")


def _folder_flag_ok(meta: bytes, is_dir: bool) -> bool:
    has_folder = b"folderAsset: yes" in meta
    return has_folder if is_dir else not has_folder


def collect(package_root: Path, project_prefix: str) -> list[AssetEntry]:
    if not package_root.is_dir():
        raise SystemExit(f"package folder not found: {package_root}")

    entries: list[AssetEntry] = []
    seen_guids: dict[str, str] = {}
    seen_paths: set[str] = set()

    for dirpath, dirs, files in os.walk(package_root):
        root = Path(dirpath)
        dirs[:] = sorted(name for name in dirs if not _excluded_dir(name))
        for name in sorted(dirs + files):
            if name.endswith(".meta") or name.startswith(".") or name.endswith("~"):
                continue
            path = root / name
            if path.is_symlink():
                raise SystemExit(f"refusing to pack a symlink: {path}")
            meta_path = Path(str(path) + ".meta")
            if not meta_path.is_file():
                raise SystemExit(f"missing .meta for {path}")
            meta = meta_path.read_bytes()
            guid = _read_guid(meta_path, meta)
            is_dir = path.is_dir()
            if not _folder_flag_ok(meta, is_dir):
                kind = "folder" if is_dir else "file"
                raise SystemExit(f"{meta_path}: folderAsset flag does not match a {kind}")
            relative = path.relative_to(package_root).as_posix()
            if relative in seen_paths:
                raise SystemExit(f"duplicate project path: {relative}")
            previous = seen_guids.get(guid)
            if previous is not None:
                raise SystemExit(f"duplicate guid {guid} on {relative} and {previous}")
            seen_guids[guid] = relative
            seen_paths.add(relative)
            project_path = f"{project_prefix}/{relative}"
            body = None if is_dir else path.read_bytes()
            entries.append(
                AssetEntry(
                    guid=guid,
                    relative_path=relative,
                    project_path=project_path,
                    is_dir=is_dir,
                    meta=meta,
                    body=body,
                )
            )

    if not entries:
        raise SystemExit(f"no assets found under {package_root}")
    entries.sort(key=lambda entry: entry.project_path)
    return entries


def _add_bytes(tar: tarfile.TarFile, name: str, data: bytes) -> None:
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = 0o644
    info.mtime = 0
    info.uid = 0
    info.gid = 0
    info.uname = ""
    info.gname = ""
    info.type = tarfile.REGTYPE
    tar.addfile(info, io.BytesIO(data))


def write_unitypackage(entries: list[AssetEntry], output: Path) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    with tarfile.open(output, "w:gz", format=tarfile.USTAR_FORMAT) as tar:
        for entry in entries:
            pathname = (entry.project_path + "\n").encode("utf-8")
            _add_bytes(tar, f"{entry.guid}/pathname", pathname)
            _add_bytes(tar, f"{entry.guid}/asset.meta", entry.meta)
            if entry.body is not None:
                _add_bytes(tar, f"{entry.guid}/asset", entry.body)


def _assert_ustar_gzip(archive: Path) -> None:
    """Unity's importer reads classic ustar. Pax extended headers break that."""
    with gzip.open(archive, "rb") as raw:
        while True:
            header = raw.read(512)
            if header == b"" or header == b"\0" * 512:
                return
            if len(header) != 512 or header[257:262] != b"ustar" or header[156:157] != b"0":
                raise SystemExit(
                    f"{archive}: gzip payload is not a ustar file of regular members"
                )
            size_field = header[124:136].split(b"\0", 1)[0].strip() or b"0"
            try:
                size = int(size_field, 8)
            except ValueError as error:
                raise SystemExit(f"{archive}: bad ustar size field") from error
            padding = (size + 511) // 512 * 512
            if len(raw.read(padding)) != padding:
                raise SystemExit(f"{archive}: truncated ustar member")


def _grouped_members(archive: Path) -> dict[str, dict[str, bytes]]:
    _assert_ustar_gzip(archive)
    grouped: dict[str, dict[str, bytes]] = {}
    with tarfile.open(archive, "r:gz") as tar:
        for member in tar.getmembers():
            if not member.isfile():
                raise SystemExit(f"{archive}: expected a regular file, found {member.name}")
            match = MEMBER_RE.match(member.name)
            if match is None:
                raise SystemExit(f"{archive}: unexpected member {member.name}")
            guid, kind = match.group(1), match.group(2)
            extracted = tar.extractfile(member)
            if extracted is None:
                raise SystemExit(f"{archive}: could not read {member.name}")
            payload = extracted.read()
            if len(payload) != member.size:
                raise SystemExit(f"{archive}: size mismatch for {member.name}")
            slot = grouped.setdefault(guid, {})
            if kind in slot:
                raise SystemExit(f"{archive}: duplicate {kind} for {guid}")
            slot[kind] = payload
    return grouped


def validate_archive(entries: list[AssetEntry], archive: Path, project_prefix: str) -> None:
    grouped = _grouped_members(archive)
    expected = {entry.guid: entry for entry in entries}
    if set(grouped) != set(expected):
        missing = sorted(set(expected) - set(grouped))
        extra = sorted(set(grouped) - set(expected))
        raise SystemExit(
            f"{archive}: guid set mismatch (missing {len(missing)}, extra {len(extra)})"
        )

    pathnames: set[str] = set()
    for guid, entry in expected.items():
        files = grouped[guid]
        if "pathname" not in files or "asset.meta" not in files:
            raise SystemExit(f"{archive}: {guid} is missing pathname or asset.meta")
        if entry.is_dir:
            if "asset" in files:
                raise SystemExit(f"{archive}: folder {entry.project_path} includes asset bytes")
        elif "asset" not in files:
            raise SystemExit(f"{archive}: file {entry.project_path} is missing asset bytes")
        if files["asset.meta"] != entry.meta:
            raise SystemExit(f"{archive}: asset.meta bytes differ for {entry.project_path}")
        if not entry.is_dir and files["asset"] != entry.body:
            raise SystemExit(f"{archive}: asset bytes differ for {entry.project_path}")
        raw_path = files["pathname"]
        if raw_path != (entry.project_path + "\n").encode("utf-8"):
            raise SystemExit(f"{archive}: pathname bytes differ for {entry.project_path}")
        text = raw_path.decode("utf-8").strip("\n")
        if text != entry.project_path or not text.startswith(project_prefix + "/"):
            raise SystemExit(f"{archive}: pathname {text!r} is outside {project_prefix}")
        if text in pathnames:
            raise SystemExit(f"{archive}: duplicate pathname {text}")
        pathnames.add(text)
        meta_guid = _read_guid(Path(entry.relative_path + ".meta"), files["asset.meta"])
        if meta_guid != guid:
            raise SystemExit(f"{archive}: meta guid {meta_guid} != directory {guid}")

    source_paths = {entry.project_path for entry in entries}
    if pathnames != source_paths:
        raise SystemExit(f"{archive}: pathnames do not match the package files")


def package_identity(package_root: Path) -> tuple[str, str]:
    manifest_path = package_root / "package.json"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise SystemExit(f"{manifest_path}: {error}") from error
    name = str(manifest.get("name", "")).strip()
    version = str(manifest.get("version", "")).strip()
    if not name or not version:
        raise SystemExit(f"{manifest_path}: name and version are required")
    if package_root.name != name:
        raise SystemExit(
            f"{manifest_path}: name {name} does not match folder {package_root.name}"
        )
    return name, version


def archived_package_version(entries: list[AssetEntry]) -> str:
    match = [entry for entry in entries if entry.relative_path == "package.json"]
    if len(match) != 1 or match[0].body is None:
        raise SystemExit("package.json was not packed")
    try:
        manifest = json.loads(match[0].body.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise SystemExit(f"packed package.json: {error}") from error
    version = str(manifest.get("version", "")).strip()
    if not version:
        raise SystemExit("packed package.json has no version")
    return version


def build(
    package_root: Path,
    output: Path,
    expect_version: str | None,
) -> list[AssetEntry]:
    name, version = package_identity(package_root)
    if expect_version is not None and version != expect_version:
        raise SystemExit(
            f"package.json version is {version}, expected {expect_version}"
        )
    prefix = f"Packages/{name}"
    entries = collect(package_root, prefix)
    packed_version = archived_package_version(entries)
    if packed_version != version:
        raise SystemExit(
            f"packed package.json version is {packed_version}, manifest is {version}"
        )
    write_unitypackage(entries, output)
    validate_archive(entries, output, prefix)
    return entries


def _print_summary(entries: list[AssetEntry], output: Path, version: str) -> None:
    folders = sum(1 for entry in entries if entry.is_dir)
    files = len(entries) - folders
    members = files * 3 + folders * 2
    print(
        f"unitypackage: {len(entries)} assets "
        f"({files} files, {folders} folders, {members} tar members), "
        f"version {version}"
    )
    print(f"output: {output}")
    print("sample pathnames:")
    for entry in entries[:8]:
        print(f"  {entry.project_path}")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--package",
        type=Path,
        default=DEFAULT_PACKAGE,
        help="UPM package folder (default: the com.yhc509.unity-cli-bridge package)",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="write the .unitypackage here (kept after validation)",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="build and validate; with no --output, use a temporary file and delete it",
    )
    parser.add_argument(
        "--expect-version",
        help="fail unless package.json version equals this value",
    )
    parser.add_argument(
        "--list",
        action="store_true",
        help="print every pathname after a successful build",
    )
    args = parser.parse_args(argv)
    if args.output is None and not args.check:
        parser.error("pass --output, --check, or both")

    package_root = args.package if args.package.is_absolute() else args.package.resolve()
    _, version = package_identity(package_root)

    if args.output is not None:
        output = args.output if args.output.is_absolute() else args.output.resolve()
        entries = build(package_root, output, args.expect_version)
        _print_summary(entries, output, version)
    else:
        with tempfile.TemporaryDirectory(prefix="unitypackage-") as temp:
            output = Path(temp) / "check.unitypackage"
            entries = build(package_root, output, args.expect_version)
            _print_summary(entries, output, version)
            print("check passed")

    if args.list:
        for entry in entries:
            print(entry.project_path)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except BrokenPipeError:
        raise SystemExit(0) from None
