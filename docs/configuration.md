# Configuration reference

binmatch resolves every setting through a fixed precedence chain. Each key
falls through independently, so you can mix sources freely.

```
command line  >  environment (BINMATCH_*)  >  user YAML file  >  built-in defaults
```

`binmatch config show` prints the effective value of every key together with
the layer it came from.

**Tokens are never configuration.** There is no config key for the GitHub
token; see the [security guide](security.md).

GitHub endpoints are intentionally not stored in the YAML configuration file.
Use `--github-api-url` / `BINMATCH_GITHUB_API_URL` and, only when necessary,
`--github-graphql-url` / `BINMATCH_GITHUB_GRAPHQL_URL`. The GraphQL endpoint
must share the REST endpoint authority. See
[GitHub endpoints and GitHub Enterprise](commands.md#github-endpoints-and-github-enterprise).

## Keys

| Key | Type | Allowed values | Default | CLI option | Environment variable |
|---|---|---|---|---|---|
| `repository` | string | `owner/name` | `microsoft/winget-pkgs` | `--repo` | `BINMATCH_REPOSITORY` |
| `concurrentDownloads` | int | ≥ 1 | `2` | `--concurrent-downloads` | `BINMATCH_CONCURRENT_DOWNLOADS` |
| `rules.enabled` | string list | rule IDs | empty | — | `BINMATCH_RULES_ENABLED` (comma-separated) |
| `rules.disabled` | string list | rule IDs | empty | — | `BINMATCH_RULES_DISABLED` (comma-separated) |
| `cache.enabled` | bool | `true` / `false` | `true` | — | `BINMATCH_CACHE_ENABLED` |
| `cache.directory` | string | filesystem path | platform default (below) | — | `BINMATCH_CACHE_DIRECTORY` |
| `overrideStore` | string | filesystem path | platform default (`binmatch/overrides`) | `--override-store` | `BINMATCH_OVERRIDE_STORE_DIRECTORY` |
| `freshnessDelay` | timespan | `d.hh:mm:ss` / `hh:mm:ss`, ≥ 0 | `04:00:00` | — | `BINMATCH_FRESHNESS_DELAY` |
| `output.format` | enum | `text`, `json` | `text` | `--format` | `BINMATCH_OUTPUT_FORMAT` |
| `output.directory` | string | filesystem path | current directory | `--output` | `BINMATCH_OUTPUT_DIRECTORY` |
| `interaction` | enum | `auto`, `always`, `never` | `auto` | `--interaction` | `BINMATCH_INTERACTION` |

### Key semantics

- **`repository`** — the WinGet manifest repository that read and mutation
  commands target.
- **`concurrentDownloads`** — maximum number of installers downloaded in
  parallel.
- **`rules.enabled` / `rules.disabled`** — user-level rule overrides: listed
  rule IDs are forced to *apply* / *disabled* respectively, overriding the
  default rule mode but **not** command-line `--rule-mode` or an override
  pack's per-rule modes. See [rule mode precedence](rules.md#rule-modes-and-precedence).
- **`cache.enabled`** — turns the persistent download cache on or off.
- **`cache.directory`** — custom cache location.
- **`overrideStore`** — stores fingerprint-bound, explicitly approved learned
  override packs plus their crash-recovery journals. Pending packs remain
  inactive until the corresponding manifest/provenance transaction commits.
- **`freshnessDelay`** — how long a release must remain unchanged before a
  remote submission is allowed. `1.12:00:00` means 1 day 12 hours. Guards
  against submitting a release the publisher is still re-uploading. The
  shipped default is four hours; set `00:00:00` to opt out explicitly:

  ```bash
  binmatch config set freshnessDelay 00:00:00
  ```

  Independently of this delay, installer bytes are re-hashed immediately
  before submission, so a changed payload is detected even with the guard
  disabled — the delay only avoids opening a doomed pull request.
- **`output.format`** — default result format on stdout.
- **`output.directory`** — where generated manifests and reports are written.
- **`interaction`** — prompting policy; see
  [interaction modes](#interaction-modes).

## File location

The user configuration file is YAML, named `config.yaml`.

| OS | Default path |
|---|---|
| Linux / macOS | `$XDG_CONFIG_HOME/binmatch/config.yaml` if `XDG_CONFIG_HOME` is set, else `~/.config/binmatch/config.yaml` |
| Windows | `%XDG_CONFIG_HOME%\binmatch\config.yaml` if `XDG_CONFIG_HOME` is set (it is honored on every platform), else `%USERPROFILE%\.config\binmatch\config.yaml` |

`--config <file>` selects an explicit file; unlike the default path, an
explicit path **must exist** (otherwise the invocation fails). A missing
default file is fine and simply contributes nothing. `binmatch config path`
prints the path in effect.

## File format

```yaml
repository: "microsoft/winget-pkgs"
concurrentDownloads: 4
freshnessDelay: "1.00:00:00"
overrideStore: "/var/lib/binmatch/overrides"
interaction: "auto"
rules:
  enabled:
    - "META-1"
  disabled:
    - "WM0101"
cache:
  enabled: true
  directory: "/var/cache/binmatch"
output:
  format: "text"
  directory: "./manifests"
```

Prefer editing through `binmatch config set` / `unset`: values are validated
with the same rules the CLI applies at startup, and writes are **atomic** —
content goes to a temporary file (mode `rw-------` on Unix) which is then
renamed into place, so a crash can never leave a truncated config file.

```bash
binmatch config set concurrentDownloads 4
binmatch config set rules.disabled WM0101
binmatch config unset freshnessDelay
```

An unreadable or invalid configuration file (or malformed `BINMATCH_*`
variable) fails the invocation with exit code 3.

## Interaction modes

| Mode | Behavior |
|---|---|
| `auto` (default) | Prompt only on an interactive terminal. Prompting is disabled when a CI environment is detected (`CI`, `GITHUB_ACTIONS`, or `TF_BUILD` set to `1`/`true`/`yes`), or when stdin or stderr is redirected. |
| `always` | Always prompt, regardless of terminal state. |
| `never` | Never prompt; commands that need input fail with exit code 4. |

Two rules apply on top of the mode:

- `--format json` **never** prompts, whatever the interaction mode.
- Confirmation of mutating actions never defaults to yes; in sessions that
  cannot prompt you must pass `--yes` explicitly.

Prompts are rendered on **stderr**, keeping stdout clean for results.

## Cache

The persistent download cache stores installer payloads with integrity
metadata so repeated runs do not re-download identical bytes.

| OS | Default directory |
|---|---|
| Windows | `%LOCALAPPDATA%\binmatch\downloads` |
| Linux / macOS | `~/.local/share/binmatch/downloads` (.NET `LocalApplicationData`) |

Properties:

- Entries record the source URL, content hash, size, and lifetime; payloads
  are verified against their recorded hash before reuse, and mismatches are
  treated as a changed upstream file, never silently reused.
- Default retention: entries expire after 7 days (or earlier if the HTTP
  response declared a shorter freshness); the cache keeps at most 64 entries
  and 5 GB, evicting least-recently-used entries first.
- Concurrent access is safe across threads, instances, and processes (a lock
  file serializes writers).
- `binmatch cache list | inspect | clear | prune` manage the cache; `clear`
  and `prune` are destructive and honor `--dry-run` and `--yes`.
