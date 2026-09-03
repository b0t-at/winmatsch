# winget-pkgs bot PR audit — 6 Aug to 3 Sep 2026

> Retrospective of the pull requests the `damn-good-b0t` account opened in
> `microsoft/winget-pkgs`, what the upstream validator rejected, and which of
> those rejections winmatsch could detect or prevent before submission.
> Historical record; not normative product documentation.

## Scope and method

- Bot account: `damn-good-b0t`. State on 3 Sep 2026: 491 closed-unmerged PRs,
  72 open PRs.
- Sample read in detail: the 100 most recently closed-unmerged PRs (6 Aug to
  2 Sep), all 72 open PRs, the 6 most recently merged PRs, ~45 individual PR
  conversations and diffs, the previously merged manifest of every package
  whose update failed, and the upstream artifacts of the failing packages
  (OmniConsole MSIX, jwtop / masterclass-dl / Oxipng zips, SODA-for-SPARC and
  EdgeTX installers, Aria2Next exe).
- Upstream references: `doc/ValidationFailureGuide.md`, `doc/Validation.md`,
  the label catalogue, the moderators' `ManualValidationPipeline.json`, and
  `.gitattributes` of winget-pkgs.
- winmatsch source at `1f84062` (v0.8.22).
- The GitHub API was not reachable from this session; everything comes from
  public github.com pages, so counts are exact for the sample and approximate
  for the whole population.

## Numbers

Closed-unmerged sample (100 PRs, 6 Aug to 2 Sep):

| Failure class | PRs |
|---|---|
| `Validation-Defender-Error` (post-install Defender hit) | 29 |
| Duplicate of another PR / wrong identifier | 20 |
| `Validation-Executable-Error` with `Azure-Pipeline-Passed` (manual validation queue; closed by the bot as superseded) | 19 |
| `URL-Validation-Error` | 14 |
| `Validation-Certificate-Root` (one package, 8bit2qubit.OmniConsole) | 6 |
| `Validation-No-Executables` | 4 |
| `Validation-Installation-Error` | 3 |
| `Validation-Unattended-Failed` | 2 |
| `Error-Hash-Mismatch`, `Binary-Validation-Error`, internal scan error | 1 each |

Open (all 72 on 3 Sep):

| Failure class | PRs |
|---|---|
| `Validation-Executable-Error` with `Azure-Pipeline-Passed` | 38 |
| `Validation-Defender-Error` | 12 |
| `URL-Validation-Error` | 4 |
| `Binary-Validation-Error` (ESRP scanner hit) | 4 |
| `Validation-Unattended-Failed` | 4 |
| `Validation-No-Executables` | 3 |
| `Validation-Installation-Error`, `Validation-Shell-Execute`, internal errors | 2 each |
| `Validation-Certificate-Root` | 1 |

Of the 100 closed PRs, roughly 60 were closed by the bot itself with
"Superseded by #N", not by a moderator. The next version was then submitted
with the same defect.

## Cross-cutting findings

### A. No memory of failed submissions, so identical PRs loop

The same manifest defect was resubmitted again and again:

| Package | Repeats | Same defect every time |
|---|---|---|
| yhay81.sqrail 0.3.4 | 4 (#418427, #420304, #423403, #426592) | `PublisherUrl` host `sqrails.yhay81.com` does not resolve (NXDOMAIN) |
| Luxview.AgainstMe 0.5.127 | 3 (#420298, #422832, #426541) | `againstme.app`, `/privacy`, `/eula` all 404 |
| OmniEdge.OmniEdgeCLI 2.9.0 | 2 (#420882, #423499, #426112 open) | five `connect.omniedge.io` URLs: DNS name exists, no address record |
| BenModigell.Vibes 1.4.2 to 1.4.5 | 4 | `vibesdj.io` answers 429 to the validator |
| 8bit2qubit.OmniConsole 4.2.1.0 to 4.4.3.0 | 7 | MSIX signed with a self-signed certificate |
| Thurbeen.thurbox 2.4.1 to 2.11.1 | 7 | Defender hit on the portable zip |
| mikf.gallery-dl.Nightly | 9 in 14 days | Defender hit; five other nightlies merged in the same window |
| fairdataihub.SODA-for-SPARC 19.0.0 to 19.0.2 | 3 | installer blocks on user input |
| RythenGlyth.masterclass-dl 1.1.0 | 2 (#418388, #420886) | installation error |

Why winmatsch does not learn: `GitHubFeedbackWorkflow.Classify` matches
labels `duplicate-entry`, `hash-mismatch`, `dependency-infrastructure`,
`transient-internal-error` and comments by `wingetbot`, `winget-bot`,
`github-actions[bot]` (`GitHubMaintenanceModels.cs:152-165`). None of those
labels exist in winget-pkgs, and the validator comments are posted by
`wingetvalidator-prod` and `microsoft-github-policy-service[bot]`. Every real
outcome therefore classifies as `Unknown` and is escalated, and nothing is
persisted per package.

Real vocabulary observed on the bot's PRs:

| Label | Blocking? | Validator comment shape |
|---|---|---|
| `Error-Hash-Mismatch` | yes, author | label only |
| `Error-Installer-Availability` | yes, author | label only |
| `URL-Validation-Error` | yes, author | "Url Validation Error" then `- <manifest path>` then `- <url>` then `- Http status code: <X>` or "No such host is known" or "The SSL connection could not be established" |
| `Validation-Defender-Error`, `Binary-Validation-Error` | yes, scanner | "One or more ESRP Scan Blocking detections found: Installer: <file> \| Detection Engine: <AV> \| Detection Description: <threat>" (Binary) or label only (Defender) |
| `Validation-Certificate-Root` | yes, author | label only |
| `Validation-Unattended-Failed`, `Validation-Installation-Error`, `Validation-Shell-Execute` | yes, author | policy-service boilerplate; moderators add the log excerpt later |
| `Possible-Duplicate` | moderator decides | "Possible duplicate package entry. Similar installer SHA256 hash found in manifest <path>" |
| `Manifest-Metadata-Consistency` | no | "Inconsistencies detected in package X version Y based on published version Z. Missing property `ReleaseNotes`" |
| `Validation-Executable-Error`, `Validation-No-Executables` | no (manual validation) | label only, with `Azure-Pipeline-Passed` |
| `Validation-InstallationMetadata-Update` | no | moderator ran `@wingetbot installationmetadata exe add <exe> --version` |

### B. The GitHub lifecycle in production is not winmatsch's

Bot branches are named `winget-autosubmit/<id>-<version>-<hash>`; winmatsch
names its submission branches `winmatsch/submissions/<op>/<package>/<version>`
(`GitHubLifecycleContracts.cs:91`). The close comment "Superseded by #N.
Closing this older automated update to keep the review queue clean." does not
occur in this repository. So an external orchestrator creates branches, PRs,
supersedes and closes. Everything that lives in winmatsch's submit path only
runs if the orchestrator actually calls `--submit` / `submit`:

- GH1010/GH1011 denied and sibling duplicate hashes, GH1013 retired
  identifiers (`GitHubLifecycleWorkflow.ValidateDuplicateHashes`)
- GH1014 to GH1016 release-freshness delay
- open-PR duplicate discovery
- VLD6009 immediate re-hash before commit

Verify which path the orchestrator uses. If it is its own git layer, these
gates must be surfaced in the local plan / `--result-json` output so the
orchestrator cannot skip them.

### C. Supersession starves the manual-validation queue

`Validation-Executable-Error` plus `Azure-Pipeline-Passed` means the pipeline
passed and a moderator still has to validate by hand (see class 2 below).
That queue is currently about three weeks deep (oldest open bot PR: 14 Aug).
The bot closes such PRs as soon as the next patch release appears:

- AnInsomniacy.Aria2Next: 2.5.6 merged 20 Aug; 2.5.9, 2.6.5, 2.6.6 each
  superseded within days; 2.6.8 waiting since 30 Aug. The repository still
  holds 2.5.6.
- RicardoCabral.icuvisor: 1.6.0, 1.6.1, 1.6.2 superseded; 1.6.3 merged;
  1.6.4 waiting.
- Same pattern for sluicesync.sluice, CyrilPeng.VeneraNext,
  Docker.docker-credential-wincred, Shssoichiro.Oxipng, dragonflylee.switchfin.

19 of the 100 closed PRs were in this state when the bot closed them.

### D. Version-identity defects (pure winmatsch, fully detectable)

1. Two open PRs for one release. SteamDatabase.SteamTokenDumper release tag
   `2026.08.20` was submitted as `2026.8.20` (#425659, 28 Aug) and again as
   `2026.08.20` (#426111, 29 Aug), identical URLs and hashes. Existing
   versions are zero-padded (`2024.05.31`, `2025.12.17`). WinGet treats the two
   strings as the same version; winmatsch's exists/duplicate checks compare
   strings.
2. Wrong version stream for pinned identifiers. `OpenJS.Electron.41` and
   `OpenJS.Electron.39` were given 43.4.0 (#418501, #418598) and
   `LookupFoundation.RevitLookup.2021` was given 2027.0.3 (#418437). The
   validator flagged them as duplicates of `OpenJS/Electron/43/43.4.0` and
   `RevitLookup/2027/2027.0.3`; the operator had to disable those pipelines.
   winmatsch has no rule tying an identifier's trailing numeric segment to
   the versions it may receive.

### E. Freshness delay is silently disabled without release provenance

`MinimumReleaseFreshness = plan.Release is null ? TimeSpan.Zero :
context.Configuration.FreshnessDelay` (`MutationCommandModule.cs:1390`).
`plan.Release` is null whenever assets were not resolved through the release
API, which is exactly the fallback the automated update path takes
(CHANGELOG 0.8.22). Two consequences in the sample:

- loreste.mako 0.5.12 (#423604, `Error-Hash-Mismatch`): the Windows zip and
  its `.sha256` were uploaded at 00:40:20Z, six to fourteen minutes after
  every other asset of that release, the signature of a CI re-upload.
- rizukirr.apic-gui 0.5.1 (#426181, `Error-Installer-Availability` at
  validation time); the publisher's own PR two days later with the same file
  hash was merged and ours closed as duplicate.

## Failure classes in detail

### 1. Defender hits on portable binaries (29 closed, 12 open)

Rust, Go and PyInstaller portable zips: thurbox, AtaraxyLabs.sem,
sluicesync.sluice, gallery-dl nightly, yt-dlp nightly, Microsoft.DSC,
Microsoft.Wassette, GeodeSDK.GeodeCLI, LocalStack, EndlessSky and others. The
same package is flagged on some builds and merged on others (thurbox 2.8.1 and
2.10.2 merged; gallery-dl 08.24, 08.27, 08.28, 08.29, 09.02 merged), so this is
signature noise, not a manifest defect.

What winmatsch can do: nothing before submission without a scanner, but it can
stop the churn. Quarantine the package after a Defender closure until the open
PR resolves; throttle channel identifiers (`.Nightly`, `.nightly`, `.Beta`,
`.PreRelease`, `.Canary`) to one submission per N days and skip while the
previous one is still open; optionally consult a hash-only VirusTotal lookup
as PIPE-5 evidence.

### 2. Executable not located after install (19 + 4 closed, 38 + 3 open)

Per `doc/ValidationFailureGuide.md`: "After installation, the test could not
locate the primary application executable." It hits nearly every portable CLI
the bot submits (Oxipng, git-cliff, dnsproxy, Kiota, Miller, CycloneDX, BMX,
clang-uml, HDiffPatch and more) as well as `Validation-No-Executables` on
zip-portable packages (Google.Protobuf, nu774.qaac, WinuxCmd, Luanti). It is
not a rejection: moderators validate by hand and merge (WinuxCmd 1.0.1,
switchfin 0.9.4, Aria2Next 2.5.6 merged with exactly this history), and
`yt-dlp.yt-dlp` carries a `.validation` waiver for this test plan.

On Microsoft.OpenAPI.Kiota (#419489) a Microsoft engineer resolved it with
`@wingetbot installationmetadata exe add kiota.exe --version`, which is the
manifest concept `InstallationMetadata.Files[].InvocationParameter`. Whether
the pipeline reads that field from the manifest is not documented publicly;
treat it as a hypothesis worth one experiment (emit
`InstallationMetadata.Files` with `FileType: launch` and
`InvocationParameter: --version` for portable executables and watch whether
the label disappears).

Certain either way: classify this label as "wait for manual validation", not
failure, and stop superseding these PRs on every patch release (finding C).

### 3. URL validation (14 closed, 4 open)

Observed causes and what the validator printed:

| Package | Cause |
|---|---|
| yhay81.sqrail | `No such host is known. (sqrails.yhay81.com:443)` (NXDOMAIN) |
| OmniEdge.OmniEdgeCLI | `The requested name is valid, but no data of the requested type was found` (DNS no-data) |
| Luxview.AgainstMe | `Http status code: NotFound` on every URL |
| Stells.EntraChecks | `https://github.com/f8l124` 404 (renamed GitHub user) |
| MonzerOsman.YallaVideo | `https://github.com/monzer15/YallaVideo/blob/master/LICENSE` 404 |
| BenModigell.Vibes | `Http status code: TooManyRequests` |
| RoniLehto.LMath | `RemoteCertificateNameMismatch` on `lehtodigital.fi` |

winmatsch's preflight (`PreflightGate.ProbeFailure`) drops an optional
metadata URL only on HTTP 404/410 (VLD5006 → `WF_DEAD_METADATA_URL_DROPPED`,
since 0.8.18). DNS and TLS failures produce a VLD5005 warning and the URL
ships. AgainstMe's 404 URLs still shipped on 22 Aug (#422832) and 30 Aug
(#426541), after 0.8.18, so either the orchestrator runs the automated path
without network preflight or the affected fields (`PublisherUrl`,
`PrivacyUrl`, agreement URLs) are outside the probed set; verify.

Detect correctly: treat NXDOMAIN, DNS no-data and TLS name mismatch as
definitively dead when a second resolver / second attempt agrees; treat 429
and 403 as "hostile to bots" and flag rather than drop; parse the validator's
"Url Validation Error" comment on the previous PR and drop exactly those URLs
on resubmission. When the field is `LicenseUrl`, fall back to the GitHub
license API result (already available to META-3).

### 4. Untrusted MSIX signing certificate (6 closed, 1 open)

8bit2qubit.OmniConsole: the MSIX inside the release zip is signed by a
self-signed certificate (`subject = issuer = CN=8bit2qubit`; only the
timestamp chain goes to DigiCert). The zip also ships
`OmniConsole_4.4.3.0_x64.cer`, `Install.bat` and `OmniConsoleInstaller.ps1`,
the usual "install our cert first" layout. Only 2.1.0.0 ever merged. winmatsch
reads MSIX identity (`MsixReader`) but no signature at all (no X509 or PKCS#7
usage in `src/`).

Detect correctly: parse `AppxSignature.p7x` (four-byte `PKCX` prefix, then
DER PKCS#7) and the PE Authenticode blob with `SignedCms`; a self-signed leaf,
or a chain whose root is not a well-known public CA, is a hard stop (proposed
`SIG-1`). A `.cer` file plus an install script beside an MSIX in an archive is
corroborating evidence.

### 5. Installer blocked on input (2 closed, 4 open) and switch hygiene

- SODA-for-SPARC 19.0.x and EdgeTX.Companion 2.12.4 have manifests identical
  to their merged predecessors (18.2.0 / 2.12.2: same installer type, switches,
  scope); the upstream installer changed. Not detectable before submission,
  only learnable afterwards.
- DiRoots.ProSheets 2.4.1 (#420154, `Changes-Requested`): switches
  `/i // /qn accept_eula=1` were carried verbatim from 2.1.2. Reviewer
  (Trenly): switches that accept a licence require `Agreements` in the locale
  manifest, and the switches no longer work.
- Calibrite.PROFILER 3.1.0 (#422969): installs a driver, `Blocking-Issue` +
  `DriverInstall`.

Detect correctly: new rule (proposed `SCOPE-5`): any switch containing a
licence-acceptance token (`accept_eula`, `ACCEPTEULA`, `--accept-license`,
`IAcceptLicense`, `AGREETOLICENSE`, `/ACCEPT`) requires
`Agreements[].AgreementLabel` + `AgreementUrl`, else ask. After
`Validation-Unattended-Failed`, `Blocking-Issue` or `DriverInstall`, quarantine
the package until a human overrides.

### 6. Installation errors (3 closed, 2 open)

jwtop 0.7.0 (zip: `LICENSE`, `README.md`, `jwtop.exe`, correct path both
architectures), masterclass-dl 1.1.0 (bare-exe zips for x86, x64, arm64 and
32-bit `arm`; dependencies Gyan.FFmpeg and yt-dlp.yt-dlp) and yt-dlp nightly
(dependencies DenoLand.Deno and yt-dlp.FFmpeg, identical to the last merged
komac manifest). Nothing in the artifacts explains the failure; it is the
pipeline environment or the dependency installs. One safe change: stop
emitting `Architecture: arm` (32-bit) installers by default; the pipeline
cannot validate them and `InnoProbe` already treats Arm as unsupported.

### 7. Duplicates (20 closed)

- Races with the publisher or another bot: apic-gui (publisher's own PR),
  raphamorim.rio (#413040), etcd (#413263), dbgate (#413274), GodotLauncher,
  ContextMenuMgrPlus.Beta, g-helper, jj, futrime.lip.
- Identifier moved or renamed: HiroSystems.Clarinet → StacksLabs.Clarinet
  (#422360), Docker.ds → Docker.sbx (#419942). `ReadSiblingInstallerEvidenceAsync`
  only scans the same publisher directory, so a cross-publisher move is
  invisible.
- The bot's own double submissions on 5 to 7 Aug (etcd, dbgate,
  superProductivity, shogihome, two PRs each). None after 13 Aug; consistent
  with the 0.8.10/0.8.11 discovery fixes, but worth a regression test.
- Wrong version stream (finding D).

Detect correctly: one GitHub code-search call per installer hash
(`repo:microsoft/winget-pkgs <sha256>`) before submitting; when the hash
already lives under a different identifier, stop and record the target as the
moved identifier in the learned store (feeds GH1013). Add a "publisher
self-submits" heuristic: when the last N merged versions were authored by the
upstream organisation, delay submission by 48 hours.

### 8. Scanner false positives (1 closed, 4 open)

golangci-lint (AVIRA `TR/Crypt.EPACK.Gen2`), k0sctl (AVAST
`Win64:Malware-gen`, maintainer obtained whitelisting on 27 Aug), Albayan,
wsl-usb-manager, quikrun. Not preventable. Keep these PRs open rather than
superseding them, because maintainers act on the thread; classify as
`ScannerFalsePositive` with a weekly retry.

### 9. Line endings (5 to 7 Aug, not recurring)

etcd 3.7.1 (#413093) and dbgate 7.2.4 (#412755) needed a human "Fix manifest
line endings" commit that rewrote the whole locale manifest. winmatsch
preserves CRLF from the previous manifest (`PreserveExistingLineEndings`,
`PackageManifestIO.cs:200`, `LocalWorkflowEngine.cs:2525`) even though PIPE-1
asserts LF and winget-pkgs' `.gitattributes` declares YAML as `text=auto`.
Always emit LF.

### 10. Already fixed and verified

Stale or missing `ReleaseNotes` / `ReleaseNotesUrl` (fixed 0.8.19 to 0.8.22):
between 18 and 23 Aug the validator posted "Missing property `ReleaseNotes`"
and a human pushed "Refresh ReleaseNotes/ReleaseNotesUrl" commits on at least
six PRs (#420886, #420305, #419190, #419489, #420154, #421521). The six most
recent merged PRs (#428657, #428540, #428404, #427973, #427639, #428408,
2 to 3 Sep) are single-commit with no consistency comment. The double title
`Add version: X - Update version: X` (#412755, canonical title passed as
custom title) has not recurred either.

## Prioritised actions for winmatsch

1. Replace the feedback vocabulary with the real winget-pkgs labels and
   commenters, persist per-package outcomes (label, parsed validator detail,
   version) in the learned store, and add a pre-submission gate that refuses a
   package whose last PR carries a hard-blocking label and whose new manifest
   still has the same trait. Ends the loops in classes 1, 3, 4, 5, 8.
2. Use WinGet numeric version equivalence in "version exists" and open-PR
   duplicate checks, pick the version string matching the existing versions'
   style, and add the pinned-identifier stream rule (finding D).
3. Dead-URL detection for DNS and TLS failures, 429/403 as "hostile", and
   parsing of the validator's URL comment (class 3).
4. Signature-chain inspection for MSIX and PE; self-signed is a hard stop
   (class 4).
5. Never zero the freshness delay: fall back to the asset's `Last-Modified`
   or release `updated_at`; re-hash immediately before the orchestrator
   commits (finding E).
6. `SCOPE-5` licence-switch → `Agreements`, drop 32-bit `arm`, throttle
   channel identifiers (classes 5, 6, 1).
7. Cross-publisher hash search, moved-identifier learning, publisher
   self-submits delay (class 7).
8. Supersede policy: do not close a PR that already has
   `Azure-Pipeline-Passed` for a patch release unless it is older than N days;
   emit `awaiting-manual-validation` in `--result-json` (finding C).
9. Emit LF unconditionally (class 9).
10. Confirm whether the orchestrator uses `submit`; if not, surface GH1010 to
    GH1016 and the duplicate discovery result in the local plan JSON
    (finding B).
