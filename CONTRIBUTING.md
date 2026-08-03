# Contributing to winmatsch

Thanks for considering a contribution! This document covers local setup and
the expectations for changes.

## Local setup

- Install the .NET SDK version pinned in [`global.json`](global.json)
  (`rollForward: latestFeature`, so any newer 10.0.x feature band also
  works). If you keep the SDK in a user-local directory, prepend it to `PATH`
  in every shell.
- No other tools are required; all dependencies are NuGet packages.

```bash
dotnet restore
dotnet build --configuration Release          # warnings are errors
dotnet test  --configuration Release          # full hermetic suite incl. E2E
dotnet format --verify-no-changes             # style gate (CI enforces it)
```

The E2E project runs the real executable as a child process; it is hermetic
by default (no network, `GITHUB_TOKEN` cleared). The optional live tests are
opt-in via environment variables and restricted to a hard-coded test
repository — see [docs/architecture.md](docs/architecture.md#testing-model).
Never point live mutation tests at `microsoft/winget-pkgs`.

## Ground rules

- **Warnings are errors** everywhere, including trim-analysis warnings in
  shipped projects. Keep code trimming- and AOT-compatible (no reflection
  serialization, no dynamic code).
- **Determinism.** Same inputs must produce byte-identical manifests and
  JSON output.
- **Safety contracts.** Results to stdout, diagnostics to stderr, exit codes
  from the documented contract, secrets always redacted, prompts only
  through the interaction abstraction.
- **Fixture policy.** Never commit third-party installer binaries. Tests use
  synthetic fixtures, JSON descriptors, and recorded HTTP interactions
  (`tests/WinMatsch.Testing`).
- **Clean-room rule.** Do not port or copy source from existing installer
  parsers or manifest tools. Analyzers are implemented from public format
  documentation.
- **Dependencies.** New runtime packages need a matching entry in
  [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) with verified license,
  source, and version, and central version management in
  `Directory.Packages.props`. See [dependency policy](#dependency-policy).

## Dependency policy

Runtime dependencies are deliberately few, and each one is a licensing and
trimming decision as much as a technical one.

- **License.** Only permissive licenses are acceptable for shipped packages:
  MIT, BSD, Apache-2.0, or a file-level copyleft we can comply with by
  attribution and source pointer (MPL-2.0 — currently only OpenMcdf). Verify
  the license of the **exact package version** (`.nuspec` `license`
  element), not just the project's README: json-everything, for example,
  publishes MIT source but ships `OSMFEULA.txt` — a maintenance-fee EULA — in
  its 9.x binaries. Anything beyond permissive terms needs an explicit,
  recorded decision, never a silent upgrade.
- **Transitives count.** Check the whole closure with
  `dotnet list package --include-transitive`; a permissive direct package can
  drag a non-permissive transitive in.
- **Trim/AOT.** Shipped projects are `IsAotCompatible`, warnings are errors,
  and reflection-based serialization is banned. A package that emits `IL2026`
  / `IL3050` cannot be used on a hot path.
- **Currency.** Prefer the latest stable version. Any hold-back must carry a
  comment in `Directory.Packages.props` naming the concrete blocker and the
  condition that would let us move — "AOT" or "risky" alone is not a reason.
- **Notices.** Update `THIRD-PARTY-NOTICES.txt` in the same commit as the
  version change, including the pinned source commit for MPL-2.0 components.
  `LicenseNoticeTests` cross-checks the notice against
  `Directory.Packages.props` and fails when the two drift, and it also fails
  when a new pin is neither noticed nor declared test-only.

```bash
dotnet list package --include-transitive        # full closure, incl. licenses to verify
dotnet list package --outdated                  # currency check before a release
```

Upgrades are validated with a full build + test run and, when a shipped
package changes, a trimmed publish for all six release RIDs (see
[docs/release.md](docs/release.md)).

## Making changes

- Adding an **analyzer**, **rule**, or **command**: follow the recipes in
  [docs/architecture.md](docs/architecture.md#extending).
- User-visible behavior changes must update the matching guide under
  [docs/](docs/) and, for CLI surface changes, the
  [command reference](docs/commands.md).
- Add or update tests next to the code you change; the full suite must pass
  on your machine before you open a PR.

## Pull requests

- Keep PRs focused; explain *why*, not just *what*.
- CI runs build + full tests on Windows, Linux, and macOS plus a format
  check; all must be green.
- By contributing you agree that your contribution is licensed under the
  [MIT License](LICENSE).

## Security issues

Do **not** open public issues for suspected vulnerabilities — see
[SECURITY.md](SECURITY.md).
