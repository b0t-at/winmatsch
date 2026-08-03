# winmatsch

**winmatsch** is a cross-platform command-line tool that automates
[WinGet](https://github.com/microsoft/winget-pkgs) package manifests. It
downloads an installer, analyzes what is actually inside it (MSI, MSIX/AppX,
NSIS, Inno Setup, WiX Burn, Advanced Installer, Squirrel, ZIP, plain PE),
generates or updates a complete multi-file manifest, validates it against the
official manifest schemas, and — only when you explicitly ask — submits it to
`microsoft/winget-pkgs` through a fork-and-pull-request workflow.

> **Status:** pre-1.0 (`0.1.0`). The command surface, exit codes, and JSON
> output described below are implemented and tested, but the project has not
> shipped a v1 release; interfaces may still change before 1.0. See the
> [release guide](docs/release.md) for the v1 checklist.

## Why winmatsch

- **Evidence-based manifests.** Fields are derived from what the installer
  binary actually declares (MSI property tables, MSIX identity, PE version
  info, NSIS/Inno/Burn/Advanced Installer/Squirrel structures), not guessed
  from file names.
- **Deterministic rule engine.** A catalogued set of normalization,
  validation, and policy rules (IDs like `WM0001`, `ARP-1`, `SCOPE-2`) shapes
  every manifest the same way, with a full before/after audit trail
  (`--explain-rules`) and per-rule modes: apply, log-only, disabled.
- **Safety first.** Every command plans before it mutates. `--dry-run` never
  changes anything, remote mutations require explicit opt-in (`--submit` for
  manifest changes, confirmation — `--yes` in non-interactive sessions — for
  every destructive step), and tokens are redacted by construction while the
  audit trail passes a secret-scrubbing sanitizer. Human-correction reviews
  are the one gate `--yes` does not cover: they are approved interactively, or
  encoded up front in an [override pack](docs/rules.md#override-packs).
- **Automation friendly.** `--format json` writes exactly one stable JSON
  document to stdout, diagnostics go to stderr, and every invocation ends with
  a documented exit code.

## Safety model

| Guarantee | Detail |
|---|---|
| Plan first | `--dry-run` validates and shows what would change; it never writes to disk beyond the cache, and never changes GitHub state (read-only API calls may still occur). |
| Explicit mutation | Manifest changes are pushed only with `--submit`; maintenance commands (`sync`, `complete --apply-safe`) plan first and apply only after confirmation. Confirmation never defaults to yes. |
| No secret echo | Tokens are validated, stored in the OS keyring, and rendered as `[REDACTED]` by construction; the audit trail, previews, and JSON output pass a secret-scrubbing sanitizer. |
| Bounded parsing | Untrusted installers and archives are parsed with hard limits (entry counts, sizes, nesting depth) to resist archive bombs. |
| Human corrections win | When a human edited a previously submitted manifest, the tool detects it and requires review instead of silently reverting. Approving a review is interactive-only; an approval is then remembered as a learned override pack and reapplied while the correction still matches. |

Details: [security guide](docs/security.md).

## Supported platforms

Release binaries are published for six runtime identifiers:

| OS | Architectures |
|---|---|
| Windows | `win-x64`, `win-arm64` |
| Linux | `linux-x64`, `linux-arm64` |
| macOS | `osx-x64`, `osx-arm64` |

Each is a single, self-contained, trimmed executable with its managed
assemblies compressed (no .NET runtime install required, no installer,
nothing to extract) — download it and run it directly. `LICENSE` and
`THIRD-PARTY-NOTICES.txt` are published once per release as separate assets
(identical for every platform).

## Install

Download the binary for your platform from the
[releases page](https://github.com/b0t-at/winmatsch/releases), verify it
against `SHA256SUMS.txt`, and run it:

```bash
# Linux / macOS
curl -LO https://github.com/b0t-at/winmatsch/releases/download/v<version>/winmatsch-v<version>-linux-x64
chmod +x winmatsch-v<version>-linux-x64
./winmatsch-v<version>-linux-x64 --version
```

```powershell
# Windows
Invoke-WebRequest https://github.com/b0t-at/winmatsch/releases/download/v<version>/winmatsch-v<version>-win-x64.exe -OutFile winmatsch.exe
.\winmatsch.exe --version
```

### Build from source

Prerequisites: the .NET SDK version pinned in [`global.json`](global.json).

```bash
git clone https://github.com/b0t-at/winmatsch.git
cd winmatsch
dotnet build --configuration Release
dotnet run --project src/WinMatsch.Cli -- --help
```

## Quick start

### Analyze an installer (read-only, no token needed)

```bash
winmatsch analyze ./MyApp-Setup.exe
winmatsch analyze https://example.com/MyApp-1.2.3.msi
```

### Validate a local manifest set (read-only)

```bash
winmatsch validate ./manifests/m/MyPublisher/MyApp/1.2.3
winmatsch validate --offline installer.yaml locale.en-US.yaml version.yaml
```

### Plan an update without touching anything

```bash
winmatsch update MyPublisher.MyApp 1.2.3 \
  --urls https://example.com/MyApp-1.2.3-x64.msi \
  --dry-run
```

### Generate manifests locally

```bash
winmatsch new MyPublisher.MyApp \
  --version 1.2.3 \
  --urls https://example.com/MyApp-1.2.3-x64.msi \
  --output ./out
```

### Submit to WinGet (requires a GitHub token)

```bash
# one-time token setup: pipe the token into the OS keyring, e.g. from gh
gh auth token | winmatsch token add --stdin

winmatsch update MyPublisher.MyApp 1.2.3 \
  --urls https://example.com/MyApp-1.2.3-x64.msi \
  --submit --yes
```

### Script it with JSON

```bash
winmatsch analyze ./MyApp.msi --format json | jq .
winmatsch update MyPublisher.MyApp 1.2.3 --urls <url> --dry-run --format json
```

`--format json` never prompts; missing required input fails with exit code 4
instead of hanging in CI. See [exit codes and JSON contract](docs/commands.md#exit-codes).

## Configuration and tokens

Configuration follows a strict precedence:
**command line > `WINMATSCH_*` environment variables > user YAML file > built-in defaults.**
The user file lives at `~/.config/winmatsch/config.yaml` (respecting
`XDG_CONFIG_HOME`; `%USERPROFILE%\.config\winmatsch\config.yaml` on Windows):

```bash
winmatsch config set repository microsoft/winget-pkgs
winmatsch config show      # every value with its source
winmatsch config path
```

GitHub tokens are **never** stored in the configuration file. Precedence:
`--token` > `GITHUB_TOKEN` > OS keyring (Windows Credential Manager, macOS
Keychain, or freedesktop Secret Service via `secret-tool`). See the
[security guide](docs/security.md).

## Documentation

| Guide | Contents |
|---|---|
| [Command reference](docs/commands.md) | Every command, option, environment variable, exit code, and the JSON output contract |
| [Configuration](docs/configuration.md) | All keys, defaults, per-OS paths, precedence, examples |
| [Rules and overrides](docs/rules.md) | Rule catalogue, modes, override packs, human-correction review |
| [Analyzers](docs/analyzers.md) | Installer format support matrix, evidence, limits, non-goals |
| [Security](docs/security.md) | Token storage, redaction, untrusted-input bounds, mutation guarantees |
| [Architecture](docs/architecture.md) | Project graph, workflow stages, how to extend |
| [Troubleshooting](docs/troubleshooting.md) | Exit codes, keyring issues, network blockers, CI behavior |
| [Release guide](docs/release.md) | Version/tag policy, gates, packaging, v1 checklist |
| [Contributing](CONTRIBUTING.md) | Local setup, tests, fixture policy, PR expectations |
| [Security policy](SECURITY.md) | Reporting vulnerabilities |
| [Changelog](CHANGELOG.md) | Unreleased scope |

## License

winmatsch is licensed under the [MIT License](LICENSE). Bundled third-party
components are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
The installer-format analyzers are clean-room implementations based on public
format documentation; no third-party parser source was copied.
