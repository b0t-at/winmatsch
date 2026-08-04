#!/usr/bin/env python3
"""Reconcile the download-site manifest and latest mirror with GitHub."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import sys
from pathlib import Path

SCHEMA_VERSION = 1
PROJECT = "winmatsch"

_SEMVER_RE = re.compile(
    r"^(?P<major>0|[1-9]\d*)\.(?P<minor>0|[1-9]\d*)\.(?P<patch>0|[1-9]\d*)"
    r"(?:-(?P<pre>[0-9A-Za-z.-]+))?$"
)


def normalize_version(value: str) -> str:
    normalized = value.strip()
    return normalized[1:] if normalized.startswith(("v", "V")) else normalized


def parse_semver(value: str) -> tuple:
    version = normalize_version(value)
    match = _SEMVER_RE.fullmatch(version)
    if not match:
        raise ValueError(f"not a release semantic version: {value!r}")

    core = tuple(int(match.group(group)) for group in ("major", "minor", "patch"))
    prerelease = match.group("pre")
    if prerelease is None:
        return (*core, 1, ())

    identifiers = []
    for identifier in prerelease.split("."):
        if not identifier or (identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0")):
            raise ValueError(f"not a release semantic version: {value!r}")
        if identifier.isdigit():
            identifiers.append((0, int(identifier), ""))
        else:
            identifiers.append((1, 0, identifier))
    return (*core, 0, tuple(identifiers))


def canonical_tag(value: str) -> str:
    version = normalize_version(value)
    parse_semver(version)
    return f"v{version}"


def read_tags(path: Path) -> list[str]:
    tags = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        value = line.strip()
        if not value:
            continue
        try:
            tag = canonical_tag(value)
        except ValueError:
            continue
        if value == tag:
            tags.add(tag)
    return sorted(tags, key=parse_semver, reverse=True)


def load_manifest(path: Path) -> dict:
    with path.open(encoding="utf-8") as handle:
        manifest = json.load(handle)
    if manifest.get("schemaVersion") != SCHEMA_VERSION:
        raise ValueError(f"{path}: unsupported schemaVersion")
    if manifest.get("project") != PROJECT:
        raise ValueError(f"{path}: unexpected project")
    if not isinstance(manifest.get("versions"), list):
        raise ValueError(f"{path}: 'versions' must be a list")

    seen = set()
    for entry in manifest["versions"]:
        if not isinstance(entry, dict):
            raise ValueError(f"{path}: every version entry must be an object")
        version = normalize_version(entry.get("version", ""))
        parse_semver(version)
        if version in seen:
            raise ValueError(f"{path}: duplicate version {version!r}")
        seen.add(version)
    return manifest


def load_releases(path: Path) -> dict[str, dict]:
    with path.open(encoding="utf-8") as handle:
        release_list = json.load(handle)
    if not isinstance(release_list, list):
        raise ValueError(f"{path}: GitHub releases must be a JSON array")

    releases = {}
    for release in release_list:
        if not isinstance(release, dict):
            raise ValueError(f"{path}: every GitHub release must be an object")
        tag = canonical_tag(str(release.get("tagName", "")))
        if release.get("tagName") != tag:
            raise ValueError(f"{path}: release tag must use canonical form {tag!r}")
        if tag in releases:
            raise ValueError(f"{path}: duplicate GitHub release tag {tag!r}")
        if not isinstance(release.get("isPrerelease"), bool):
            raise ValueError(f"{path}: release {tag!r} has no Boolean isPrerelease")
        if not release.get("publishedAt"):
            raise ValueError(f"{path}: release {tag!r} has no publication timestamp")
        releases[tag] = release
    return releases


def find_missing_release_tags(
    manifest_path: Path, releases_path: Path, storage_tags_path: Path
) -> list[str]:
    manifest = load_manifest(manifest_path)
    releases = load_releases(releases_path)
    catalog_tags = {canonical_tag(entry["version"]) for entry in manifest["versions"]}
    storage_tags = set(read_tags(storage_tags_path))
    deployed_tags = catalog_tags & storage_tags
    return sorted(set(releases) - deployed_tags, key=parse_semver, reverse=True)


def print_missing_releases(args: argparse.Namespace) -> None:
    for tag in find_missing_release_tags(args.manifest_in, args.github_releases, args.storage_tags):
        print(tag)


def reconcile_manifest(args: argparse.Namespace) -> None:
    manifest = load_manifest(args.manifest_in)
    releases = load_releases(args.github_releases)
    github_tags = set(releases)
    storage_tags = set(read_tags(args.storage_tags))
    manifest_entries = {canonical_tag(entry["version"]): entry for entry in manifest["versions"]}

    missing_catalog_tags = sorted(github_tags - set(manifest_entries), key=parse_semver, reverse=True)
    missing_storage_tags = sorted(github_tags - storage_tags, key=parse_semver, reverse=True)
    if missing_catalog_tags:
        raise ValueError(
            "published GitHub releases are missing from versions.json: " + ", ".join(missing_catalog_tags)
        )
    if missing_storage_tags:
        raise ValueError(
            "published GitHub releases are missing from live Azure storage: " + ", ".join(missing_storage_tags)
        )

    latest_tag = canonical_tag(args.github_latest_tag) if args.github_latest_tag else None
    if latest_tag and latest_tag not in releases:
        raise ValueError(f"GitHub latest release {latest_tag!r} is not in the published release list")
    if latest_tag and releases[latest_tag]["isPrerelease"]:
        raise ValueError(f"GitHub latest release {latest_tag!r} is marked as a prerelease")

    previous_latest = manifest.get("latest")
    latest = normalize_version(latest_tag) if latest_tag else None
    remaining = []
    for tag in github_tags:
        entry = dict(manifest_entries[tag])
        release = releases[tag]
        entry.update(
            {
                "version": normalize_version(tag),
                "tag": tag,
                "date": release["publishedAt"],
                "prerelease": release["isPrerelease"],
                "notes": release.get("url"),
            }
        )
        remaining.append(entry)
    remaining.sort(key=lambda entry: parse_semver(entry["version"]), reverse=True)

    archive_tags = sorted(storage_tags - github_tags, key=parse_semver, reverse=True)
    removed_catalog_tags = sorted(set(manifest_entries) - github_tags, key=parse_semver, reverse=True)
    changed = manifest["versions"] != remaining or previous_latest != latest
    if changed:
        previous_updated = manifest.get("updated")
        manifest["updated"] = max(value for value in (previous_updated, args.updated) if value)
        manifest["latest"] = latest
        manifest["versions"] = remaining

    args.manifest_out.parent.mkdir(parents=True, exist_ok=True)
    with args.manifest_out.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(manifest, handle, indent=2, ensure_ascii=False)
        handle.write("\n")

    state = {
        "manifestChanged": changed,
        "previousLatest": previous_latest,
        "latest": latest,
        "latestChanged": previous_latest != latest,
        "githubTags": sorted(github_tags, key=parse_semver, reverse=True),
        "archiveTags": archive_tags,
        "removedCatalogTags": removed_catalog_tags,
    }
    args.state_out.parent.mkdir(parents=True, exist_ok=True)
    with args.state_out.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(state, handle, indent=2, ensure_ascii=False)
        handle.write("\n")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def stage_latest(args: argparse.Namespace) -> None:
    manifest = load_manifest(args.manifest)
    latest = manifest.get("latest")
    if not latest:
        raise ValueError("manifest has no stable latest release")
    entry = next(
        (candidate for candidate in manifest["versions"] if normalize_version(candidate["version"]) == latest),
        None,
    )
    if entry is None or entry.get("prerelease"):
        raise ValueError("manifest latest does not identify a stable release")

    if args.out.exists() and any(args.out.iterdir()):
        raise ValueError(f"latest staging directory must be empty: {args.out}")
    args.out.mkdir(parents=True, exist_ok=True)

    checksum_lines = []
    stable_artifacts = []
    for artifact in entry.get("artifacts", []):
        source = args.version_dir / artifact["name"]
        if not source.is_file():
            raise ValueError(f"missing latest source artifact: {source}")
        digest = sha256(source)
        if digest.lower() != artifact["sha256"].lower():
            raise ValueError(f"latest source artifact digest mismatch: {source}")
        extension = ".exe" if artifact["os"] == "windows" else ""
        stable_name = f"winmatsch-{artifact['rid']}{extension}"
        shutil.copy2(source, args.out / stable_name)
        checksum_lines.append(f"{digest}  {stable_name}")
        stable_artifacts.append({**artifact, "name": stable_name, "url": f"/latest/{stable_name}"})

    for name in ("LICENSE", "THIRD-PARTY-NOTICES.txt", "index.html"):
        source = args.version_dir / name
        if not source.is_file():
            raise ValueError(f"missing latest source file: {source}")
        shutil.copy2(source, args.out / name)

    (args.out / "SHA256SUMS.txt").write_text(
        "\n".join(checksum_lines) + "\n", encoding="utf-8", newline="\n"
    )
    (args.out / "version.txt").write_text(f"{latest}\n", encoding="utf-8", newline="\n")
    latest_json = {
        "version": entry["version"],
        "tag": entry["tag"],
        "date": entry["date"],
        "path": entry["path"],
        "notes": entry.get("notes"),
        "artifacts": stable_artifacts,
    }
    with (args.out / "latest.json").open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(latest_json, handle, indent=2, ensure_ascii=False)
        handle.write("\n")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    reconcile = subparsers.add_parser(
        "reconcile-manifest", help="make versions.json match published GitHub releases"
    )
    reconcile.add_argument("--manifest-in", required=True, type=Path)
    reconcile.add_argument("--github-releases", required=True, type=Path)
    reconcile.add_argument("--github-latest-tag", default="")
    reconcile.add_argument("--storage-tags", required=True, type=Path)
    reconcile.add_argument("--updated", required=True)
    reconcile.add_argument("--manifest-out", required=True, type=Path)
    reconcile.add_argument("--state-out", required=True, type=Path)
    reconcile.set_defaults(handler=reconcile_manifest)

    missing = subparsers.add_parser(
        "missing-releases", help="list published GitHub releases not fully deployed to Azure"
    )
    missing.add_argument("--manifest-in", required=True, type=Path)
    missing.add_argument("--github-releases", required=True, type=Path)
    missing.add_argument("--storage-tags", required=True, type=Path)
    missing.set_defaults(handler=print_missing_releases)

    latest = subparsers.add_parser("stage-latest", help="stage the stable latest mirror")
    latest.add_argument("--manifest", required=True, type=Path)
    latest.add_argument("--version-dir", required=True, type=Path)
    latest.add_argument("--out", required=True, type=Path)
    latest.set_defaults(handler=stage_latest)

    return parser


def main(argv: list[str]) -> int:
    args = build_parser().parse_args(argv)
    args.handler(args)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        sys.exit(1)