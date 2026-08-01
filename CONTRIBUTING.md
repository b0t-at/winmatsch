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
  `Directory.Packages.props`.

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
