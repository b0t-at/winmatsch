"""Tests for scripts/update-third-party-notices.py."""

from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

_SCRIPT = Path(__file__).resolve().parent.parent.parent / "scripts" / "update-third-party-notices.py"
_SPEC = importlib.util.spec_from_file_location("update_third_party_notices", _SCRIPT)
notices = importlib.util.module_from_spec(_SPEC)
sys.modules[_SPEC.name] = notices
_SPEC.loader.exec_module(notices)

PINS = """<Project>
  <ItemGroup>
    <PackageVersion Include="YamlDotNet" Version="18.2.0" />
    <PackageVersion Include="SharpCompress" Version="0.50.4" />
    <PackageVersion Include="Spectre.Console" Version="0.57.2" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
  </ItemGroup>
</Project>
"""

NOTICE = """THIRD-PARTY SOFTWARE NOTICES AND INFORMATION

WinGet manifest schemas 1.12.0
Source: https://github.com/microsoft/winget-cli
License: MIT

YamlDotNet 18.1.0
Source: https://github.com/aaubry/YamlDotNet
License: MIT

SharpCompress 0.50.3
Source: https://github.com/adamhathcock/sharpcompress
License: MIT

Spectre.Console 0.57.2 and Spectre.Console.Ansi 0.57.2
Source: https://github.com/spectreconsole/spectre.console
License: MIT
"""

ASSETS = """{
  "targets": {
    "net10.0": {
      "Spectre.Console/0.57.2": {"type": "package", "runtime": {"lib/net9.0/Spectre.Console.dll": {}}},
      "Spectre.Console.Ansi/0.57.3": {"type": "package", "runtime": {"lib/net9.0/Spectre.Console.Ansi.dll": {}}},
      "Microsoft.NET.ILLink.Tasks/10.0.0": {"type": "package", "runtime": {"x": {}}},
      "Microsoft.NETCore.App.Runtime.win-x64/10.0.0": {"type": "package", "native": {"x": {}}},
      "SomeBuildOnly/1.0.0": {"type": "package"}
    }
  }
}
"""


class UpdateThirdPartyNoticesTests(unittest.TestCase):
    def _create_root(self, notice: str = NOTICE, with_assets: bool = True) -> Path:
        root = Path(tempfile.mkdtemp(prefix="winmatsch-notices-"))
        (root / "Directory.Packages.props").write_text(PINS, encoding="utf-8")
        (root / "THIRD-PARTY-NOTICES.txt").write_text(notice, encoding="utf-8")
        if with_assets:
            assets = root / "src" / "WinMatsch.Cli" / "obj"
            assets.mkdir(parents=True)
            (assets / "project.assets.json").write_text(ASSETS, encoding="utf-8")
        return root

    def _run(self, root: Path, check: bool = False) -> int:
        argv = ["update-third-party-notices.py", "--root", str(root)]
        if check:
            argv.append("--check")
        original = sys.argv
        sys.argv = argv
        try:
            return notices.main()
        finally:
            sys.argv = original

    def test_versions_are_refreshed_from_pins_and_closure(self) -> None:
        root = self._create_root()

        self.assertEqual(0, self._run(root))

        updated = (root / "THIRD-PARTY-NOTICES.txt").read_text(encoding="utf-8")
        self.assertIn("YamlDotNet 18.2.0", updated)
        self.assertIn("SharpCompress 0.50.4", updated)
        self.assertIn("Spectre.Console 0.57.2 and Spectre.Console.Ansi 0.57.3", updated)
        self.assertIn("WinGet manifest schemas 1.12.0", updated)
        self.assertNotIn("18.1.0", updated)

    def test_check_mode_fails_on_stale_notice_without_writing(self) -> None:
        root = self._create_root()

        self.assertEqual(1, self._run(root, check=True))
        self.assertEqual(NOTICE, (root / "THIRD-PARTY-NOTICES.txt").read_text(encoding="utf-8"))

    def test_check_mode_passes_when_notice_is_fresh(self) -> None:
        root = self._create_root()
        self.assertEqual(0, self._run(root))

        self.assertEqual(0, self._run(root, check=True))

    def test_missing_attribution_for_shipped_package_is_an_error(self) -> None:
        notice = NOTICE.replace(
            "SharpCompress 0.50.3\nSource: https://github.com/adamhathcock/sharpcompress\nLicense: MIT\n\n",
            "",
        )
        root = self._create_root(notice=notice)

        self.assertEqual(1, self._run(root))

    def test_missing_assets_file_still_refreshes_pins(self) -> None:
        root = self._create_root(with_assets=False)

        self.assertEqual(0, self._run(root))

        updated = (root / "THIRD-PARTY-NOTICES.txt").read_text(encoding="utf-8")
        self.assertIn("YamlDotNet 18.2.0", updated)
        self.assertIn("Spectre.Console.Ansi 0.57.2", updated)


if __name__ == "__main__":
    unittest.main()
