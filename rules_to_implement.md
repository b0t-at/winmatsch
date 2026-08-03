# Rules to Implement (v1 tool) — Deep Analysis of damn-good-b0t PR Failures

> **Source data**: All 7,596 closed PRs by [`damn-good-b0t`](https://github.com/microsoft/winget-pkgs/issues?q=is%3Apr+state%3Aclosed+author%3Adamn-good-b0t) in `microsoft/winget-pkgs` (2024-04 … 2026-07) were enumerated via the GitHub GraphQL API.
> Signal used per the analysis request: **any PR that needed more than one commit = something was wrong**, plus **all 340 PRs that were closed without being merged**.
>
> - 7,256 merged / 340 unmerged (4.5 % dead PRs)
> - 274 multi-commit PRs → 169 with real fix commits (rest were intentional mass-deletion cleanups); all 169 fix diffs were downloaded and categorized
> - Fix-commit category counts (a PR can be in several): **Architecture line changed: 63**, ARP/AppsAndFeaturesEntries touched: 79, InstallerSha256 changed: 35, locale/metadata URLs changed: 32, InstallerUrl swapped: 26, Scope changed: 26, ReleaseNotes fixed: 18, dependencies added: 6
> - Old tools in use: **Komac** (v2.2 → v2.16, majority), wingetcreate, YamlCreate.ps1 — visible in the `# Created with …` header lines of the diffs
>
> Every rule below is: (a) backed by concrete PR links, (b) explained with the *root cause* of why the old tool failed, (c) written as an implementable instruction, and (d) designed to be **reversible** (see §0).

---

## 0. Meta-architecture requirements (read first)

### 0.1 Every rule must be a feature flag
All rules below get a stable ID (`ARCH-1`, `MAP-2`, …). Implement a rules engine where each rule can be independently:
- enabled/disabled globally (config file, e.g. `rules.json` / `appsettings.json` section),
- overridden per package identifier,
- run in **log-only mode** (rule evaluates and logs what it *would* change without applying).

The PR body / commit message must list which rule IDs fired (e.g. `applied: ARCH-1, MAP-3`). When strange behaviour shows up in production, the offending rule can be identified from the PR itself and switched off without a redeploy. Persist the *pre-rule* value next to the *post-rule* value in the run log.

### 0.2 Per-package memory ("learned overrides") — the single highest-leverage feature
The most expensive pattern in the whole dataset is **the same manual fix being re-applied version after version** because Komac has no memory:

| Package | Same fix repeated in |
|---|---|
| `fairdataihub.SODA-for-SPARC` (x86→x64) | [#245552](https://github.com/microsoft/winget-pkgs/pull/245552), [#290128](https://github.com/microsoft/winget-pkgs/pull/290128), [#290907](https://github.com/microsoft/winget-pkgs/pull/290907), [#296715](https://github.com/microsoft/winget-pkgs/pull/296715), [#323791](https://github.com/microsoft/winget-pkgs/pull/323791) |
| `JohannesMillan.superProductivity` (2nd entry x64→x86) | [#362105](https://github.com/microsoft/winget-pkgs/pull/362105), [#362578](https://github.com/microsoft/winget-pkgs/pull/362578), [#362837](https://github.com/microsoft/winget-pkgs/pull/362837), [#363745](https://github.com/microsoft/winget-pkgs/pull/363745), [#363945](https://github.com/microsoft/winget-pkgs/pull/363945) |
| `Streetwriters.Notesnook` (setup/portable URL swap) | [#245541](https://github.com/microsoft/winget-pkgs/pull/245541), [#259103](https://github.com/microsoft/winget-pkgs/pull/259103), [#267445](https://github.com/microsoft/winget-pkgs/pull/267445), [#269084](https://github.com/microsoft/winget-pkgs/pull/269084), [#292471](https://github.com/microsoft/winget-pkgs/pull/292471) |
| `ransome1.sleek` (portable↔setup swap) | [#245180](https://github.com/microsoft/winget-pkgs/pull/245180), [#291179](https://github.com/microsoft/winget-pkgs/pull/291179), [#354743](https://github.com/microsoft/winget-pkgs/pull/354743), [#360629](https://github.com/microsoft/winget-pkgs/pull/360629) + 2 PRs that died stale ([#283473](https://github.com/microsoft/winget-pkgs/pull/283473), [#336955](https://github.com/microsoft/winget-pkgs/pull/336955)) |
| `JohnMacFarlane.Pandoc` (root Scope → per-installer Scope) | [#153256](https://github.com/microsoft/winget-pkgs/pull/153256), [#165049](https://github.com/microsoft/winget-pkgs/pull/165049), [#197015](https://github.com/microsoft/winget-pkgs/pull/197015), [#201767](https://github.com/microsoft/winget-pkgs/pull/201767), [#210752](https://github.com/microsoft/winget-pkgs/pull/210752) |
| `ASGARDEXMaintainers.ASGARDEX` (x86→x64) | [#287481](https://github.com/microsoft/winget-pkgs/pull/287481), [#293277](https://github.com/microsoft/winget-pkgs/pull/293277), [#304518](https://github.com/microsoft/winget-pkgs/pull/304518), [#323074](https://github.com/microsoft/winget-pkgs/pull/323074) |
| `1zilc.FishingFunds` (PublisherUrl 404 fix) | [#304857](https://github.com/microsoft/winget-pkgs/pull/304857), [#305025](https://github.com/microsoft/winget-pkgs/pull/305025) (same day — 2nd PR created before 1st fix merged) |
| `OliverBetz.ExifTool` (32/64 URL swap + ARP entries) | [#154236](https://github.com/microsoft/winget-pkgs/pull/154236), [#154983](https://github.com/microsoft/winget-pkgs/pull/154983), [#157018](https://github.com/microsoft/winget-pkgs/pull/157018), [#157949](https://github.com/microsoft/winget-pkgs/pull/157949), [#162860](https://github.com/microsoft/winget-pkgs/pull/162860) |

**Implement**: a per-package override store (checked into the tool's repo, e.g. `overrides/<PackageIdentifier>.yaml`) holding: forced arch per asset-pattern, asset→entry mapping rules, scope layout, version source, metadata URL replacements, fields to always drop/keep. The update pipeline must consult it *after* detection and *before* manifest emission. Additionally: **diff the generated manifest against the last merged manifest** — if the tool is about to revert a field a human changed after PR creation (i.e. the merged manifest differs from what the bot originally pushed), treat that as a learned override and keep the human value, or halt for review.

### 0.3 Local validation gate before any PR
Replicate the pipeline checks that killed PRs, locally, and refuse to open a PR that fails:
1. Schema validation (current `winget validate` / schema version, see PIPE-2).
2. **Duplicate-installer-entry check** (see DUP-1 — the most lethal one).
3. Per-URL uniqueness vs InstallerType (see MAP-1).
4. HTTP HEAD on every URL in the manifest (installer + metadata; see META-2).
5. Hash re-verification straight before push (see HASH-1).
6. ARP DisplayVersion sanity (see ARP-2).

---

## 1. ARCH — Architecture detection (63 fix PRs — the #1 error class)

### ARCH-1: Never trust "x86" for NSIS/Electron installers without corroboration
**Observed**: dozens of merged PRs needed `x86` → `x64` fix commits for electron-builder NSIS installers: Gather ([#196793](https://github.com/microsoft/winget-pkgs/pull/196793), [#216240](https://github.com/microsoft/winget-pkgs/pull/216240), [#251737](https://github.com/microsoft/winget-pkgs/pull/251737)), ueli ([#246477](https://github.com/microsoft/winget-pkgs/pull/246477), [#249804](https://github.com/microsoft/winget-pkgs/pull/249804)), Miru ([#233669](https://github.com/microsoft/winget-pkgs/pull/233669)), PostyBirb ([#249404](https://github.com/microsoft/winget-pkgs/pull/249404)), BrightScript ([#249412](https://github.com/microsoft/winget-pkgs/pull/249412), [#305457](https://github.com/microsoft/winget-pkgs/pull/305457), [#305730](https://github.com/microsoft/winget-pkgs/pull/305730)), shogihome ([#245183](https://github.com/microsoft/winget-pkgs/pull/245183), [#247725](https://github.com/microsoft/winget-pkgs/pull/247725), [#271710](https://github.com/microsoft/winget-pkgs/pull/271710), [#291075](https://github.com/microsoft/winget-pkgs/pull/291075) — the last one died unmerged), Chia ([#297026](https://github.com/microsoft/winget-pkgs/pull/297026)), HandheldCompanion ([#245543](https://github.com/microsoft/winget-pkgs/pull/245543)), Keeper Commander ([#196924](https://github.com/microsoft/winget-pkgs/pull/196924), [#198202](https://github.com/microsoft/winget-pkgs/pull/198202), [#271802](https://github.com/microsoft/winget-pkgs/pull/271802)), wire ([#194941](https://github.com/microsoft/winget-pkgs/pull/194941)), and many more (SnailKM, BSManager, AllToMP3, ODrive, defi, LoopEmail, Cartridges, StudioRack, Curiosity, pinokio, youtube-dl-gui, sunfish-shogi, STY1001, ASGARDEX ×4, SODA-for-SPARC ×5).

**Root cause** (documented in the user's own Komac bug report [russellbanks/Komac#967](https://github.com/russellbanks/Komac/issues/967)):
- NSIS installer *stubs* are always 32-bit PE files, so PE-machine-header detection returns x86 even when the payload is x64.
- Komac's `update` path relies primarily on URL tokens and previous-entry pairing; when the URL contains no arch token (`Gather-1.25.0-Setup.exe`), it falls back to x86. Even when `komac analyse` detects x64 correctly, `komac update` may still write x86 (see [sitiom's comment](https://github.com/russellbanks/Komac/issues/967) re `sshpass`).

**Implement** (in order of trust):
1. URL/asset-name token (see ARCH-3 token table).
2. If no token → **binary analysis of the payload, not the stub**: for NSIS, parse the install script/payload (`app-64.7z` naming in electron-builder, `$PROGRAMFILES64` usage, x64 PE inside); for plain PE use the machine header; for MSI use the Template Summary property; for zip inspect the nested binaries.
3. If still ambiguous → previous version's architecture for the *corresponding entry* (same position/type/scope after MAP rules).
4. Log a low-confidence warning when 1–3 disagree; per-package override (§0.2) wins over everything.

### ARCH-2: An asset *without* an arch token, next to siblings *with* tokens, is NOT x64
**Observed**: superProductivity ships `Super-Productivity-Setup-x64.exe` *and* `Super-Productivity-Setup.exe` (the plain one is ia32). Komac assigned **x64 to both**, producing "Duplicate installer entry found" pipeline errors and requiring a fix in *five consecutive version PRs* (links in §0.2 table). Same for UHK Agent `win.exe` vs `win-x64.exe`/`win-ia32.exe` ([#199079](https://github.com/microsoft/winget-pkgs/pull/199079)) and dbgate `dbgate-X-win.exe` ([#253582](https://github.com/microsoft/winget-pkgs/pull/253582), [#363888](https://github.com/microsoft/winget-pkgs/pull/363888)).

**Implement**: when a release contains sibling assets where at least one has an explicit arch token and one has none:
- the token-less asset is either the **x86/ia32 build** (electron-builder convention) or a **combined universal installer**;
- if it's a combined installer (contains both arch payloads — e.g. UHK `win.exe`), **omit it entirely** — winget already picks the right arch-specific installer; this is [russellbanks' explicit recommendation](https://github.com/russellbanks/Komac/issues/967): *"this installer entry should be removed as it has no purpose"*. Do not mark it `neutral` (see ARCH-5);
- otherwise map it to the architecture *not yet covered* (usually x86), and verify via payload analysis (ARCH-1 step 2).

### ARCH-3: Token table, priority, and the `win64a` / mixed-token traps
**Observed**:
- cURL `win64a-mingw.zip` (ARM64) emitted as `neutral` in two consecutive PRs ([#197837](https://github.com/microsoft/winget-pkgs/pull/197837), [#233979](https://github.com/microsoft/winget-pkgs/pull/233979)); acknowledged and fixed later in Komac ([comment](https://github.com/russellbanks/Komac/issues/967#issuecomment), commit `58bb7211`).
- Electron `electron-v35.2.0-win32-arm64.zip` mapped to the **x86** entry because "win32" matched first ([#249591](https://github.com/microsoft/winget-pkgs/pull/249591)) — the x86 entry pointed at the ARM64 zip while the actual ia32 zip was dropped.
- Xray `Xray-windows-64.zip` marked `neutral`, x86/x64 assets missing ([#245583](https://github.com/microsoft/winget-pkgs/pull/245583)).

**Implement**: token matching must be (a) case-insensitive on delimited tokens, (b) evaluated with **most-specific-first priority**: `arm64|aarch64|win64a|winarm64` → arm64; then `x64|amd64|x86_64|win64|64-bit|_64` → x64; then `x86|ia32|i386|386|win32|32-bit|_32` → x86; `arm` (alone) → arm. A URL containing *both* a 32-token and an arm64/x64 token (e.g. `win32-arm64`, `win32-x64`) must resolve to the more specific right-hand token — electron-builder uses `win32` as the *platform*, not the architecture. Add regression tests for: `win32-ia32.zip`, `win32-x64.zip`, `win32-arm64.zip`, `win64a-mingw.zip`, `windows_386.zip`, `windows-amd64.exe`, `windows-arm64-v8a.zip`.

### ARCH-4: Inno Setup — `x86compatible` header ≠ manifest x86
**Observed**: Keeper Commander is an Inno installer whose header says `ArchitecturesAllowed: x86compatible`, but submitting it as x86 produced `Validation-Executable-Error` — *"The specified executable is not a valid application for this OS platform"* — and moderators repeatedly fixed it to x64 ([#196924](https://github.com/microsoft/winget-pkgs/pull/196924), [#198202](https://github.com/microsoft/winget-pkgs/pull/198202), [#271802](https://github.com/microsoft/winget-pkgs/pull/271802); full discussion in [Komac#967](https://github.com/russellbanks/Komac/issues/967)).

**Implement**: for Inno installers read **both** `ArchitecturesAllowed` and `ArchitecturesInstallIn64BitMode`; if the *installed payload* is 64-bit-only (check the largest embedded PE files), emit x64 even when the header claims x86-compatible. When in doubt prefer what the previous *merged* manifest used (it survived validation).

### ARCH-5: `neutral` is only for genuinely architecture-independent packages
**Observed**: `neutral` was misused for combined NSIS installers and ARM64 zips (cURL above; UHK [#199079](https://github.com/microsoft/winget-pkgs/pull/199079) briefly used neutral for the universal exe). Moderator guidance: neutral is effectively for MSIX/scripts that run everywhere.

**Implement**: never *generate* `neutral` from detection heuristics; only preserve it if the previous merged manifest used it AND the asset is genuinely arch-independent. Otherwise resolve to a concrete architecture or omit the entry (ARCH-2).

---

## 2. MAP — Asset→installer-entry mapping (26 URL-swap fix PRs)

### MAP-1: Distinct entries must get distinct URLs; same URL + different InstallerType is a hard error
**Observed**: Notesnook releases contain `notesnook_win_x64.exe` (nullsoft) and `notesnook_win_x64_portable.exe` (portable). Komac's similarity-based URL pairing repeatedly assigned the **portable** URL to the **nullsoft** entry (and/or the same URL to both), producing either wrong manifests ([#245541](https://github.com/microsoft/winget-pkgs/pull/245541), [#259103](https://github.com/microsoft/winget-pkgs/pull/259103), [#267445](https://github.com/microsoft/winget-pkgs/pull/267445), [#269084](https://github.com/microsoft/winget-pkgs/pull/269084), [#292471](https://github.com/microsoft/winget-pkgs/pull/292471)) or the pipeline error *"An installer is defined multiple times with different InstallerType values"* which killed [#266552](https://github.com/microsoft/winget-pkgs/pull/266552) (Notesnook 3.2.0 never shipped). Identical story for sleek `-win.exe` (portable) vs `-win-Setup.exe` (nullsoft): §0.2 table.

**Implement**:
- Map assets to entries using **stable token rules**, not string similarity: `portable` token → the portable-type entry; setup/installer token (or absence of portable token) → the executable-installer entry.
- After mapping, assert: no two entries share the same URL unless *everything else* (type/scope/switches) is also identical; if the same URL legitimately serves two entries (see DUP-2) their InstallerType must match.
- Assert: hash equality ⇔ URL equality across entries (two different URLs with identical hashes is suspicious → re-download and verify; caught late in [#269084](https://github.com/microsoft/winget-pkgs/pull/269084) as an extra "fix hash" commit).

### MAP-2: The new URL must actually belong to the new version
**Observed**: wire 3.36.4913 manifest pointed at the **3.36.5047** asset (a different release), including the ReleaseNotesUrl ([#189252](https://github.com/microsoft/winget-pkgs/pull/189252) — two fix commits: URL, then hash). ExifTool x64 entries pointed at `_32.exe` and vice versa across five PRs (§0.2). SimpleWebServer x86/arm64 entries pointed at the x64 asset ([#154530](https://github.com/microsoft/winget-pkgs/pull/154530)).

**Implement**: if the previous entry's URL embedded the version string, require the new URL to embed the new version; if the previous URL embedded an arch token, require the same token class in the new URL. Any entry whose URL fails these invariants blocks PR creation (or falls to per-package override).

### MAP-3: Enumerate the whole release — publishers add/remove/rename assets
**Observed**:
- New arch assets ignored by the updater and added manually later: immich-go arm64 ([#242677](https://github.com/microsoft/winget-pkgs/pull/242677)), ente-io x64+arm64 ([#243810](https://github.com/microsoft/winget-pkgs/pull/243810)), BGInfo `Bginfo64.exe` ([#229527](https://github.com/microsoft/winget-pkgs/pull/229527)), Xray x86+x64 ([#245583](https://github.com/microsoft/winget-pkgs/pull/245583)), Electron arm64 ([#249591](https://github.com/microsoft/winget-pkgs/pull/249591)).
- Packaging switched from bare exe to zip: buf 1.64.0 kept `InstallerType: portable` pointing at a zip; had to become `zip` + `NestedInstallerType: portable` with three `NestedInstallerFiles` ([#335275](https://github.com/microsoft/winget-pkgs/pull/335275)).

**Implement**: each run, list *all* Windows assets of the release; map to existing entries first (MAP-1/2), then **create new entries for unmapped arch/type assets** and **flag removed ones**. If the file extension or magic bytes of a mapped asset changed (exe→zip, msi→exe), re-run full installer-type detection instead of copying the old type (this also covers EPOS wix→burn, [#174323](https://github.com/microsoft/winget-pkgs/pull/174323)).

### MAP-4: Nested installer paths must be re-derived from the actual archive
**Observed**: `RelativeFilePath` copied from the previous version with a stale version string inside — EdgeTX `companion-windows-2.10.0.exe` in the 2.10.1 manifest ([#155891](https://github.com/microsoft/winget-pkgs/pull/155891)), cyme `cyme-v2.2.5-…/cyme.exe` in the 2.2.6 manifest ([#297655](https://github.com/microsoft/winget-pkgs/pull/297655) — plus a follow-up hash fix). Also duplicated paths: etcd's `etcdutl` alias pointed at `etcdctl.exe` ([#253476](https://github.com/microsoft/winget-pkgs/pull/253476) — PR died with Validation-Installation-Error anyway).

**Implement**: after downloading the archive, list its contents and resolve every `NestedInstallerFiles.RelativeFilePath` against the real listing (template the version segment). Assert distinct `PortableCommandAlias` → distinct existing paths. Never string-copy nested paths.

---

## 3. DUP — Duplicate-entry manifest errors (killed ≥14 PRs; some packages stopped updating)

### DUP-1: Local "duplicate installer entry" gate (most lethal single check)
**Observed**: `##[error] Manifest Error: Duplicate installer entry found.` killed unmerged PRs and stalled packages for months: Jellyfin.FFmpeg 7.1.3-3/-4/-5 ([#339420](https://github.com/microsoft/winget-pkgs/pull/339420), [#353963](https://github.com/microsoft/winget-pkgs/pull/353963), [#355598](https://github.com/microsoft/winget-pkgs/pull/355598) — plus 7.1.3-1 [#319314](https://github.com/microsoft/winget-pkgs/pull/319314) closed as *"Invalid manifest format — please resubmit using winget-create, Komac, or YamlCreate.ps1"*: four consecutive versions lost), superProductivity 18.1.0/18.1.3 ([#353575](https://github.com/microsoft/winget-pkgs/pull/353575), [#354595](https://github.com/microsoft/winget-pkgs/pull/354595)), Spotube 5.0.0/5.1.0/5.1.1 ([#292388](https://github.com/microsoft/winget-pkgs/pull/292388), [#341826](https://github.com/microsoft/winget-pkgs/pull/341826), [#342062](https://github.com/microsoft/winget-pkgs/pull/342062)), dbgate 6.4.2/7.1.8 ([#256490](https://github.com/microsoft/winget-pkgs/pull/256490), [#357143](https://github.com/microsoft/winget-pkgs/pull/357143)), ente-io 1.7.13/1.7.22 ([#261451](https://github.com/microsoft/winget-pkgs/pull/261451), [#354390](https://github.com/microsoft/winget-pkgs/pull/354390)), draw.io ([#244337](https://github.com/microsoft/winget-pkgs/pull/244337)), superProductivity 18.2.7 needed an in-PR fix ([#363745](https://github.com/microsoft/winget-pkgs/pull/363745) — bot comment shows the exact error).

**Root cause**: after Komac's arch detection collapsed distinct entries onto the same architecture (ARCH-2) or paired the same URL twice (MAP-1), the manifest contained two entries with identical effective keys `(Architecture, InstallerType, Scope, InstallerLocale)`. The bot never validated this locally, pushed the PR, the pipeline failed, `Needs-Author-Feedback` was applied, nobody reacted, and the stale-bot closed it (see WORK-4).

**Implement**: before opening/updating a PR, compute the winget uniqueness key for every installer entry and **hard-fail on duplicates**. This one check would have saved every PR listed above.

### DUP-2: Preserve the intentional "same URL, x86 + x64" emulation pattern
**Observed**: SurrealDB publishes only `windows-amd64.exe`, but the merged manifests intentionally carry **two entries (x64 and x86) with the same URL** (the x86 entry serving WoW-style matching). Komac normalized them into x64+x64 → moderators re-added x86 in four separate PRs ([#245160](https://github.com/microsoft/winget-pkgs/pull/245160), [#247197](https://github.com/microsoft/winget-pkgs/pull/247197), [#247235](https://github.com/microsoft/winget-pkgs/pull/247235), [#253118](https://github.com/microsoft/winget-pkgs/pull/253118)). Same for dbgate (see ARCH-2) and LoopEmail/defi/Cartridges scope-pairs ([#245360](https://github.com/microsoft/winget-pkgs/pull/245360), [#274117](https://github.com/microsoft/winget-pkgs/pull/274117), [#245170](https://github.com/microsoft/winget-pkgs/pull/245170), [#245198](https://github.com/microsoft/winget-pkgs/pull/245198)).

**Implement**: when the previous merged manifest has N entries and the new release maps 1:1 onto the same URLs/assets, **keep the previous per-entry Architecture/Scope/Type verbatim** and only swap URL/hash/version-derived strings. Structural "improvements" to a layout that already passed review are forbidden by default (opt-in via override).

---

## 4. ARP — AppsAndFeaturesEntries correctness (79 PRs touched ARP data)

### ARP-1: Version strings inside ARP entries must be updated (or derived), never copied
**Observed**: stale DisplayName/DisplayVersion copied from the previous manifest: MongoDB Server 7.0.9 shipped with `DisplayName: MongoDB 6.0.6 …` ([#151617](https://github.com/microsoft/winget-pkgs/pull/151617)); UHK Agent 4.2.0 carried `UHK Agent 3.1.0` ([#174713](https://github.com/microsoft/winget-pkgs/pull/174713)) and 4.2.1 carried `4.2.0` ([#181570](https://github.com/microsoft/winget-pkgs/pull/181570)); Notesnook arm64 entry still said `Notesnook 3.2.3` in the 3.2.4 manifest ([#292471](https://github.com/microsoft/winget-pkgs/pull/292471)).

**Implement**: treat ARP entries as **templates**: any token in DisplayName/DisplayVersion equal to the *old* PackageVersion (or old marketing version) must be replaced with the new one; better, extract actual ARP values from the installer (MSI property table / NSIS strings / inno `AppVerName`). If extraction is impossible and templating finds no version token, keep entry but log for review.

### ARP-2: DisplayVersion — omit when redundant; block when it collides with the index
**Observed**:
- Redundant `DisplayVersion` equal to PackageVersion removed by moderators: KONNEKT 2.10.1 (`2.10.1.0`, [#193899](https://github.com/microsoft/winget-pkgs/pull/193899)), QubaViewer ([#190815](https://github.com/microsoft/winget-pkgs/pull/190815)), NeoAxis ([#155239](https://github.com/microsoft/winget-pkgs/pull/155239)), Kindle ([#246196](https://github.com/microsoft/winget-pkgs/pull/246196)).
- **Static** DisplayVersions that never change (`1.0` for CloudDrive2) triggered `Manifest Error: DisplayVersion declared in the manifest has overlap with existing DisplayVersion range in the index` and killed PRs: Sonarr 4.0.15/4.0.16 ([#267360](https://github.com/microsoft/winget-pkgs/pull/267360), [#309893](https://github.com/microsoft/winget-pkgs/pull/309893)), CloudDrive2 ([#287069](https://github.com/microsoft/winget-pkgs/pull/287069)), ACT 3.8.5 ([#340741](https://github.com/microsoft/winget-pkgs/pull/340741)), TeamSpeak Beta ([#321316](https://github.com/microsoft/winget-pkgs/pull/321316)).

**Implement**: emit `DisplayVersion` only when (a) it differs from PackageVersion, **and** (b) it changes per release. Before submitting, scan the package's existing manifests in the repo: if any other version already declares the same DisplayVersion (or an overlapping range), drop the field and log. This requires a tiny index of existing manifests per package (sparse checkout of `manifests/<letter>/<publisher>/<package>/`).

### ARP-3: Sanitize garbage values read from installers
**Observed** (all emitted by Komac from raw installer data):
- `DisplayName: Jellyfin Server $_44_` — unexpanded NSIS variable ([#216728](https://github.com/microsoft/winget-pkgs/pull/216728));
- `DisplayVersion: $INSTDIR\Advanced Combat Tracker.exe` ([#256360](https://github.com/microsoft/winget-pkgs/pull/256360));
- `DisplayName: ms-resource:ManifestResources/…` from an MSIX ([#241407](https://github.com/microsoft/winget-pkgs/pull/241407));
- `InstallationMetadata.DefaultInstallLocation: "├¥\x1A#\\AltSnap\\"` — binary junk ([#295607](https://github.com/microsoft/winget-pkgs/pull/295607));
- empty objects: `AppsAndFeaturesEntries: [- {}]` ([#278914](https://github.com/microsoft/winget-pkgs/pull/278914)), `InstallationMetadata: {}` ([#245562](https://github.com/microsoft/winget-pkgs/pull/245562), [#245333](https://github.com/microsoft/winget-pkgs/pull/245333), [#245177](https://github.com/microsoft/winget-pkgs/pull/245177));
- random per-build temp paths: `DefaultInstallLocation: '%Temp%\2z4plp6FQmxAQvtOB2otCZcMPuf'` ([#269084](https://github.com/microsoft/winget-pkgs/pull/269084), [#292471](https://github.com/microsoft/winget-pkgs/pull/292471)).

**Implement**: reject/drop any ARP or InstallationMetadata value that (a) contains `$` NSIS/Inno variable markers (`$INSTDIR`, `$_`), (b) starts with `ms-resource:`, (c) contains control characters or non-printable bytes, (d) is an empty mapping/sequence, or (e) points into `%Temp%` with a random suffix. Prune parent keys that become empty.

### ARP-4: ARP consistency across versions — never add-only or drop-only
**Observed**: automated moderation flags both directions: *"This manifest removes Apps and Features entries that are present in previous PR versions… prevent the 'upgrade always available' situation"* (IsoBuster [#156670](https://github.com/microsoft/winget-pkgs/pull/156670)) and *"This manifest adds Apps and Features entries that aren't present in previous PR versions… `DisplayVersion` is an important one"* (GitHubDesktop.Beta [#156239](https://github.com/microsoft/winget-pkgs/pull/156239), [#156498](https://github.com/microsoft/winget-pkgs/pull/156498) — both died unmerged).

**Implement**: compare the ARP key-set against the previous merged version; if they differ, either mirror the previous shape or require an explicit override. (The winget-pkgs check name for this class is `Manifest-Metadata-Consistency` / `Manifest-AppsAndFeaturesVersion-Error`.)

---

## 5. HASH — Hash mismatches & release timing (35 PRs)

### HASH-1: Hash the bytes you upload — and re-verify immediately before PR creation
**Observed**: plain wrong hashes required fix commits: MongoDB Server ([#232343](https://github.com/microsoft/winget-pkgs/pull/232343)), Atlas CLI ([#250817](https://github.com/microsoft/winget-pkgs/pull/250817)), BlenderLauncher ([#234267](https://github.com/microsoft/winget-pkgs/pull/234267)), altair ([#322414](https://github.com/microsoft/winget-pkgs/pull/322414)), FirebaseCLI ([#314833](https://github.com/microsoft/winget-pkgs/pull/314833)), Terragrunt (both archs, [#371111](https://github.com/microsoft/winget-pkgs/pull/371111)), gup (all three archs, [#290249](https://github.com/microsoft/winget-pkgs/pull/290249)).

**Root cause**: publishers **re-upload/rebuild assets shortly after publishing a release** (KONNEKT explicitly: *"fix filehashes after silent hotfix was released"*, [#193899](https://github.com/microsoft/winget-pkgs/pull/193899)); the tool hashed version N of the file, the pipeline downloaded version N+1.

**Implement**:
- Compute the SHA256 from the file the tool itself downloaded (never trust a checksums.txt without verification).
- Immediately before pushing the branch, issue a conditional GET (ETag/If-Modified-Since) or re-download+re-hash; abort/refresh on change.
- For GitHub releases, compare each asset's `updated_at` vs `created_at`; if `updated_at` is newer or the release is younger than a configurable delay (**suggest 3–6 h**), defer PR creation one cycle. (Reversible: the delay is a config value; 0 disables.)

### HASH-2: Vanity/unversioned URLs — flag and prefer versioned mirrors
**Observed**: `Error-Hash-Mismatch` / `Validation-Hash-Verification-Failed` unmerged PRs cluster on packages with unversioned "latest" URLs. GlaryUtilities and Logitech Options+ ultimately had *all* old versions mass-deleted by this same bot because every archived manifest pointed at a rotting vanity URL (the dozens of `Delete manifests/...` commits in the dataset; e.g. GlaryUtilities cleanup begun after [#150479](https://github.com/microsoft/winget-pkgs/pull/150479) — *"Not worth keeping as vanity domain used for Downloads"*). The [ValidationFailureGuide](https://github.com/microsoft/winget-pkgs/blob/master/doc/ValidationFailureGuide.md) codifies: *"Use version-specific URLs rather than vanity URLs"*.

**Implement**: detect URLs lacking any version token; prefer a versioned URL if the publisher offers one; otherwise tag the package in the override store as `vanity-url: true` so the tool (a) re-hashes right before submission (HASH-1) and (b) knows old manifests may need removal PRs.

---

## 6. SCOPE — Scope & switches (26 PRs)

### SCOPE-1: Per-installer Scope for user/machine twin entries; never hoist differing scopes to root
**Observed**: the Pandoc saga (5 PRs, §0.2): manifest-level `Scope: user` with two x64 entries differing only in `ALLUSERS` custom switches; every fix moved Scope down to per-installer `user`/`machine`. Komac re-introduced the broken layout each version.

**Implement**: when ≥2 entries share URL but differ in switches like `/CURRENTUSER` vs `/ALLUSERS` or `ALLUSERS=1/2`, assign per-entry `Scope: user`/`machine` and keep root scope-free. Cover with the DUP-2 "preserve merged layout" rule.

### SCOPE-2: Machine-scope defaults for machine-only installers
**Observed**: missing `Scope: machine` had to be added for RancherDesktop MSI ([#224118](https://github.com/microsoft/winget-pkgs/pull/224118)) and 4KVideoDownloader burn installers ([#154036](https://github.com/microsoft/winget-pkgs/pull/154036) — died unmerged anyway).

**Implement**: derive scope from installer data (MSI `ALLUSERS`, Inno `PrivilegesRequired`); emit it explicitly when unambiguous.

### SCOPE-3: Never emit blank/whitespace switch values; re-validate switches when vendor changes installers
**Observed**: wire shipped `SilentWithProgress: ' '` (a single space) which had to be removed ([#194941](https://github.com/microsoft/winget-pkgs/pull/194941)); Fork switched silent args from `/s` to `--silent` between versions ([#233659](https://github.com/microsoft/winget-pkgs/pull/233659)); Loupedeck needed an added `SilentWithProgress: /quiet` ([#176888](https://github.com/microsoft/winget-pkgs/pull/176888)); missing `RequireExplicitUpgrade` for a self-updating neutral app (Fork, [#226698](https://github.com/microsoft/winget-pkgs/pull/226698)).

**Implement**: trim & validate switch values (reject empty/whitespace); when InstallerType or installer product family changes between versions, do not carry the old switches forward silently — re-detect or flag.

### SCOPE-4: Wrapper installers — don't trust embedded-MSI metadata
**Observed**: EPOS Connect exe wrapper was detected as `wix` + machine + ProductCode `{56C3…}` (metadata of an *inner* MSI); real answer was `burn` with `Custom: /S /v/qn` ([#174323](https://github.com/microsoft/winget-pkgs/pull/174323) — two fix commits). GitHub Desktop Beta squirrel exe detected as `wix`/machine with ARP `GitHub Desktop Deployment Tool` — full manifests had to be rewritten by hand to `exe`+user and both PRs still died ([#156239](https://github.com/microsoft/winget-pkgs/pull/156239), [#156498](https://github.com/microsoft/winget-pkgs/pull/156498)).

**Implement**: when an exe contains an embedded MSI, classify the *outer* container first (burn/squirrel/self-extractor); only inherit inner-MSI ProductCode/scope when the outer type is genuinely `wix`/`msi`. Electron-builder & Squirrel outputs are user-scope `nullsoft`/`exe` — never `wix`.

---

## 7. VER — PackageVersion derivation

### VER-1: Version comes from binary/release metadata, not from a URL fragment
**Observed** (all needed manual re-versioning, several died):
- Kindle `2.7.70978` should have been `2.7.1.70978` (URL omitted a component; fix renamed the whole folder, [#246196](https://github.com/microsoft/winget-pkgs/pull/246196), closed as duplicate);
- CumulusMX `b4088` (build tag) → `4.4.5` ([#248309](https://github.com/microsoft/winget-pkgs/pull/248309) — folder rename + closed unmerged);
- Yubico `yubikey-manager-qt-1.2.6` as the version string ([#152918](https://github.com/microsoft/winget-pkgs/pull/152918), author comment "reopened with correct version");
- Citrix folder `24.2.1000` vs real file version `24.2.1000.1016` ([#174100](https://github.com/microsoft/winget-pkgs/pull/174100));
- Amazon.Games manifest said `3.0.9201.1` in a `3.0.9202.1` folder ([#165260](https://github.com/microsoft/winget-pkgs/pull/165260));
- Keeper `16.11.0` → ARP uses `v16.11.0` ([#156662](https://github.com/microsoft/winget-pkgs/pull/156662) — `Version-Parameter-Mismatch` class).

**Implement**: precedence for PackageVersion: (1) per-package override, (2) installer binary product/file version (or MSI ProductVersion), (3) release tag *stripped of standard prefixes* (`v`, package-name prefixes), (4) URL token. Validate PackageVersion == folder name == all three manifest files. If ARP DisplayVersion is a superset (e.g. adds a 4th part), follow the package's historical convention (compare previous merged version).

---

## 8. META — Locale/metadata hygiene (32 PRs)

### META-1: Auto-upgrade `http://` → `https://` in all metadata URLs
**Observed**: recurring single-line fixes: FOSSA ([#245325](https://github.com/microsoft/winget-pkgs/pull/245325)), Huggle ([#245554](https://github.com/microsoft/winget-pkgs/pull/245554)), WinSSHTerm ([#245590](https://github.com/microsoft/winget-pkgs/pull/245590)), flyingpie ×2 ([#245564](https://github.com/microsoft/winget-pkgs/pull/245564), [#287886](https://github.com/microsoft/winget-pkgs/pull/287886)), ResourceHacker ([#235308](https://github.com/microsoft/winget-pkgs/pull/235308)), Lidarr ([#314001](https://github.com/microsoft/winget-pkgs/pull/314001)), MySQL ([#248826](https://github.com/microsoft/winget-pkgs/pull/248826)). The repo enforces HTTPS for installer URLs (`Validation-HTTP-Error`).

**Implement**: for every http URL in copied locale data, probe the https variant; upgrade if it answers 2xx/3xx. Log-only mode available.

### META-2: HEAD-check every metadata URL; fix or drop 404s before submission
**Observed**: 404/moved URLs merged into new manifests, needing fixes: FishingFunds PublisherUrl ×2 (§0.2), altair PackageUrl ×2 ([#322414](https://github.com/microsoft/winget-pkgs/pull/322414), [#322867](https://github.com/microsoft/winget-pkgs/pull/322867)), tealdeer repo move dbrgn→tealdeer-rs ([#245546](https://github.com/microsoft/winget-pkgs/pull/245546)), Gather support URL ([#165825](https://github.com/microsoft/winget-pkgs/pull/165825) — first fixed, then dropped entirely), Loupedeck PackageUrl ([#363536](https://github.com/microsoft/winget-pkgs/pull/363536)), glueckkanja PublisherUrl language-dependent 404 → point to `/en` ([#258260](https://github.com/microsoft/winget-pkgs/pull/258260)), Caesium PrivacyUrl removed ([#257417](https://github.com/microsoft/winget-pkgs/pull/257417)), Egnyte PackageUrl+ReleaseNotesUrl removed ([#154147](https://github.com/microsoft/winget-pkgs/pull/154147)).

**Implement**: HEAD (fallback GET) every URL field with a browser-ish UA; on 404/410: (1) apply override replacement if known, (2) try well-known alternates (GitHub repo URL for PublisherSupportUrl etc.), (3) otherwise **omit the optional field** rather than shipping a dead link. Cache per-URL results ~7 days. Send `Accept-Language: en` and store language-neutral URLs (the glueckkanja case: root URL 404'd only for non-German clients).

### META-3: License/Copyright URLs must be stable links
**Observed**: commit-pinned or raw links replaced with stable ones: GitButler `blob/7d01a53…/LICENSE.md` → `blob/master/LICENSE.md` ([#162317](https://github.com/microsoft/winget-pkgs/pull/162317)); Authme `raw.githubusercontent.com/...main/LICENSE.md` → `github.com/.../blob/HEAD/LICENSE.md` ([#197643](https://github.com/microsoft/winget-pkgs/pull/197643)).

**Implement**: normalize GitHub license/copyright URLs to `https://github.com/<owner>/<repo>/blob/HEAD/<file>`.

### META-4: ReleaseNotes sanitization (a surprisingly frequent validation-killer)
**Observed**:
- Lines beginning with `- ` inside `ReleaseNotes`/`Description` block scalars caused repeated pipeline failures/timeouts; moderators fixed by swapping `-` for `•` (and `:` for `：` where a line contained `key: value`-looking text): mise ([#322416](https://github.com/microsoft/winget-pkgs/pull/322416) — moderator DandelionSprout: *"We've succeeded twice for damn-good-b0t's PRs thus far"*), superProductivity ([#321272](https://github.com/microsoft/winget-pkgs/pull/321272)), Xray ([#320595](https://github.com/microsoft/winget-pkgs/pull/320595)), Cosign ([#321439](https://github.com/microsoft/winget-pkgs/pull/321439)).
- Malformed/overlong notes removed entirely: Pandoc ([#159692](https://github.com/microsoft/winget-pkgs/pull/159692)), Zstandard ([#153822](https://github.com/microsoft/winget-pkgs/pull/153822)), wire ([#189252](https://github.com/microsoft/winget-pkgs/pull/189252)).
- Append bug: `ReleaseNotesUrl:` concatenated onto the last ReleaseNotes line without a newline (KONNEKT, [#198760](https://github.com/microsoft/winget-pkgs/pull/198760)).

**Implement**: when importing release notes: replace leading `- `/`* ` bullets with `• `; guard any `text: text` line inside block scalars (replace with fullwidth `：` or reflow); enforce a max length (e.g. 10 000 chars — truncate at a paragraph boundary or omit); always emit `ReleaseNotesUrl` as its own line via the YAML serializer (never string-append); `ReleaseNotesUrl` must reference the *same version* being packaged (wire bug, MAP-2). Feature-flag the bullet transform (`META-4-bullets`) since it is cosmetic and the pipeline behavior may change.

### META-5: Field-set parity with the previous version ("Manifest-Metadata-Consistency")
**Observed**: wingetbot posts *"Missing Properties value based on version X: ReleaseDate / ReleaseNotesUrl / MinimumOSVersion / Switches"* — e.g. Mercurial ([#153496](https://github.com/microsoft/winget-pkgs/pull/153496)), GitHubDesktop.Beta ([#156239](https://github.com/microsoft/winget-pkgs/pull/156239)), GlaryUtilities ([#150479](https://github.com/microsoft/winget-pkgs/pull/150479)).

**Implement**: diff the emitted field-set against the previous merged manifest; carry forward every field that is still valid (URLs re-checked per META-2); require explicit rule/override to drop a field. This subsumes: Tags, Moniker, Documentations, MinimumOSVersion, InstallModes, ReleaseDate (recompute, don't drop).

---

## 9. DEP — Dependencies

### DEP-1: Detect VC++ runtime / .NET dependencies from the payload
**Observed**: manual `Microsoft.VCRedist.2015+.x64` additions: SurrealDB ([#232481 FFmpeg](https://github.com/microsoft/winget-pkgs/pull/232481), [#252633 S3Drive](https://github.com/microsoft/winget-pkgs/pull/252633), [#253817 UltraGrid](https://github.com/microsoft/winget-pkgs/pull/253817), [#245560 Micronaut](https://github.com/microsoft/winget-pkgs/pull/245560), [#245546 tealdeer](https://github.com/microsoft/winget-pkgs/pull/245546)); .NET runtime major-version bump FamiStudio 5→8 ([#203022](https://github.com/microsoft/winget-pkgs/pull/203022)); arch-mismatched dependency (VCRedist x64 vs needed x86) Oracle MySQL ([#154168](https://github.com/microsoft/winget-pkgs/pull/154168) — died unmerged).

**Implement**: scan the main payload PEs for imports (`vcruntime140.dll`, `msvcp140.dll` → VCRedist; `hostfxr`/`.runtimeconfig.json` → DotNet.Runtime.<major>); match dependency architecture to the *installer entry* architecture; when the previous manifest pinned a .NET major, verify it against the new payload's `runtimeconfig.json`.

### DEP-2: Know the pipeline's dependency quirks
**Observed**: PRs failed with `No suitable installer found for manifest Microsoft.VCRedist.2015+.x64 with version 14.38.33135.0` — a **pipeline-side outage** ([microsoft/winget-pkgs#152555](https://github.com/microsoft/winget-pkgs/issues/152555)), which stale-killed 4KVideoDownloaderPlus PRs ([#154036](https://github.com/microsoft/winget-pkgs/pull/154036), [#157370](https://github.com/microsoft/winget-pkgs/pull/157370), [#158516](https://github.com/microsoft/winget-pkgs/pull/158516), [#166814](https://github.com/microsoft/winget-pkgs/pull/166814), [#166869](https://github.com/microsoft/winget-pkgs/pull/166869)).

**Implement**: classify this error signature as *infrastructure*, not manifest error → auto-comment `@wingetbot run` / keep the PR alive (WORK-4) instead of letting it rot.

---

## 10. WORK — PR workflow & lifecycle (340 unmerged PRs analyzed)

### WORK-1: Duplicate-PR prevention (72 `Possible-Duplicate` + 23 `Resolution-Duplicate` labels)
**Observed**:
- **Self-race**: two identical PRs created within the same minute — Mercurial [#153496](https://github.com/microsoft/winget-pkgs/pull/153496) closed as dup of #153497; Chrome Canary remove-version triples [#172994](https://github.com/microsoft/winget-pkgs/pull/172994)/[#172995](https://github.com/microsoft/winget-pkgs/pull/172995)/[#172996](https://github.com/microsoft/winget-pkgs/pull/172996) dups of #172989–91.
- **Lost race to other bots/humans**: git-cliff [#163835](https://github.com/microsoft/winget-pkgs/pull/163835) (other PR existed 6 min earlier), IsoBuster [#156670](https://github.com/microsoft/winget-pkgs/pull/156670), Kindle [#246196](https://github.com/microsoft/winget-pkgs/pull/246196), AllToMP3 [#245311](https://github.com/microsoft/winget-pkgs/pull/245311).
- **Duplicate by content**: wingetbot flags *"Similar installer SHA256 hash found in manifest: …\MongoDB\Compass\Full\…"* when updating the retired `MongoDB.Compass.Community` id ([#151623](https://github.com/microsoft/winget-pkgs/pull/151623), [#151634](https://github.com/microsoft/winget-pkgs/pull/151634) — author note: *".Community will be ignored in future"*); DotNet UninstallTool hash matched another version ([#245237](https://github.com/microsoft/winget-pkgs/pull/245237)).

**Implement**:
1. Before creating a PR: search open PRs (any author) for `<PackageIdentifier> <version>` in title AND for the manifest path; skip if found.
2. Global run-lock per package (no two concurrent runs of the tool on the same identifier).
3. Maintain a retired-identifier deny list (e.g. `MongoDB.Compass.Community`); check whether the new installer SHA already exists in a sibling identifier's manifests.
4. Before opening a *remove-version* PR, re-check the version still exists on master.

### WORK-2: Never open empty or off-target PRs
**Observed**: `Unexpected-File` / *"This pull request does not update any files"*: mitmproxy [#231800](https://github.com/microsoft/winget-pkgs/pull/231800), g-helper [#231808](https://github.com/microsoft/winget-pkgs/pull/231808), dbgate [#231795](https://github.com/microsoft/winget-pkgs/pull/231795) (all the same day — a branch-state bug in the updater). `PullRequest-Error` (4×) for PRs touching >1 package/version.

**Implement**: after commit, assert `git diff origin/master --name-only` is non-empty and every path matches `manifests/<letter>/<publisher>/<package>/<version>/…` for exactly one (package, version). Recreate the branch from a fresh master each run instead of reusing (also prevents the stray-merge-commit pattern of [#164202](https://github.com/microsoft/winget-pkgs/pull/164202)).

### WORK-3: Backfill of old versions with rolling URLs is doomed — verify before opening
**Observed**: 19 unmerged `[Update Package] Microsoft.VisualStudioCode.Insiders …` PRs ([#202340](https://github.com/microsoft/winget-pkgs/pull/202340)–[#202358](https://github.com/microsoft/winget-pkgs/pull/202358)) all failed URL validation with `Http status code: BadRequest` — Insiders build URLs die when superseded.

**Implement**: any bulk/backfill operation must URL-HEAD-verify every installer URL first and skip dead ones (possibly generating remove-version PRs instead).

### WORK-4: Respond to `Needs-Author-Feedback` or PRs die (85 × `No-Recent-Activity`)
**Observed**: the single biggest *throughput* loss: 94 unmerged PRs carried `Needs-Author-Feedback`, 85 were closed by the stale-bot after 3–10 days of silence, including whole version streaks (Jellyfin.FFmpeg ×4, Spotube ×3, sleek ×2, shogihome 1.25.0). Several would have been trivially fixable (see DUP-1/ARCH-1).

**Implement**: poll the bot's open PRs; on `Needs-Author-Feedback`:
1. match the failure comment against known signatures (duplicate entry → apply DUP-1 fix and push; hash mismatch → re-hash and push; infra errors → comment to re-run);
2. otherwise escalate to a human (notification) well before the stale window;
3. when a version PR dies anyway, **queue the package for retry at the next release** with the learned fix applied (Jellyfin.FFmpeg shows the same failure four times with zero learning).

### WORK-5: Close-reason hygiene for superseded PRs
**Observed**: the bot already sometimes closes its own superseded PRs with comments ("closed in favor of .Full", "Superseeded by #150487", "reopened with correct version"). Good behaviour — formalize it: when the tool replaces a PR, close the old one with a cross-link comment.

---

## 11. PIPE — Repo/schema conventions

### PIPE-1: Line endings — one serializer, CRLF, single trailing newline, no hand-edits
**Observed**: an entire class of `fix line endings` / `next try: line endings` commits caused by editing CRLF manifests in the GitHub web editor, producing `\r\r\n` and mixed endings: PerfView ([#198344](https://github.com/microsoft/winget-pkgs/pull/198344)), Keeper ([#198202](https://github.com/microsoft/winget-pkgs/pull/198202), [#196924](https://github.com/microsoft/winget-pkgs/pull/196924)), cURL ([#197837](https://github.com/microsoft/winget-pkgs/pull/197837)), Jellyfin ([#216728](https://github.com/microsoft/winget-pkgs/pull/216728)), ResourceHacker (commit msg: *"fix line endings ... wtf is the new editor doing?!"*, [#235308](https://github.com/microsoft/winget-pkgs/pull/235308)), Gather full-file rewrite CRLF→LF ([#196793](https://github.com/microsoft/winget-pkgs/pull/196793)).

**Implement**: the v1 tool must always rewrite whole files through its own YAML emitter with **consistent CRLF** and exactly one trailing newline; any in-PR fix goes through the tool (checkout → regenerate → push), never the web editor. Add a serializer round-trip test asserting byte-identical output for unchanged input.
*(Note: `WinMatsch.Core` already owns `YamlEmitter`/`ManifestWriteOptions` — enforce these invariants there.)*

### PIPE-2: ManifestVersion + schema comment must be current and in sync
**Observed**: 52 PRs needed a follow-up commit `Update manifest version to 1.9.0` because the tool emitted/updated manifests at 1.0.0/1.4.0 during the VS Code URL-fix campaign ([#201589](https://github.com/microsoft/winget-pkgs/pull/201589)–[#201667](https://github.com/microsoft/winget-pkgs/pull/201667), Terraformer [#244877](https://github.com/microsoft/winget-pkgs/pull/244877)); EdgeTX needed the `# yaml-language-server: $schema=…1.6.0…` header comment bumped to match ManifestVersion 1.10.0 ([#275407](https://github.com/microsoft/winget-pkgs/pull/275407)).

**Implement**: single source of truth for the target ManifestVersion (config; currently 1.12.0 per [ValidationFailureGuide](https://github.com/microsoft/winget-pkgs/blob/master/doc/ValidationFailureGuide.md)); whenever a manifest is created *or modified*, upgrade all three files and regenerate the `$schema` comment from the same constant. Validate comment ⇔ field agreement in the local gate.

### PIPE-3: Package identity is immutable and case-sensitive
**Observed**: new-package PRs inventing wrong identifiers: `TesseractOCR.Tesseract` for the existing `tesseract-ocr.tesseract` ([#224123](https://github.com/microsoft/winget-pkgs/pull/224123)); `busytag.busytag` with Publisher "Busy Tag" for an MSIX whose identity is `Greynut SIA` ([#242669](https://github.com/microsoft/winget-pkgs/pull/242669) — died with `Manifest-Installer-Validation-Error`); publisher move handled via explicit "Move" PR (Terraformer, [#244877](https://github.com/microsoft/winget-pkgs/pull/244877)).

**Implement**: for updates, resolve the identifier from the repo (exact folder casing), never regenerate it from names. For new MSIX packages, derive Publisher/PackageName from the MSIX identity/signature (and include `PackageFamilyName` + `SignatureSha256`). Never emit half-filled ARP `Publisher`-only entries for MSIX.

### PIPE-4: Portable-in-zip needs `ArchiveBinariesDependOnPath` when binaries load siblings
**Observed**: hdrview ([#203020](https://github.com/microsoft/winget-pkgs/pull/203020)), Electron zips ([#249591](https://github.com/microsoft/winget-pkgs/pull/249591) header shows it set).
**Implement**: when a zip-portable's exe imports DLLs that sit next to it in the archive, set `ArchiveBinariesDependOnPath: true`.

### PIPE-5: Content-policy traps to avoid proactively
**Observed**: PowerShell-script "installer" blocked (`Scripted-Application`, WingetPathUpdater [#328932](https://github.com/microsoft/winget-pkgs/pull/328932)); UPX-packed binaries hitting SmartScreen/Defender FPs (miniserve [#155335](https://github.com/microsoft/winget-pkgs/pull/155335), refs [svenstaro/miniserve#1210](https://github.com/svenstaro/miniserve/issues/1210), [upx/upx#437](https://github.com/upx/upx/issues/437)); publishers that block Azure IP ranges → `Network-Blocker` (Oracle MySQL [#154168](https://github.com/microsoft/winget-pkgs/pull/154168): DeliveryOptimization `403 Forbidden`); installers registering Windows services fail as non-elevated (CrowdSec `Error 1920` [#172661](https://github.com/microsoft/winget-pkgs/pull/172661)) → needs `ElevationRequirement: elevationRequired` per [ValidationFailureGuide](https://github.com/microsoft/winget-pkgs/blob/master/doc/ValidationFailureGuide.md).

**Implement**: maintain package skip/annotation lists: `blocked-installer-type` (scripts, portable archives), `network-blocked-publishers` (expect manual validation; don't auto-abandon), `defender-fp-risk` (UPX-packed — pre-submit a Defender scan or expect FP workflow), `needs-elevation` (service installers → emit ElevationRequirement).

---

## 12. Priority order for implementation

Ranked by (PRs saved × severity of outcome):

| # | Rule | Impact evidence |
|---|------|-----------------|
| 1 | **DUP-1** local duplicate-entry gate | ≥14 dead PRs, multi-version package outages |
| 2 | **ARCH-1/2/3** payload-based arch detection + token table | 63 fix PRs, several dead PRs |
| 3 | **0.2** per-package learned overrides + preserve-merged-layout (DUP-2, SCOPE-1) | ~40 recurring fix PRs across 10+ packages |
| 4 | **WORK-4** feedback-loop automation (respond before stale-close) | 85 stale-closed PRs |
| 5 | **HASH-1/2** re-hash before push + fresh-release delay | 35 hash-fix PRs + hash-mismatch deaths |
| 6 | **MAP-1/2/3** deterministic asset mapping | 26 URL-swap PRs, Notesnook/sleek outages |
| 7 | **ARP-1/2/3/4** ARP hygiene | 79 PRs touched; DisplayVersion-overlap deaths |
| 8 | **META-1/2/4** URL & release-notes hygiene | 32 + 18 PRs |
| 9 | **WORK-1/2** duplicate/empty-PR prevention | 95 label hits, 3 empty PRs |
| 10 | **PIPE-1/2** serializer + schema currency | 52 + ~8 PRs |

---

## 13. Test corpus (real-world regression fixtures)

Use these exact releases as integration-test fixtures (expected outputs = the final merged manifests):

- **superProductivity 18.2.x** — plain vs `-x64` twin assets → x64 + x86 entries ([#363945](https://github.com/microsoft/winget-pkgs/pull/363945))
- **Notesnook 3.2.4** — setup vs `_portable` twins × x64/arm64 → 4 entries, distinct URLs/types ([#292471](https://github.com/microsoft/winget-pkgs/pull/292471))
- **UHK Agent 5.0.0** — ia32/x64/universal triple → 2 entries, universal omitted ([#199079](https://github.com/microsoft/winget-pkgs/pull/199079), [Komac#967](https://github.com/russellbanks/Komac/issues/967))
- **cURL 8.12.x** — `win32/win64/win64a` zips → x86/x64/arm64 ([#233979](https://github.com/microsoft/winget-pkgs/pull/233979))
- **Electron 35.2.0** — `win32-{ia32,x64,arm64}.zip` → 3 entries ([#249591](https://github.com/microsoft/winget-pkgs/pull/249591))
- **Pandoc 3.6.x** — single MSI, ALLUSERS twins → per-entry scope ([#210752](https://github.com/microsoft/winget-pkgs/pull/210752))
- **SurrealDB 2.x** — amd64-only asset → preserved x64+x86 same-URL pair ([#253118](https://github.com/microsoft/winget-pkgs/pull/253118))
- **ExifTool 12.8x** — `_32`/`_64` × user/machine → 4 entries with per-arch URLs + ARP entries ([#157949](https://github.com/microsoft/winget-pkgs/pull/157949))
- **Keeper Commander** — Inno x86compatible-but-x64 ([#271802](https://github.com/microsoft/winget-pkgs/pull/271802))
- **buf 1.64.0** — exe→zip packaging switch ([#335275](https://github.com/microsoft/winget-pkgs/pull/335275))
- **CloudDrive2 / Sonarr** — static ARP DisplayVersion → omit ([#287069](https://github.com/microsoft/winget-pkgs/pull/287069), [#267360](https://github.com/microsoft/winget-pkgs/pull/267360))
- **mise 2025.12.2** — ReleaseNotes bullet sanitization ([#322416](https://github.com/microsoft/winget-pkgs/pull/322416))

---

## 14. Explicit non-goals / known-unfixable (document, don't engineer around)

- **Pipeline infra flakiness** (`Internal-Error-*`, VCRedist outage [#152555](https://github.com/microsoft/winget-pkgs/issues/152555), SmartScreen re-scans): retry/comment, never mutate the manifest to chase infra errors — russellbanks' advice in [Komac#967](https://github.com/russellbanks/Komac/issues/967): a validation label doesn't necessarily mean the manifest is wrong.
- **Publisher-side chaos** (Betterbird's per-locale installer matrix, [#172491](https://github.com/microsoft/winget-pkgs/pull/172491) — author gave up: *"the amount of different locales and download links makes it difficult to automate"*): keep a `manual-only` package list.
- **Defender/PUA false positives** (Binary-Validation-Error, 10 unmerged): out of the tool's control; auto-file the Defender submission link in a PR comment and move on.
