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

- approve the fingerprint-bound review interactively to stage a learned
  [override pack](rules.md#override-packs); it becomes active only after the
  manifest and provenance transaction commits, and a retained recovery journal
  finishes activation on the next apply run, or
- encode the decision manually in an override pack (`preservedFields`,
  `quirks`, `policies`), or
- change the responsible rule's mode (`--rule-mode RULE_ID=log-only`), or
- decide the regenerated value is right and re-run with the review resolved.

Non-interactive and JSON invocations never auto-approve a review: they emit the
full plan (including the review fingerprints) and then exit 4. Inspect the
output and rerun with `--approve-reviews` to approve only those listed
reviews. `--yes` does not cover this gate. Use `--override-store` (or
`overrideStore`) to relocate the learned-pack store.

## Interrupted local writes

A crash, power loss, or `kill` during a local write leaves a hidden
`.winmatsch-transaction-*` directory in the output/repository root. This is
expected and self-healing: the next apply run for the same package recovers it —
rolling the staged changes back, or forward when the manifests were already
committed — before it stages anything new. A plan run (`--dry-run`) never
recovers, because it never writes. See
[recovery journals](architecture.md#local-writes-transactions-and-recovery-journals).

- `Invalid transaction journal '<path>'` (or an unhandled `FormatException`
  from corrupt path data in it) means recovery refused to guess. Inspect the
  directory, restore what you need from its `backup/` folder, then remove the
  directory to unblock the next apply run.
- `Another local operation is already running for this package.` (exit 5,
  `Conflict`) means a second process holds the operation lock — which is
  per *package*, not per version — not a stale transaction.
- A retained learned-override journal in the override store is reported
  separately; re-running the apply for the same package finishes or discards
  the activation. See
  [learned corrections](rules.md#learned-corrections-approved-once-reapplied-later).

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

## Explicitly unsupported (not bugs)

These are current, deliberate boundaries — reporting them as defects will get
the issue closed as by-design:

| Not supported | Detail |
|---|---|
| Branch deletion during `cleanup` | GitHub has no atomic expected-SHA branch delete, so candidates are reported for manual action. |
| `remove-dead-versions` submitting pull requests | The hidden command plans and reports only. |
| Running installers, even sandboxed | Static analysis only; see [analyzer non-goals](analyzers.md#known-unsupported-variants-and-non-goals). |
| Signature/certificate validation | Out of scope; integrity is hash-based. |

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
