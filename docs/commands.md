# Command reference

This reference matches the `--help` output of the built executable. When in
doubt, `winmatsch <command> --help` is the source of truth; the CLI test
suite exercises every command's help output to keep the surface stable.

```text
winmatsch [command] [options]
```

- [Global options](#global-options)
- [Environment variables](#environment-variables)
- [Exit codes](#exit-codes)
- [Output streams and the JSON contract](#output-streams-and-the-json-contract)
- [Non-interactive and CI behavior](#non-interactive-and-ci-behavior)
- [Read-only commands](#read-only-commands): [`analyze`](#analyze), [`validate`](#validate), [`show`](#show), [`list-versions`](#list-versions)
- [Mutation commands](#mutation-commands): [`new`](#new), [`update`](#update), [`remove`](#remove), [`submit`](#submit), [`new-locale`](#new-locale), [`update-locale`](#update-locale)
- [Repository maintenance](#repository-maintenance): [`sync`](#sync), [`cleanup`](#cleanup), [`complete`](#complete), [`remove-dead-versions` (hidden)](#remove-dead-versions-hidden)
- [Local maintenance](#local-maintenance): [`token`](#token), [`config`](#config), [`cache`](#cache), [`completion`](#completion)

## Global options

Every command accepts these recursive options. Each maps to the top layer of
the configuration precedence chain
(`command > environment > user file > defaults`); an option left unset falls
through to the next layer. See the
[configuration reference](configuration.md).

| Option | Value | Description |
|---|---|---|
| `--repo` | `owner/name` | Target repository (default: `microsoft/winget-pkgs`). |
| `--format` | `text` \| `json` | Result format on stdout. JSON output never prompts. |
| `--output` | directory | Where generated manifests and reports are written. |
| `--concurrent-downloads` | count | Maximum parallel installer downloads (default: 2). |
| `--dry-run` | flag | Plan mode: validate and show what would change without mutating anything. |
| `--interaction` | `auto` \| `always` \| `never` | Prompting policy (default: `auto`). |
| `--no-color` | flag | Disable ANSI color (the `NO_COLOR` environment variable is also honored). |
| `--config` | file | Path to the user configuration file (default: `~/.config/winmatsch/config.yaml`). |
| `--token` | token | GitHub token. Precedence: `--token` > `GITHUB_TOKEN` > OS keyring. Never echoed. Prefer `token add --stdin` or `GITHUB_TOKEN` over passing a secret as a process argument. |
| `-?`, `-h`, `--help` | flag | Show help. |
| `--version` | flag | Show the version (root command only), e.g. `0.1.0+<commit sha>`. |

### `--dry-run`

`--dry-run` is honored by every command that could change anything: mutation
commands stop after local planning and validation, `sync`/`cleanup`/`complete`
report their plan without applying it, and `cache clear`/`cache prune` list
what would be removed. Exit code 0 means the plan is valid.

## Environment variables

| Variable | Effect |
|---|---|
| `WINMATSCH_REPOSITORY` | Configuration: target repository (`owner/name`). |
| `WINMATSCH_CONCURRENT_DOWNLOADS` | Configuration: max parallel downloads (integer ≥ 1). |
| `WINMATSCH_RULES_ENABLED` | Configuration: comma-separated rule IDs forced to *apply*. |
| `WINMATSCH_RULES_DISABLED` | Configuration: comma-separated rule IDs forced to *disabled*. |
| `WINMATSCH_CACHE_ENABLED` | Configuration: `true`/`false`, enable the download cache. |
| `WINMATSCH_CACHE_DIRECTORY` | Configuration: custom cache directory. |
| `WINMATSCH_FRESHNESS_DELAY` | Configuration: `d.hh:mm:ss` or `hh:mm:ss` minimum release age before submission. |
| `WINMATSCH_OUTPUT_FORMAT` | Configuration: `text` or `json`. |
| `WINMATSCH_OUTPUT_DIRECTORY` | Configuration: output directory. |
| `WINMATSCH_INTERACTION` | Configuration: `auto`, `always`, or `never`. |
| `GITHUB_TOKEN` | GitHub token; used when `--token` is not given, before the OS keyring. |
| `NO_COLOR` | Any non-empty value disables ANSI color ([no-color.org](https://no-color.org)). |
| `CI`, `GITHUB_ACTIONS`, `TF_BUILD` | Truthy values (`1`, `true`, `yes`) mark the session as CI: `auto` interaction stops prompting. |
| `XDG_CONFIG_HOME` | Overrides the base directory of the default config path. |

Empty or whitespace-only `WINMATSCH_*` values are treated as unset. A
malformed value (for example a non-numeric `WINMATSCH_CONCURRENT_DOWNLOADS`)
fails the invocation with exit code 3.

## Exit codes

The exit code contract is deterministic; scripts can branch on the category
without parsing output.

| Code | Meaning |
|---|---|
| `0` | Success. In plan mode (`--dry-run`) this means the plan is valid. |
| `1` | Unexpected internal error (a bug or unclassified failure). Message on stderr. |
| `2` | Usage error: unknown command/option, malformed option value. |
| `3` | Configuration error: malformed `WINMATSCH_*` variable, unreadable or invalid config file, contradictory settings. |
| `4` | Missing input: required input was not provided and prompting is unavailable (non-interactive session, `--interaction never`, or JSON output). |
| `5` | Operation failed for a domain reason: validation failed, resource not found, remote rejection. |
| `130` | Cancelled (Ctrl+C), following the POSIX 128+SIGINT convention. |

## Output streams and the JSON contract

The stream contract is stable and scripts may rely on it:

- **stdout** carries the result only: human-readable text in text mode, or
  exactly one JSON document in JSON mode. Nothing else is ever written there.
- **stderr** carries diagnostics, warnings, progress, prompts, and error
  messages as plain text in both formats.

JSON documents are compact (not indented), UTF-8 with non-ASCII preserved,
byte-stable for identical inputs, and terminated by a single trailing newline.
There is currently **no explicit version field** inside the JSON envelope;
the shape below is the contract for the 0.x series, and any breaking change
to it is treated as a breaking release. Fields may be *added* in minor
releases; consumers should ignore unknown fields.

Mutation commands (`new`, `update`, `remove`, `submit`, `new-locale`,
`update-locale`) emit this envelope:

```jsonc
{
  "operation": "update",              // kebab-case operation name
  "packageIdentifier": "…",
  "packageVersion": "…",
  "result": "…",                      // kebab-case result state
  "applied": false,                   // true only when something was mutated
  "warning": "…",                     // present only when there is a warning
  "outputDirectory": "…",
  "requiresReview": false,            // true => human-correction review needed
  "changes":   [ { "kind": "…", "path": "…" } ],
  "questions": [ { "code": "…", "prompt": "…", "path": null, "options": [] } ],
  "rules": {
    "executions": [ { "id": "WM0001", "mode": "apply", "source": "default" } ],
    "changes":    [ { "id": "…", "path": "…", "field": "…", "before": "…", "after": "…" } ],
    "reviews":    [ { "path": "…", "field": "…", "human": "…", "generated": "…" } ]
  },
  "validation": [ { "code": "…", "severity": "error", "message": "…", "path": null } ],
  "preview":    [ { "path": "…", "content": "…" } ],   // secrets redacted
  "remote": {                          // null unless --submit
    "result": "…",
    "applied": true,
    "operations": [ { "kind": "…", "target": "…", "description": "…" } ],
    "state": {
      "fork": "…", "branch": "…", "commitSha": "…",
      "pullRequestNumber": 123, "pullRequestUrl": "…",
      "forkCreated": true, "branchCreated": true, "commitCreated": true,
      "pullRequestCreated": true,
      "outcomeUncertain": false        // true => partial remote state, see below
    },
    "diagnostics": [ { "code": "…", "message": "…", "path": "…" } ]
  }
}
```

Free-form values in the document (messages, previews, before/after values)
pass a secret-scrubbing sanitizer that replaces recognizable secrets — GitHub
token shapes, authorization header values, password- and API-key-style
assignments, URL user-info and query strings — with `[REDACTED]`. This is
defense in depth; do not put secrets into URLs or manifest fields.

### Approvals, review-required, and partial remote states

- **Approvals.** Destructive or account-shaping actions (fork creation,
  branch push, PR creation, structural rewrites) require approval. In an
  interactive terminal you are prompted; otherwise pass the specific approval
  flag (`--yes`, `--allow-structural-rewrite`, `--allow-stable-url-change`,
  `--allow-shared-content`). Confirmation never defaults to yes.
- **Review required.** When the tool detects that a human edited a previously
  merged manifest in a way a new run would revert, it sets
  `requiresReview: true` and lists the conflicts under `rules.reviews`
  instead of silently overwriting the human's work.
- **Partial remote state.** Remote submission is a sequence (fork → branch →
  commit → pull request). If it is interrupted, the `remote.state` object
  reports exactly which steps completed and `outcomeUncertain: true` when the
  tool could not verify the final state. Re-running is safe: completed steps
  are detected and not repeated blindly, and the duplicate-PR preflight
  prevents accidental double submissions (`--skip-pr-check` disables only
  that early check).

## Non-interactive and CI behavior

With `--interaction auto` (the default), prompting is disabled whenever a CI
environment is detected (`CI`, `GITHUB_ACTIONS`, or `TF_BUILD` set to
`1`/`true`/`yes`) or stdin/stderr is redirected; `--interaction never` and
`--format json` disable prompting unconditionally. In a session that cannot
prompt:

- missing required input fails with exit code 4 instead of hanging;
- confirmations must be given explicitly with `--yes` — they never default
  to yes;
- prompts would render on stderr anyway, so stdout stays parseable.

See [interaction modes](configuration.md#interaction-modes) for the full
decision rules.

## Read-only commands

These commands never write to any repository. `analyze` and `validate` may
download installers into the local cache.

### analyze

```text
winmatsch analyze <source> [options]
```

Analyze an installer without generating or changing manifests. `<source>` is
a local installer path or HTTPS URL. Reports the detected format, extracted
metadata (product name/version/publisher, architecture, product codes,
elevation, …) and the evidence each value came from. See the
[analyzer guide](analyzers.md).

### validate

```text
winmatsch validate <paths>... [options]
```

Validate a local multi-file manifest set (a manifest directory, or one or
more YAML manifest files) without changing files or repositories.

| Option | Description |
|---|---|
| `--offline` | Skip optional live metadata checks; mandatory origin/hash validation remains blocking. |
| `--warnings-as-errors` | Treat validation warnings as blocking findings. |

Exit code 5 when validation fails.

### show

```text
winmatsch show <package> <version> [options]
```

Read one exact package version from the configured repository. Package
identifier and version must match repository casing exactly.

| Option | Description |
|---|---|
| `--raw` | Display repository bytes instead of normalized readable YAML. |

### list-versions

```text
winmatsch list-versions <package> [options]
```

List versions of one package from the configured repository.

| Option | Description |
|---|---|
| `--skip <count>` | Number of newest versions to skip (default: 0). |
| `--limit <count>` | Maximum versions to return (default: 100). |

## Mutation commands

All mutation commands share a lifecycle: **discover → download → analyze →
generate → rules → validate → (optionally) submit**. Without `--submit` they
stop after writing validated manifests to the output directory. With
`--dry-run` they stop after planning and validation, writing nothing except
cache entries.

### Shared mutation options

| Option | Description |
|---|---|
| `--submit` | Submit the validated local plan through the GitHub lifecycle workflow (fork → branch → commit → PR). |
| `--open-pr` | Open the created pull request in the default browser. |
| `--resolves <ref>` | Issue reference included in the pull request body. |
| `--created-with <name>` | Tool name written to generated manifest headers. |
| `--created-with-url <url>` | Tool HTTP(S) URL written with generated manifest provenance. |
| `--skip-pr-check` | Skip only the early duplicate pull-request check. |
| `--prtitle <title>` | Custom pull-request title. |
| `--yes` | Explicitly approve destructive actions, fork creation, and submission. |
| `--edit` | Edit an isolated temporary manifest copy and rerun full preflight. |
| `--warnings-as-errors` | Treat validation warnings as blocking. |
| `--default-rule-mode <mode>` | Default rule mode: `apply`, `log-only`, or `disabled`. |
| `--rule-mode <RULE_ID=mode>` | Per-rule override, repeatable. Highest-precedence rule setting. |
| `--explain-rules` | Include deterministic rule trace output. |
| `--override-pack <file>` | Path to a package override-pack YAML file. See [rules guide](rules.md). |

`new` and `update` additionally accept installer selection options:

| Option | Description |
|---|---|
| `--version <version>` | Target package version. |
| `--release <tag>` | Release tag or name to discover. |
| `--urls <urls>` | Installer HTTP(S) URLs. |
| `--release-url <urls>` | Release metadata HTTP(S) URLs. |
| `--url <spec>` | Installer override in `url\|arch\|scope\|displayVersion` form. |
| `--allow-structural-rewrite` | Approve an installer architecture/type/scope layout rewrite. |
| `--allow-stable-url-change` | Approve changed bytes behind a stable installer URL. |
| `--allow-shared-content` | Approve distinct installer URLs resolving to identical bytes. |

`new`, `new-locale`, and `update-locale` additionally accept locale metadata
options: `--locale`, `--publisher`, `--publisher-url`,
`--publisher-support-url`, `--privacy-url`, `--author`, `--package-name`,
`--package-url`, `--license`, `--license-url`, `--copyright`,
`--copyright-url`, `--short-description`, `--description`, `--tags`,
`--release-notes`, `--release-notes-url`.

### new

```text
winmatsch new [<package>] [options]
```

Create and validate a new package version. Prompts for missing inputs in
interactive sessions; in non-interactive sessions missing input fails with
exit code 4.

### update

```text
winmatsch update [<package> [<version>]] [options]
```

Update an existing exact package version. Carries hand-maintained fields
forward from the previous version (rule `WM0007`) and templates
version-bearing ARP fields (`ARP-1`).

| Extra option | Description |
|---|---|
| `--replace <version>` | Replace the previous version, optionally naming its exact version. |

### remove

```text
winmatsch remove [<package> [<version>]] [options]
```

Remove one exact package version. Destructive: requires approval (`--yes` in
non-interactive sessions).

### submit

```text
winmatsch submit [<path>] [options]
```

Validate and optionally submit existing raw manifests (a manifest file or
directory you already have on disk).

| Extra option | Description |
|---|---|
| `--normalize` | Explicitly normalize the raw manifest set before submission. |

### new-locale

```text
winmatsch new-locale [<package> [<version> [<locale>]]] [options]
```

Create one exact locale manifest (`<locale>` uses exact BCP-47 casing, e.g.
`de-DE`).

### update-locale

```text
winmatsch update-locale [<package> [<version> [<locale>]]] [options]
```

Update one exact locale manifest.

## Repository maintenance

These commands operate on your fork and your tool-created pull requests.

### sync

```text
winmatsch sync [options]
```

Synchronize the fork's default branch with the upstream repository. Plans
first; applying requires confirmation and never force-updates user commits.

| Option | Description |
|---|---|
| `--fork <owner/name>` | Fork repository (default: `<authenticated user>/<upstream name>`). |
| `--yes` | Confirm mutating actions without prompting. Required in non-interactive and JSON sessions. |

### cleanup

```text
winmatsch cleanup [options]
```

Inspect stale tool-created branches whose pull requests are closed. GitHub
offers no atomic expected-SHA branch delete, so candidates are rendered for
manual escalation; unknown or user branches are never touched.

| Option | Description |
|---|---|
| `--fork <owner/name>` | Fork repository (default: `<authenticated user>/<upstream name>`). |
| `--branch-prefix <prefix>` | Tool branch prefix considered for cleanup (default: `winmatsch/`). Branches outside this prefix are never candidates. |
| `--yes` | Confirm mutating actions without prompting. |

### complete

```text
winmatsch complete [options]
```

Inspect the lifecycle of open tool-created pull requests and report the
recommended action for each.

| Option | Description |
|---|---|
| `--fork <owner/name>` | Fork repository (default: `<authenticated user>/<upstream name>`). |
| `--apply-safe` | After inspection, apply only known-safe responses (fixed keep-alive comments for transient infrastructure failures). Requires confirmation; never posts arbitrary comments and never repairs manifests. |
| `--yes` | Confirm mutating actions without prompting. |

### remove-dead-versions (hidden)

```text
winmatsch remove-dead-versions <package> <versions>... [--yes]
```

Hidden command (not listed in `--help`, and `remove-dead-versions --help`
prints nothing). Plans the removal of package versions whose installers are
no longer downloadable. It inspects each version, reports removability and
diagnostics, and escalates to a human when the state is uncertain — it does
**not** submit pull requests itself. Hidden because the workflow is
deliberately incomplete and its dead-installer probes are expensive.

## Local maintenance

### token

Manage the GitHub token in the OS keyring. See the
[security guide](security.md) for storage adapters per platform.

| Subcommand | Description |
|---|---|
| `token add [--stdin]` | Validate a token and store it in the OS keyring. `--stdin` reads the first line of standard input (recommended; keeps the secret out of the argument list and shell history). Alternatively supply the global `--token`. |
| `token remove` | Remove the stored token. Succeeds even when no token is stored. |
| `token status` | Show whether a token is stored. The token value is never shown. |

### config

Inspect and edit the user configuration file. See the
[configuration reference](configuration.md) for keys and semantics.

| Subcommand | Description |
|---|---|
| `config show` | Show the effective configuration and where each value comes from (`command > environment > user file > default`). Tokens are never configuration. |
| `config set <key> <value>` | Set one key in the user configuration file (atomic write). Value is validated with the same rules the CLI applies at startup. |
| `config unset <key>` | Remove one key. Succeeds when the key is absent. |
| `config path` | Print the user configuration file path in effect for this invocation. |

Valid keys: `repository`, `concurrentDownloads`, `rules.enabled`,
`rules.disabled`, `cache.enabled`, `cache.directory`, `freshnessDelay`,
`output.format`, `output.directory`, `interaction`.

### cache

Inspect and maintain the persistent download cache. See
[configuration](configuration.md#cache) for locations and limits.

| Subcommand | Description |
|---|---|
| `cache list` | List all entries with integrity state, size, and lifetime information. |
| `cache inspect <url>` | Details of the entry for one exact installer URL. |
| `cache clear [<url>] [--yes]` | Remove one entry, or the whole cache. Destructive; supports `--dry-run`. |
| `cache prune [--yes]` | Remove stale and corrupt entries, keeping fresh ones. Destructive; supports `--dry-run`. |

### completion

```text
winmatsch completion <bash|fish|powershell|zsh>
```

Write a static shell completion script to standard output, e.g.:

```bash
winmatsch completion bash > /etc/bash_completion.d/winmatsch
winmatsch completion zsh  > "${fpath[1]}/_winmatsch"
```
