#!/usr/bin/env python3
"""Upsert a release into the download site's versions.json manifest.

Reads an existing manifest (or starts fresh), inserts/replaces the entry for
one version, sorts versions descending by semver, recomputes the ``latest``
pointer (highest stable version), and writes the result.

The artifact list is read as JSON from --artifacts-file, produced by
scripts/publish-download-site.sh. Prints the computed latest version to
stdout so the caller can decide whether to refresh the /latest/ mirror.

Usage:
  update-versions-manifest.py --version 0.9.0 --date 2026-08-03T12:00:00Z \
      --artifacts-file artifacts.json [--in versions.json] --out versions.json \
      [--prerelease] [--notes-url URL]
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

SCHEMA_VERSION = 1
PROJECT = "winmatsch"

_SEMVER_RE = re.compile(
    r"^(?P<major>\d+)\.(?P<minor>\d+)\.(?P<patch>\d+)"
    r"(?:-(?P<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$"
)


def parse_semver(version: str):
    """Return a sort key implementing semver 2.0 precedence."""
    match = _SEMVER_RE.match(version.strip())
    if not match:
        raise ValueError(f"not a semantic version: {version!r}")
    core = tuple(int(match.group(g)) for g in ("major", "minor", "patch"))
    pre = match.group("pre")
    if pre is None:
        # A release compares greater than any of its own pre-releases.
        return (*core, 1, ())
    identifiers = []
    for ident in pre.split("."):
        if ident.isdigit():
            identifiers.append((0, int(ident), ""))
        else:
            identifiers.append((1, 0, ident))
    return (*core, 0, tuple(identifiers))


def normalize_version(version: str) -> str:
    return version.strip().lstrip("vV")


def is_prerelease(version: str) -> bool:
    match = _SEMVER_RE.match(version)
    return bool(match and match.group("pre"))


def load_manifest(path: Path | None) -> dict:
    if path is None or not path.exists():
        return {
            "schemaVersion": SCHEMA_VERSION,
            "project": PROJECT,
            "updated": None,
            "latest": None,
            "versions": [],
        }
    with path.open(encoding="utf-8") as handle:
        manifest = json.load(handle)
    if not isinstance(manifest.get("versions"), list):
        raise ValueError(f"{path}: 'versions' must be a list")
    return manifest


def build_entry(args, artifacts: list[dict]) -> dict:
    version = normalize_version(args.version)
    return {
        "version": version,
        "tag": f"v{version}",
        "date": args.date,
        "prerelease": args.prerelease or is_prerelease(version),
        "path": f"/v{version}/",
        "sha256sums": f"/v{version}/SHA256SUMS.txt",
        "notes": args.notes_url or None,
        "artifacts": artifacts,
    }


def upsert(manifest: dict, entry: dict, updated: str) -> dict:
    versions = [
        existing
        for existing in manifest["versions"]
        if normalize_version(existing.get("version", "")) != entry["version"]
    ]
    versions.append(entry)
    versions.sort(key=lambda v: parse_semver(normalize_version(v["version"])), reverse=True)
    stable = [v for v in versions if not v.get("prerelease")]
    manifest.update(
        {
            "schemaVersion": SCHEMA_VERSION,
            "project": manifest.get("project") or PROJECT,
            "updated": updated,
            "latest": stable[0]["version"] if stable else None,
            "versions": versions,
        }
    )
    return manifest


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="release version (with or without leading v)")
    parser.add_argument("--date", required=True, help="release date, ISO 8601 UTC")
    parser.add_argument("--artifacts-file", required=True, type=Path, help="JSON array of artifact objects")
    parser.add_argument("--in", dest="manifest_in", type=Path, default=None, help="existing versions.json")
    parser.add_argument("--out", dest="manifest_out", required=True, type=Path, help="output path")
    parser.add_argument("--prerelease", action="store_true", help="force pre-release flag")
    parser.add_argument("--notes-url", default=None, help="release notes URL")
    args = parser.parse_args(argv)

    version = normalize_version(args.version)
    parse_semver(version)  # validate early, raises on garbage

    with args.artifacts_file.open(encoding="utf-8") as handle:
        artifacts = json.load(handle)
    if not isinstance(artifacts, list) or not artifacts:
        raise ValueError("artifacts file must contain a non-empty JSON array")

    manifest = load_manifest(args.manifest_in)
    manifest = upsert(manifest, build_entry(args, artifacts), updated=args.date)

    args.manifest_out.parent.mkdir(parents=True, exist_ok=True)
    with args.manifest_out.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(manifest, handle, indent=2, ensure_ascii=False)
        handle.write("\n")

    print(manifest["latest"] or "")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
