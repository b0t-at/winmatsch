# Current Test Matrix

**Run date:** 2026-08-02
**Subject:** WinMatsch installer analysis against the 100 newest merged pull requests in [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs)
**Analyzer build:** current Debug build from this worktree (`src/WinMatsch.Cli/bin/Debug/net10.0/winmatsch.exe`)
**Comparison:** SHA-256, declared architecture, and declared installer type for each installer declaration.

## Executive result

The run is **not fully clear**. Most downloaded artifacts were the exact bytes expected by WinGet manifests, but the analyzer still has multiple format and architecture classification gaps.

| Batch | Merged PRs | Installer declarations tested | Exact matches | Discrepancies | Download/analysis errors | Other skipped items |
|---|---:|---:|---:|---:|---:|---:|
| Newest 1–50 | 50 | 73 | 59 | 12 | 2 | 2 PRs had no current installer declaration |
| Next 51–100 | 50 | 71 | 49 | 17 | 5 | 2 historical Edge installer manifests unavailable through GitHub contents API |
| **Total** | **100** | **144** | **108** | **29** | **7** | **4** |

Of the 29 discrepancies, every one had a matching SHA-256. The failures are therefore metadata-detection differences, not evidence of downloading the wrong artifact.

## Scope and method

1. Built the solution and used the rebuilt CLI executable.
2. Selected the 100 newest merged PRs, ordered by merge time.
3. Loaded the installer manifest data and invoked:
   ```text
   winmatsch analyze <InstallerUrl> --format json --interaction never
   ```
4. Compared `sha256`, each `installers[].architecture`, and `installers[].installerType` with the manifest declaration.
5. Download/transient failures are reported separately from analyzer metadata discrepancies.

### Source-revision caveat

- **Batch 2** was parsed from each PR's `merge_commit_sha`; it is pinned to the merged manifest revision.
- **Batch 1** used the current manifest file returned by GitHub after identifying the PR's changed installer paths. Its artifacts and expected SHA values are still valid for this run, but this is weaker provenance for packages whose manifest was modified after merge.
- Three initial Batch-1 errors were caused by the first temporary parser retaining YAML scalar quotes. They were rerun with normalized URLs and all three matched; they are **not** counted as errors.

## Batch 1 — newest 50 merged PRs

### PRs included

`411286`, `411276`, `411275`, `411273`, `411272`, `411269`, `411268`, `410299`, `410270`, `410262`, `410248`, `409967`, `411214`, `411271`, `411261`, `411266`, `411260`, `411257`, `408012`, `411258`, `411256`, `411255`, `411218`, `411253`, `411251`, `411249`, `411248`, `411246`, `411245`, `411244`, `411242`, `411236`, `411243`, `411241`, `411240`, `411239`, `411238`, `411237`, `411234`, `411233`, `411232`, `411222`, `411231`, `411228`, `411203`, `410576`, `409557`, `409558`, `409524`, `406213`.

### Exact matches

59 of 73 declarations matched hash, architecture, and installer type. This includes the real-world regression targets fixed earlier in this branch: OpenKara x64, InferenceHub x64, Alma, TableCloth x64 and arm64, Chrome Canary x64, and OpenRCT2 x86/x64.

### Discrepancies

| PR | Package / artifact | Manifest declaration | Analyzer result | Assessment |
|---:|---|---|---|---|
| 411214 | JanDeDobbeleer.OhMyPosh 30.2.0 x64 | `msix`, x64 | `appx`, x64 | Package subtype terminology mismatch; SHA matches. |
| 411214 | JanDeDobbeleer.OhMyPosh 30.2.0 arm64 | `msix`, arm64 | `appx`, arm64 | Same subtype terminology mismatch; SHA matches. |
| 411242 | TopalaSoftwareSolutions.SIW 2026.08.02.0 x64 | `inno`, x64 | `inno`, architecture `null` | Inno parser identifies format but has insufficient architecture evidence. |
| 411242 | TopalaSoftwareSolutions.SIW 2026.08.02.0 arm64 | `inno`, arm64 | `inno`, architecture `null` | Same URL is declared twice for x64 and arm64; analyzer cannot infer either target architecture. |
| 411243 | SoftPerfect.NetworkScanner 26.7 | `inno`, x64 | `portable`, x86 | Generic fallback; expected Inno signature/architecture was not recognized. |
| 411228 | OpenRCT2 0.5.4 arm64 | `nullsoft`, arm64 | `nullsoft`, x64 | NSIS architecture inference incorrectly promotes the ARM64 package to x64. |
| 411203 | willibrandon.scout 0.6.1 x64 | `msi`, x64 | `wix`, x64 | More-specific WiX classification. Previously assessed as intentional/acceptable raw detection, but differs from manifest. |
| 411203 | willibrandon.scout 0.6.1 arm64 | `msi`, arm64 | `wix`, arm64 | Same WiX-vs-MSI classification difference. |
| 409557 | Zed Preview 1.14.1-pre x64 | `inno`, x64 | `inno`, architecture `null` | Inno format recognized but target architecture unavailable. |
| 409557 | Zed Preview 1.14.1-pre arm64 | `inno`, arm64 | `inno`, architecture `null` | Same issue for ARM64 asset. |
| 409524 | Zed 1.13.1 x64 | `inno`, x64 | `inno`, architecture `null` | Inno architecture evidence unavailable. |
| 409524 | Zed 1.13.1 arm64 | `inno`, arm64 | `inno`, architecture `null` | Same issue for ARM64 asset. |

### Download/analysis errors

| PR | Package / artifact | Outcome |
|---:|---|---|
| 411261 | Gitea.tea 0.15.1 x64 | `dl.gitea.com` transient download failure after the CLI's four attempts. |
| 411261 | Gitea.tea 0.15.1 arm64 | Same transient origin failure. |

### Non-installer / skipped PRs

- `411286` removed `ZedIndustries.Zed.Preview` manifests; it had no new installer to validate.
- `409558` had no current installer declaration available to the initial collector.

## Batch 2 — merged PRs 51–100

### PRs included

`411232`, `411226`, `411222`, `411229`, `411227`, `411225`, `411223`, `411219`, `411217`, `411215`, `411212`, `411211`, `411208`, `411199`, `411201`, `411200`, `411183`, `411197`, `411196`, `411194`, `411193`, `411192`, `411190`, `411188`, `411189`, `411185`, `411187`, `411186`, `411182`, `411181`, `411177`, `411176`, `411180`, `411179`, `411173`, `411170`, `411172`, `411171`, `411169`, `411168`, `411166`, `411081`, `410895`, `410956`, `410946`, `410635`, `409931`, `407897`, `407397`, `386777`.

### Exact matches

49 of 71 declarations matched all checked fields. This includes x64 and ARM64 TableCloth installers, all three Chrome Canary architectures, ActiveDirectoryPro.365ProToolkit, the two QMPlay2 assets, LLM.Wiki EXE and MSI, MAA archives, MSI packages, and the remaining portable/ZIP installer families.

### Discrepancies

| PR | Package / artifact(s) | Manifest declaration | Analyzer result | Assessment |
|---:|---|---|---|---|
| 411225 | HMCL Dev CNB 3.17.0.353 | `portable`, `neutral` | `portable`, x86 | Architecture declaration is neutral while PE header is x86. SHA matches. |
| 411223 | HMCL Dev GitHub 3.17.0.353 | `portable`, `neutral` | `portable`, x86 | Same binary and same neutral-vs-x86 difference. |
| 411193 | Easydict 0.8.9 x64 | `inno`, x64 | generic `exe`, x86 | Inno signature not recognized; outer stub is x86. SHA matches. |
| 409931 | Easydict 0.8.8 x64 | `inno`, x64 | generic `exe`, x86 | Same classification/architecture gap on prior version. |
| 411190 | DBMobile.Resonance 3.1.7 arm64 | `nullsoft`, arm64 | `nullsoft`, x64 | NSIS architecture inference misclassifies ARM64 asset as x64. |
| 411188 | LogivoreForVeeam 1.23.0 (4 assets: x64/arm64 × user/machine) | `inno`, declared x64 or arm64 | generic `exe`, x86 | All four artifacts are byte-correct but not recognized as Inno; x86 outer stub is reported. |
| 411189 | Logivore 1.23.0 (4 assets: x64/arm64 × user/machine) | `inno`, declared x64 or arm64 | generic `exe`, x86 | Same family-wide Inno signature/architecture gap. |
| 411169 | ShareX.XerahS 0.24.17 x64 | `inno`, x64 | generic `exe`, x86 | Inno not recognized; x86 outer stub reported. |
| 411169 | ShareX.XerahS 0.24.17 arm64 | `inno`, arm64 | generic `exe`, x86 | Same family gap for ARM64 asset. |
| 411168 | Jackett 0.24.2315 x86 | `inno`, x86 | generic `exe`, x86 | Architecture agrees but Inno type is missed. |
| 411166 | Zen Browser Twilight x64 | `exe`, x64 | generic `exe`, x86 | Type agrees; analyzer reports outer x86 bootstrapper rather than claimed x64 target. |

All 17 entries in this table had an exact SHA-256 match.

### Download/analysis errors

| PR | Package / artifact(s) | Outcome |
|---:|---|---|
| 411219 | goptics.vizb 0.17.1 x64 and arm64 ZIP | CLI received a filename with no recognized extension after download and returned `No installer analyzer supports ''`. |
| 407897 | goptics.vizb 0.17.0 x64 and arm64 ZIP | Same extension-loss failure. |
| 411183 | Kingsoft.WPSOffice 12.1.0.28022 x64 | WPS CDN request exceeded the initial run budget and was recorded as a timeout; no hash or classification conclusion was possible. |

### Historical manifest retrieval skips

- `411180` — Microsoft.Edge.Beta 129.0.2792.12 installer manifest could not be fetched through GitHub contents API at its merged SHA (`HTTP 404`).
- `411179` — Microsoft.Edge.Beta 128.0.2739.42 had the same historical-content retrieval failure.

These two PRs were not counted as installer declarations or as analyzer failures.

## Cross-batch findings

### Confirmed high-priority analyzer gaps

1. **Inno detection coverage:** SoftPerfect Network Scanner, Easydict, both Logivore families, XerahS, and Jackett are manifest-declared as Inno installers but fall through to generic EXE classification. This is the largest finding: 14 declared assets across both batches have a type mismatch or missing architecture because their Inno signatures are not handled.
2. **Architecture of wrapper installers:** ARM64 NSIS targets (OpenRCT2 and Resonance) are reported as x64. Generic/unknown wrapper installers are frequently reported as the x86 bootstrapper even when the manifest declares x64 or arm64.
3. **Inno architecture evidence:** SIW and Zed are correctly recognized as Inno but return no architecture. The analyzer should ideally derive target architecture from payload evidence or installer metadata before returning `null`.
4. **ZIP filename preservation:** goptics.vizb assets download successfully enough to reach analysis but lose their `.zip` filename; this is a downloader/content-disposition filename-resolution issue because `FileAnalyzer` selects analyzers from the filename extension.

### Lower-priority / policy-level differences

- `msix` vs `appx` for Oh My Posh is a subtype naming policy difference; architecture and hash are correct.
- `msi` vs `wix` for scout is a more-specific detection result (`wix`) than the manifest's generic `msi`; update workflow safeguards already prevent unapproved structural rewrites.
- HMCL's `neutral` manifest architecture vs x86 PE header may be a legitimate declaration choice for a Java launcher rather than a defect.

### Origin reliability observations

- The Gitea CDN (`dl.gitea.com`) failed both assets after normal retry policy.
- The WPS CDN stalled beyond the initial test budget.
- These outcomes validate the need for the newly added downloader connect/stall safeguards, but they do not establish an analyzer defect.

## Reproduction commands

Run an individual comparison with:

```text
src/WinMatsch.Cli/bin/Debug/net10.0/winmatsch.exe analyze <InstallerUrl> --format json --interaction never
```

Examples:

```text
src/WinMatsch.Cli/bin/Debug/net10.0/winmatsch.exe analyze https://github.com/xiaocang/easydict_win32/releases/download/v0.8.9/Easydict-v0.8.9-x64-setup.unsigned.exe --format json --interaction never
src/WinMatsch.Cli/bin/Debug/net10.0/winmatsch.exe analyze https://github.com/goptics/vizb/releases/download/v0.17.1/vizb@0.17.1-windows-amd64.zip --format json --interaction never
```

## Conclusion

The mass test confirms strong content integrity behavior: 108 declared installers were complete matches, and every reported metadata discrepancy still downloaded the manifest-declared SHA-256. It also identifies a concentrated backlog around Inno recognition, wrapper-architecture inference, and download filename preservation. The run is therefore useful but **not clean enough to call all analyzer metadata validated**.
