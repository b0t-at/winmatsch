# Security, privacy, and tokens

## GitHub token handling

### Precedence

A token is resolved in this order; the first source that yields one wins:

1. `--token` on the command line,
2. the `GITHUB_TOKEN` environment variable,
3. the OS keyring (managed with `winmatsch token add|remove|status`).

If none is found, commands that need GitHub access fail with a clear message;
read-only local commands (`analyze`, `validate`, `cache`, `config`,
`completion`) work without any token.

### Storage adapters

| Platform | Backend |
|---|---|
| Windows | Windows Credential Manager (generic credential `winmatsch:github`) |
| macOS | login Keychain via the Security framework (service `winmatsch`, account `github`) |
| Linux | freedesktop Secret Service via the `secret-tool` binary; the secret travels only over stdin/stdout, never as a process argument |

When no keyring backend is available (e.g. `secret-tool` missing, headless
session without a secret service), `token add` fails with an explanatory
error and token resolution simply skips the keyring — use `GITHUB_TOKEN`
instead. Secret buffers are zeroed after use where the platform allows it.

### Recommendations and least privilege

- Prefer `winmatsch token add --stdin` (pipe the token) or `GITHUB_TOKEN`.
  Avoid `--token <value>`: process arguments can leak via shell history and
  process listings. The tool itself never echoes the value either way.
- Use a **fine-grained personal access token** restricted to the minimum:
  the tool needs to read the upstream manifest repository, and — only when
  you submit — to create/update a fork of it under your account, push
  branches to that fork, and open pull requests against upstream
  (classic-token equivalent: `public_repo`). Read-only usage needs only
  public repository read access.
- Tokens are **never** written to the configuration file, generated
  manifests, logs, or JSON output.

### Redaction

Tokens are wrapped in a type whose string form is always `[REDACTED]`, so a
token value cannot leak through logging, JSON serialization, or exception
messages by construction; equality checks are constant-time. On top of that,
a sanitizer scrubs the rule audit trail, manifest previews, and mutation JSON
output: GitHub token shapes (`ghp_…`, `github_pat_…`, …), bearer and basic
authorization values, JWTs, password- and API-key-style key/value
assignments, and URL user-info and query strings — including recursively
percent- or backslash-escaped variants. Sensitive manifest field paths (for
example installer switches, which can embed passwords) are redacted wholesale
in audit output.

Redaction of free-form text is defense in depth, not an absolute guarantee:
do not put secrets into installer URLs, manifest fields, or command
arguments in the first place.

## Untrusted input bounds

- **Installer binaries** are parsed statically with hard limits on entry
  counts, sizes, nesting depth, and section/resource/stream sizes; declared
  sizes are verified against actual bytes (zip-bomb defense). Installers are
  never executed. See the [analyzer guide](analyzers.md#limits-and-safety).
- **Override packs** are parsed strictly: unknown keys rejected, no YAML
  aliases, exactly one document, bounded size, depth, node count, and scalar
  length. Manifests and the config file are parsed with a conventional YAML
  reader and validated structurally afterwards; treat override packs and
  config files as trusted local input.
- **Downloads** are hash-verified; a payload that no longer matches its
  recorded hash is treated as changed upstream content and re-fetched, and
  byte changes behind a stable URL block submission unless explicitly
  approved (`--allow-stable-url-change`).

## Mutation guarantees

- `--dry-run` never mutates: no manifest writes (beyond the local download
  cache), no GitHub calls that change state.
- Manifest mutation commands change GitHub state only with explicit
  `--submit`; maintenance commands (`sync`, `complete --apply-safe`) plan
  first and mutate only after explicit confirmation. Every destructive or
  account-shaping step (fork creation, branch push, PR creation) requires
  confirmation; confirmation never defaults to yes and must be given as
  `--yes` in non-interactive or JSON sessions.
- Interrupted submissions report exactly which steps completed
  (`remote.state` in JSON, including `outcomeUncertain`) instead of
  pretending success or failure.
- `sync` never force-updates user commits; `cleanup` never touches branches
  outside the tool's own prefix; `complete --apply-safe` posts only fixed
  keep-alive comments, never arbitrary content.

## What this tool does *not* protect against

For honesty's sake: winmatsch validates integrity, not trustworthiness. It
does not sandbox-execute installers, does not scan for malware, and cannot
verify that a publisher's URL serves benign content. Review what you submit.

## Reporting vulnerabilities

Please report suspected vulnerabilities privately via
[GitHub security advisories](https://github.com/b0t-at/winmatsch/security/advisories/new)
rather than public issues. See [SECURITY.md](../SECURITY.md).
