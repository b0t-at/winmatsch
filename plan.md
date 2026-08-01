# Plan: winmatsch — cross-platform WinGet manifest automation tool (.NET)

## Context & decisions (confirmed with user)
- Name: **winmatsch** (repo d:\winmatsch, currently only README.md)
- Full Komac feature parity in v1 (incl. deep Inno/NSIS/Burn parsing) + extras from wingetcreate
- Interaction: automation-first flags/env (CI) **and** interactive prompts, both from day one
- Target repo configurable (default microsoft/winget-pkgs; override for testing, e.g. winget-pkgs-submission-test fork)
- .NET 10 LTS; AOT-*ready* design (source-gen, no reflection in hot paths), ship trimmed self-contained single-file first, flip Native AOT in v1.x after validation
- License consideration: Komac is GPL-3.0 — port *concepts/algorithms*, do not copy code verbatim unless we accept GPL. Plan assumes clean-room reimplementation in C# referencing public docs (MSI spec, NSIS/Inno formats, WiX source is MS-RSL/MIT for wix4+, innoextract is zlib licensed as reference).

## Research summary
### Komac (Rust) — commands
new, update, remove, cleanup, token (add/remove), list [versions], show, sync [fork], complete, analyse, remove-dead-versions (hidden), submit (local manifests dir).
Flags: --version, --urls (multi), --submit, --dry-run, --output, --open-pr, --resolves, --replace [version], --skip-pr-check, --created-with(-url), --concurrent-downloads, token via GITHUB_TOKEN/keyring.

### Komac — architecture highlights
- winget-types crate: strongly typed manifests (Installer/DefaultLocale/Locale/Version), PackageIdentifier, PackageVersion ordering, typed URLs, LanguageTag, Sha256String
- Analyzer dispatch by extension: MSI, MSIX/APPX(+bundles), ZIP, EXE probe chain: AdvancedInstaller → Burn → Inno → NSIS → Squirrel → generic (keyword heuristics "installer/setup/7zs.sfx")
- MSI: property/directory/control tables + summary info (arch, lang, creating app); WiX detection; ALLUSERS scope; install dir walk; Chrome version quirk hardcoded (we make it a rule)
- NSIS: first-header, zlib/bzip2/LZMA solid & non-solid decompression, instruction entries → registry writes (ProductCode/Publisher/DisplayVersion), $INSTDIR → scope, language tables, portable detect, inner exe arch
- Inno: header parse across versions, architectures allowed/disallowed → arch + UnsupportedOSArchitectures, PrivilegesRequired(+OverridesAllowed) → scope/ElevationRequirement, AppId→ProductCode, DefaultDirName→install dir, code-page language → locale
- Burn: .wixburn PE section → stub → UX cab → manifest "0" XML: registration/ARP, related bundles (UpgradeCode), chain MSI packages with install-condition evaluation (VersionNT64/NativeMachine vars), ARPSYSTEMCOMPONENT filter; JDK MSI-in-resource fallback
- MSIX: AppxManifest parse, SignatureSha256 = hash of AppxSignature.p7x, PackageFamilyName (publisher hash = Crockford Base32 of SHA256(publisher UTF-16LE) first 8 bytes), platform/minOSVersion, capabilities
- Zip: nested installer detection, single-candidate auto-pick else prompt, portable command alias, relative path fixup on update
- GitHub via GraphQL (cynic): get repo info (id/default branch oid), createRef, createCommitOnBranch (server-side commit = verified, no local git), createPullRequest, existing-PR search (in:title + exact token match), branches+associated PRs for cleanup, REST compare + merge-upstream for sync; get_all_values pulls GitHub release (notes HTML→markdown, date, license, topics→tags, publisher URL)
- update flow: fetch existing manifests → concurrent download w/ progress + streaming SHA256 → analyze → match_installers (arch/type/scope + URL levenshtein) → carry over per-installer fields → optimize (hoist common fields to root, dedupe ARP vs locale) → YAML with schema header comments → prompt Submit/Edit/Exit → branch+commit+PR; --replace deletes previous version dir in same commit; UpdateState → commit title ("New version:", "Update version:", "Add version:", "Remove version:")
- Token in OS keyring; validation via API; CI env detection; VHS demo mode

### wingetcreate (C#) — extras Komac lacks
- new-locale / update-locale commands, show, cache management, settings file (incl. custom WingetRepoOwner/WingetRepo), DSC v3 resource, JSON or YAML manifest output format
- URL argument overrides: 'url|x64|user|displayVersion' (arch/scope/displayVersion per URL)
- Arch detection priority: OverrideArchitecture > UrlArchitecture (regex on URL, e.g. arm64/arm/win64/win32) > BinaryArchitecture > existing manifest arch
- MoveInstallerFieldsToRoot / ShiftRootFieldsToInstallerLevel normalization; retain non-null installer fields on update; DisplayVersion counting warnings
- Vanity URL detection → auto-replace flow when URLs identical across versions
- VerifyUpdatedInstallerHash blocks no-op submissions
- WinGetUtil native validation (Windows-only — we replace with cross-platform schema+rules validation)
- Auto-fork gh repo if missing

### Known pain points to encode as rules (Komac past mistakes category)
- Wrong hoisting of installer fields to root (fields not truly common)
- ARP/AppsAndFeaturesEntries duplication with defaultLocale data (dedupe), DisplayVersion vs PackageVersion consistency
- InstallerType for Burn-wrapping-MSI, exe-with-nested-msi
- Loss of hand-maintained fields on update (custom switches, dependencies, docs)
- ProductCode/UpgradeCode formatting (braces, uppercase)
- Empty string vs null scrubbing; duplicate installer entries (same arch+type+scope)
- ReleaseDate provenance (GitHub release date vs Last-Modified)
- Package-specific quirks (Chrome MSI comments version) → data-driven quirk pack, not hardcoded

## Architecture (solution layout)
- src/WinMatsch.Core — manifest object model, YAML read/write (byte-fidelity emitter), PackageVersion ordering, PackageIdentifier/PackagePath, ManifestVersion pin (1.12.0 constant — planned as 1.10.0, raised when the pinned schemas were refreshed), UpdateState
- src/WinMatsch.Analysis — IInstallerAnalyzer registry + Pe/, Msi/, Msix/, Zip/, Burn/, Nsis/, Inno/, Advanced/, Squirrel/; AnalysisResult = installers + evidence bag
- src/WinMatsch.Rules — IRule (Id WMxxxx, Category: Normalization|Validation|Quirk|Policy, Severity), RulePipeline (deterministic order, enable/disable via config, --explain trace), ManifestContext (manifests + analysis evidence + previous manifests + options), quirk pack (data-driven per-package)
- src/WinMatsch.GitHub — GraphQL client (raw HTTP + source-gen JSON; NOT Octokit.GraphQL for AOT-readiness), REST bits, token manager (OS keyring: Windows CredMan / libsecret; env; flag), repo config, PR conventions
- src/WinMatsch.Downloads — HttpClient concurrent downloader, streaming SHA256, progress events, redirect/vanity capture, Last-Modified, retries, --allow-insecure gate
- src/WinMatsch.Cli — command host (System.CommandLine), Spectre.Console prompts/progress/preview/diff, $EDITOR loop, config file (~/.config/winmatsch/config.yaml), completions, exit-code contract, --format json machine output
- tests/ — Core.Tests, Analysis.Tests (fixture corpus), Rules.Tests, GitHub.Tests (recorded), Cli.Tests, E2E.Tests (gated by token vs configurable test repo)
- schemas/ — bundled pinned winget JSON schemas for validate
- .github/workflows — ci.yml (win+linux+mac matrix, format gate), release.yml (single-file trimmed win-x64/arm64 linux-x64/arm64 macOS, checksums, winget self-publish)

## Command surface v1 (superset)
new, update, remove, submit, show, list-versions, sync, cleanup, token (add/remove/status), analyze (with --format json), validate (schema + rules, cross-platform), new-locale, update-locale, complete, config, cache, remove-dead-versions (hidden). Global: --token/GITHUB_TOKEN, --dry-run (flag only — the planned DRY_RUN env var was dropped: an exported variable must never be able to flip submission into a no-op or back), --output, --submit, --open-pr, --resolves, --created-with(-url), --repo owner/repo, --skip-pr-check, --replace [ver], URL overrides 'url|arch|scope|displayVersion', --concurrent-downloads, --prtitle.

## Implementation phases (v1.0 gate = all complete)
- P0 scaffold: solution, Directory.Build.props (nullable, analyzers, TreatWarningsAsErrors), dotnet format gate, CI matrix, release skeleton. 
- P1 Core: manifest model + custom YAML emitter (schema $ comment header, Created with winmatsch vX, field order per schema, UTF-8 BOM policy, block scalars) + parser (YamlDotNet static context or custom); PackageVersion ordering port; golden round-trip tests vs real winget-pkgs manifests corpus
- P2 Downloads: downloader + hashing + metadata capture; unit tests w/ local test server
- P3 Analysis wave 1: framework + PE reader (System.Reflection.PortableExecutable + resource walker for VS_VERSIONINFO/manifest) + MSI (OpenMcdf 3 + table decoder) + MSIX/bundles + ZIP + generic EXE heuristics
- P4 Analysis wave 2 (parity): Burn (SharpCompress cab + XML) → NSIS (decompressors: zlib=System.IO.Compression, bzip2+LZMA=SharpCompress/ManagedLzma; instruction decode) → Inno (setup-loader + versioned header parse; reference innoextract zlib-licensed) → AdvancedInstaller (7z SFX via SharpCompress) + Squirrel (nupkg)
- P5 Rules: engine + seed rules (normalization set incl. hoist/push-down, dedupe ARP, scrub empties, dupes, code formats, preserve-on-update) + quirk pack (Chrome) + validate command w/ bundled JSON schemas
- P6 GitHub + flows: client, token mgr, auto-fork w/ consent, update/new/submit/remove flows, installer matching (Komac match + wingetcreate overrides), existing-PR check, UpdateState commit titles, --replace deletion, show/list/sync/cleanup
- P7 UX & extras: interactive prompts everywhere, preview/diff/edit loop, locale commands, completions, config command, cache
- P8 hardening: E2E vs test repo, analyzer corpus in CI (compile fixtures with Inno/NSIS/WiX on windows job), docs, v1.0 release, submit winmatsch to winget with itself
  - **Deviation, reconciliation PENDING (2026-08-01):** no such CI job exists; the corpus is realized as in-code *synthetic* fixture builders instead (see docs/architecture.md "Fixture policy"). Whether the compiled-installer corpus is dropped, deferred, or added is owned by the E2E workstream — this line stays as planned until that handoff lands, and must not be re-written to describe the substitute as if it were the plan.
Parallelizable: P2 ∥ P3 after P1; P4 ∥ P5 ∥ P6 mostly independent after P3/P1.

## Verification strategy
- Golden byte-fidelity round-trips on real manifests (fidelity is non-negotiable for PR diffs)
- Analyzer corpus: fixture installers built in CI (Inno, NSIS, WiX/Burn, MSI, MSIX) + snapshot (Verify) of produced installer models; cross-check vs komac analyse output for same files during development
- Rules: table-driven unit tests, one test per rule id
- E2E: full update+submit dry-run against configurable fork; real PR test gated by secret
- dotnet format + analyzers as CI gate; cross-platform matrix win/linux/mac

## Open items / future (post-v1)
- Mine recent winget-pkgs + Komac PRs/issues to grow rule set (user explicitly wants this later)
- Native AOT flip (validate YamlDotNet-free path, source-gen everywhere)
- Font/nested new installer types as schema evolves; MSI transform/patch; DSC resource; GitHub Action wrapper

## PROGRESS LOG
### Session 2026-07-20: P0 + P1 DONE (commit 43a7740, local only — NOT pushed)
- .NET 10.0.302 SDK installed user-locally at $env:LOCALAPPDATA\Microsoft\dotnet (dotnet NOT on system PATH — prepend in every new terminal!)
- P0 scaffold: WinMatsch.slnx (slnx format!), Directory.Build.props (net10.0, TreatWarningsAsErrors, latest-recommended analyzers, IsAotCompatible for non-test), Directory.Build.targets (CA1707 off for tests), Directory.Packages.props (central pkg mgmt: YamlDotNet 16.3.0, xunit 2.9.3), .editorconfig (LF, naming rules incl. private const PascalCase), .gitattributes (eol=lf), global.json, CI (3-OS matrix + format gate), release.yml skeleton
- P1 Core DONE: src/WinMatsch.Core — Primitives (PackageVersion w/ winget-cli ordering + unknown + suffix rules + ordinal tiebreak; PackageIdentifier; ManifestVersion (Default=1.10.0); LanguageTag; Sha256Hash; MinimumOSVersion), Enums + Yaml/YamlValues.cs (hand-written switch maps, AOT-safe), Manifests (InstallerFieldsBase abstract → Installer + InstallerManifest for hoisting; LocaleManifest → DefaultLocaleManifest; VersionManifest; PackageManifests; ManifestHeader), Yaml/YamlEmitter (internal; precise NeedsQuoting incl. YAML 1.1 numbers/bools/timestamps/sexagesimal; block literals; MappingSequence renders items via nested emitter), Yaml/ManifestYamlWriter (canonical schema field order, throws on missing required), Yaml/ManifestYamlReader (YamlDotNet RepresentationModel walk — NO reflection deserializer: IL3050 AOT error otherwise!), ManifestPaths
- Tests: 140 passing (ordering tables, quoting matrix, byte-for-byte round-trips w/ raw-string fixtures + "+\n" trailing-newline helper, stability, TryDetectType, unknown-key tolerance)
- Conventions locked: double-quote style for quoted scalars ("{ProductCode}"), LF everywhere, sequence dash at key indent, 2-space nesting, header comments "# Created with X" + "# yaml-language-server: $schema=..."

### Session 2026-07-20 (later): P2 Downloads DONE (not committed)
- src/WinMatsch.Downloads: DownloadResult, DownloaderOptions (UA=Microsoft-Delivery-Optimization/10.1, 3 retries, 1s base backoff, 30min/attempt timeout), DownloadProgress record struct, InstallerDownloader (streaming SHA256 via IncrementalHash, .part temp + rename, filename: Content-Disposition filename*/filename → final-url segment → initial-url segment, Windows-invalid-char sanitize on all OSes, per-attempt linked-CTS timeout b/c HttpClient.Timeout kills big streams, transient=conn/IO/timeout/5xx/408/429, DownloadManyAsync SemaphoreSlim + fail-fast linked CTS, WhenAll surfaces faulted over canceled)
- tests/WinMatsch.Downloads.Tests: 20 tests, StubHttpMessageHandler simulates redirect-following (sets RequestMessage.RequestUri), FaultyStream, ProgressCollector. Both projects added to slnx under /src/ + /tests/ folders.
- All green: build 0 warn, 160 tests pass, format clean (create_file wrote CRLF → had to run dotnet format once)

### Session 2026-07-20 (later): P3 wave 1 Analysis DONE (not committed)
- src/WinMatsch.Analysis: DetectedInstallerFormat, InstallerAnalysis (required Format+Installers w/ validating init), ZipContents, IInstallerAnalyzer, IExeFormatProbe (probe order comment: AdvancedInstaller→Burn→Inno→Nsis→Squirrel→generic; list empty in wave 1), FileAnalyzer (static facade, internal Analyzers list [Zip, Exe], CanAnalyze capability check), ExeAnalyzer (keyword fallback installer/setup/7zs.sfx/7zsd.sfx on OriginalFilename/FileDescription; ARP entry only when metadata present), ZipAnalyzer (candidate ext msi/msix/appx/exe/+bundles, skips __MACOSX+resources folders, zip-slip guard throws InvalidDataException, single-candidate .exe refinement via inner FileAnalyzer → arch/metadata/Portable, ZipContents on BOTH single+multi), UrlArchitectureDetector (token-bounded manual scan, group priority arm64>arm>x64(x86_64 first)>x86), Pe/PeFile (PEReader LeaveOpen, IDisposable, span-based .rsrc walk root→type→first name→first lang→data(RVA!), RT_MANIFEST via XmlReader → RequestedElevation + ScopeHint), Pe/VersionInfo (VS_VERSIONINFO block parser, wValueLength words-vs-bytes per wType, first StringTable)
- tests/WinMatsch.Analysis.Tests: 92 tests. PeFixtures builds REAL PEs via ManagedPEBuilder + custom ResourceSectionBuilder subclass (hand-written .rsrc: 16-byte dirs, 8-byte entries high-bit=subdir, data entry holds RVA via SectionLocation.RelativeVirtualAddress) + independent spec-based VS_VERSIONINFO writer (cross-checks parser). All 252 tests green, 0 warnings, format clean.
- Gotchas hit: IDE0040 requires explicit `public` on interface members in this repo; tests use per-file `using Xunit;` (no global usings); AssemblyHashAlgorithm lives in System.Reflection namespace.

### Session 2026-07-20 (later): P3 remainder MSI+MSIX DONE (commit pending)
- src/WinMatsch.Analysis/Msi: MsiAnalyzer (OpenMcdf 3 RootStorage LeaveOpen; reads _StringPool/_StringData/_Tables/_Columns/Property + \x05SummaryInformation; Template platform → arch [Intel/empty=x86, x64/Intel64/AMD64, Arm64, Arm, else InvalidDataException]; WiX detect via CreatingApplication OR any property name/value containing "wix"; ALLUSERS "1"=Machine, absent/empty=User, "2"/other=null; LCID→BCP-47 hand map ~30 entries b/c InvariantGlobalization; ARP entry only when any of the 5 fields present), MsiStreamName (compound-file name decode incl. table prefix bit), MsiStringPool (2/3-byte string refs, codepage), MsiTableReader (columns/rows decode), MsiSummaryInformation (property set parse)
- src/WinMatsch.Analysis/Msix: MsixReader (AppxManifest.xml via XmlReader, SignatureSha256 = SHA256 of AppxSignature.p7x, platform/minOSVersion), MsixPackageFamilyName (Crockford Base32 of SHA256(publisher UTF-16LE)[..8]), MsixAnalyzer, MsixBundleAnalyzer (AppxBundleManifest.xml, per-app packages)
- Registered in FileAnalyzer.Analyzers: [Msi, Msix, MsixBundle, Zip, Exe]; ZipAnalyzer .msi refinement now active
- Interruption artifact fixed this session: MsiAnalyzerTests.cs missed `using WinMatsch.Analysis.Msi;` + MsixFixtures.cs had CRLF → dotnet format
- All 342 tests green (140 Core + 182 Analysis + 20 Downloads), 0 warnings, format clean

### Session 2026-07-20 (later): P4 Burn DONE (commit 517de9e)
- src/WinMatsch.Analysis/Burn: BurnSectionHeader (.wixburn layout verified vs wix3+wix4 section.cpp, IDENTICAL: magic 0x00f14300@0, version 2@4, guid@8, stubSize@24, origChecksum@28, origSigOffset@32, origSigSize@36, containerFormat@40 (1=cab), containerCount@44, uint32 containerSizes[]@48 — container sizes are uint32 in wix4 too), CabinetReader (~200-line hand-rolled CFHEADER/CFFOLDER/CFFILE/CFDATA reader, none+MSZIP; MSZIP cross-block history via synthetic non-final stored-deflate block carrying prev 32KiB window b/c DeflateStream has no preset-dictionary API; SharpCompress does NOT support cab at all — README confirmed), BurnManifest (namespace-agnostic manifest "0" parse: Registration/@Id=ProductCode, Arp Register="no"→no ARP entry, DisplayVersion falls back to Registration/@Version, RelatedBundle Action="Upgrade"→UpgradeCode), BurnProbe (walks raw PE section table itself; arch = PE machine, promoted Arm64 when chain InstallCondition mentions NativeMachine + 0xAA64/arm64 since Burn stubs are x86 even for ARM64 bundles)
- ExeAnalyzer._probes = [BurnProbe]; PeFixtures refactored: WriteResourceSection extracted/public for BurnFixtures raw PEBuilder stub (gotcha: PEBuilder.GetDirectories() override must be `protected`, NOT `protected internal`; fixture serializes twice b/c stubSize known only after serialization)
- 36 tests added → 378 total green, 0 warnings, format clean

### Session 2026-07-20 (later): P5 Rules baseline DONE (commit 1fdb2c5, subsequently integrated)
- src/WinMatsch.Rules + tests/WinMatsch.Rules.Tests (97 tests) + schemas/ (all 4 pinned winget 1.10.0 JSON schemas downloaded)
- Pipeline order (ctor-enforced mutating-before-validation, dup ids rejected): WM0007 PreserveOnUpdate → WM0201 quirks → WM0002 PushDownRoot → WM0004 ScrubEmpty → WM0006 NormalizeProductCodes → WM0003 DedupeArpVsLocale → WM0005 RemoveDupInstallers → WM0001 Hoist → WM0101 DisplayVersionConsistency → WM0102 DupEntries → WM0103 TypeConsistency
- Key design: hand-written InstallerFieldsBase accessors (Get/Set/deep-Equals/deep-Clone delegates, zero reflection in src; reflection-based test guards table vs new model props); EffectiveInstallerValues resolves installer/root fields so rules work pre/post hoist; InstallerEvidence lives in Rules. The former schema-validation seam was superseded by the AOT-vetted WinMatsch.Validation project with embedded WinGet 1.12 schemas.
- Gotchas: CA1861 in tests (no inline new[] in assertions); IDE1006 wants _camelCase on private static readonly in tests too; Guid.ToString("B").ToUpperInvariant() dodges CA1305/CA1308
- Worktree note: agent fast-forwarded its branch to db2796a before starting

### Session 2026-07-20 (later): P4 NSIS DONE (commit c1877bb)
- src/WinMatsch.Analysis/Nsis: NsisFirstHeader (overlay from raw section table, 512-byte-step scan for 0xDEADBEEF+"NullsoftInst" @+4 of 28-byte first header), NsisCompression (solid/non-solid + compressor inference, 7-Zip-compatible order: stored → LZMA sig (solid) → high-bit size prefix (non-solid; MUST precede solid-bzip2 check — 0x800002xx low byte collides with bzip2 magic) → bzip2 (solid) → deflate (solid)), NsisHeader (8 block headers @4, langtable_size@100, install_directory_ptr@280, 28-byte entries; langtable LANGID@0), NsisStringReader (NSIS 3 ANSI+UTF-16LE; codes 1=LANG (recursive via default langtable) 2=SHELL (0x80=registry-resolved ProgramFilesDir/CommonFilesDir, 0x40=64-bit "…64", else CSIDL) 3=VAR ($0-$9/$R0-$R9/builtins 20-29, $INSTDIR=21) 4=SKIP; DECODE_SHORT low-7-bits×2 low byte first), NsisProbe
- NSIS 3 official-release assumption; NSIS 2 ANSI escape codes 252-255 NOT interpreted (literal ARP strings still fine). Opcodes: EW_WRITEREG=51 (NOT 62=EW_WRITEUNINSTALLER!), REG_SZ filter, roots HKLM/HKCU/SHCTX; EW_SETFLAG=13 parm0=12 alter_reg_view parm1→256=KEY_WOW64_64KEY→SetRegView 64. Arch: x86 stub promoted X64 on $PROGRAMFILES64/$COMMONFILES64/SetRegView64; ARM64 not detectable. Scope from $INSTDIR default. Compressors: deflate+LZMA1 (SharpCompress 1.0.0 LzmaStream) both modes; NSIS-bzip2 + BCJ-LZMA detected→InvalidDataException
- Lcid.cs extracted as shared LCID map (MsiAnalyzer delegates). SharpCompress 1.0.0 gotcha: DataErrorException internal → catch SharpCompressException. 35 tests added → 413 green, format clean

### Session 2026-08-01: P4 remainder through P8 implemented
- Added Inno, Advanced Installer, and Squirrel analyzers and wired the complete EXE probe chain.
- Added schema/preflight validation, deterministic mapping, rule runtime/policy catalogue, learned-correction detection, secure GitHub client/lifecycle, local workflows, full CLI command surface, cache/config/token management, and release engineering.
- Added hermetic E2E/regression coverage, six-RID trimmed publishing, documentation, licensing, and third-party notices.
- Post-hoc audit findings are tracked in audit-report.md and require remediation before merge.


### Session 2026-08-01: H1 release-docs DONE
- LICENSE (MIT, winmatsch contributors 2026) + THIRD-PARTY-NOTICES.txt moved to repo root (single source; Validation + Cli link it, copied into every output/publish; LicenseNoticeTests still green); Directory.Build.props: PackageLicenseExpression=MIT, Copyright, PackageProjectUrl; Cli Description user-facing
- README rewritten + docs/: commands (full reference incl. hidden remove-dead-versions, exit codes, JSON contract, partial remote states), configuration, rules (full catalogue WM/ARP/SCOPE/META/DEP/PIPE + override-pack schema), analyzers (support matrix + limits), security, architecture, troubleshooting, release; CONTRIBUTING/SECURITY/CHANGELOG
- release.yml hardened: tag validation, fail-if-CLI/artifact-missing (silent hashFiles skip removed), single full test gate, per-RID verify (exe+LICENSE+notices), host-matching smoke (--version==tag, help, completion, config path), deterministic winmatsch-<tag>-<rid> names, checked SHA256SUMS (no || true), archive test-extract, draft release fail_on_unmatched_files; ci.yml uploads trx on failure
- Verified: 1781 tests green (3 skipped opt-in live), format clean, all six RID trimmed publishes locally + artifact inspection, win-x64 smoke (version/help/analyze/validate exit 5/completion/config path), workflow YAML parsed, relative doc links checked
- Gotcha: PowerShell 'native | Select-Object -First N' truncation corrupts $LASTEXITCODE — retest exit codes without early pipeline stops

### Progress-log gap (recorded 2026-08-01)
The ~100 commits between P5 and the H1 session (P4 remainder → P8: Inno/AdvancedInstaller/Squirrel analyzers and the probe chain, real schema validation, the GitHub client and lifecycle workflows, the policy-rule catalogue, override packs, CLI command surface, download cache, hermetic E2E) were committed **without** session entries here. They are not undocumented — the per-item verdicts, evidence and gaps are in `audit-report.md` §3 (plan.md and rules_to_implement.md coverage tables). Treat that file as the progress record for that stretch; the pre-P5 entries above and the sessions below are the only ones written contemporaneously.

### Session 2026-08-01: dependency audit + documentation reconciliation
- Dependency currency reviewed live against nuget.org (all six shipped packages + transitives). Upgraded: **YamlDotNet 16.3.0 → 18.1.0** (MIT; adds net10.0 target; 18.0.0's ITypeInspector break does not apply — the project never uses the reflection (de)serializer, only RepresentationModel/Parser; 18.1.0's new default recursion ceiling of 130 sits above our own budgets of 64/32) and **OpenMcdf 3.1.4 → 3.2.0** (MPL-2.0 unchanged; API used — RootStorage.Open/EnumerateEntries/OpenStream/EntryInfo/CfbStream — unchanged; 3.2.0 moves two structural checks behind the new opt-in StorageModeFlags.StrictValidation, i.e. the default path is slightly more permissive on malformed MSI, which we keep because our own AnalysisLimits + FileFormatException handling already bound the damage and strict mode would reject legitimate real-world MSIs).
- **JsonSchema.Net deliberately held at 8.0.5** — the last MIT-licensed release. 9.x ships OSMFEULA.txt (Open Source Maintenance Fee EULA) in the package: MIT source, but the redistributed *binary* adds a monthly maintenance-fee obligation for revenue-generating users, and we redistribute the assembly in every release archive. The terms are not even stable across 9.x (9.0.0 covers every revenue-generating user; 9.4.0 exempts those under US$10k/yr), which is the argument for treating the move as a licensing decision rather than a version bump. The 9.x transitive JsonPointer.Net 7.x carries the same terms. Rationale + re-evaluation triggers are recorded in `Directory.Packages.props`; the policy is in CONTRIBUTING.md.
- THIRD-PARTY-NOTICES.txt reconciled with the restored closure (`dotnet list package --include-transitive`); OpenMcdf's MPL source pointer moved to the 3.2.0 commit. `LicenseNoticeTests` rewritten from hard-coded strings into a drift guard: it reads `Directory.Packages.props` (copied into the test output) and fails when a pin is missing from the notice or is neither noticed nor declared test-only.
- Docs reconciled against the built binary and current code: project count 9→8, plan's stale schema-validation seam marked superseded, ManifestVersion 1.10.0→1.12.0, DRY_RUN dropped from the command surface, LF normalization policy, transaction recovery journals, token-required reads, and an explicit "unsupported (not bugs)" table.
- Reconciled onto the integrated branch (learned override memory, authoritative PR manifest evidence, submission recovery journals): the doc claims written against the pre-integration tree were **not** carried over where the code has since moved on. Corrected in this integration — GHES is supported by the `WinMatsch.GitHub` library (configurable `ApiBaseUri`, derived same-origin GraphQL) and only unexposed by the CLI; `freshnessDelay` defaults to `04:00:00`, not `00:00:00`; `scopeLayout` / `metadataUrlReplacements` / `preservedFields` / `droppedFields` are all enforced by rule WM0202, so the "accepted but not yet enforced" section was deleted and replaced with their real semantics; approved reviews **are** persisted as learned override packs (pending → manifest-committed → activated, content-hash CAS, crash recovery under a per-package lock); pipeline feedback **is** persisted by `FileFeedbackStateStore`; the "persisted learning" non-goal was removed. Documented the three journal families (local manifest/provenance transactions, learned override store, local→remote submission journals) and the `--override-store` global option.
- CLI audit (`utesgui-integrate-cli-audit`) is **not** merged here. Documented CLI surfaces are those on this branch; anything that only exists on that branch is deferred to the final docs reconciliation after it lands.
- Verified on the integrated branch: build 0 warnings, **2128 tests green** (3 skipped opt-in live), `dotnet format --verify-no-changes` clean, all six RID trimmed publishes, win-x64 trimmed-binary smoke (`--version`, root help, `update --help`, `config show`), every documented option cross-checked against the built `--help` (confirming `freshnessDelay = 04:00:00 [default]` and the `--override-store` global option), and all relative doc links and anchors resolved.
