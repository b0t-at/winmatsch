# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/) (currently pre-1.0:
minor versions may contain breaking changes).

## [Unreleased]

## [0.8.0] - 2026-08-03

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

### Changed

- Updated YamlDotNet to 18.1.0 (from 16.3.0) and OpenMcdf to 3.2.0 (from
  3.1.4). YamlDotNet 18.x adds a default YAML recursion ceiling of 130 —
  well above winmatsch's own manifest depth budgets (64 for manifests, 32 for
  override packs), so parser behavior is unchanged for valid input while
  hostile deeply nested input now fails earlier.

### Documentation

- Documented the dependency policy (license, transitives, trim/AOT,
  currency) and why JsonSchema.Net stays on the MIT-licensed 8.x line.
- Documented previously implicit behavior: LF line-ending normalization,
  local write transactions and their recovery journals, anonymous public
  `show`/`list-versions` reads with optional authentication, the learned-override store behind approved
  human-correction reviews, the durable local-to-remote submission journals,
  and the override-pack field selectors and scope-layout semantics.

[Unreleased]: https://github.com/b0t-at/winmatsch/compare/v0.8.0...main
[0.8.0]: https://github.com/b0t-at/winmatsch/releases/tag/v0.8.0
