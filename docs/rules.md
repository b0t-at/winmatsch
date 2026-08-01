# Rules and overrides

winmatsch shapes every generated manifest with a deterministic rule pipeline.
Each rule has a stable ID, a category, and a mode; every change a rule makes
is recorded with a before/after audit trail and the evidence it was based on.

## Rule modes and precedence

Every rule runs in one of three modes:

| Mode | Effect |
|---|---|
| `apply` | The rule mutates the manifests (or reports findings, for validation rules). |
| `log-only` | The rule runs on a **clone**; its would-be changes and findings are reported, but the real manifests are untouched. |
| `disabled` | The rule is skipped entirely. |

The mode of each rule is resolved per invocation, highest precedence first:

1. **Command line** — `--rule-mode RULE_ID=apply|log-only|disabled` (repeatable).
2. **Override pack** — the `rules:` map of a package override pack.
3. **User configuration** — `rules.enabled` (forces *apply*) and
   `rules.disabled` (forces *disabled*) in the config file or the
   `WINMATSCH_RULES_ENABLED` / `WINMATSCH_RULES_DISABLED` environment
   variables.
4. **Default** — `--default-rule-mode` (itself defaulting to `apply`).

The resolved mode and its source are recorded for every rule in every run
(`rules.executions` in JSON output), so a manifest can always be explained.

## Audit trail

Pass `--explain-rules` to include the deterministic rule trace. The output
(text, or the `rules` object in JSON) contains:

- **executions** — every rule with its resolved mode and mode source;
- **changes** — every field mutation: rule ID, manifest path, field path,
  before value, after value, source evidence, and a confidence level
  (`high` when backed by installer analysis or explicit rule evidence,
  `medium` when derived from a raw download only);
- **findings** — validation results with severity, message, and path;
- **trace** — per-rule explanatory messages;
- **reviews** — human-correction conflicts (see below).

Values in the audit trail pass through the same redaction as all other
output: secrets, credential-shaped strings, and sensitive field paths (for
example installer switches that could embed passwords) are shown as
`[REDACTED]`, and URLs are stripped of user-info and query strings.

## Rule catalogue

The IDs below are the complete set of implemented rules. IDs are stable and
form the ownership boundary: a rule may only touch the manifest fields its
catalogue entry describes.

### Normalization and quirk rules

Run first, in a fixed order, to produce a canonical manifest shape
(IDs `WM00xx`, plus the quirk rule `WM0201`).

| ID | Purpose |
|---|---|
| `WM0007` | Preserve hand-maintained fields (switches, dependencies, locale URLs) from the previous version on update. Runs first so carry-over sees the original shape. |
| `WM0201` | Apply data-driven per-package quirks from override packs (see [quirks](#quirks)). |
| `WM0202` | Apply validated scope layout, exact metadata URL rewrites, preserved/dropped fields, and explicitly approved learned fields. |
| `WM0002` | Push root-level installer fields down to installers when per-installer conflicts exist. |
| `WM0004` | Scrub junk: null out empty strings, remove empty lists, prune empty composite objects. |
| `WM0006` | Normalize GUID product/upgrade codes to canonical uppercase-in-braces form. |
| `WM0003` | Drop ARP (Apps & Features) entries that merely duplicate the default locale name/publisher or the package version. |
| `WM0005` | Remove installers that are exact duplicates on effective architecture + type + scope + URL. |
| `WM0001` | Hoist installer fields shared by every installer up to the manifest root. Runs last so it sees the final shape. |

### Policy rules

Policy rules encode `winget-pkgs` community conventions. They run after
normalization, observing the canonical shape.

| ID | Purpose |
|---|---|
| `ARP-1` | Template old version tokens in ARP DisplayName/DisplayVersion on updates; prefers installer evidence; warns on unreplaceable tokens. |
| `ARP-2` | Remove ARP DisplayVersion values that equal PackageVersion or are already declared elsewhere. |
| `ARP-3` | Drop garbage from ARP/installation metadata: unexpanded `%VAR%` variables, `ms-resource:` references, non-printable characters, temp paths. |
| `ARP-4` | Require ARP entry shape parity with the previously merged version, or an explicit override annotation. |
| `SCOPE-1` | Assign per-installer scope to same-URL switch twins (`/CURRENTUSER` vs `/ALLUSERS`), keeping the root scope null. |
| `SCOPE-2` | Set installer scope from trusted installer-metadata evidence only — never guessed. |
| `SCOPE-3` | Trim or drop blank installer switches; flag switches carried across installer-family changes. |
| `SCOPE-4` | Correct `msi`/`wix` classifications to the analyzer's detected outer wrapper. |
| `META-1` | Upgrade `http://` metadata URLs to `https://` when a workflow probe confirmed the HTTPS variant. |
| `META-3` | Normalize GitHub license/copyright URLs to their stable `blob/HEAD` form. |
| `META-4` | Sanitize release-notes formatting, bound their length, verify the ReleaseNotesUrl refers to the version. |
| `META-5` | Carry still-valid locale fields forward from the previous version, or require an explicit drop override. |
| `DEP-1` | Add architecture-matched runtime dependencies discovered from payload analysis evidence. |
| `DEP-2` | Classify dependency-outage pipeline signatures as infrastructure issues rather than manifest errors. |
| `PIPE-1` | Assert serializer invariants: LF-only line endings, single trailing newline. |
| `PIPE-2` | Pin all manifests of a version to a single manifest schema version. |
| `PIPE-3` | Enforce exact-casing package identity across versions; verify complete MSIX identity. |
| `PIPE-4` | Set `ArchiveBinariesDependOnPath` from sibling-import evidence only. |
| `PIPE-5` | Apply override-pack content-policy annotations (blocked types, network blocks, Defender risk). |

Further catalogue IDs (`ARCH-*`, `MAP-*`, `DUP-*`, `HASH-*`, `VER-*`,
`WORK-*`, `META-2`) are reserved in the catalogue but not yet implemented as
standalone rules; do not reference them in overrides.

### Validation rules (`WM01xx`)

Run last; they never mutate, they only report findings.

| ID | Severity | Purpose |
|---|---|---|
| `WM0101` | warning | ARP DisplayVersion inconsistent across installers, or redundantly equal to PackageVersion. |
| `WM0102` | error | Two installers collide on architecture + type + scope. |
| `WM0103` | error | `NestedInstallerType` without a zip installer type, or `NestedInstallerFiles` without `NestedInstallerType`. |

Findings of severity `error` fail the run (exit code 5); warnings fail it
only under `--warnings-as-errors`.

## Override packs

An override pack is a per-package YAML file carrying corrections that
evidence alone cannot supply. Pass one with `--override-pack <file>`;
built-in packs ship with the tool (currently `Google.Chrome`) and apply
automatically to their package.

### Schema

Top-level keys (`formatVersion` and `packageIdentifier` are required, the
rest optional):

```yaml
formatVersion: 1                     # must be 1
packageIdentifier: "Publisher.App"
rules:                               # per-rule mode overrides
  META-4: log-only
forcedArchitectures:                 # architecture corrections by asset pattern
  - assetPattern: "*-win64.exe"
    architecture: x64
    sourceEvidence: "vendor names 64-bit assets '-win64'"
assetMappings:                       # asset → installer entry mapping
  - assetPattern: "*.msixbundle"
    entry: primary
scopeLayout: Preserve                # Preserve | Root | PerInstaller
versionSource: "…"                   # version detection override
metadataUrlReplacements:             # exact metadata-field URL rewrites; targets must be safe HTTPS
  "http://old.example/": "https://new.example/"
preservedFields:                     # preserve prior merged values when generation drops them
  - "DefaultLocale.ReleaseNotesUrl"
  - "Installers[*].InstallerSwitches"
droppedFields: []                    # explicit drops win over preservation
vanityUrls: []                       # URLs that redirect per release (vanity)
manualOnly: false                    # true => refuse automated submission
policies:                            # human-readable policy annotations
  - id: "ARP-1"
    annotation: "Chrome MSI ProductVersion is internal; use Comments for marketing DisplayVersion."
quirks:
  displayVersionFromEvidenceProperty: "Comments"
```

Parsing is strict and defensive: unknown keys are rejected, YAML aliases are
forbidden, exactly one document is allowed, and hard limits apply (file at
most 1,048,576 characters, nesting depth 32, 10,000 nodes, 65,536 characters
per scalar). A pack that fails validation fails the run — it is never
partially applied.

### Quirks

Quirks are data-driven per-package behaviors executed by rule `WM0201`.
Currently one quirk exists:

- `displayVersionFromEvidenceProperty` — the name of an installer-evidence
  property (for example the MSI `Comments` field) whose value is preferred
  as the ARP DisplayVersion. Used when a vendor's internal ProductVersion
  differs from the marketed version (the *vanity version* pattern).

`manualOnly: true` marks a package as unsuitable for automation: workflows
refuse to submit it and escalate to a human. `policies` entries attach
human-readable annotations to rule IDs, documenting *why* a package deviates.

## Human-correction review

Before overwriting anything, update workflows perform a three-way comparison:

1. the manifest the tool originally submitted,
2. the manifest actually merged upstream (which a human may have edited),
3. the manifest the current run would generate.

When a human changed a value and the new run would revert it, the run is
flagged `requiresReview` and each conflict is reported with the bot value,
the human value, and the newly generated value. The tool never silently reverts
a human correction. Interactive approval is bound to both the correction
fingerprints and a digest of the reviewed package/version, raw before/after
documents, file changes, installer artifacts, existing-version evidence, and
release provenance. Apply mode writes a separate per-user learned pack with
optimistic content locking, backup recovery, and before/after/source audit
hashes; Plan mode never writes. The approved human value is applied to the
current manifest transaction, while the pack remains inactive until manifest
and provenance commit. A durable journal completes or discards activation
idempotently after crashes. Later runs apply a learned value only while the
generator still emits the fingerprinted bot value, and compose that learned
layer beneath explicit `--override-pack` files. Unsupported, stale, or
ambiguous corrections remain review-visible instead of being learned with
false confidence. Checked-in built-in packs are never modified at runtime.

## Security and redaction

All rule output — findings, traces, change records, previews — passes through
a sanitizer that redacts GitHub token shapes, bearer/basic authorization
values, JWTs, password- and API-key-style key/value assignments,
and URL user-info/query strings, including recursively percent- or
backslash-escaped variants. Sensitive field paths (e.g. installer switches)
are redacted wholesale. See the [security guide](security.md).
