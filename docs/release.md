# Release guide

## Version and tag policy

- The project follows semantic versioning and is currently **pre-1.0**
  (`VersionPrefix` `0.8.0` in `Directory.Build.props`).
- Releases are cut from tags of the form `v<major>.<minor>.<patch>`
  (optionally `-<prerelease>`), e.g. `v0.2.0`. The release workflow rejects
  malformed tags.
- The tag version is stamped into the executable (`-p:Version`), so
  `winmatsch --version` of a release artifact prints the tag version plus
  the commit SHA. Untagged builds print the `VersionPrefix` plus SHA.
- Update `VersionPrefix` together with tagging so source builds after the
  release report the right version.

## What the release workflow does (`.github/workflows/release.yml`)

The build path is triggered by pushing a `v*` tag:

1. **Gate** (once, on Linux): validates the tag shape, fails hard if the CLI
   project is missing, then restores, builds Release, and runs the complete
   hermetic test suite including the E2E project.
2. **Publish** (six jobs): for each RID — `win-x64`, `win-arm64`,
   `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` — publishes a
   self-contained, trimmed, single-file executable with its managed
   assemblies compressed, trim-analysis warnings treated as errors, and the
   tag version stamped in. Verifies the executable, `LICENSE`, and
   `THIRD-PARTY-NOTICES.txt` are present, rejects binaries above the 20 MiB
   size budget, and smokes the binary on hosts whose architecture matches
   (`--version` must equal the tag version exactly, ignoring the `+<sha>`
   suffix; `--help`, `analyze --help`, `completion bash`, `config path`),
   then copies the raw executable to a deterministic release name
   `winmatsch-<tag>-<rid>` (`.exe` on Windows) — no zip/tar.gz wrapping.
3. **Release** (once): downloads all six binaries, **fails if any expected
   binary is missing**, adds the shared `LICENSE` and
   `THIRD-PARTY-NOTICES.txt` (published once, not per-platform, since they
   are identical across RIDs), generates `SHA256SUMS.txt`, verifies it
   (`sha256sum -c`), and creates a **draft** GitHub release with generated
   notes. Publishing the draft is a manual, human step.

## Azure publication workflow (`.github/workflows/publish-azure-release.yml`)

Publishing the GitHub release triggers the separate **Publish download site**
workflow. It downloads and verifies the exact final asset set, publishes the
immutable version directory, updates the public catalog, refreshes the stable
download aliases when appropriate, and deploys the static index.

The workflow uses the `Release` GitHub environment and exchanges GitHub's OIDC
token for a short-lived Azure token. No storage key, SAS, or client secret is
stored in GitHub. **Actions > Publish download site > Run workflow** can
backfill or retry an already-published tag; the job rejects drafts and applies
the same validation and immutability checks. The publisher implementation is
always checked out from the default branch, so older releases can be backfilled
even when their tags predate the download-site scripts.

### Azure release layout and catalog contract

The container layout is intentionally append-oriented and CDN-friendly:

```text
/
├── index.html
├── 404.html
├── versions.json
├── latest/
│  ├── index.html
│  ├── latest.json
│  ├── version.txt
│  ├── SHA256SUMS.txt
│  └── winmatsch-<rid>[.exe]
└── v0.8.0/
   ├── index.html
   ├── SHA256SUMS.txt
   ├── LICENSE
   ├── THIRD-PARTY-NOTICES.txt
   └── winmatsch-v0.8.0-<rid>[.exe]
```

- `v<version>/` is the stable, version-qualified download namespace.
  Versioned release files use a one-year immutable cache policy; the
  client-side `index.html` shell remains short-lived. Front Door also serves
  `/<version>/` as a human-page alias without creating a second storage tree;
  downloads remain canonical under `/v<version>/`.
- `versions.json` is the enumeration endpoint and the browser's source of
  truth. It is sorted by semantic-version precedence and includes each
  artifact's RID, platform, architecture, byte size, URL, and SHA-256.
- `latest/` mirrors only the highest stable semantic version under unversioned
  names. Backfilling an older release or publishing a prerelease cannot move it
  backward or replace it.
- The mutable site and catalog files use a short revalidation cache policy.
  Updating `versions.json` is protected by its Azure Blob ETag so concurrent or
  stale writers fail instead of losing an entry.

Re-running publication is idempotent only when every existing versioned release
file and catalog entry are unchanged. Versioned release blobs are never
overwritten; a changed file at an already-published path fails the run. The
HTML shell can be refreshed independently. The complete URL and manifest
contract is documented in [download-site.md](download-site.md).

## Release checklist

1. **Green main.** CI (build + full tests on Windows/Linux/macOS + format
   check) must be green on the commit to release.
2. **Version bump.** Set `VersionPrefix` in `Directory.Build.props` to the
   release version; land it via PR.
3. **Changelog.** Move the `Unreleased` section of
   [CHANGELOG.md](../CHANGELOG.md) under the new version heading.
4. **Docs sanity.** `README.md` and `docs/` must not promise anything the
   tagged build does not do; re-check the command reference against
   `--help`.
5. **Tag.** `git tag v<version> && git push origin v<version>`.
6. **Watch the tag workflow.** The gate, six publish jobs, and draft-release
   job must succeed; any missing artifact fails the run by design.
7. **Verify the draft.** Download at least one binary, check
   `SHA256SUMS.txt`, run `winmatsch --version`, `--help`, and
   `winmatsch analyze` against a local file.
8. **Optional live E2E.** Opt-in live verification against the dedicated
   test repository only (`WINMATSCH_E2E_TEST_REPOSITORY` /
   `WINMATSCH_E2E_LIVE_MUTATION=1` with the hard-allowlisted
   `b0t-at/winmatsch-e2e`); never against `microsoft/winget-pkgs`.
9. **Publish the draft release.** This triggers the separate Azure publication
   workflow; verify that its job updates the catalog successfully.
   For a release that predates the Azure job, use **Run workflow** with its
   published tag.
10. **Self-submission (post-1.0 goal).** Once the project is stable enough
    to publish to WinGet, use winmatsch itself:
    `winmatsch new <Publisher>.winmatsch --version <version> --urls <release binary URL> --submit`.

## Rollback and recovery

- **Before publishing the draft:** delete the draft release and the tag
  (`git push origin :refs/tags/v<version>`), fix, re-tag. Azure Storage is not
  changed until the GitHub release is published.
- **After publishing:** do not delete published artifacts users may have
  downloaded. Ship a fixed `v<version+1>` instead, and mark the broken
  release as such in its notes.
- **Partial workflow failure:** the release job refuses to create a draft
  unless all six binaries exist and verify, so a half-failed matrix cannot
  produce an incomplete release; re-run the failed jobs.

## v1.0 checklist (not yet shipped)

The project deliberately stays 0.x until:

- [ ] JSON output carries an explicit contract version field.
- [ ] The command surface has been stable for at least one release cycle.
- [ ] Live E2E against the test repository is part of the pre-release
      routine.
- [ ] winmatsch is itself published to WinGet via self-submission.
