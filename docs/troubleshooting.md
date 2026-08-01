# Troubleshooting

## Interpreting exit codes

| Code | Category | First things to check |
|---|---|---|
| `1` | Internal error | This is a bug; the message is on stderr. Please file an issue with the command line (redact secrets) and output. |
| `2` | Usage | `winmatsch <command> --help` — an option name or value is wrong. |
| `3` | Configuration | `winmatsch config show` reports every effective value and its source; check `WINMATSCH_*` variables and the YAML file for malformed values. |
| `4` | Missing input | The command needed input but could not prompt (CI, `--interaction never`, or `--format json`). Supply the value as an option/environment/config, or add `--yes` for confirmations. |
| `5` | Operation failed | Domain failure: validation findings, resource not found, remote rejection. Details are in the output (`validation` array in JSON). |
| `130` | Cancelled | Ctrl+C or propagated cancellation. |

## Validation failures

- Run `winmatsch validate <path> --format json` and inspect the `validation`
  array; each finding has a code, severity, message, and manifest path.
- Warnings block only under `--warnings-as-errors`.
- `--offline` skips optional live metadata checks (useful without network),
  but mandatory origin/hash validation remains blocking by design.

## Keyring unavailable

Symptoms: `token add` fails, or `token status` reports no storage backend.

- **Linux**: the keyring adapter shells out to `secret-tool` (libsecret).
  Install it (`apt install libsecret-tools` or distro equivalent) and ensure
  a Secret Service (GNOME Keyring / KWallet with the freedesktop bridge) is
  running. Headless sessions often have none — use the `GITHUB_TOKEN`
  environment variable instead; the resolver checks it before the keyring.
- **Windows / macOS**: Credential Manager / Keychain are effectively always
  present; errors surface the native OS status code.

## URL and network blockers

- Only HTTPS installer URLs are accepted where the option says HTTP(S) —
  plain-HTTP metadata URLs are upgraded by rule `META-1` only when probe
  evidence confirms the HTTPS variant.
- Corporate proxies: standard .NET proxy environment conventions apply
  (`HTTPS_PROXY`, …).
- A release that was republished moments ago can be guarded with
  `freshnessDelay` (see [configuration](configuration.md#key-semantics)) so
  submission waits until the release stops changing.

## "Installer bytes changed behind a stable URL"

The cache stores the hash of every downloaded payload. If the same URL later
serves different bytes, winmatsch refuses to proceed silently:

- If the vendor really replaced the file, re-run with
  `--allow-stable-url-change` to approve it.
- If two different URLs serve identical bytes, approve with
  `--allow-shared-content`.
- If the architecture/type/scope layout of the release changed, approve the
  rewrite with `--allow-structural-rewrite`.

## Review-required escalations

`requiresReview: true` (or the equivalent text output) means a human edited a
previously merged manifest and the current run would revert that edit. The
run stops on purpose. Options:

- accept the human value permanently: encode it in an
  [override pack](rules.md#override-packs) (`preservedFields`, `quirks`,
  `policies`), or
- change the responsible rule's mode (`--rule-mode RULE_ID=log-only`), or
- decide the regenerated value is right and re-run with the review resolved.

## Cache problems

```bash
winmatsch cache list            # entries with integrity + lifetime state
winmatsch cache inspect <url>   # one entry in detail
winmatsch cache prune --yes     # drop stale/corrupt entries
winmatsch cache clear --yes     # nuke everything
```

Cache corruption is self-healing: payloads failing their integrity check are
discarded and re-downloaded. To relocate the cache set `cache.directory`; to
bypass it entirely set `cache.enabled` to `false`.

## Unsupported installer formats

`analyze` classifies unknown executables as `GenericInstallerExe` or
`PortableExe` with PE metadata only. If the workflow cannot derive required
fields from evidence, supply them explicitly (`--url url`, optionally followed
by `|arch`, `|scope`, and `|displayVersion`) or add `forcedArchitectures` /
`assetMappings` in an override pack. See
[known non-goals](analyzers.md#known-unsupported-variants-and-non-goals).

## CI, non-interactive, and editor behavior

- With `CI`, `GITHUB_ACTIONS`, or `TF_BUILD` set (`1`/`true`/`yes`), `auto`
  interaction stops prompting; so do redirected stdin/stderr. Commands then
  fail with exit code 4 instead of hanging — pass `--yes` and the required
  options.
- `--format json` never prompts, regardless of interaction mode.
- `--edit` opens an isolated temporary copy of the manifests and reruns full
  preflight after the editor closes; in sessions that cannot prompt, `--edit`
  is unavailable.
- Color: `--no-color` or a non-empty `NO_COLOR` disables ANSI output;
  redirected streams are never colored.

## Still stuck?

Open an issue at <https://github.com/b0t-at/winmatsch/issues> with the
command (secrets redacted), the exit code, and stderr output. For suspected
security problems use [SECURITY.md](../SECURITY.md) instead.
