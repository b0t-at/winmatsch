from __future__ import annotations

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace


SCRIPT_PATH = Path(__file__).parents[2] / "scripts" / "manage-release-versions.py"
SPEC = importlib.util.spec_from_file_location("manage_release_versions", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
MANAGER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MANAGER)


class ManageReleaseVersionsTests(unittest.TestCase):
    def test_reconcile_uses_github_latest_and_archives_azure_only_versions(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            manifest_in = root / "manifest-in.json"
            releases = root / "releases.json"
            storage_tags = root / "storage-tags.txt"
            manifest_out = root / "manifest-out.json"
            state_out = root / "state.json"
            self._write_json(
                manifest_in,
                self._manifest(["1.2.0", "1.1.0", "1.0.0"], latest="1.2.0"),
            )
            self._write_json(
                releases,
                [self._release("1.1.0"), self._release("1.0.0")],
            )
            storage_tags.write_text("v1.2.0\nv1.1.0\nv1.0.0\n", encoding="utf-8")

            MANAGER.reconcile_manifest(
                SimpleNamespace(
                    manifest_in=manifest_in,
                    github_releases=releases,
                    github_latest_tag="v1.0.0",
                    storage_tags=storage_tags,
                    updated="2026-08-03T20:00:00Z",
                    manifest_out=manifest_out,
                    state_out=state_out,
                )
            )

            updated = json.loads(manifest_out.read_text(encoding="utf-8"))
            state = json.loads(state_out.read_text(encoding="utf-8"))
            self.assertEqual("1.0.0", updated["latest"])
            self.assertEqual(["1.1.0", "1.0.0"], [entry["version"] for entry in updated["versions"]])
            self.assertEqual(["v1.2.0"], state["archiveTags"])
            self.assertEqual(["v1.2.0"], state["removedCatalogTags"])

    def test_reconcile_fails_when_a_github_release_is_not_deployed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            manifest_in = root / "manifest-in.json"
            releases = root / "releases.json"
            storage_tags = root / "storage-tags.txt"
            self._write_json(manifest_in, self._manifest(["1.0.0"], latest="1.0.0"))
            self._write_json(releases, [self._release("1.0.0"), self._release("1.1.0")])
            storage_tags.write_text("v1.0.0\n", encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "missing from versions.json"):
                MANAGER.reconcile_manifest(
                    SimpleNamespace(
                        manifest_in=manifest_in,
                        github_releases=releases,
                        github_latest_tag="v1.1.0",
                        storage_tags=storage_tags,
                        updated="2026-08-03T20:00:00Z",
                        manifest_out=root / "manifest-out.json",
                        state_out=root / "state.json",
                    )
                )

    def test_reconcile_archives_every_live_version_when_github_is_empty(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            manifest_in = root / "manifest-in.json"
            releases = root / "releases.json"
            storage_tags = root / "storage-tags.txt"
            manifest_out = root / "manifest-out.json"
            state_out = root / "state.json"
            self._write_json(manifest_in, self._manifest(["1.0.0"], latest="1.0.0"))
            self._write_json(releases, [])
            storage_tags.write_text("archive\nlatest\nv1.0.0\n", encoding="utf-8")

            MANAGER.reconcile_manifest(
                SimpleNamespace(
                    manifest_in=manifest_in,
                    github_releases=releases,
                    github_latest_tag="",
                    storage_tags=storage_tags,
                    updated="2026-08-03T20:00:00Z",
                    manifest_out=manifest_out,
                    state_out=state_out,
                )
            )

            updated = json.loads(manifest_out.read_text(encoding="utf-8"))
            state = json.loads(state_out.read_text(encoding="utf-8"))
            self.assertIsNone(updated["latest"])
            self.assertEqual([], updated["versions"])
            self.assertEqual(["v1.0.0"], state["archiveTags"])

    def test_stage_latest_verifies_and_renames_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            version_directory = root / "version"
            latest_directory = root / "latest"
            version_directory.mkdir()
            binary = version_directory / "winmatsch-v1.0.0-win-x64.exe"
            binary.write_bytes(b"release binary")
            for name in ("LICENSE", "THIRD-PARTY-NOTICES.txt", "index.html"):
                (version_directory / name).write_text(name, encoding="utf-8")

            manifest = self._manifest(["1.0.0"], latest="1.0.0")
            manifest["versions"][0]["artifacts"] = [
                {
                    "name": binary.name,
                    "rid": "win-x64",
                    "os": "windows",
                    "arch": "x64",
                    "url": f"/v1.0.0/{binary.name}",
                    "sizeBytes": binary.stat().st_size,
                    "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
                }
            ]
            manifest_path = root / "manifest.json"
            self._write_json(manifest_path, manifest)

            MANAGER.stage_latest(
                SimpleNamespace(
                    manifest=manifest_path,
                    version_dir=version_directory,
                    out=latest_directory,
                )
            )

            stable_binary = latest_directory / "winmatsch-win-x64.exe"
            latest_json = json.loads((latest_directory / "latest.json").read_text(encoding="utf-8"))
            self.assertEqual(binary.read_bytes(), stable_binary.read_bytes())
            self.assertEqual("/latest/winmatsch-win-x64.exe", latest_json["artifacts"][0]["url"])
            self.assertEqual("1.0.0\n", (latest_directory / "version.txt").read_text(encoding="utf-8"))

    @staticmethod
    def _release(version: str) -> dict:
        return {
            "tagName": f"v{version}",
            "isPrerelease": "-" in version,
            "publishedAt": "2026-08-03T12:00:00Z",
            "url": f"https://github.com/b0t-at/winmatsch/releases/tag/v{version}",
        }

    @staticmethod
    def _manifest(versions: list[str], latest: str | None) -> dict:
        return {
            "schemaVersion": 1,
            "project": "winmatsch",
            "updated": "2026-08-03T12:00:00Z",
            "latest": latest,
            "versions": [
                {
                    "version": version,
                    "tag": f"v{version}",
                    "date": "2026-08-03T12:00:00Z",
                    "prerelease": "-" in version,
                    "path": f"/v{version}/",
                    "sha256sums": f"/v{version}/SHA256SUMS.txt",
                    "notes": f"https://github.com/b0t-at/winmatsch/releases/tag/v{version}",
                    "artifacts": [],
                }
                for version in versions
            ],
        }

    @staticmethod
    def _write_json(path: Path, value: object) -> None:
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    unittest.main()