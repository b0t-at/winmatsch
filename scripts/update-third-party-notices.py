#!/usr/bin/env python3
"""Refresh package versions in THIRD-PARTY-NOTICES.txt.

Attribution entries (copyright, source URL, license) stay hand-maintained because they
require human license review; only the version tokens are mechanical. This script rewrites
them from two sources of truth:

  * Directory.Packages.props - the central version pins.
  * src/WinMatsch.Cli/obj/project.assets.json (when present, i.e. after a restore) - the
    shipped dependency closure, so transitive packages such as Spectre.Console.Ansi are
    refreshed too.

A pinned shipped package with no attribution entry is an error: adding a package requires
a human to review its license and write the attribution, which no generator can do.

Usage:
    python3 scripts/update-third-party-notices.py           # rewrite the notice in place
    python3 scripts/update-third-party-notices.py --check   # exit 1 when the notice is stale
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

NOTICE_FILE = "THIRD-PARTY-NOTICES.txt"
PINS_FILE = "Directory.Packages.props"
ASSETS_FILE = Path("src") / "WinMatsch.Cli" / "obj" / "project.assets.json"

# Mirrors LicenseNoticeTests: packages that never ship to users.
TEST_ONLY_PACKAGES = {
    "Microsoft.NET.Test.Sdk",
    "xunit.v3",
    "xunit.runner.visualstudio",
}

# Mirrors LicenseNoticeTests: build-time-only packages in the CLI closure.
BUILD_ONLY_PACKAGES = {
    "Microsoft.NET.ILLink.Tasks",
}

# Mirrors LicenseNoticeTests: .NET platform packs covered by the .NET license.
PLATFORM_PACKAGE_PREFIXES = (
    "Microsoft.NETCore.App.",
    "Microsoft.AspNetCore.App.",
    "Microsoft.WindowsDesktop.App.",
    "Microsoft.DotNet.ILCompiler",
)

# Mirrors LicenseNoticeTests: asset sections that mean a package ships files.
SHIPPED_ASSET_SECTIONS = ("runtime", "runtimeTargets", "native")

_PAIR_RE = re.compile(
    r"^(?P<id>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)\s+"
    r"(?P<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?)$"
)
_SEPARATOR_RE = re.compile(r"(, and | and |, )")


def read_pinned_packages(root: Path) -> dict[str, str]:
    versions: dict[str, str] = {}
    for element in ElementTree.parse(root / PINS_FILE).getroot().iter("PackageVersion"):
        package_id = element.attrib.get("Include")
        version = element.attrib.get("Version")
        if package_id and version and package_id not in TEST_ONLY_PACKAGES:
            versions[package_id] = version
    if not versions:
        raise SystemExit(f"error: no <PackageVersion> pins found in {PINS_FILE}")
    return versions


def read_shipped_closure(root: Path) -> dict[str, str]:
    assets_path = root / ASSETS_FILE
    if not assets_path.is_file():
        return {}

    versions: dict[str, str] = {}
    document = json.loads(assets_path.read_text(encoding="utf-8"))
    for target in document.get("targets", {}).values():
        for name, library in target.items():
            if library.get("type") != "package":
                continue
            if not any(section in library for section in SHIPPED_ASSET_SECTIONS):
                continue
            package_id, _, version = name.partition("/")
            if not version or package_id in BUILD_ONLY_PACKAGES:
                continue
            if package_id.startswith(PLATFORM_PACKAGE_PREFIXES):
                continue
            versions[package_id] = version
    return versions


def is_attribution_line(line: str) -> bool:
    parts = [part for part in _SEPARATOR_RE.split(line.strip()) if not _SEPARATOR_RE.fullmatch(part)]
    return bool(parts) and all(_PAIR_RE.fullmatch(part) for part in parts)


def rewrite_line(line: str, versions: dict[str, str], refreshed: set[str]) -> str:
    if not is_attribution_line(line):
        return line

    def replace(match: re.Match[str]) -> str:
        package_id = match.group("id")
        new_version = versions.get(package_id)
        if new_version is None:
            return match.group(0)
        if new_version != match.group("version"):
            refreshed.add(f"{package_id} {match.group('version')} -> {new_version}")
        return f"{package_id} {new_version}"

    stripped = line.strip()
    pieces = _SEPARATOR_RE.split(stripped)
    rewritten = "".join(
        piece if _SEPARATOR_RE.fullmatch(piece) else _PAIR_RE.sub(replace, piece)
        for piece in pieces
    )
    return line.replace(stripped, rewritten)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="fail (exit 1) when the notice needs refreshing instead of rewriting it",
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parent.parent,
        help="repository root (defaults to the script's parent directory)",
    )
    arguments = parser.parse_args()
    root: Path = arguments.root

    versions = read_pinned_packages(root)
    versions.update(read_shipped_closure(root))

    notice_path = root / NOTICE_FILE
    original = notice_path.read_text(encoding="utf-8")
    refreshed: set[str] = set()
    lines = original.splitlines(keepends=True)
    rewritten = "".join(rewrite_line(line, versions, refreshed) for line in lines)

    attributed_ids = {
        match.group("id")
        for line in lines
        if is_attribution_line(line)
        for piece in _SEPARATOR_RE.split(line.strip())
        for match in [_PAIR_RE.fullmatch(piece)]
        if match is not None
    }
    missing = sorted(set(versions) - attributed_ids)
    if missing:
        for package_id in missing:
            print(
                f"error: {NOTICE_FILE} has no attribution entry for shipped package "
                f"'{package_id} {versions[package_id]}'. Review its license and add one.",
                file=sys.stderr,
            )
        return 1

    if rewritten == original:
        print(f"{NOTICE_FILE} is up to date.")
        return 0

    if arguments.check:
        for entry in sorted(refreshed):
            print(f"stale: {entry}", file=sys.stderr)
        print(
            f"error: {NOTICE_FILE} is stale. Run 'python3 scripts/update-third-party-notices.py' "
            "and commit the result.",
            file=sys.stderr,
        )
        return 1

    notice_path.write_text(rewritten, encoding="utf-8")
    for entry in sorted(refreshed):
        print(f"refreshed: {entry}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
