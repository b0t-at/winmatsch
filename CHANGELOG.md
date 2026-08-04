# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/) (currently pre-1.0:
minor versions may contain breaking changes).

## [Unreleased]

### Fixed

- Journaled raw submissions now reacquire installer bytes into verified absolute
  paths before remote preflight, preventing nested ZIP validation from opening an
  empty recovery placeholder while retaining hash, size, and mutation-recovery
  guarantees.

## [0.8.8] - 2026-08-04

### Fixed

- GitHub `createCommitOnBranch` mutations no longer select the query-only
  `rateLimit` field from the mutation root; transport header-based rate-limit
  tracking remains in place.

## [0.8.7] - 2026-08-04

### Fixed

- Submit validation accepts the exact WinGet schema directive after leading
  generator comments used by WingetCreate, Komac, and other repository tools.
- Verified submissions retain prefetched installer artifacts through final
  archive and hash validation instead of referring to deleted planning files.
- Duplicate pull-request discovery uses changed-file snapshots at the GitHub
  identity actually observed, so unrelated active pull requests can move
  without aborting the entire submission while candidate identity remains
  pinned and reverified.

## [0.8.6] - 2026-08-04

### Fixed

- GitHub submission no longer requires `read:user` or `user:email`; a token
  with repository access can resolve the authenticated fork owner.

## [0.8.5] - 2026-08-04

### Fixed

- Multilingual NSIS installers no longer claim their first language table as
  an exclusive installer locale.
- ZIP analysis ignores deeper helper executables when valid shallower payloads
  exist, and nested paths follow bounded versioned or uniquely relocated files.
- Updates deduplicate identical requested URLs while preserving intentional
  same-URL installer layouts and their per-entry switches and metadata.
- Fresh analysis may fill previously unspecified scope and locale values, and
  stale nested metadata is removed from direct executable installers.
- `submit` maps flat manifest input directories to their canonical WinGet
  repository paths while preserving the supplied manifest bytes.

## [0.8.4] - 2026-08-03

### Fixed

- Repository-backed updates now clone manifests using per-document YAML
  serialization, allowing the `PIPE-2` rule to upgrade source schemas older
  than 1.12.0 before the manifest set is written.

## [0.8.3] - 2026-08-03

### Added

- The Azure publish workflow now renders a job summary on the run page:
  published binaries with sizes and SHA-256 digests, blob upload counts,
  and upload/Front Door purge durations (dry runs show the upload plan).

### Changed

- Replaced JsonSchema.Net with a reflection-free, load-time-gated Draft-07
  subset validator tailored to the bundled WinGet schemas. This removes
  JsonSchema.Net, JsonPointer.Net, Json.More.Net, and Humanizer.Core from
  shipped binaries while preserving VLD diagnostics and exact instance paths
  (issue #9).
- Repository-backed updates can resolve the latest published source version
  when the caller does not provide one explicitly.

## [0.8.2] - 2026-08-03

### Changed

- The Azure release workflow now purges the Front Door cache after each
  publish when the `AZURE_AFD_RESOURCE_GROUP`, `AZURE_AFD_PROFILE` and
  `AZURE_AFD_ENDPOINT` secrets are configured, so new releases show up on
  the download site immediately instead of after the five-minute edge TTL.
  The purge covers the rewrite-rule page URLs (`/latest`, `/v<version>`,
  `/<version>`) in addition to the mutable blobs. See
  `docs/download-site.md` for the least-privilege purge role.
- Dependabot no longer proposes JsonSchema.Net major updates: the 9.x
  binaries carry the OSMFEULA maintenance-fee EULA and remain declined
  under the dependency policy in `Directory.Packages.props` (PR #8).
  Issue #9 tracks replacing the validator with a self-built Draft-07
  subset implementation.

## [0.8.1] - 2026-08-03

### Added

- Download site for an Azure Blob container behind Front Door:
  a single self-contained `site/index.html` (uploaded as the index and
  404 document and into `/latest/` and every `/v<version>/` folder)
  renders a version browser, per-version artifact pages with checksums
  and verify snippets, and best-effort platform/architecture detection
  from a `/versions.json` manifest. `scripts/publish-download-site.sh`
  verifies release assets, updates the manifest
  (`scripts/update-versions-manifest.py`), uploads immutable release files
  under `/v<version>/`, mirrors the newest stable release to
  `/latest/` under stable unversioned names (plus `version.txt` and
  `latest.json`), and optionally purges Front Door. See
  `docs/download-site.md` for the URL contract and CI wiring.

### Changed

- Release assets are now published as raw, portable per-platform binaries
  (e.g. `winmatsch-<tag>-win-x64.exe`, `winmatsch-<tag>-linux-x64`) instead
  of `.zip`/`.tar.gz` archives, so binaries can be downloaded and run
  directly with no extraction step. `LICENSE` and `THIRD-PARTY-NOTICES.txt`
  are published once per release as shared assets (identical across all
  platforms) rather than duplicated inside each archive.
- Release binaries now compress their embedded managed assemblies, reducing
  all six assets by 32–35% while preserving self-contained execution. The
  release workflow also enforces a 20 MiB per-binary size budget.
- Windows download snippets consistently save the selected architecture build
  as `winmatsch.exe` and use that local name for verification and execution.

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

[Unreleased]: https://github.com/b0t-at/winmatsch/compare/v0.8.8...main
[0.8.8]: https://github.com/b0t-at/winmatsch/compare/v0.8.7...v0.8.8
[0.8.7]: https://github.com/b0t-at/winmatsch/compare/v0.8.6...v0.8.7
[0.8.6]: https://github.com/b0t-at/winmatsch/compare/v0.8.5...v0.8.6
[0.8.5]: https://github.com/b0t-at/winmatsch/compare/v0.8.4...v0.8.5
[0.8.4]: https://github.com/b0t-at/winmatsch/compare/v0.8.3...v0.8.4
[0.8.3]: https://github.com/b0t-at/winmatsch/compare/v0.8.2...v0.8.3
[0.8.2]: https://github.com/b0t-at/winmatsch/compare/v0.8.1...v0.8.2
[0.8.1]: https://github.com/b0t-at/winmatsch/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/b0t-at/winmatsch/releases/tag/v0.8.0
