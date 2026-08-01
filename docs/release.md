# Release guide

## Version and tag policy

- The project follows semantic versioning and is currently **pre-1.0**
  (`VersionPrefix` `0.1.0` in `Directory.Build.props`).
- Releases are cut from tags of the form `v<major>.<minor>.<patch>`
  (optionally `-<prerelease>`), e.g. `v0.2.0`. The release workflow rejects
  malformed tags.
- The tag version is stamped into the executable (`-p:Version`), so
  `winmatsch --version` of a release artifact prints the tag version plus
  the commit SHA. Untagged builds print the `VersionPrefix` plus SHA.
- Update `VersionPrefix` together with tagging so source builds after the
  release report the right version.

## What the release workflow does (`.github/workflows/release.yml`)

Triggered by pushing a `v*` tag:

1. **Gate** (once, on Linux): validates the tag shape, fails hard if the CLI
   project is missing, then restores, builds Release, and runs the complete
   hermetic test suite including the E2E project.
2. **Publish** (six jobs): for each RID — `win-x64`, `win-arm64`,
   `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` — publishes a
   self-contained, trimmed, single-file executable with trim-analysis
   warnings treated as errors and the tag version stamped in. Verifies the
   executable, `LICENSE`, and `THIRD-PARTY-NOTICES.txt` are present, smokes
   the binary on hosts whose architecture matches
   (`--version` equals the tag, `--help`, `analyze --help`,
   `completion bash`, `config path`), then packages `zip` (Windows) or
   `tar.gz` (Linux/macOS) with deterministic names
   `winmatsch-<tag>-<rid>.<ext>`.
3. **Release** (once): downloads all six archives, **fails if any expected
   archive is missing**, test-extracts every archive, generates
   `SHA256SUMS.txt`, verifies it (`sha256sum -c`), and creates a **draft**
   GitHub release with generated notes. Publishing the draft is a manual,
   human step.

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
6. **Watch the workflow.** All eight jobs (gate, six publishes, release)
   must succeed; any missing artifact fails the run by design.
7. **Verify the draft.** Download at least one archive, check
   `SHA256SUMS.txt`, run `winmatsch --version`, `--help`, and
   `winmatsch analyze` against a local file.
8. **Optional live E2E.** Opt-in live verification against the dedicated
   test repository only (`WINMATSCH_E2E_TEST_REPOSITORY` /
   `WINMATSCH_E2E_LIVE_MUTATION=1` with the hard-allowlisted
   `b0t-at/winmatsch-e2e`); never against `microsoft/winget-pkgs`.
9. **Publish the draft release.**
10. **Self-submission (post-1.0 goal).** Once the project is stable enough
    to publish to WinGet, use winmatsch itself:
    `winmatsch new <Publisher>.winmatsch --version <version> --urls <release zip> --submit`.

## Rollback and recovery

- **Before publishing the draft:** delete the draft release and the tag
  (`git push origin :refs/tags/v<version>`), fix, re-tag.
- **After publishing:** do not delete published artifacts users may have
  downloaded. Ship a fixed `v<version+1>` instead, and mark the broken
  release as such in its notes.
- **Partial workflow failure:** the release job refuses to create a draft
  unless all six archives exist and verify, so a half-failed matrix cannot
  produce an incomplete release; re-run the failed jobs.

## v1.0 checklist (not yet shipped)

The project deliberately stays 0.x until:

- [ ] JSON output carries an explicit contract version field.
- [ ] The command surface has been stable for at least one release cycle.
- [ ] Live E2E against the test repository is part of the pre-release
      routine.
- [ ] winmatsch is itself published to WinGet via self-submission.
