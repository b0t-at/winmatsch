# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/) (currently pre-1.0:
minor versions may contain breaking changes).

## [Unreleased]

## [0.8.20] - 2026-08-21

### Fixed

- A `ReleaseNotesUrl` pointing at a release-tag page (`/releases/tag/…`) is no
  longer carried forward on update, even when the embedded tag does not match
  the previous package version — a tag that lagged behind the manifest version
  once was previously carried forever (Microsoft.WSL.PreRelease, winget-pkgs
  PR #421624 shipped `releases/tag/2.7.0` in the 2.9.4 manifest). When the
  release object cannot be resolved through the API (tag-only releases,
  discovery outages, caller-supplied direct URLs), the fresh `ReleaseNotesUrl`
  is now derived from the immutable `releases/download/<tag>/…` installer
  URLs instead of being left empty.

## [0.8.19] - 2026-08-20

### Fixed

- Raised analysis resource limits for real-world installers, added LZMA ZIP
  support, and release-notes refresh on update: `ReleaseNotes`/
  `ReleaseNotesUrl` are re-discovered from the target release on every update
  instead of only on first creation (#57).

### Changed

- Bumped SharpCompress and System.CommandLine (#55).

## [0.8.18] - 2026-08-19

### Fixed

- The analysis-side URL architecture detector and the mapping-side token
  classifier now share one token table in `BinMatch.Core`, so filenames like
  `ugene-53.1-win-x86-64.exe` classify identically on both sides instead of
  producing a false `ARCH_CONFLICT` safety stop. The classifier gains
  `x86-64`/`i686`/`686`; the detector gains `winarm64`/`win64a` and the
  `_64`/`_32` suffix tokens.
- Deterministic analyzer refusals now surface as structured needs-decision
  questions (exit 4) instead of unhandled crashes the pipeline retries
  forever: artifact acquisition converts `InvalidDataException` and resource
  limit failures into an `ANALYSIS_MANUAL_REQUIRED` mapping question; portable
  archives with colliding command aliases (multi-target-framework layouts)
  degrade to a `ZIP006` manual-selection result; Inno header parse failures
  degrade to `INNO016` and unknown privilege values to `INNO017`; Burn bundles
  with unreadable UX containers (e.g. LZX cabinets) degrade to `BURN004`.
- Well-known .NET host binaries (`createdump.exe`, `apphost.exe`,
  `singlefilehost.exe`) no longer participate in portable payload selection,
  and the per-entry archive byte ceiling applies only to entries that are
  actually extracted. Both ceilings are now configurable via
  `BINMATCH_MAX_ENTRY_BYTES` and `BINMATCH_MAX_EXPANDED_ARCHIVE_BYTES`.
- Version continuity checks compare URL tokens with WinGet numeric
  equivalence, tolerate short numeric vendor revisions on the URL side
  (`Converseen-0.15.2.7-1`, `meson-1.12.0-64`), extract versions glued to the
  product name (`AlbayanV6.2.0`) or to a trailing architecture token
  (`Thetis-v2.10.3.14x64`), and fall back to the release-tag path segment when
  the file name has no parseable version — eliminating recurring false
  `MAP_VERSION_DISCONTINUITY`/`MAP_VERSION_AMBIGUOUS` stops for zero-padded
  release paths such as `/v2026.08.18/`.
- Stale metadata no longer ships on updates: carried `ReleaseNotes`/
  `ReleaseNotesUrl` are cleared from cloned manifests (the guarded carry now
  also matches trailing-`.0` version variants), license/copyright links are
  rewritten to `blob/HEAD` only when the rewritten URL is confirmed reachable
  (raw links keep their pinned ref in the HTML `blob` form otherwise),
  optional metadata URLs that return a definitive HTTP 404/410 during
  preflight are dropped before submission (`VLD5006` +
  `WF_DEAD_METADATA_URL_DROPPED`), MSIX/AppX updates recompute
  `SignatureSha256` from the analyzed artifact, and `DefaultInstallLocation`
  is version-substituted like other version-embedding fields.

### Changed

- `THIRD-PARTY-NOTICES.txt` is now guarded by package identity instead of
  exact version, so dependency bumps no longer fail CI; version numbers are
  refreshed with the new `scripts/update-third-party-notices.py`, which the
  release workflow enforces with `--check`.

## [0.8.17] - 2026-08-13

### Fixed

- `.NET` single-file bundles are now inspected for their embedded
  `runtimeconfig.json`, so bundled apps report their real runtime major and
  family (base, ASP.NET Core, or Windows Desktop) instead of appearing
  runtime-free.
- `DEP-1` now recognizes every .NET runtime package family and refreshes a stale
  pin of the same family in place — at the manifest root when the installer only
  inherits it — instead of leaving an outdated runtime dependency behind. Pins
  from a different family still require review.
- Updates no longer carry the previous version's `ReleaseDate` forward; the
  value is cleared so `META-5` recomputes it from release metadata, falling back
  to the download's `Last-Modified` header when the installer URL belongs to no
  discoverable release.
- Payload dependency evidence gathered while pre-downloading artifacts now
  reaches the rule pipeline, so `DEP-1` is no longer silent in `new` and
  `update` runs.

## [0.8.16] - 2026-08-05

### Fixed

- ZIP analysis now safely defers rejection of irrelevant encrypted or unsupported
  entries until an installer candidate is read, while required encrypted
  installer content continues to fail closed with the `ZIP004` diagnostic.

## [0.8.15] - 2026-08-05

### Fixed

- ZIP analysis now hardens encrypted and unsupported ZIP entry handling while
  preserving fail-closed `ZIP004` diagnostics for encrypted installer
  candidates.

## [0.8.14] - 2026-08-05

### Fixed

- Updates now retain every previously declared per-installer property while
  rebuilding mapped installers, refresh declared ARP identity and version values
  from fresh analysis, and stop with a structural question when explicit ARP
  evidence contradicts the accepted entry count. This also covers installers
  added through release-asset continuity.
- A qualified architecture override now collapses same-URL analyzed variants
  that resolve to one effective installer key, while distinct conflicting
  overrides remain rejected. A unique replacement asset also inherits a
  previous single-neutral ZIP layout without requiring an override.

## [0.8.13] - 2026-08-05

### Fixed

- Updates now preserve validated nested ZIP installer metadata and stop with a
  structural question when fresh analysis would otherwise remove accepted
  `NestedInstallerType`, `NestedInstallerFiles`, or archive-path semantics.
- Updates with a partial set of GitHub release URLs now auto-complete uniquely
  matching same-repository installer siblings from the target release before
  asking `MAP_REMOVED`. Completed mappings retain explicit audit provenance and
  pass through the normal download, analysis, hash, rule, and validation gates.
- Updates now accept distinct same-URL architecture/scope overrides, inherit
  intentional same-URL scope layouts from one resolved asset, and let a
  qualified override absorb duplicate inferred candidates while preserving
  each previous entry's switches and metadata. Same-URL user/machine twins
  also avoid false `VLD3002` conflicts when standard installer switches differ
  only by scope markers.
- URL version continuity checks now preserve prerelease and build suffixes,
  avoiding false mapping conflicts when release paths contain the exact target version.
- ZIP analysis now supports bounded Deflate64 payload reads and reports encrypted
  or otherwise unsupported entries as the stable `ZIP004` domain diagnostic,
  including the archive, entry path, and compression method, instead of crashing.
  ZIP64 archives whose end-of-central-directory fields all use sentinel values
  are now parsed through the ZIP64 records instead of being rejected as corrupt.

## [0.8.12] - 2026-08-05

### Added

- A global `--result-json <path>` flag atomically writes a redacted,
  machine-readable terminal outcome on success and controlled failure without
  changing console output or process exit codes. Mutation outcomes include
  package identity, absolute manifest path, stable domain error identifiers,
  and verified pull-request metadata when a submission created one.

### Changed

- Generated upstream pull request bodies now focus on the package change,
  tool attribution, validation success, and optional issue resolution while
  preserving hidden lifecycle metadata and omitting validation details.

### Fixed

- Update source resolution now skips version directories that contain no manifest
  set, falls back to the next valid source version, and reports the unusable
  candidates when none can be loaded.

## [0.8.11] - 2026-08-05

### Fixed

- GitHub submission now tolerates unrelated upstream default-branch churn by
  re-anchoring scoped manifest-path, repository-evidence, duplicate-PR, and fork
  freshness checks with bounded retries. Planned-path changes, new duplicates,
  retry exhaustion, and validated branch or pull-request identity drift still
  fail closed, including verified cleanup when post-creation validation changes.

## [0.8.10] - 2026-08-05

### Fixed

- GitHub submission now combines bounded repository-scoped text search with
  authoritative changed-path screening for up to 5,000 open pull requests.
  Complete GraphQL nonmatches are discarded before full evidence collection;
  only ambiguous large or renamed pull requests use per-PR REST completion.
  Large upstreams no longer fail at the former 1,000-result or 16-completion
  bounds, while GitHub's 3,000-file ceiling, merge-base, and pinned identity
  checks remain fail-closed.

## [0.8.9] - 2026-08-04

### Fixed

- Journaled raw submissions now reacquire approved installer bytes into an
  async-disposable, lifetime-owned lease before remote preflight. Recovery
  journals use query-free canonical redirect identity schema v1 and retain the
  verified hash and size; recovery rejects legacy journals or any redirect,
  hash, or size drift and cleans every failure path, preventing nested ZIP
  validation from reopening an empty placeholder.

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
  (e.g. `binmatch-<tag>-win-x64.exe`, `binmatch-<tag>-linux-x64`) instead
  of `.zip`/`.tar.gz` archives, so binaries can be downloaded and run
  directly with no extraction step. `LICENSE` and `THIRD-PARTY-NOTICES.txt`
  are published once per release as shared assets (identical across all
  platforms) rather than duplicated inside each archive.
- Release binaries now compress their embedded managed assemblies, reducing
  all six assets by 32–35% while preserving self-contained execution. The
  release workflow also enforces a 20 MiB per-binary size budget.
- Windows download snippets consistently save the selected architecture build
  as `binmatch.exe` and use that local name for verification and execution.

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
  well above binmatch's own manifest depth budgets (64 for manifests, 32 for
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

[Unreleased]: https://github.com/b0t-at/binmatch/compare/v0.8.20...main
[0.8.20]: https://github.com/b0t-at/binmatch/compare/v0.8.19...v0.8.20
[0.8.19]: https://github.com/b0t-at/binmatch/compare/v0.8.18...v0.8.19
[0.8.18]: https://github.com/b0t-at/binmatch/compare/v0.8.17...v0.8.18
[0.8.17]: https://github.com/b0t-at/binmatch/compare/v0.8.16...v0.8.17
[0.8.16]: https://github.com/b0t-at/binmatch/compare/v0.8.15...v0.8.16
[0.8.15]: https://github.com/b0t-at/binmatch/compare/v0.8.14...v0.8.15
[0.8.14]: https://github.com/b0t-at/binmatch/compare/v0.8.13...v0.8.14
[0.8.13]: https://github.com/b0t-at/binmatch/compare/v0.8.12...v0.8.13
[0.8.12]: https://github.com/b0t-at/binmatch/compare/v0.8.11...v0.8.12
[0.8.11]: https://github.com/b0t-at/binmatch/compare/v0.8.10...v0.8.11
[0.8.10]: https://github.com/b0t-at/binmatch/compare/v0.8.9...v0.8.10
[0.8.9]: https://github.com/b0t-at/binmatch/compare/v0.8.8...v0.8.9
[0.8.8]: https://github.com/b0t-at/binmatch/compare/v0.8.7...v0.8.8
[0.8.7]: https://github.com/b0t-at/binmatch/compare/v0.8.6...v0.8.7
[0.8.6]: https://github.com/b0t-at/binmatch/compare/v0.8.5...v0.8.6
[0.8.5]: https://github.com/b0t-at/binmatch/compare/v0.8.4...v0.8.5
[0.8.4]: https://github.com/b0t-at/binmatch/compare/v0.8.3...v0.8.4
[0.8.3]: https://github.com/b0t-at/binmatch/compare/v0.8.2...v0.8.3
[0.8.2]: https://github.com/b0t-at/binmatch/compare/v0.8.1...v0.8.2
[0.8.1]: https://github.com/b0t-at/binmatch/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/b0t-at/binmatch/releases/tag/v0.8.0
