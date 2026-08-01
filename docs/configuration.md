# Configuration reference

winmatsch resolves every setting through a fixed precedence chain. Each key
falls through independently, so you can mix sources freely.

```
command line  >  environment (WINMATSCH_*)  >  user YAML file  >  built-in defaults
```

`winmatsch config show` prints the effective value of every key together with
the layer it came from.

**Tokens are never configuration.** There is no config key for the GitHub
token; see the [security guide](security.md).

## Keys

| Key | Type | Allowed values | Default | CLI option | Environment variable |
|---|---|---|---|---|---|
| `repository` | string | `owner/name` | `microsoft/winget-pkgs` | `--repo` | `WINMATSCH_REPOSITORY` |
| `concurrentDownloads` | int | ≥ 1 | `2` | `--concurrent-downloads` | `WINMATSCH_CONCURRENT_DOWNLOADS` |
| `rules.enabled` | string list | rule IDs | empty | — | `WINMATSCH_RULES_ENABLED` (comma-separated) |
| `rules.disabled` | string list | rule IDs | empty | — | `WINMATSCH_RULES_DISABLED` (comma-separated) |
| `cache.enabled` | bool | `true` / `false` | `true` | — | `WINMATSCH_CACHE_ENABLED` |
| `cache.directory` | string | filesystem path | platform default (below) | — | `WINMATSCH_CACHE_DIRECTORY` |
| `freshnessDelay` | timespan | `d.hh:mm:ss` / `hh:mm:ss`, ≥ 0 | `00:00:00` | — | `WINMATSCH_FRESHNESS_DELAY` |
| `output.format` | enum | `text`, `json` | `text` | `--format` | `WINMATSCH_OUTPUT_FORMAT` |
| `output.directory` | string | filesystem path | current directory | `--output` | `WINMATSCH_OUTPUT_DIRECTORY` |
| `interaction` | enum | `auto`, `always`, `never` | `auto` | `--interaction` | `WINMATSCH_INTERACTION` |

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
- **`freshnessDelay`** — how long a release must remain unchanged before a
  remote submission is allowed. `1.12:00:00` means 1 day 12 hours. Guards
  against submitting a release the publisher is still re-uploading.
- **`output.format`** — default result format on stdout.
- **`output.directory`** — where generated manifests and reports are written.
- **`interaction`** — prompting policy; see
  [interaction modes](#interaction-modes).

## File location

The user configuration file is YAML, named `config.yaml`.

| OS | Default path |
|---|---|
| Linux / macOS | `$XDG_CONFIG_HOME/winmatsch/config.yaml` if `XDG_CONFIG_HOME` is set, else `~/.config/winmatsch/config.yaml` |
| Windows | `%XDG_CONFIG_HOME%\winmatsch\config.yaml` if `XDG_CONFIG_HOME` is set (it is honored on every platform), else `%USERPROFILE%\.config\winmatsch\config.yaml` |

`--config <file>` selects an explicit file; unlike the default path, an
explicit path **must exist** (otherwise the invocation fails). A missing
default file is fine and simply contributes nothing. `winmatsch config path`
prints the path in effect.

## File format

```yaml
repository: "microsoft/winget-pkgs"
concurrentDownloads: 4
freshnessDelay: "1.00:00:00"
interaction: "auto"
rules:
  enabled:
    - "META-1"
  disabled:
    - "WM0101"
cache:
  enabled: true
  directory: "/var/cache/winmatsch"
output:
  format: "text"
  directory: "./manifests"
```

Prefer editing through `winmatsch config set` / `unset`: values are validated
with the same rules the CLI applies at startup, and writes are **atomic** —
content goes to a temporary file (mode `rw-------` on Unix) which is then
renamed into place, so a crash can never leave a truncated config file.

```bash
winmatsch config set concurrentDownloads 4
winmatsch config set rules.disabled WM0101
winmatsch config unset freshnessDelay
```

An unreadable or invalid configuration file (or malformed `WINMATSCH_*`
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
| Windows | `%LOCALAPPDATA%\winmatsch\downloads` |
| Linux / macOS | `~/.local/share/winmatsch/downloads` (.NET `LocalApplicationData`) |

Properties:

- Entries record the source URL, content hash, size, and lifetime; payloads
  are verified against their recorded hash before reuse, and mismatches are
  treated as a changed upstream file, never silently reused.
- Default retention: entries expire after 7 days (or earlier if the HTTP
  response declared a shorter freshness); the cache keeps at most 64 entries
  and 5 GB, evicting least-recently-used entries first.
- Concurrent access is safe across threads, instances, and processes (a lock
  file serializes writers).
- `winmatsch cache list | inspect | clear | prune` manage the cache; `clear`
  and `prune` are destructive and honor `--dry-run` and `--yes`.
