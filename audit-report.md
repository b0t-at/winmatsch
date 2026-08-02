# Audit report — original audit, remediation closeout, and independent re-audit

**Audited worktree:** `D:\copilot-worktrees\winmatsch\utesgui-psychic-umbrella` @ `29e6f7b` (116 commits ahead of `main`; 389 files, +71,746 / −433)
**Audit date:** 2026-08-01 · **Method:** Phase 0 inventory + central build/test by the coordinating auditor; 7 parallel per-unit deep-review subagents (all Claude Fable 5, long context — **no unit hit model guardrails, the GPT‑5.6 Sol fallback was never needed**); Phase 2 independent re-verification of every blocker and load-bearing major (code re-read and/or live repro against the built binary).
**Methodology deviation (disclosed):** builds/tests were run **once, centrally** by the coordinator instead of per-subagent — 7 concurrent `dotnet build/test` runs in one worktree would deadlock on `obj/` file locks. Per-project results are recorded in §4 from that central run. Subagents were read-only.

---

## R. Independent re-audit — 2026-08-02 (verification of the §0 remediation closeout)

> Performed by the original (independent) auditor against branch HEAD `43ec8c0`
> (54 remediation commits after the audit report landed; +42,622 / −3,644 over
> 342 files). Method: central build + full test run, live CLI repros of the
> originally-reproduced defects, and 3 parallel verification subagents
> (Claude Fable 5, long context) re-reading the current code for every
> blocker/major/appendix claim — the §0 closeout below is the remediation
> agent's self-report; **this section is the independent check of it.**
> This section supersedes §0's self-assessment where they differ.

### R.0 Final remediation closeout — 2026-08-02

> **Final status after the section-R remediation:** audited from the exact
> integrated parent `dcc862958ae9b23c03883bbb639fd3293536f7c0`, covering every
> production change in `c213af522b03e3c4273d9a2c87b45bcf4073d6f4..dcc8629`,
> plus integrated independent closeout fixes `d58eef0` and `5f07e52`
> (reviewed as patch-equivalent commits `7b6b207` and `45a6d46`). The original R findings and
> historical remediation record below are preserved; this subsection is the
> final disposition. No live GitHub mutation was performed.

**Final verdict: section R is closed.** R-N1, R-N2, both stale-ledger findings,
every listed minor/nit, and the integration defects found during this final
read are resolved. The bounded evidence and parser changes preserve the
original B1/B2, M1–M18, and A1–A29 contracts: resource exhaustion degrades
only dependency evidence; corruption, I/O, cancellation, rate-limit, identity,
and uncertain-remote-state failures remain distinct and fail closed.

| R item | Final code/test disposition |
|---|---|
| R-N1 request complexity | `959225e` batches changed-file evidence through GraphQL (50 PR nodes/request), `b6bf2cc` keys/revalidates cache entries by upstream repository + PR number + head SHA + node/base identity, `11b083b` caps discovery at 1,000 open PRs, `c300dcf` accepts GitHub's `CHANGED` status, `496d120` caps cumulative REST fallback at 64 requests, and `e2f477e`/`8cb4d21` preserve GraphQL/REST rate-limit classification. `Production_scale_discovery_batches_and_reuses_pinned_head_evidence` proves 400 PRs use 8 initial GraphQL requests, zero on an unchanged repeat, and one after a single head moves; focused R-N1 run: **3 passed**. GraphQL unavailable above the 64-PR REST bound, truncated/renamed completion above 16 PRs, pagination loops, incomplete batches, and rate limits all stop before mutation. |
| R-N2 ZIP boundaries/taxonomy | `0e97e0c` converts dependency-analysis resource ceilings to `Unavailable` + DEP003, `58f127f` introduces the distinct resource-limit exception, `035bed0` keeps malformed declarations corrupt, `c255a5f` propagates corrupt payload failures, and `3ee1891` rejects inconsistent expanded sizes. Exact unit boundaries cover **4,096 complete; 4,097 and 10,000 unavailable; 10,001 rejected by `FileAnalyzer` while dependency evidence still degrades**. Truncation/inconsistent declarations remain `InvalidDataException`, ordinary I/O propagates, and cancellation remains `OperationCanceledException`. The nested reviewer found that single-disk EOCD/ZIP64 per-disk counts were not cross-checked; `5f07e52` rejects contradictory counts before applying resource ceilings and pins both classic ZIP and ZIP64 plus a valid ZIP64 control. Focused taxonomy run: **10 passed**; live real-process 4,097-entry CLI run: **1 passed**, exit 0 with unavailable evidence and DEP003. |
| Corrupt journals, scope, and crash windows | `a790848` adds atomic quarantine, scope sidecars, bounded lock wait, feedback directory sync, and durable writes; `586c600`, `04a4443`, and `dcc8629` close migration, quarantine, cross-directory replacement, completion, and interrupted-scope windows. Final trace found that resume still re-read the global pending list and matched corruption by package without repository identity; `d58eef0` returns the valid pending snapshot with recovery, binds corruption to the recovered repository filesystem identity, lets proven-unrelated packages continue, and still blocks unknown/same-repository+package evidence. It also redacts persisted `LastError` diagnostics. The nested reviewer found that first creation of state roots did not persist new parent directory entries on POSIX; `5f07e52` durably creates each missing path component for submission journals, feedback state, and learned overrides. Full journal class stress: **24 tests × 20 iterations = 480 passed**, including lock wait/timeout/cancellation, corrupt intent/journal quarantine, scope conflict interruption, activation/completion, and learned-override ordering. |
| CLI ownership/open/cache nits | `8356ac6` gives token validation an owned-client default and caller-owned injected-client behavior, accepts the documented Windows `explorer.exe` handoff exit, applies mutation-consistent feedback cache defaults, and tests CLI/env precedence. Final trace found two adjacent ownership gaps; `d58eef0` removes the inline maintenance `HttpClient` leak and prevents `GitHubPullRequestMetadataSource` from disposing caller-owned clients. |
| E2E isolation/tooling nits | `f738cbb` isolates regression overrides in a temporary store, reuses the production cache lock in the lock host, adds checksum-pinned compiler mirror fallback, and moves tooling logic behind parser-tested helpers. `cde3667` requires MakeAppx to have valid Microsoft Authenticode, the pinned SDK major/minor/build, and at least the approved file version; no runner-latest fallback is accepted. The real compiled corpus passed for Inno, NSIS, WiX MSI/duplicate MSI/Burn, and MSIX. |
| Mechanical plan/CHANGELOG/Inno items | `32de600` reconciles `DRY_RUN` precedence and the now-present P8 compiled-corpus job in `plan.md`, corrects anonymous `show`/`list-versions` behavior in CHANGELOG, and removes the surviving no-op Inno catch. |
| Nested independent review | A final read-only **GPT-5.6 Sol** review of the patch-equivalent child range `c213af5..cec68b2` (integrated through `8d1fcec`) reported exactly two medium-confidence findings: first-write parent-directory durability and contradictory single-disk ZIP counts. Both were fixed in `5f07e52` (reviewed as `45a6d46`); affected gates and the full suites were rerun cleanly. No reviewer finding remains open. |

The exact-tree verification at integrated code head `5f07e52` (patch-equivalent to
reviewed code head `45a6d46`) was:

| Gate | Final result |
|---|---|
| Restore/build | All **20 projects** restored; Release build **0 warnings, 0 errors**. |
| Full tests, serialized (`-m:1`) | **2,344 passed, 0 failed, 3 skipped** (2,347 total). |
| Full tests, default parallel | **2,344 passed, 0 failed, 3 skipped** (2,347 total), identical counts. |
| Focused R-N1 | **3 passed**: production-scale request budget/cache invalidation and rate-limit fail-closed classification. |
| Focused R-N2 | **10 boundary/taxonomy cases passed** plus **1 live CLI case passed**. |
| Journal stress | **480 passed** across 20 fresh-process iterations. |
| Full hermetic E2E | **30 passed, 0 failed, 3 skipped**. The compiled gate was then enabled separately and passed; the two token-gated GitHub E2E tests remained opt-in, including the deliberately mutating test. |
| Windows compiled corpus | **1 passed, 0 skipped** with the checksum-pinned installed tools and approved MakeAppx. |
| Trimmed publishes | `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`: **6/6** self-contained, trimmed, single-file outputs with LICENSE/notices; native `win-x64` version/help/analyze-help/completion/config-path smoke passed. |
| Format/tooling | `dotnet format --verify-no-changes` clean; PowerShell tooling tests passed; every repository `.ps1`/`.psm1`/`.psd1` parsed cleanly. |
| Workflow gates | actionlint **1.7.12** clean; zizmor **1.29.0**, auditor/offline/minimum-low, no findings (one ignored by the tool). |
| Action pins | Live GitHub release metadata resolved all six committed comments and SHAs exactly: checkout 7.0.1, setup-dotnet 6.0.0, upload-artifact 7.0.1, download-artifact 8.0.1, zizmor-action 0.6.2, action-gh-release 3.0.2. No pin was stale. |

**Residuals:** no actionable section-R code issue remains. The genuine accepted
residuals remain those already disclosed in §0: A9 audit-attribution precision,
no committed NuGet lockfiles, deferred artifact attestations and administrator
repository-setting enforcement, manual branch deletion, and opt-in live GitHub
tests. The deliberately mutating live GitHub E2E was not run. Detailed command
logs and the concise command/count ledger are in the closeout session artifacts,
under `files/closeout-logs` and `files/final-r-closeout.md`.

### R.1 Verdict

**The remediation is genuine and essentially complete: every blocker and major
from the original audit is verified fixed in current code, and the original
merge gate (§5 items 1–6) is cleared.** Both former blockers were re-reproduced
as fixed live (`analyze` on a 70 MiB PE and a ZIP-wrapped PE now exit 0 with
bounded evidence; the typo'd `--token` parse error no longer echoes the secret).
Build: 0 warnings / 0 errors. Tests: **2,274 passed, 0 failed, 3 skipped**
(intended opt-in gates), matching the closeout's claim exactly. The closeout's
per-item table is substantively accurate — my verification confirmed
**Fixed** for B1, B2, M1–M18 and every appendix item except A1k
(partially fixed: the no-op `catch (InvalidDataException) { throw; }` at
`InnoFormatReader.cs:145-154` survives despite the "no-op catches were removed"
claim) and A9 (deferred, as disclosed). The re-audit found **two new majors in
the remediation code itself** (R-N1, R-N2 below) plus a handful of minors —
none data-corrupting or safety-breaking. **Recommendation: mergeable now**,
with R-N1/R-N2 and the plan.md contradictions as prioritized fast-follows
(R-N2 is a small fix; R-N1 needs a design decision before production-scale
use against `microsoft/winget-pkgs`).

### R.2 Spot-confirmations of the closeout table (independent evidence)

| Item | Independent evidence |
|---|---|
| B1 | Live: `analyze` 70 MiB PE → exit 0, full output; 70 MiB-class ZIP+PE → exit 0. Code: header-only PE read; ZIP budgets degrade to `DependencyEvidenceStatus.Unavailable` + DEP001/002/003; DEP-1 rule treats Unavailable as info-only, `Absent` only for complete direct-PE evidence (`Dep1PayloadDependencyRule.cs:67-81`). One residual axis → R-N2. |
| B2 | `HumanCorrectionDetector.cs:169-296` — occurrence-queue index with consumed-flags replaces `ToDictionary`; 1→2 same-URL twin pinned by review-asserting tests (`RuleRuntimeTests.cs:1039,1076,1318`). |
| M1 | Shared pairwise `InstallerDuplicateRelation` (`src/WinMatsch.Core/InstallerDuplicateRelation.cs:11-48`) with null-scope/locale wildcard + NestedInstallerType; the formerly-passing `{x64,exe,no-scope}`+`{x64,exe,user}` case pinned red (`AuditValidationRegressionTests.cs:70-85`); multi-locale legal again. |
| M2 | WM0202 (`ApplyOverridePackFieldsRule`, `ProductionRuleComposer.cs:49`) consumes all four former-dead fields; learned packs persist via fingerprint-bound approval → `StageAsync` → prepared/manifest-committed/activated journal with hash CAS (`OverridePackStore.cs:287-295,492-631`) and are consumed on the next run (`LocalWorkflowEngine.cs:502-551`); value+bot-value hash matching makes re-application conservatively strict (degrades to re-review, never blind rewrite). No ABBA lock order found. |
| M3/M4 | Discovery is author-independent: all open PRs on the base branch, association proven from changed-file paths or pinned head content (`GitHubSubmissionEvidence.cs:461-527`); evidence limits fail closed → GH2034. Titles canonical `New version:`/`Update version:`/`Add version:`/`Remove version:` (`GitHubSubmissionFormatter.cs:12-23,118-126`). Cost regression → R-N1. |
| M5 | Fresh reservation adopted (GH2032), journaled exact-commit recovery (GH2041), bounded `-2…-8` suffixes; the 422-forever brick is gone (`GitHubLifecycleWorkflow.cs:454-585,1366-1432`). Branch deletion still manual (disclosed). |
| M6 | Evidence fetched pre-plan (`GitHubLifecycleWorkflow.cs:221-227`) from a real bounded repo-tree scan (sibling hashes — the load-bearing half, live against any repo) plus a tool-defined policy file `.github/winmatsch/submission-evidence.json` for retired-ids/vanity — **only live in repos adopting that convention; microsoft/winget-pkgs does not carry it** (closeout overstates this half). |
| M7 | `[JsonIgnore(Condition = Never)]` on `CreateTreeEntryDto.Sha` (`GitHubTransportDtos.cs:400`); raw serialized `"sha":null` asserted in recorded tests (`GitHubMutationOperationsTests.cs:290,343`). |
| M8 | Envelope written **before** the exit-4 throw (`MutationCommandModule.cs:361-401`); `--approve-reviews` is fingerprint-bound at CLI and engine layers (`ReviewApproval.cs:8-94`). Residual (inherent, docs-disclosed): across two invocations the boolean flag approves the second run's recomputed review set. |
| M9 | Live: `--tokn ghp_…` → `Unrecognized option '--tokn'` (value never printed); unmatched-token values render `[REDACTED]`. `questions[].path` now redacted via `CliRedactor.RedactUrl`; all CliHost exception paths redact; JSON string values redacted on the wire (`CommandOutput.cs:37-149`). Note: engines were *aligned by decorating every production client* with `RedactingGitHubRepositoryClient`, not unified — `GitHubSubmissionFormatter`'s own engine still lacks `ghp_` rules (latent for library consumers only). |
| M10 | All decline sites now exit 5; 130 only under a genuinely cancelled token (`MaintenanceCommandHelpers.cs:54-64`; `MaintenanceCommandModule.cs:599-602`); docs updated. |
| M11–M15 | Inno ANSI → replacement-fallback decoding + INNO004/005 diagnostics, overflow guard replaced `checked()` (`InnoFormatReader.cs:345-374,1240-1291`); schema-invalid docs skip materialization and `OverflowException` → VLD2003 (`ManifestPackageParser.cs:73-133`); all YAML file reads route through budgeted `ManifestYamlDocument`/`SafeManifestFile` (byte/event/depth/node/alias bans + reparse-point-safe opens) incl. `PackageManifestIO`, `PreflightRequest.FromDirectory`, `OriginalSubmissionStore`. |
| M12/M13 | `x86compatible` without payload proof is now **inconclusive** + INNO001 manual-analysis; size-weighted dominance (≥2× + largest image) + INNO002; `ArchitecturesInstallIn64BitMode` hint (INNO011); header/payload divergence INNO012 (`InnoProbe.cs:220-383`). Dependencies: Inno + Squirrel payloads decompressed (bounded) for imports; NSIS/Burn/AdvancedInstaller wrappers → `Ambiguous` outer-stub evidence with `outer-stub-only` signal, never `Absent`. |
| M16 | `InstallerVersionTrustPolicy` admits direct-PE (non-SFX) and single-candidate-ZIP versions; wrappers/placeholder shapes rejected; production-wired (`ProductionAdapters.cs:285-334`). |
| M17 | All 13 descriptors drive the **real** production engine (`WorkflowProductionComposition.CreateLocalEngine` → `NewAsync`/`UpdateAsync`) with SHA-pinned synthetic binaries served to the real downloader/analyzer, byte-compared to complete merged-manifest YAML goldens (`RegressionDescriptorE2ETests.cs:31-188`). Previous installers now come from independent descriptor scenarios, not the expected output. |
| M18 | Production composes durable `FileFeedbackStateStore` + `AllowlistedApprovedRepairPlanner` (DuplicateEntry/HashMismatch only, full preflight re-entry); `NullApprovedRepairPlanner` has zero references. |
| A15b | Default `freshnessDelay` = 4 h (`ConfigurationDefaults.cs:12`). Nuance: submissions **without** release provenance still run with no delay (the gate can't evaluate without a release identity) — "4 h" holds for release-provenance submissions. |
| A17 | `File.Replace` metadata publication race fix present (`DownloadCache.cs:535-546`); lock poll has deadline + cancellation. |
| A20f | Live: `list-versions Git.Git` with no token → exit 0 (anonymous public read works). |
| A21/A22 | All 6 action pins live-resolved tag→SHA: **6/6 match** the committed SHAs; workflow-level `permissions: contents: read`, write only on the release job, `persist-credentials: false` everywhere; no injectable `${{ }}` in run blocks. |
| A29 | The compiled-installer corpus CI job **exists and is real**: unconditional `windows-compiled-corpus` job, SHA-pinned Inno 6.4.0 / NSIS 3.10 / WiX 5.0.2 / MakeAppx toolchain, compiles six formats, asserts real `FileAnalyzer` results (`ci.yml:54-101`, `Build-WindowsInstallerCorpus.ps1`, `WindowsCompiledCorpusTests.cs`). But see R.4: plan.md still claims the job doesn't exist. |

### R.3 New findings in the remediation code

- **R-N1 `[major]` Duplicate-PR discovery does a changed-files API call for every open PR on the base branch — throughput/rate-limit regression.**
  `GitHubSubmissionEvidence.cs:475-517` fetches `GetPullRequestChangedFilesAsync` for **each** open PR *before* any filtering (the title-hint/`canVerifyEveryOpenPullRequest` check at :512-517 only gates candidacy, not the fetch), and discovery runs at least twice per submission (pre-create + post-create reconciliation, `GitHubLifecycleWorkflow.cs:241,787`). The old server-side `ExactTitleToken` narrowing is gone. Against `microsoft/winget-pkgs` (routinely 200–400 open PRs on master) that is 400–800+ sequential REST calls (each up to 10 pages) per submission — minutes of latency and a large slice of the 5,000/hr limit, capping realistic throughput at a handful of submissions per hour. Correctness is unaffected (failures escalate, never silently mis-associate); this is the price of the M3 fix as designed. Fix directions: server-side title/head prefilter before file fetches, batching via GraphQL, or caching per (PR, headSha). **Confirmed (code); live impact depends on open-PR count.**
- **R-N2 `[major]` ZIPs with 4,097–10,000 entries still crash dependency analysis — the B1 degrade contract is violated on the entry-count axis.**
  `PayloadDependencyAnalyzer.cs:206-217` throws `InvalidDataException` above its own 4,096-entry cap while `FileAnalyzer` accepts up to 10,000; `InvalidDataException` is absent from every catch list on the path (`LocalWorkflowEngine.cs:1593-1628`, `DiagnosticsCommandModule.cs:372-382`) → `analyze`/`update` on a large portable ZIP (SDK-style, ~5k files) exits 1 "unexpected error" instead of degrading to `Unavailable` evidence like every other budget in the same file. Small fix: convert to the existing Unavailable+DEP003 path or align the caps. **Confirmed (static trace end-to-end).**
- `[minor]` **plan.md contradicts the shipped code twice** (closeout doesn't disclose either): (a) plan.md:64 says the `DRY_RUN` env var "was dropped: an exported variable must never be able to flip submission into a no-op or back" — but the variable **is implemented** (`GlobalOptions.cs:30,183-210`, documented in docs/commands.md:70,80-82), and an exported `DRY_RUN=true` does silently turn `--submit` into a plan-mode no-op with exit 0 — exactly the failure mode the plan text forbids; (b) plan.md:73 still says the P8 compiled-corpus CI job "does not exist" / reconciliation PENDING while `ci.yml:54-101` has carried it since `b2e291a`. Either the plan or the behavior needs to change; today the ledger is wrong on both.
- `[minor]` One corrupt submission-journal file blocks **all** submissions: `FileSubmissionJournalStore.cs:61-62,659-724` throws `SubmissionJournalTamperedException` on any unreadable `*.journal`/`*.intent` during `PrepareAsync`/recovery, with no quarantine path (the override store quarantines to `.corrupt`; the journal store doesn't). Global blast radius for a single bit-rotted file.
- `[minor]` E2E `RegressionFixturePipeline.cs:38-42` builds the production engine with the **real user-profile override store** (`%LOCALAPPDATA%\winmatsch\overrides`); a developer machine holding genuine learned packs for one of the 13 real package ids breaks the byte-exact goldens, and `WINMATSCH_UPDATE_REGRESSION_GOLDENS=1` there would bake user state into regenerated goldens. CI-safe; developer-hostile.
- `[minor]` `TokenCommandModule.cs:64-68` (and `MaintenanceGitHubAdapters.cs:27` fallback) create an inline `HttpClient` for `token add` validation without ownership (`disposeHttpClient: true` not set) — leaks in-process; contradicts the A17d ownership contract this same remediation introduced.
- `[minor]` `MutationContracts.cs:569-583`: Windows `explorer.exe <url>` conventionally exits 1 even on success — `--open-pr` on Windows will typically print a false "browser could not be opened … exited with code 1" diagnostic (PR creation and exit code unaffected).
- `[nit]` Journal global lock is fail-fast (`FileSubmissionJournalStore.cs:847-867`) — concurrent CLI invocations get immediate `SubmissionJournalConflictException` instead of the bounded wait the override store uses. `[nit]` `FileFeedbackStateStore.cs:57-73` skips the directory-fsync pattern its sibling stores use. `[nit]` Journaled prepare/execute/resume paths interpolate raw `exception.Message` (`MutationCommandModule.cs:974,1000,1030`) — still redacted at the host boundary, but with shallower depth than sibling paths. `[nit]` `tests/WinMatsch.Downloads.LockHost` copy-pastes the cache's platform lock strategy (test-only drift risk). `[nit]` Feedback downloader enables the cache only when explicitly configured while mutation workflows default it on (`MaintenanceCommandModule.cs:432-438` vs `ProductionMutationWorkflows.cs:304-309`). `[nit]` `InnoFormatReader.cs:145-154` no-op catch remains (A1k). `[nit]` NSIS corpus tool pinned to a single SourceForge mirror host + runner-image MakeAppx byte-pin — availability treadmill (`Build-WindowsInstallerCorpus.ps1:20,147-180`). `[nit]` CHANGELOG still documents the pre-A20f "token requirement for show/list-versions".

### R.4 Closeout accuracy assessment

The §0 closeout is **substantively honest** — no claimed fix was found to be
fake, and its three self-found extra defects are real. Corrections for the
record: (1) its fix-commit hashes (`c78d0c7`, `f7cbe52`, `1703c4c`) are from
the pre-rebase closeout branch and are **not in this branch's history** — the
same changes landed here as `16a05e5`, `7541397`, `21505bc`; (2) "the two
redaction engines unified" → actually *aligned via a decorator*, with the
weaker formatter engine remaining for library consumers; (3) "no-op catches
were removed" (A1k) → one remains; (4) B1's "returns bounded unavailable
evidence instead of aborting" → true except the ZIP entry-count axis (R-N2);
(5) M6's evidence sources → the retired-id/vanity half depends on a
tool-defined repo policy file that the real winget-pkgs doesn't carry;
(6) "zero is an explicit opt-out" for the freshness delay → zero is also the
implicit behavior for provenance-less submissions; (7) the plan.md
contradictions in R.3 are undisclosed by the closeout.

### R.5 Build & test matrix (re-audit run, HEAD `43ec8c0`)

Build: **0 warnings, 0 errors** (56.6 s). Tests (`dotnet test --no-build`):

| Project | Passed | Skipped | | Project | Passed | Skipped |
|---|---:|---:|---|---|---:|---:|
| Core.Tests | 188 | 0 | | GitHub.Tests | 120 | 0 |
| Analysis.Tests | 489 | 0 | | Workflows.Tests | 389 | 0 |
| Downloads.Tests | 84 | 0 | | Cli.Tests | 420 | 0 |
| Rules.Tests | 469 | 0 | | Testing.Tests | 12 | 0 |
| Validation.Tests | 76 | 0 | | E2E.Tests | 27 | 3 |

**Total: 2,274 passed, 0 failed, 3 skipped** (opt-in live/compile gates). Live
CLI re-repros: B1 PE/ZIP exit 0 ✓, M9 secret not echoed ✓, M10 130-only-on-
cancel confirmed in code ✓, A20f anonymous read exit 0 ✓, `DRY_RUN=bogus` →
exit 3 ✓, new flags (`--approve-reviews`, `--override-store`,
`--github-api-url`, `--github-graphql-url`) present ✓.

### R.6 Updated merge recommendation

**Merge.** The original fix-first gate is fully cleared and verified. Ship the
following as immediate fast-follows (none needs to block the merge, but R-N1
should land before pointing the tool at `microsoft/winget-pkgs` at scale):

1. **R-N2** — degrade the dependency-analyzer entry-count budget to
   `Unavailable` evidence (or align the 4,096 cap with FileAnalyzer's 10,000);
   one-file fix plus a regression test.
2. **R-N1** — bound the duplicate-PR evidence cost (server-side prefilter,
   GraphQL batching, or per-(PR, headSha) caching) and add a rate-budget test.
3. **plan.md reconciliation** — fix the DRY_RUN paragraph (the env var exists;
   decide whether that's acceptable and document it, or remove the variable)
   and the stale P8-corpus PENDING note.
4. Journal-store quarantine for corrupt files; E2E override-store isolation;
   the HttpClient ownership nit; the explorer.exe false diagnostic.

---

## 0. Final remediation closeout — 2026-08-02

> **Current status supersedes §§1–6 below.** The original 2026-08-01 audit is
> retained verbatim as historical provenance. This closeout independently
> inspected the integrated production composition and reran the mechanical
> verification on branch `utesgui-audit-remediation-closeout`, code head
> `c78d0c7` (the report itself is committed immediately after that code head).
> No live GitHub mutation was performed.

### 0.1 Current conclusion and merge recommendation

**Mergeable from the code, test, and local release-engineering perspective.**
Both blockers, all 18 major findings, and every merge-affecting appendix item
are fixed. The final audit found and fixed three additional integration defects
that the remediation ledger missed:

1. `f7cbe52` keeps generic preflight re-download artifacts alive until their
   caller consumes them and adds a regression test for the former deleted-path
   contract (A19).
2. `1703c4c` replaces NuGet-deprecated, unlisted SharpCompress 1.0.0 with the
   matured listed 0.50.1 API, updates the test SDK/runner, and reconciles
   release-facing CLI/GHES/anonymous-read/review/line-ending documentation.
3. `c78d0c7` fixes a Windows cache metadata publication race reproduced on
   iteration 8 of the closeout race loop; 20 clean six-test iterations passed
   after the fix.

Accepted residuals are limited to A9's audit-attribution precision, the absence
of committed NuGet lockfiles, deferred artifact attestations and repository
settings that require administrator enforcement, manual branch deletion, and
opt-in live GitHub tests that were deliberately not run because this audit
prohibited remote mutation. None is a blocker or major product-behavior gap.

### 0.2 Blockers and major findings

| ID | Status | Current production/test evidence | Fix commit(s) / residual |
|---|---|---|---|
| B1 | **Fixed** | Oversized PE/archive dependency analysis returns bounded unavailable evidence instead of aborting (`PayloadDependencyAnalyzer`); live 70 MiB PE and 70 MiB ZIP-with-PE both exit 0. | `f75e87a`, `413bbf5`, `d827bb8`; conservative evidence can require review. |
| B2 | **Fixed** | `HumanCorrectionDetector` uses duplicate-safe occurrence queues; flagship 1→2 same-URL twin tests pass. | `0324226`. |
| M1 | **Fixed** | Validation and WM0102 share `InstallerDuplicateRelation`; wildcard scope/locale and nested-type cases are pinned. | `954af82`. |
| M2 | **Fixed** | Production composes `ApplyOverridePackFieldsRule` and `FileOverridePackStore`; approved corrections stage, commit, activate, recover, and are consumed on the next run. | `e2f9465`…`de56935`. |
| M3 | **Fixed** | Duplicate PR discovery is author-independent and proves association from changed files or pinned head content. | `f7b81d2`, `807e90c`; bounded evidence fails closed. |
| M4 | **Fixed** | New/Update/Add/Remove titles use canonical WinGet prefixes; custom titles remain explicit. | `f7b81d2`. |
| M5 | **Fixed** | Fresh branch reservations are adopted, exact planned commits recover, and conflicts use bounded suffixes. | `f7b81d2`; deletion remains manual by design. |
| M6 | **Fixed** | Production fetches pinned policy, retired identifiers, sibling hashes, duplicate hashes, and vanity annotations before planning. | `807e90c`, `de56935`. |
| M7 | **Fixed** | REST tree deletion serializes explicit `sha: null`; recorded body assertion passes. | `793aeea`. |
| M8 | **Fixed** | JSON/noninteractive questions and reviews emit the auditable envelope before exit 4; `--approve-reviews` is explicit and fingerprint-bound. | `a64c13f`…`72fde4d`. |
| M9 | **Fixed** | Shared `CliRedactor` covers host/parser/output/question/maintenance paths; live secret-shaped parse input is absent from stdout/stderr. | `a64c13f`. |
| M10 | **Fixed** | Declines consistently return 5; only cancellation returns 130. | `a64c13f`. |
| M11 | **Fixed** | Inno ANSI decoding uses bounded replacement/code-page fallback with diagnostics. | `f75e87a`, `d827bb8`. |
| M12 | **Fixed** | Inno architecture uses payload dominance and 64-bit-mode hints; mixed/absent proof is inconclusive and diagnosed. | `f75e87a`, `413bbf5`. |
| M13 | **Fixed** | Wrapper-aware dependency inspection analyzes bounded Inno/Squirrel payloads and never claims unsupported outer-stub absence. | `f75e87a`, `2156f60`. |
| M14 | **Fixed** | Schema-invalid documents do not materialize; oversized success codes become structured validation findings. | `954af82`. |
| M15 | **Fixed** | `SafeManifestFile`/`ManifestYamlDocument` enforce byte, depth, node, alias, and reparse-point boundaries before materialization. | `954af82`. |
| M16 | **Fixed** | `InstallerVersionTrustPolicy` admits conservative direct-PE/single-ZIP version evidence and rejects wrappers/placeholders. | `e2f9465`…`de56935`. |
| M17 | **Fixed** | All 13 descriptors run the production plan/rules/emit path to exact YAML goldens. | `6e267a6`…`aacf0f0`. |
| M18 | **Fixed** | Production maintenance uses durable feedback state and an allowlisted native repair planner that re-enters full lifecycle/preflight. | `a64c13f`…`7e1f7a7`. |

### 0.3 Appendix finding disposition

Status vocabulary for this closeout is exactly: **Fixed**, **Deliberately
Deferred**, **Not Reproducible**, and **Still Open**.

| ID | Status | Current evidence / residual |
|---|---|---|
| A1 | **Fixed** | Inno emits manual-analysis diagnostics for ambiguous/mixed payload architecture. |
| A1b | **Fixed** | Empty architecture expressions consult embedded payload and 64-bit-mode hints. |
| A1c | **Fixed** | Legacy flags distinguish strict x86/x64; IA64 is preserved as manual-only evidence. |
| A1d | **Fixed** | Future Inno setup-data versions become manual analysis, not process failure. |
| A1e | **Fixed** | Unsupported bzip2 evidence is explicit (`INNO003`). |
| A1f | **Fixed** | Privilege overrides preserve scope/elevation uncertainty. |
| A1g | **Fixed** | ARM64-only Burn chains no longer silently inherit an x64 stub. |
| A1h | **Fixed** | Squirrel architecture inspects nested nupkg PE payloads. |
| A1i | **Fixed** | Advanced Installer MSI stream length is checked before allocation. |
| A1j | **Fixed** | ZIP budgets count actual reads, compressed bytes, operations, entries, and central-directory bounds. |
| A1k | **Fixed** | Dead/unreachable advanced-analysis branches and no-op catches were removed; bounded error taxonomy remains. |
| A2 | **Fixed** | JWT recognition no longer redacts dotted package identifiers; secret-shaped cases remain covered. |
| A3 | **Fixed** | Apply and log-only snapshot failures both fail closed. |
| A4 | **Fixed** | Compatibility factories delegate to the single `ProductionRuleComposer` catalogue. |
| A5 | **Fixed** | `META-4-bullets` is independently configurable and production-composed. |
| A6 | **Fixed** | Dead Chrome `ARP-1` annotation removed (`894bc91`). |
| A7 | **Fixed** | URL identity preserves bare architecture tokens. |
| A8 | **Fixed** | Duplicate/multi-locale matching is bounded and occurrence-aware. |
| A9 | **Deliberately Deferred** | Reverse-order ARP removals can fall back to generic source attribution after index shifts; output, safety, and approval decisions are unaffected. |
| A10 | **Fixed** | Policy evidence dictionaries are case-insensitively unique and deterministically ordered; dead helper removed. |
| A11 | **Fixed** | Dead stale-PID recovery removed; held remote locks are cleanup-safe. |
| A12 | **Fixed** | Lock roots use application state and canonical/symlink-safe identity; held files cannot be moved by cleanup. |
| A13 | **Fixed** | Apply, rollback, and cleanup failures remain visible together. |
| A14 | **Fixed** | Successful safety revalidation is not failed by scratch cleanup; recovery diagnostics are retained. |
| A15 | **Fixed** | Snapshot contention maps to structured conflict; cancellation remains cancellation. |
| A15b | **Fixed** | Default freshness delay is four hours; zero is an explicit opt-out. |
| A15c | **Fixed** | Provenance is captured only for tool-created manifests and finalizes atomically with learned state. |
| A16 | **Fixed** | Cache maintenance removes only old, owned, inactive temporary files. |
| A17 | **Fixed** | Cache process locking has deadline/cancellation/recovery; closeout also replaced existing metadata with `File.Replace` after reproducing an intermittent Windows publication race (`c78d0c7`). |
| A17b | **Fixed** | Retry delays are capped; pagination enforces origin, loop, page, and item limits. |
| A17c | **Fixed** | GitHub fallback/retry decisions use structured error kinds, not message text. |
| A17d | **Fixed** | Injected `HttpClient` ownership is explicit and tested. |
| A17e | **Fixed** | Downloader disposal cancels and drains in-flight work; 20 post-fix race iterations pass. |
| A17f | **Fixed** | Head filtering uses full owner/ref identity and authoritative PR evidence. |
| A17g | **Fixed** | GHES GraphQL is safely derived from REST; explicit endpoints must remain same-authority and are exposed by the CLI. |
| A18 | **Fixed** | Validated `--created-with-url` reaches local manifests and the submission request. |
| A19 | **Fixed** | Workflow and generic validation re-download paths now retain usable artifacts through consumption; `Downloader_adapter_keeps_fresh_revalidation_artifact_alive_for_its_owner` pins the generic contract (`f7cbe52`). |
| A19b | **Fixed** | Unparseable display versions never create ordinal ranges; exact overlap still blocks. |
| A19c | **Fixed** | `$schema` must be the exact first line. |
| A19d | **Fixed** | Schema-valid empty collections/mappings survive round-trip. |
| A19e | **Fixed** | Existing LF/CRLF convention is preserved; new files use LF. The integration ledger's deferred classification was stale. |
| A19f | **Fixed** | `ManifestVersion.Default` is the sole `1.12.0` literal used by validation. |
| A20 | **Fixed** | Structural/stable-URL options are registered only on `update`. |
| A20b | **Fixed** | `--edit` + `--replace` rejects before workflow creation. |
| A20c | **Fixed** | Hidden dead-version flow rejects multi-version input before remote inspection and fully inspects one version. |
| A20d | **Fixed** | Fish completion descriptions are generated and shell-safe. |
| A20e | **Fixed** | Config mutation rejects unknown keys and refuses comment-losing rewrites. |
| A20f | **Fixed** | Public reads are anonymous-capable and retry anonymously after stale optional tokens. |
| A20g | **Fixed** | Platform URL launch uses one literal argument and propagates nonzero/cancellation. |
| A20h | **Fixed** | Shared CLI JSON contracts pin enum naming and compact newline-terminated output. |
| A21 | **Fixed** | Every action is pinned to a current full release SHA with a version comment; live tag/SHA resolution matches all six pins. |
| A22 | **Fixed** | Workflows default to `contents: read`; only draft release creation has `contents: write`; checkout never persists credentials. |
| A23 | **Fixed** | Dead HTTP recording infrastructure and obsolete smoke tests are gone; E2E drives production paths. |
| A24 | **Fixed** | Real-process parser tests inject and reject four realistic secret shapes. |
| A25 | **Fixed** | Tautological assembly-load smoke removed and replaced by process/pipeline/lifecycle tests. |
| A26 | **Fixed** | Shared fixture semantics and exact YAML goldens eliminate divergent parsers. |
| A27 | **Not Reproducible** | Remaining `C:\...` values are manifest/test data, not host filesystem operations; no production hard-coded Windows path was found. |
| A28 | **Fixed** | Architecture documentation states eight production projects plus shared test infrastructure. |
| A29 | **Fixed** | CI has a pinned Windows compiled-corpus job; this closeout compiled and analyzed Inno, NSIS, WiX MSI, duplicate MSI, Burn, and MSIX successfully. |

No original blocker, major, appendix item, or appendix subitem is **Still
Open** after the closeout commits.

### 0.4 Production composition re-verification

The final read traced the sole production factories, not convenience/test
constructors:

- `ProductionCliComposition` binds `DRY_RUN`, GHES REST/GraphQL endpoints,
  output/interaction, redaction, diagnostics, mutation, submission,
  maintenance, cache, token, and completion surfaces.
- `WorkflowProductionComposition` injects populated repository and PR evidence
  providers, the complete rule catalogue, durable override/provenance stores,
  and the durable preflight network.
- Local completion stages learned state, commits manifest/provenance
  atomically, then activates the learned pack under a consistent lock order.
- Submission recovery prepares before local mutation, activates after the exact
  local commit, records every remote boundary, and removes the journal only
  after verified PR creation.
- Feedback replay uses durable state, authoritative changed-file evidence, the
  allowlisted native repair planner, and the full GitHub lifecycle. No null
  planner or empty evidence provider is used by production.

No stale production factory, duplicate persistence owner, lost merge wiring,
or lock-order inversion was found.

### 0.5 Build, test, race, corpus, publish, and lint evidence

All results below are from code head `c78d0c7` after the final fixes:

| Verification | Result |
|---|---|
| Restore/build | 19 projects restored; Release build succeeded with 0 warnings and 0 errors. |
| Full serialized tests | **2,274 passed, 0 failed, 3 skipped** (the opt-in compiled/live E2E gates). |
| Full default-parallel tests | **2,274 passed, 0 failed, 3 skipped**. |
| Historical repro matrix | Rules 2 + validation 8 + CLI 7 + GitHub 8 + workflows 14 = **39 passed**. |
| Download race closeout | Initial loop reproduced 1 metadata-publication failure on iteration 8; after `c78d0c7`, 6 tests × 20 iterations = **120 passed**. |
| Learned/provenance races | 8 tests × 10 iterations = **80 passed**. |
| Journal/PR-evidence races | 12 selected cases × 10 iterations = **120 passed**. |
| Live local CLI | Version/help/analyze-help/completion/config-path, 70 MiB PE, 70 MiB ZIP containing a valid PE, and secret-shaped parse error = **8 expected exits/contracts passed**. |
| Windows compiled corpus | Pinned/hash-checked Inno 6.4.0, NSIS 3.10, WiX 5.0.2/Bal, and Windows SDK MakeAppx produced six formats; **1 test passed, 0 skipped**. |
| Regression corpus | 13 descriptor-to-production-pipeline exact-YAML cases passed in E2E. |
| Trimmed RID publish | `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`: **6/6** self-contained single-file outputs contained LICENSE/notices; native `win-x64` five-command smoke passed. |
| Workflow lint | actionlint **1.7.12**, 2 workflows, no findings. |
| Workflow security | zizmor **1.29.0**, offline auditor persona, minimum low: no findings (one tool-reported ignore). |
| Live action SHA map | checkout v7.0.1, setup-dotnet v6.0.0, upload-artifact v7.0.1, download-artifact v8.0.1, zizmor-action v0.6.2, and action-gh-release v3.0.2 all resolve to the committed workflow SHAs. |

### 0.6 Dependency, license, notice, and supply-chain closeout

Direct shipped pins are JsonSchema.Net 8.0.5 (documented MIT/license-term
hold-back), YamlDotNet 18.1.0, OpenMcdf 3.2.0, SharpCompress 0.50.1,
Spectre.Console 0.57.2, and System.CommandLine 2.0.10. Test pins are
Microsoft.NET.Test.Sdk 18.8.1, xunit 2.9.3, and
xunit.runner.visualstudio 3.1.5. SharpCompress 1.0.0 was removed because NuGet
marks it unlisted/deprecated as an accidental build; 0.50.1 was the newest
listed patch outside the repository's three-day cooldown on the verification
date. The restored runtime closure/license/notice drift guard passed all four
checks.

The 2026-07-30 hardening baseline was refreshed against GitHub's current
Actions secure-use reference. No newer guidance changed this repository's
required controls: immutable action SHAs, least-privilege token permissions,
no privileged untrusted checkout, no event-data shell interpolation,
`persist-credentials: false`, hosted runners, CODEOWNERS, and Dependabot
cooldown remain current. The workflows contain no `pull_request_target`,
`workflow_run`, self-hosted runner, `secrets.*`, or untrusted
`github.event.*` shell interpolation.

Residual supply-chain controls:

- **Deliberately Deferred:** NuGet `packages.lock.json` files and central
  transitive pinning are absent. Restore plus the runtime-closure drift guard
  verifies the resolved graph, but the repository does not cryptographically
  freeze every transitive package.
- **Deliberately Deferred:** artifact attestations until the private repository
  has a supported verification/enforcement path
  (`.github/SECURITY-SETTINGS.md`).
- **Administrator action:** enforce the documented Actions allow-list,
  SHA-only policy, branch/tag rulesets, dependency graph/alerts/security
  updates, secret scanning, and push protection in repository settings.

### 0.7 Originally skipped audit areas

| Original skipped area | Closeout status | Disposition |
|---|---|---|
| Unchanged MSI/NSIS/Cabinet analyzer internals | **Deliberately Deferred** | Not re-reviewed line-by-line outside remediation paths; full analysis tests and compiled corpus pass. |
| Legacy WM0001–WM0101/0103 rule bodies | **Deliberately Deferred** | Not re-reviewed body-by-body; complete rule suite and production-catalogue boundary tests pass. |
| `InstallerFieldAccessors.cs` | **Not Reproducible** | Independently inspected; explicit full-field clone/accessor coverage exists and no defect was found. |
| ~50 remaining `RuleRuntimeTests` bodies | **Deliberately Deferred** | Not individually re-derived; the full 469-case rules project passes. |
| Tail halves of largest Analysis/GitHub tests | **Deliberately Deferred** | Not assertion-audited line-by-line; full projects pass and targeted safety cases were rerun. |
| Untracked `DESIGN.md` / `PRODUCT.md` | **Not Reproducible** | Absent from this tracked worktree and never touched. |
| Live-network E2E | **Deliberately Deferred** | Token-gated by design; prohibited here to avoid live GitHub mutation. Read-only live action metadata was verified. |
| NuGet latest/license review beyond six runtime packages | **Fixed** | All direct runtime/test pins and the restored runtime closure were checked in this closeout. |

---

## 1. Original executive summary (2026-08-01; superseded)

**Historical snapshot:** retained to preserve the original audit provenance.
The current conclusion and recommendation are in §0.

The branch is far more real than a typical overnight agent run: the solution builds with 0 warnings, all 1,781 tests pass (+3 opt-in live tests skipped, exactly matching plan.md's claim), there are **zero** TODO/FIXME/NotImplementedException markers in `src/`, docs are unusually accurate, and hard sub-systems (journaled file transactions, cache process-locking, GitHub race handling, adversarial YAML/ZIP validation) are genuinely engineered rather than stubbed. However, it is **not mergeable as-is**. Two blockers exist: (B1) the payload-dependency analyzer hard-fails on any `.exe`/`.zip` over 64 MiB and is called unguarded by both `analyze` and the update/new acquisition path — reproduced live: `winmatsch analyze` on a 70 MB PE exits 1 with "exceeds the analysis limit" — which makes the tool unusable for a large share of real winget packages (Electron apps, VS Code-class installers); and (B2) the human-correction detector crashes with an unhandled `ToDictionary` duplicate-key `ArgumentException` on the same-URL twin-entry pattern (SurrealDB/DUP-2) that the spec itself designates a flagship scenario. Beyond that, the spec's self-declared "single highest-leverage feature" — per-package *memory* (§0.2) — is only half-delivered (detection and halt work; nothing ever persists a learned override, and three override-pack fields are parsed but consumed by nothing), the DUP-1 duplicate-entry gate implements a key that diverges from winget's actual comparator in both directions, and the WORK-1 duplicate-PR gate cannot see other tools' PRs. A cluster of "built-but-unwired" seams (retired-identifier/duplicate-hash evidence, feedback auto-repair, PE-version trust) shows the parallel workstreams were assembled but not fully composed. The biggest merge risk is therefore **not** hidden breakage of what exists — it is that several spec-critical behaviors quietly do less than plan.md and the test-green status imply. Recommendation: **fix-first, then merge** (list in §5); the codebase quality justifies completion rather than rework.

---

## 2. Blockers and major findings (ranked)

Status legend: **confirmed-live** = reproduced against the built binary by the coordinator; **confirmed** = independently re-read/re-derived from code by the coordinator; **confirmed-agent** = verified by the unit subagent with cited evidence, spot-checked but not independently re-derived; *plausible* = reported, not fully verified.

### Blockers

- **B1 · Any installer > 64 MiB aborts analyze/update/new — confirmed-live**
  `src/WinMatsch.Analysis/Dependencies/PayloadDependencyAnalyzer.cs:35,393-410` (throws `InvalidDataException` when the stream exceeds `MaximumPayloadBytes`, default 64 MiB per `PayloadDependencyAnalyzerOptions.cs:7`; ZIP path throws likewise per entry at `:69-72`) — called **unguarded** for every `.exe`/`.zip` by `src/WinMatsch.Workflows/Operations/ProductionAdapters.cs:129-134` (update/new acquisition) and `src/WinMatsch.Workflows/Diagnostics/InstallerDiagnosticService.cs:123` (`analyze`).
  *Failure scenario:* `winmatsch update SomeElectronApp` (typical NSIS installers are 70–150 MB) downloads the asset, then dies. Reproduced: `winmatsch analyze <70MB exe>` → `Unexpected error: Payload 'big-setup.exe' exceeds the analysis limit of 67108864 bytes.`, exit 1 (not even the documented failure exit-code class). There is no CLI/config knob to raise the limit and no degrade-to-no-evidence path.

- **B2 · Human-correction detector crashes on the DUP-2 same-URL twin pattern — confirmed (static trace)**
  `src/WinMatsch.Rules/HumanCorrectionDetector.cs:25-29` builds a `ToDictionary` keyed on `(DocumentKey, SemanticPath)` with no duplicate-key handling. `ManifestSnapshot.cs:1745-1751` excludes `Architecture` from installer identity, and matched pairs use the *before*-occurrence ordinal (`:1456-1458`) while unmatched added entries use the *after*-occurrence ordinal (`:1494-1508`) — so "bot originally submitted 1 entry, human added a same-URL twin (x86+x64 SurrealDB pattern), tool regenerates the preserved 2-entry layout" produces two changes with the identical `{installer:HASH#0}` semantic path → `ArgumentException` propagates uncaught through `RulePipeline.Run` (`RulePipeline.cs:112`), crashing the run instead of raising a review. This is precisely the learned-override flow the spec calls the highest-leverage feature, on a package pattern in the spec's own §13 corpus. Existing tests only cover 2v2 and distinct-URL cases.

### Major findings

- **M1 · DUP-1 gate key is wrong in both directions — confirmed**
  (a) `src/WinMatsch.Validation/ManifestSemanticValidator.cs:175-179` (VLD3001) treats absent/`Unknown` scope/locale as a *distinct* key value, but winget-cli's duplicate comparator treats Unknown as **equal to every value**; `{x64, exe, no-scope}` + `{x64, exe, user}` passes this gate and then dies in the winget pipeline with "Duplicate installer entry found" — the exact dead-PR class DUP-1 exists to kill (verified against winget-cli `ManifestValidation.cpp` semantics by the unit agent; code side re-verified by me). It also omits `NestedInstallerType`, so legal archive twins are hard-blocked (false positive).
  (b) The Rules-side twin WM0102 (`src/WinMatsch.Rules/Rules/DuplicateInstallerEntriesRule.cs:37`) uses a **3-part** key (`Architecture|InstallerType|Scope`, no locale — `EffectiveInstallerValues.GetEntryKey`), so a legitimate multi-locale manifest gets an Error finding, and the two gates contradict each other.

- **M2 · §0.2 per-package memory is detection-only — confirmed (grep-verified)**
  `OverridePackYaml.WriteFile` (`src/WinMatsch.Rules/Overrides/OverridePackYaml.cs:121`) has **zero callers** in `src/`; detected human corrections only halt for review (`LocalWorkflowEngine.cs:712`, `WorkflowModels.cs:147`) and are never persisted, so the same halt recurs every version — the spec's "single highest-leverage feature" has no memory. Additionally `OverridePack.ScopeLayout`, `.MetadataUrlReplacements`, `.PreservedFields` (`OverridePack.cs:22,26,29`) are parsed, serialized, round-trip-tested — and **consumed by nothing**: a maintainer writing the §0.2-mandated scope-layout or URL-replacement override gets a silent no-op.

- **M3 · WORK-1 duplicate-PR gate cannot see other tools' PRs — confirmed**
  `src/WinMatsch.Workflows/GitHub/GitHubLifecycleWorkflow.cs:736-748`: a PR counts as duplicate only if the title contains id **and** version **and** the body contains the winmatsch HTML marker or the version directory path. Komac/YamlCreate/human PR bodies contain neither → winmatsch opens a second PR → `Possible-Duplicate` (the top WORK-1 failure class). The spec demands title-token *or* manifest-path search across *any author*. Test fakes always use winmatsch-format bodies (`GitHubLifecycleTestSupport.cs:99-101`), masking this.

- **M4 · PR/commit title convention deviates from the pinned UpdateState format — confirmed**
  `src/WinMatsch.Workflows/GitHub/GitHubSubmissionFormatter.cs:18` renders `"{operation}: {id} version {ver}"` → "Update: Foo.Bar version 1.2.3". plan.md:26/70 pins Komac/winget-pkgs convention "New version:", "Update version:", "Add version:", "Remove version:". Moderation tooling and other bots' `in:title` duplicate searches key on these canonical prefixes — this also *worsens* M3 in the reverse direction (other tools won't recognize winmatsch PRs as duplicates).

- **M5 · Crash between branch creation and PR creation bricks that package-version — confirmed-agent**
  Deterministic branch name with no run suffix (`GitHubLifecycleContracts.cs:36`), no adopt-existing-branch path, and cleanup requires an associated **closed PR** while Apply-mode cleanup always returns `HumanEscalationRequired` (`GitHubMaintenanceWorkflow.cs:134-136,163-173`). Crash/cancel after `CreateUniqueReferenceAsync` (`GitHubLifecycleWorkflow.cs:274`) but before PR creation → every retry gets 422/Conflict forever until a human deletes the branch.

- **M6 · Retired-identifier / sibling-hash / vanity-URL submission gates are vacuous in production — confirmed (grep-verified)**
  `RepositoryInstallerEvidence.RetiredIdentifier` (`GitHubLifecycleModels.cs:104`) is never read; no production caller populates `GitHubSubmissionRequest.RepositoryEvidence`, `.Policy.DuplicateHashes`, or `.VanityUrlAnnotations` (CLI wiring at `MutationCommandModule.cs:1014-1066` omits all three), so GH1010/GH1011 (WORK-1 #3, the MongoDB.Compass.Community class) can never fire outside tests.

- **M7 · REST-fallback commits silently break deletions — confirmed**
  `GitHubJsonContext.cs:7` sets `DefaultIgnoreCondition = WhenWritingNull`; deletion entries are `CreateTreeEntryDto { Sha = null }` (`GitHubRepositoryClient.cs:921-925`, DTO at `GitHubTransportDtos.cs:360-369` with no per-property override) → the serialized tree entry has *neither* `sha` nor `content`, which GitHub's create-tree API rejects (422). Any `--replace`/remove flow that takes the REST fallback (GraphQL classified unavailable, `GitHubRepositoryClient.cs:1123`) fails. The recorded test passes a deletion but never asserts the serialized body shape.

- **M8 · JSON/non-interactive automation is hard-walled out of the two states it most needs — confirmed**
  `MutationCommandModule.cs:337-353`: when a plan `RequiresReview`, `EnsurePrompting` throws `MissingInputException` **before** `MutationOutput.Write`, so in `--format json` / `--interaction never` the documented `requiresReview:true` + `rules.reviews` envelope (docs/commands.md:155-158) is never emitted (exit 4, empty stdout) and `--yes` does not approve reviews despite docs listing it as the approval flag; the same pattern makes the documented `questions` array unreachable in JSON mode (`:617-621`). CI usage cannot even *read* what needs approving.

- **M9 · Secret-redaction inconsistencies in the CLI — confirmed-live (first item)**
  (a) Parse errors echo raw argument values: `winmatsch config path --tokn ghp_FAKE…` prints the token verbatim on stderr (reproduced; `CliHost.cs:60-66` has no redaction). (b) `questions[].path` is written **unredacted** in the JSON envelope (`MutationOutput.cs:232` uses `WriteNullable` while the sibling prompt/options fields go through `Redact` — re-verified by me); question paths carry installer URLs which routinely embed presigned query-string secrets. (c) Two divergent redaction engines (`MutationOutput.cs:425-440` vs `GitHubSubmissionFormatter.cs:124-131` — the latter has no `ghp_`/`github_pat_` shape rule), plus unredacted `CliHost` exception paths (`CliHost.cs:131-184`) and maintenance plan/diagnostic renderers (`MaintenanceCommandHelpers.cs:148-201`) whose doc comment claims "secrets stay redacted".

- **M10 · Exit-code contract drift: declined confirmation = 130 vs 5 depending on command family — confirmed**
  Maintenance/cache decline returns `ExitCodes.Cancelled` (130) (`MaintenanceCommandModule.cs:108,195,272,296,362`; `CacheCommandModule.cs:141,197`), mutations return 5 on decline, and docs/commands.md:86 defines 130 as "Ctrl+C, POSIX 128+SIGINT". Scripts cannot distinguish user-abort from SIGINT; both behaviors are pinned by tests, so the drift is baked in.

- **M11 · ANSI Inno installers with non-cp1252 strings crash analysis — confirmed**
  `src/WinMatsch.Analysis/Inno/InnoFormatReader.cs:313` first-pass-decodes every ANSI header as cp1252 with `DecoderFallback.ExceptionFallback` (`:1031-1042`); a genuine ANSI Inno 5.5.x installer with any byte undefined in cp1252 (0x81/0x8D/0x8F/0x90/0x9D — common Shift-JIS lead bytes) throws `DecoderFallbackException` (derives from `ArgumentException`, caught by none of the `InvalidDataException`-shaped probe contracts) and kills the whole file analysis. Also `checked((int)codePage)` at `:324` can throw uncaught `OverflowException` on a hostile header.

- **M12 · ARCH-4 only half-implemented: `x86compatible` still becomes *conclusive* x86 — confirmed**
  `src/WinMatsch.Analysis/Inno/InnoProbe.cs:158-209`: the payload-PE override fires only when payload evidence has exactly one distinct architecture (`:178-183`); with absent (bzip2 payloads yield none — `InnoFormatReader.cs:668,678-686` swallows them silently; >64 MB scan window) or mixed-arch payloads, `x86compatible` returns `(X86, Conclusive=true)` (`InnoProbe.cs:195-201`), telling downstream not to second-guess — recreating the Keeper-Commander x86→x64 fix-PR class the rule exists to kill. Payload sizes are collected but never weighted ("largest embedded PE" per spec). Tests codify this behavior rather than challenge it.

- **M13 · DEP-1 evidence is stub-only for exe installers — confirmed**
  `PayloadDependencyAnalyzer.cs:30-37`: for `.exe` the analyzer inspects the **outer stub PE's** imports only (payload never decompressed) and reports a confident `VisualCppRuntime=Absent` — exactly wrong for the wrapped-payload cases DEP-1 cites (S3Drive/UltraGrid VCRedist class). Interacts with B1 (same method).

- **M14 · Preflight gate can crash instead of failing closed on numeric overflow — confirmed**
  `src/WinMatsch.Validation/ManifestPackageParser.cs:67-115`: materialization runs even when schema validation already produced errors, and the catch list (YamlException/FormatException/ArgumentException) omits `OverflowException` — a 20-digit `InstallerSuccessCodes` value (passes the 256-char numeric budget; schema violation is only a finding) reaches `long.Parse` (`ManifestYamlReader.cs:322,351`) → uncaught `OverflowException` out of `PreflightGate.ValidateAsync`.

- **M15 · A second, completely unbudgeted YAML entry point bypasses every cffa8af protection — confirmed**
  `src/WinMatsch.Core/PackageManifestIO.cs:24-77` does unbounded `File.ReadAllText` + `ManifestYamlReader`/`YamlStream.Load` with no byte/depth/node budget and no alias ban (all budgets live only in `ManifestSchemaValidator.ConvertYamlToJson`). Consumed by `OriginalSubmissionStore.cs:47` and `ProductionAdapters.cs:244` on **repo-sourced previous manifests** (attacker-influenceable content in a public repo): deep-nesting → `StackOverflowException` (uncatchable process kill); multi-GB planted `.yaml` → full materialization. `PreflightRequest.FromDirectory` (`PreflightModels.cs:85`) also `ReadAllText`s before its 16 MB check.

- **M16 · VER-1 tier 2 (binary version) never applies to exe/zip-only releases — confirmed**
  `ProductionAdapters.cs:141-144` marks product versions trustworthy only for `Msi/Msix/MsixBundle`; PE `VS_VERSIONINFO` is never a version candidate, so an exe-only release with a build-style tag (`b4088`) resolves the tag as PackageVersion — the CumulusMX failure class — unless a per-package override exists.

- **M17 · §13 regression corpus never exercises the pipeline it exists to protect — confirmed-agent**
  All 13 descriptor/expected pairs exist, but every fixture plan asserts `CanApply == false` via `ANALYSIS_METADATA_ONLY` (`RegressionDescriptorE2ETests.cs:60-63`), `Expected/*.json` are reduced installer-topology snapshots (not the spec-mandated "final merged manifests"), and the mapping theory seeds `PreviousInstallers` *from* the expected snapshot (`AssetMappingFixtureTests.cs:39-42`) — verifying layout preservation, not derivation. A regression in generate/rules/merge for these exact packages would pass untouched.

- **M18 · WORK-4 feedback loop is report-only in production — confirmed-agent**
  The auto-repair path exists and is forced through full preflight (`GitHubFeedbackWorkflow.cs:47-133`), but production wires `NullApprovedRepairPlanner` (always null — `MaintenanceGitHubAdapters.cs:397-404`), and retry metadata is returned, never persisted — so nothing is ever auto-fixed and no learning survives the process (Jellyfin.FFmpeg ×4 zero-learning is still possible). Disclosed in code as intentional composition, but plan-spec-wise this is "partial".

---

## 3. Original spec coverage (2026-08-01; superseded)

### 3.1 plan.md (branch scope: P4-remainder → P8 + command surface; P0–P5 were already on `main`)

| Item | Verdict | Evidence / gap |
|---|---|---|
| P4: Inno analyzer (loader, versioned header, arch, privileges→scope/elevation, AppId→ProductCode, DefaultDirName, language) | **partial** | Implemented within pinned 5.5.7–6.4.0.1 (`InnoFormatReader.cs:210-214` hard-throws outside); ARCH-4 gap (M12); cp1252 crash (M11); `UnsupportedOSArchitectures` **never emitted by any analyzer** (grep-clean across src/) despite plan.md:21 |
| P4: AdvancedInstaller (7z SFX) | **fully** | `Advanced/AdvancedInstallerProbe.cs:29-108` (footer/table/XOR/nested-7z); one hardening drift: unbounded `new byte[entry.Length]` in `AdvancedMsiProperties.cs:69-75` |
| P4: Squirrel (nupkg) | **fully** | `Squirrel/SquirrelProbe.cs:37-61`; arch from nupkg name else x86 stub — payload PEs never inspected (minor) |
| P4: probe chain order AdvancedInstaller→Burn→Inno→NSIS→Squirrel→generic | **fully** | `ExeAnalyzer.cs:18-25`, order pinned by test |
| P5→branch: schema validation (was "seam only") | **fully** (better than plan) | Real JsonSchema.Net Draft-07 validation, embedded byte-pinned schemas verified semantically identical to upstream winget-cli 1.12.0 (`ManifestSchemaValidator.cs:16-131`, `SchemaParityTests.cs`); plan.md:120 claim now stale in the *conservative* direction |
| P6: GitHub client (repo info, createRef, createCommitOnBranch, createPullRequest, PR search, compare, merge-upstream) | **fully** | `GitHubRepositoryClient.cs` (verified commits via GraphQL + CAS REST fallback — deletion bug M7) |
| P6: release metadata → locale (notes, date, **license, topics→tags, publisher URL**) | **partial** | notes/date/assets fully; license/topics/publisher-URL **not fetched anywhere** (`RestRepositoryDto`/GraphQL query lack the fields) |
| P6: update/new/submit/remove flows, installer matching, existing-PR check, `--replace` same-commit delete, auto-fork w/ consent, sync, cleanup | **mostly** | Flows + matching + `--replace` (`LocalWorkflowEngine.cs:438-452`) + fork consent + safe sync ✓; commit titles ✗ (M4); duplicate check partial (M3); cleanup is plan-only — Apply always escalates (`GitHubMaintenanceWorkflow.cs:163-173`) |
| P6: UpdateState commit titles | **missing** | M4 |
| P7: interactive prompts everywhere + automation flags | **fully** for input/confirm; **partial** for review gate (M8) |
| P7: preview/diff/edit loop | **partial** | before/after ±blocks, no rich diff; `$EDITOR` is single-pass with full preflight rerun, not an edit-until-valid loop (`MutationContracts.cs:273-351`) |
| P7: locale commands, completions, config, cache | **fully** | `new-locale`/`update-locale`, static bash/zsh/fish/powershell (fish descriptions dead), config show/set/unset/path, cache list/inspect/clear/prune |
| P7: download progress events UI | **missing** | plain `ReportStatus` lines only; no Spectre progress despite plan.md:55 |
| P8: hermetic E2E + safety contracts | **fully** on safety (fail-closed acquisition, mutation allow-list `b0t-at/winmatsch-e2e` hard-asserted, `GITHUB_TOKEN` cleared, only 3 env-gated live tests) | "full update+submit dry-run against configurable fork" is realized as 1 read-only GraphQL call + in-process fakes — **partial** vs plan.md:79 |
| P8: analyzer corpus compiled in CI (Inno/NSIS/WiX on windows job) | **missing (substituted)** | No such CI job; replaced by in-code synthetic fixture builders — defensible but an undocumented plan deviation |
| P8: docs, release engineering | **fully** | Every H1 claim in plan.md:135-140 verified individually true (LICENSE/NOTICES wiring, release.yml tag-validation/per-RID verify/SHA256SUMS-checked/fail_on_unmatched_files, ci.yml trx-on-failure); "1781 tests green (3 skipped)" **exactly matches** the central run |
| Command surface (17 commands + hidden) | **fully** with 3 caveats | All present incl. hidden `remove-dead-versions`; `complete` repurposed to PR-lifecycle inspection with shell scripts under `completion` (documented); `remove-dead-versions` submission deliberately unwired (always exit 5 "not wired up yet" — disclosed in docs); **`DRY_RUN` env var missing** (flag exists; env var promised in plan.md:61 appears nowhere) |
| Global flags | **fully** | incl. `--url 'url\|arch\|scope\|displayVersion'` (exact-4-part split, whitelisted, injection-safe), `--replace [ver]`, `--prtitle`, `--skip-pr-check`, `--resolves`, `--created-with(-url)` (provenance drift minor A18) |

### 3.2 rules_to_implement.md

| Rule | Verdict | Where / gap |
|---|---|---|
| §0.1 feature flags, log-only, per-pkg mode, pre/post log, PR lists rule IDs | **fully** | 4-layer mode resolution (command>package>user>default); true clone-based log-only simulation (`RulePipeline.cs:189-231`); PR body lists executions (`GitHubSubmissionFormatter.cs:106-120`); degraded by over-redaction (A2) and silent snapshot-capture fallback (A3) |
| §0.2 override store consulted after detection / before emission | **partial** | Forced-arch, asset→entry mapping, version source, drop-fields, vanity-url, manual-only all consumed (`AssetMappingPlanner.cs:430,444,458`; `PackageVersionResolver.cs:61,119`; `Meta5…:47`); **ScopeLayout / MetadataUrlReplacements / PreservedFields dead (M2)** |
| §0.2 learned overrides (three-way diff, keep-human-value-or-halt) | **partial** | Detection + halt genuinely work end-to-end; **no persistence, no auto-keep path (M2), crash bug in the flagship pattern (B2)** |
| §0.3 local gate: schema / DUP-1 / MAP-1 / URL HEAD / re-hash / ARP-2 | **partial** | Schema ✓ (VLD2xxx, live-verified), MAP-1 ✓ (VLD3002), URL probes ✓ (VLD5004 live-verified, `--offline` honest), re-hash ✓ (VLD6012 + boundary revalidator), ARP-2 index ✓ (VLD3101); **DUP-1 key wrong (M1)** |
| ARCH-1 payload-based arch | **partial** | NSIS marker/token heuristics (`NsisProbe.cs:213-268`, `$PROGRAMFILES64`) ✓; **inner-payload PE parsing for NSIS not implemented**; Inno payload override exists but gated (M12) |
| ARCH-2 token-less sibling ≠ x64 | **fully** | `AssetMappingPlanner.ApplySiblingCoverage` (`:43`) |
| ARCH-3 token table most-specific-first (win64a, win32-arm64) | **fully** | `ArchitectureTokenClassifier.cs:19-32` weighted classes |
| ARCH-4 x86compatible ≠ x86 | **partial** | M12 |
| ARCH-5 never generate neutral | **fully** | neutral filtered from generated candidates (`AssetMappingPlanner.cs:607`) |
| MAP-1 stable tokens, no similarity, URL⇔hash identity | **fully** | no similarity metric anywhere (grep-clean); `MAP_DUPLICATE_ASSET`, `CONTENT_IDENTITY_CONFLICT`/`CONTENT_SHARED_ACROSS_URLS` (opt-in override) |
| MAP-2 new URL belongs to new version | **fully** (indirect arch-token check) | version continuity + URL-vs-target checks block `CanApply`; arch preserved via entry-arch equality rather than literal token-class compare |
| MAP-3 whole-release enumeration, new/removed entries, magic-bytes re-detection | **fully** with caveat | exe→zip / wix→burn transitions degrade to blocking questions instead of automatic re-typed mapping — unattended runs stall (acceptable-safe, spec-tension) |
| MAP-4 nested paths re-derived, alias↔path uniqueness | **fully** | `NestedInstallerPathResolver.cs:69-148` with version templating |
| DUP-1 | **partial** | M1 |
| DUP-2 preserve same-URL x86+x64 verbatim | **fully** | `BuildPreservedSharedGroups`/`preserveIntentionalLayout` + `MAP_STRUCTURAL_REWRITE` opt-in; **but the follow-on detector crashes (B2)** |
| ARP-1 version templating | **fully** | `Arp1VersionTemplateRule` (MongoDB #151617 reproduced in test) |
| ARP-2 omit redundant / index collision | **fully** | Rules redundancy + VLD3101 split |
| ARP-3 garbage sanitization | **fully** | rule + Inno sanitizes at source |
| ARP-4 key-set parity | **fully** | `Arp4ShapeParityRule` |
| HASH-1 re-hash before push + fresh-release delay | **fully** / delay **default 0** | full re-download + identity compare at plan AND final boundary + post-PR close-on-failure (commit 4edcf94 real); spec suggests 3–6 h default — `ConfigurationDefaults.cs:13` ships 0 (spec allows "0 disables", so config-compliant but not recommendation-compliant) |
| HASH-2 vanity URLs | **fully** (annotation store + re-hash path) | `vanity-url` override consumed in planner; PR-body annotations exist but CLI never populates them (M6 adjacency) |
| SCOPE-1/2/3 | **fully** | per-installer twins, evidence-only explicit scope, switch hygiene |
| SCOPE-4 wrapper classification | **fully** | Burn outer-type + gated inner ARP; Squirrel never wix |
| VER-1 precedence | **partial** | resolver implements override>product>tag>URL with prefix-stripping and folder==manifest by construction; **binary tier restricted to MSI/MSIX (M16)** |
| META-1 https upgrade | **fully** | probe-evidence-gated |
| META-2 HEAD-check metadata URLs | **fully** | VLD5xxx probes + workflows evidence |
| META-3 blob/HEAD license URLs | **fully** (conservative) | full-sha + raw links normalized; abbreviated-sha links deliberately untouched |
| META-4 release-notes sanitization | **partial** | bullets/colons/10k truncation/URL retarget ✓; **the spec-mandated separate `META-4-bullets` flag does not exist at runtime** (ctor flag never wired — `Meta4…Rule.cs:26-34`, `ProductionRuleComposer.cs:43`) |
| META-5 field-set parity | **fully** | incl. ambiguous-previous-layout reporting (commit ec11aa4 verified real) |
| DEP-1 | **partial** | rule + arch matching + previous-major verification ✓; **evidence pipeline defective for exe (M13) and >64 MiB (B1)** |
| DEP-2 infra-error classification | **fully** | feedback workflow classifies infra signatures → rerun/keep-alive |
| WORK-1 duplicate prevention (4 sub-items) | **partial** | search too narrow (M3); machine-local per-package lock + repo-side branch reservation ✓; retired-id/sibling-hash unwired (M6); remove-version re-check ✓ |
| WORK-2 non-empty single-(pkg,version) PR, fresh branch | **fully** | `GitHubManifestChangeGuard` GH1002-1013 |
| WORK-3 backfill URL-verify | **partial / lives-elsewhere** | dead-version proof gating exists; no bulk backfill-update flow as such |
| WORK-4 feedback loop | **partial** | M18 |
| WORK-5 close superseded w/ cross-link | **fully** | ownership+freshness-proved comment-then-close |
| PIPE-1 one serializer, single trailing newline, byte-identical round-trip | **fully** (with documented deviation) | byte-fidelity re-verified for 1.12 near-exhaustively; **project uses LF everywhere while the spec text says CRLF** — deliberate main-era decision, but rewriting an existing CRLF manifest produces a whole-file line-ending diff, the PIPE-1 symptom itself |
| PIPE-2 ManifestVersion + $schema sync | **fully** | pinned end-to-end; caveat: *two* hardcoded `"1.12.0"` literals (`ManifestSchemaValidator.cs:18` vs `ManifestVersion.cs:10`) with no cross-assembly equality test |
| PIPE-3 identity immutability | **fully** | findings-rule + repo-casing resolution in workflows |
| PIPE-4 ArchiveBinariesDependOnPath | **fully** (bonus — implemented though not explicitly requested of this branch) |
| PIPE-5 content-policy annotation lists | **fully** | incl. `manual-only`; nit: built-in Chrome pack carries a dead `ARP-1` annotation producing permanent Info noise |
| §13 test corpus = final merged manifests | **partial** | M17 |
| §14 non-goals documented | **fully** | docs/troubleshooting.md + annotation lists |

### 3.3 plan.md "done"-claim spot-checks (question 3: are claims/versions current?)

- **"1781 tests green (3 skipped opt-in live)" — TRUE**, matches the central run exactly (§4).
- **All H1 release-docs claims (plan.md:135-140) — TRUE**, each verified individually (release.yml/ci.yml/LICENSE/NOTICES).
- **plan.md is otherwise badly stale**: the progress log documents only P0–P5 (pre-branch) and the final H1 session. The branch's ~100 commits of Workflows/GitHub/CLI/Validation/Rules-policy work have **no session entries at all**, and plan.md:120 still claims "schema validation NOT implemented — IManifestSchemaValidator seam only", which the branch superseded (the seam file is deleted). The premise "the agent updated plan.md as it worked" holds only for the last session.
- **rules_to_implement.md**: unchanged vs main (as expected — it is the spec).
- **Dependency currency** (NuGet, checked 2026-08-01): SharpCompress 1.0.0, Spectre.Console 0.57.2, System.CommandLine 2.0.10 = latest stable ✓; OpenMcdf 3.1.4 (latest 3.2.0, one minor behind); **YamlDotNet 16.3.0 (latest 18.1.0, two majors behind)** and **JsonSchema.Net 8.0.5 (latest 9.4.0, one major behind)** — possibly deliberate AOT-vetting pins, but undocumented.
- **GitHub Actions**: all pinned to mutable major tags (`@v4`, `@v2`) incl. third-party `softprops/action-gh-release@v2` running with `contents: write`; ci.yml has no `permissions:` block (A21/A22).

---

## 4. Original build & test matrix (2026-08-01; superseded)

Central run in the audited worktree (SDK 10.0.302, user-local; logs: session artifacts `build.log` / `tests.log`).

`dotnet build WinMatsch.slnx` → **Build succeeded, 0 Warning(s), 0 Error(s)** (35.1 s). All 19 projects build.

`dotnet test WinMatsch.slnx --no-build`:

| Test project | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| WinMatsch.Core.Tests | 169 | 0 | 0 | 169 |
| WinMatsch.Analysis.Tests | 432 | 0 | 0 | 432 |
| WinMatsch.Downloads.Tests | 64 | 0 | 0 | 64 |
| WinMatsch.Rules.Tests | 413 | 0 | 0 | 413 |
| WinMatsch.Validation.Tests | 53 | 0 | 0 | 53 |
| WinMatsch.GitHub.Tests | 90 | 0 | 0 | 90 |
| WinMatsch.Workflows.Tests | 226 | 0 | 0 | 226 |
| WinMatsch.Cli.Tests | 296 | 0 | 0 | 296 |
| WinMatsch.Testing.Tests | 15 | 0 | 0 | 15 |
| WinMatsch.E2E.Tests | 23 | 0 | 3 | 26 |
| **Total** | **1781** | **0** | **3** | **1784** |

The 3 skips are the intended env-gated live tests (`Opt_in_acquisition…`, `Live_mutation_contract…`, `Configurable_test_fork_contract…`).

**CLI smoke (built binary, coordinator-run):** `--version` = `0.1.0+29e6f7b…` ✓ · root help lists all 17 commands ✓ · hidden `remove-dead-versions` reachable (its `--help` intentionally prints nothing, exit 0 — matches docs; missing-arg exit 2 with the usage message duplicated twice, nit) · `analyze <PE>` text+JSON well-formed, exit 0 ✓ · `analyze <70 MB PE>` → **exit 1, "exceeds the analysis limit"** (B1 repro) · `validate` schema-header gate VLD2104 fires, exit 5 ✓; `--offline` honest (VLD5001 warning) ✓; **default mode probes installer URLs over the network** (VLD5004/VLD6012 observed against a dead URL — by design per §0.3, but surprising for a "local" validate; docs do document it) · flag typo `--network` was swallowed as a *path* argument (exit 5 "Manifest path '--network' does not exist" instead of usage exit 2 — greedy `<paths>...` arity, minor A16) · `config path --tokn ghp_…` echoes the token (M9 repro).

**E2E:** the suite's own hermetic tests pass in the central run (real-process CLI invocations, golden SHA256-pinned manifest bytes, transaction atomicity/rollback/16-way concurrency, path-traversal rejection). No live-network E2E was executed by this audit (requires opt-in secrets by design).

---

## 5. Original merge recommendation (2026-08-01; superseded)

**Historical snapshot:** the fix-first recommendation below described commit
`29e6f7b`. The final closeout recommendation is in §0.1.

**Do not merge yet.** The branch is a strong foundation with no evidence of fabricated work, but B1 makes the tool unusable for a large class of real packages and B2 crashes the flagship safety flow; both are cheap to fix relative to the branch size. Recommended fix-first order:

1. **B1** — make dependency analysis degrade (skip with a diagnostic) instead of throwing on oversize payloads, or raise/scope the cap and guard the two call sites (`ProductionAdapters.cs:129-134`, `InstallerDiagnosticService.cs:123`).
2. **B2** — deduplicate/disambiguate `SemanticChangeKey` construction (include occurrence disambiguation consistent across matched/added paths) in `HumanCorrectionDetector` + regression test for 1→2 same-URL twins.
3. **M1** — align both duplicate gates with winget's comparator (Unknown-scope/locale wildcard, NestedInstallerType) and make WM0102 delegate to or agree with VLD3001.
4. **M7** — force `sha: null` serialization for tree deletions (per-property `[JsonIgnore(Condition = Never)]`) + assert the serialized body in the recorded test.
5. **M3 + M4** — broaden duplicate-PR detection to title-token/path search across any author; adopt the canonical "Update version:"-style titles (fixes both directions of duplicate blindness).
6. **M8 + M10 + M9** — emit the documented JSON envelopes before throwing on review/questions gates, honor `--yes` for reviews (or add `--approve-reviews`); unify decline exit codes with docs; route parse errors and `questions[].path` through redaction and consolidate the two redaction engines.
7. **M5, M6, M18** — adopt-or-suffix the submission branch and allow cleanup of PR-less own branches; wire `RepositoryEvidence`/`DuplicateHashes`/retired-identifier population; either wire a real repair planner or mark WORK-4 partial in docs/plan.
8. **M2** — implement consumers for `ScopeLayout`/`MetadataUrlReplacements`/`PreservedFields` (or reject those keys in the parser) and add the learned-override persistence path (`WriteFile` exists, unused).
9. **M11–M16** — Inno codepage fallback-to-replacement (not exception), Inno payload-size weighting + payload use for empty/absent arch expressions, exe payload decompression for DEP-1 or downgrade its confidence, add `OverflowException` to the parser catch list, budget `PackageManifestIO` reads (or route them through the validator), extend version trust to PE evidence behind a flag.
10. **M17** — extend the §13 corpus to drive the full plan→rules→emit pipeline against stored merged-manifest goldens.
11. Housekeeping before v1: update plan.md progress log (or drop the stale line 120), decide/document the YamlDotNet/JsonSchema.Net major-version pins, pin GitHub Actions to SHAs, add `permissions:` to ci.yml, add the missing `DRY_RUN` env var or remove it from the plan.

Items 1–6 are the merge gate; 7–11 can be fast-follow issues if the team prefers a shorter gate, but 7's branch-deadlock (M5) will bite the first crashed CI run against a real repo.

---

## 6. Original appendix — minor findings and nits (2026-08-01; superseded)

**Analysis** · A1 Inno emits no diagnostic for ambiguous/mixed payload arch (unlike NSIS001/BURN001) so `RequiresManualAnalysis` never fires for Inno (`InnoProbe.cs:75-83`) · A1b empty/absent `ArchitecturesAllowed` never consults payload evidence; `ArchitecturesInstallIn64BitMode` unused as arch signal (`InnoProbe.cs:178-183`) · A1c pre-6.3 flag 0x04 rendered `x64compatible` (overstates OS set), 0x08 → untokenizable `ia64` (`InnoFormatReader.cs:572-596`) · A1d Inno version window hard-throws on future releases (`:210-214`) · A1e bzip2 payloads silently yield zero evidence via message-less swallowed throw (`:668,678-686`) · A1f `PrivilegesRequired=lowest`→`ElevationProhibited` is stronger than the data warrants (`InnoProbe.cs:119-124`) · A1g Burn x64-stub + arm64-only chain stays x64 silently (`BurnProbe.cs:110-152`) · A1h Squirrel ignores in-hand nupkg payload PEs for arch (`SquirrelProbe.cs:212-213`) · A1i unbounded alloc drift in `AdvancedMsiProperties.cs:69-75` · A1j aggregate ZIP budget counts declared not actual bytes → CPU amplification (`PayloadDependencyAnalyzer.cs:69-83`) · A1k dead `PeOverlay.FindSignature`, no-op `catch{throw;}` blocks, unreachable zlib arm, NRE-catch masking own bugs (`AdvancedInstallerProbe.cs:277-287`).

**Rules** · A2 log sanitizer over-redacts: 3-segment dotted identifiers (`Microsoft.VisualStudioCode.Insiders`) JWT-match → whole message `[REDACTED]`, erasing exactly the §0.1 audit values (`RuleLogSanitizer.cs:359-396`) · A3 Apply-mode snapshot-capture failure silently skips change recording while log-only throws (`RulePipeline.cs:173-204`) · A4 `RulePipeline.CreateDefault(WithRuntime)` maintain a policy-rule-free copy of the order; `WithRuntime` has zero callers (`RulePipeline.cs:121-160`) · A5 META-4 bullets flag unwired (M-adjacent, see table) · A6 built-in Chrome pack dead `ARP-1` annotation → permanent Pipe5 Info noise (`Overrides/BuiltIn/Google.Chrome.yaml:5`) · A7 bare `-64`/`-32` tokens version-normalized out of URL identity → pairing relies on arch score alone (`ManifestSnapshot.cs:1806-1824`) · A8 multi-locale previous-entry matching declares ambiguity and skips (conservative) (`PolicyValues.cs:102-128`) · A9 ARP evidence field paths use pre-removal indices → provenance falls back to generic attribution · A10 `FindIgnoreCase` dictionary-order nondeterminism seed (`PolicyEvidence.cs:111-133`) · dead: `IsBase64UrlToken`.

**Workflows** · A11 `RecoverStaleMetadata` is dead logic superseded by unconditional `SetLength(0)`; PID written, never consumed (`FileRemoteOperationLockProvider.cs:55-88`) — the *actual* mutual exclusion (exclusive open handle, never deleted) is sound on Windows · A12 non-Windows lock identity = SHA256(full path), symlinks defeat it; lock files under `/tmp` never deleted → tmp-cleaner can break flock mutual exclusion on very long runs (`ProductionAdapters.cs:897-899,959-964`) · A13 rollback-failure rethrow discards the original exception (`ProductionAdapters.cs:411-425`) · A14 scratch-cleanup failure fails an otherwise-valid revalidation (GH1021, fail-closed for a non-safety reason) · A15 snapshot reads under contention throw raw while applies return Conflict results (`LocalWorkflowEngine.cs:57-61`) · A15b `FreshnessDelay` default 0 (spec suggests 3–6 h) · A15c first-write provenance capture can record human manifests as bot-original on locale-edit of pre-existing versions (`OriginalSubmissionStore.cs:95-100`) · nits: mutable never-written `Candidate.Architecture`; per-call regex construction in `ArchitectureTokenClassifier` (unfinished `GeneratedRegex` migration); `SingleOrDefault` case-collision throw; `DateTimeOffset.UtcNow` in pure `Plan()`; `UrlOverride` requires exactly-4 parts (wingetcreate accepts shorter).

**Downloads/GitHub** · A16 crash-orphaned `*.tmp.<guid>` files never swept (only `ClearAsync` removes) (`DownloadCache.cs:151,363-405,490`) · A17 lock spin has no deadline/diagnostics; POSIX exclusion depends on .NET flock emulation (`DownloadCache.cs:734-785`) · A17b server-directed retry delay has no ceiling (`GitHubHttpTransport.cs:259-266`); Link-header pagination unbounded (same-origin loop) · A17c GraphQL-unavailable / fork-not-ready detection sniffs its own exception message text (`GitHubRepositoryClient.cs:1123-1130,1180-1182`) · A17d `Dispose()` disposes the caller-supplied `HttpClient` · A17e downloader `Dispose` races in-flight semaphores · A17f `SearchPullRequestsAsync` head-filter without owner silently unfiltered · A17g `GraphQlUri` independent of `ApiBaseUri` — a GHES library consumer setting only `ApiBaseUri` would POST the token to `api.github.com/graphql`; **latent-only**: the shipped CLI never passes options (grep: sole construction uses defaults), which equally means **GHES is not actually configurable from the CLI at all** · nits: dead GraphQL `defaultBranchRef.oid` + extra REST round-trip; `SanitizeFileName` misses reserved device names; unescaped `|`-joined PR fingerprint; benign double dispose; `secret-tool` stderr never drained; `TokenValidationResult.Scopes` never populated.

**Core/Validation** · A18 `--created-with-url` reaches local manifests but not the submission request (provenance drift) (`MutationCommandModule.cs:755-761` vs `:1057`) · A19 revalidation result hands back a `FilePath` into a deleted scratch dir; `finally` delete can mask the real error (`PreflightModels.cs:136-160`) · A19b ARP-2 overlap falls back to ordinal string compare for unparseable versions · A19c `$schema` header accepted on any line, not just line 1 · A19d empty non-Markets sequences (`Tags: []`) silently dropped on re-serialization (pre-existing on main) · A19e LF-vs-CRLF spec tension (documented deviation) · A19f two `"1.12.0"` literals without a sync test · nits: VLD3006/3010 message overstates scope; plan.md:120 stale.

**CLI** · A20 dead options `--allow-structural-rewrite`/`--allow-stable-url-change` on `new` (registered, never read — `MutationCommandModule.cs:116-130` vs `:1478-1480`) · A20b `--edit`+`--replace` combo rejected only after the full plan ran · A20c `remove-dead-versions` multi-version arity always yields "not removable" with zero inspections (policy never enabled) · A20d fish completions ship without descriptions (space-rejecting `Sanitize` makes the `-d` branch dead) · A20e `config set/unset` silently drops comments/unknown keys (undocumented) · A20f `show`/`list-versions` require a token though the repo is public (parity gap vs komac) · A20g `--open-pr` uses `UseShellExecute` (no xdg-open path on Linux); Ctrl+C in that window exits 0 · A20h JSON enum-casing drift between command families (kebab vs camel) · nits: `submit` error names nonexistent `--path`; duplicate `--rule-mode` surfaces raw framework message; editor temp-dir delete can throw in `finally`; duplicated missing-arg error line.

**Testing/E2E/CI/docs** · A21 actions pinned to mutable tags; third-party release action with `contents: write` · A22 ci.yml missing `permissions:`; release.yml workflow-wide `contents: write` · A23 recordings (`http-recordings.json`) exercise a REST shape (`releases/tags/{tag}`) production never calls, and `RecordedHttpMessageHandler` has no consumer outside its own self-test — replay infrastructure is effectively dead · A24 `AssertSafe`'s secret parameter is vacuous (no test injects the default value) · A25 tautological `FoundationSmokeTests.Production_assemblies_load` · A26 duplicated fixture-parsing helpers with silent semantic divergence + undocumented super-productivity null-arch carve-out (E2E vs mapping tests) · A27 hardcoded `C:\`-style paths in cross-platform in-memory tests (benign) · A28 docs/architecture.md says "Nine production projects", table has 8 + test-infra · A29 plan.md P8 "analyzer corpus in CI" silently substituted by synthetic fixtures.

**Skipped / not audited (explicit):** unchanged-on-main analyzer internals (MSI/NSIS string readers, CabinetReader beyond diffs), legacy rule bodies WM0001–WM0101/0103, `InstallerFieldAccessors.cs`, ~50 of 66 `RuleRuntimeTests` bodies (names+targeted reads only), tail halves of the largest Analysis/GitHub test files (assertion-skimmed), `DESIGN.md`/`PRODUCT.md` (untracked files, not part of the branch), live-network E2E (requires opt-in secrets by design), and NuGet-latest verification beyond the six main packages. Subagent coverage notes are preserved verbatim in their unit reports.
