# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/) (currently pre-1.0:
minor versions may contain breaking changes).

## [Unreleased]

Initial development toward a first release. Implemented so far:

### Added

- Command surface: `analyze`, `validate`, `show`, `list-versions`, `new`,
  `update`, `remove`, `submit`, `new-locale`, `update-locale`, `sync`,
  `cleanup`, `complete`, `token`, `config`, `cache`, `completion`.
- Installer analyzers for MSI, MSIX/AppX (incl. bundles), ZIP, PE,
  WiX Burn, NSIS, Inno Setup, Advanced Installer, and Squirrel, with hard
  parsing limits for untrusted input.
- Deterministic rule pipeline (normalization, policy, validation rules) with
  per-rule modes, override packs, quirks, and human-correction review.
- Manifest validation against the embedded WinGet 1.12.0 schemas.
- GitHub submission lifecycle (fork → branch → commit → PR) with duplicate
  detection, provenance, and partial-state reporting.
- Persistent, integrity-checked download cache.
- Configuration system (`command > env > user YAML > defaults`) and OS
  keyring token storage.
- Stable JSON output contract and deterministic exit codes.
- CI across Windows/Linux/macOS and a six-RID release pipeline producing
  self-contained, trimmed single-file executables.

[Unreleased]: https://github.com/b0t-at/winmatsch/commits/main
