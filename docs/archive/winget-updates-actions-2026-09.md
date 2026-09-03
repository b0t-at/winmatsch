# Action list for the winget-updates orchestrator — September 2026

> Hand this file to the agent that maintains the `winget-updates` repository
> (the automation that drives `damn-good-b0t`). It lists per-package actions
> derived from the audit in `winget-pr-audit-2026-09.md`, plus the global
> orchestrator settings the audit calls for. Every row cites the winget-pkgs
> pull request that is the evidence. PR numbers are in `microsoft/winget-pkgs`.

## Vocabulary

| Action | Meaning for the orchestrator |
|---|---|
| `disable` | Remove the package from automated monitoring/updates until a human re-enables it. Close the listed open PR with a one-line reason. |
| `pause` | Keep the package configured but submit nothing new while the condition holds (usually: an open PR with a blocking label). Never supersede the open PR. |
| `keep-open` | The open PR is waiting for a moderator. Do not close or supersede it for a patch release; re-check weekly. |
| `retire` | The identifier was replaced by another identifier. Stop monitoring it and record the successor. |
| `fix-mapping` | The release-to-identifier or release-to-version mapping is wrong; correct the pipeline configuration. |
| `close-pr` | Close the specific PR (duplicate or wrong spelling) with a one-line reason. |
| `override` | Add a manifest override (drop or replace a field) before the next submission. |
| `delay` | Another submitter (usually the publisher) files this package within hours of a release; wait 48 hours after a release before submitting, or drop the package. |
| `investigate` | A human needs to test the installer (Windows Sandbox) before the next submission. |

## Global settings (do these first)

1. **Release freshness delay.** winmatsch's 4-hour delay is silently zero when
   a release is resolved through direct asset URLs instead of the release API.
   Until the winmatsch fix lands, make the orchestrator wait at least 4 hours
   after the newest asset's upload time before running `update`. Evidence:
   loreste.mako 0.5.12 (#423604, `Error-Hash-Mismatch`, Windows asset
   re-uploaded 00:40:20Z), rizukirr.apic-gui 0.5.1 (#426181,
   `Error-Installer-Availability`).
2. **Supersede policy.** Do not close a bot PR that carries
   `Azure-Pipeline-Passed` plus `Validation-Executable-Error` or
   `Validation-No-Executables` when a patch release appears; that PR is in the
   moderators' manual-validation queue (about three weeks deep). Supersede
   only when the old PR is older than 14 days or the new release is not a
   patch. Evidence: AnInsomniacy.Aria2Next 2.5.9, 2.6.5, 2.6.6 all superseded
   within days, repository still at 2.5.6.
3. **Failure memory.** Before submitting, look at the package's last bot PR.
   If it is open or closed with `Validation-Defender-Error`,
   `Binary-Validation-Error`, `Validation-Certificate-Root`,
   `URL-Validation-Error`, `Validation-Unattended-Failed`,
   `Validation-Installation-Error`, `Validation-Shell-Execute`,
   `Blocking-Issue` or `DriverInstall`, do not submit unless the upstream
   release or the manifest trait that failed has changed. winmatsch's
   `complete` command now classifies these labels (`GH3211` escalations);
   consume its output.
4. **Channel throttle.** For identifiers ending in `.Nightly`, `.nightly`,
   `.Beta`, `.PreRelease`, `.Canary`: at most one submission per 3 days, and
   none while the previous channel PR is open or was closed with a blocking
   label in the last 3 days. Evidence: mikf.gallery-dl.Nightly, 9 PRs in 14
   days, 8 of them Defender closures.
5. **Version spelling.** Use the release tag's spelling when it is numerically
   equal to the derived version, and match the zero-padding style of the
   package's existing versions. Evidence: SteamDatabase.SteamTokenDumper
   submitted as `2026.8.20` (#425659) and `2026.08.20` (#426111).
6. **Pinned identifiers.** For identifiers whose last segment is a number
   (`OpenJS.Electron.41`, `LookupFoundation.RevitLookup.2021`), select only
   releases whose version starts with that number. winmatsch preflight now
   rejects the rest (`VLD2302`), but the pipeline should not pick them.
7. **Line endings.** Ensure the orchestrator's checkout writes YAML with LF
   (`core.autocrlf=false`); two PRs on 5–7 August needed a human "Fix manifest
   line endings" commit (#413093, #412755).
8. **Custom PR titles.** Do not pass the canonical title as `--title`; it is
   appended after a dash (#412755: "Add version: X - Update version: X").

## Per-package actions

### Disable: dead publisher sites or permanently failing installers

| Package | Action | Open PR to close | Evidence |
|---|---|---|---|
| yhay81.sqrail | `disable` | #426592 | `sqrails.yhay81.com` does not resolve; submitted four times (#418427, #420304, #423403, #426592). The earlier exclusion did not stick. |
| Luxview.AgainstMe | `disable` | #426541 | `againstme.app`, `/privacy`, `/eula` all 404; three submissions (#420298, #422832, #426541). |
| OmniEdge.OmniEdgeCLI | `disable` until `connect.omniedge.io` answers again | #426112 | DNS name exists with no address record for five URLs (#420882, #423499, #426112). |
| RoniLehto.LMath | `disable` (confirm it stayed disabled) | — | TLS certificate name mismatch on `lehtodigital.fi` (#420967). |
| Stells.EntraChecks | `disable`, or `override` PublisherUrl | — | `https://github.com/f8l124` returns 404, renamed GitHub user (#418503). |
| MonzerOsman.YallaVideo | `override` LicenseUrl to an existing path, else `disable` | — | `…/YallaVideo/blob/master/LICENSE` returns 404 (#418595). |
| 8bit2qubit.OmniConsole | `disable` until upstream signs with a trusted certificate | #427972 | MSIX signed by self-signed `CN=8bit2qubit`; seven submissions 4.2.1.0 → 4.4.3.0, only 2.1.0.0 ever merged. |
| Calibrite.PROFILER | `disable` | #422969 (leave to moderators, it has `Blocking-Issue`) | Installs a driver; `DriverInstall`, `Blocking-Issue`. |
| BenModigell.Vibes | `pause`; human decides whether to `override` (drop `vibesdj.io` URLs) | #426177 (second PR for 1.4.5; #424635 already closed) | `vibesdj.io` answers 429 to the validator on four versions. |

### Pause: scanner verdicts (do not supersede the open PR)

| Package | Open PR | Evidence |
|---|---|---|
| Thurbeen.thurbox | #427820 | `Validation-Defender-Error` on seven versions; 2.8.1 and 2.10.2 merged, so re-check per release. |
| mikf.gallery-dl.Nightly | (throttle per global setting 4) | Daily nightly, Defender on most builds. |
| yt-dlp.yt-dlp.nightly | (throttle per global setting 4) | Defender and installation errors; last merged nightly 2026.08.04. |
| sluicesync.sluice | #427640 | Defender plus executable error. |
| AtaraxyLabs.sem | #427636 | Defender (0.22.1, 0.23.0, 0.24.0). |
| Tyrrrz.DiscordChatExporter.CLI | #425880 | Defender (2.47.3, 2.48). |
| GeodeSDK.GeodeCLI | #426481 | Defender. |
| LocalStack.localstack-cli | #426241 | Defender. |
| Arihant25.Chargle | #423126 | Defender. |
| Microsoft.DSC | #420305 | Defender on the portable zip; PR already carries human refresh commits. |
| AntimatterStudios.ext4-win-driver | #422549 | Defender. |
| SparkLabs.openvpn-configuration-generator | #425428 | Defender. |
| goplus.xgo | #421166 | Defender plus executable error. |
| EndlessSky.EndlessSky | #425143 | Defender. |
| Winix.Peep, Odonno.jj-commit, OnionShare.OnionShare, getzola.zola, agentteamland.atl | — (closed) | Defender on 16–17 August; submit the next upstream release once, then pause again on a repeat. |
| k0sproject.k0sctl | #419190 (`keep-open`) | AVAST false positive; maintainer obtained whitelisting on 27 August. Ask a moderator for `@wingetbot run`. |
| GolangCI.golangci-lint | check #426245 | AVIRA `TR/Crypt.EPACK.Gen2` on 2.13.1 (#422073). |
| tecwindow.Albayan | #421725 (`keep-open`) | `Binary-Validation-Error`. |
| nickbeth.wsl-usb-manager | #419203 (`keep-open`) | `Binary-Validation-Error`. |
| soymadip.quikrun | #421174 (`keep-open`) | `Binary-Validation-Error`. |

### Investigate: installer behaviour changed or switches are wrong

| Package | Open PR | What to check |
|---|---|---|
| fairdataihub.SODA-for-SPARC | #427237 (`pause`) | Manifest identical to merged 18.2.0 (nullsoft, x86, no switches) yet 19.0.0–19.0.2 block on input. Test `soda-for-sparc-19.0.2-setup.exe /S` in Windows Sandbox; electron-builder installers sometimes need `/S /allusers` or now prompt on upgrade. |
| EdgeTX.Companion | #428303 (`pause`) | Manifest identical to merged 2.12.2 (zip → nullsoft, `requireAdministrator`); validator reports a blocking prompt. Test the nested `companion-windows-2.12.4.exe /S`. |
| DiRoots.ProSheets | #420154 (`pause`) | Reviewer: `accept_eula=1` switches require `Agreements` in the locale manifest, and the switches no longer work. Add `Agreements` (label + URL) and re-verify silent switches; winmatsch now warns (`SCOPE-5`). |
| RythenGlyth.masterclass-dl | #420886 (`pause`) | `Validation-Installation-Error` twice. Archives are correct (bare `masterclass-dl.exe`); suspect the dependency installs (Gyan.FFmpeg, yt-dlp.yt-dlp) or the 32-bit `arm` entry. Try without `arm`. |
| CerberAuth.jwtop | #426482 (`pause`) | `Validation-Installation-Error`; archives contain `LICENSE`, `README.md`, `jwtop.exe`. Previous merged 0.6.0 had the same layout, so ask a moderator for the log. |
| OpenCoworkAI.OpenCoDesign | #421718 (`pause`) | `ShellExecute installer failed: 3221225477` (access violation); installer crashes under the validator. |
| CrowdSecurity.CrowdSecWindowsFirewallBouncer | #420774 (`pause`) | `Validation-Shell-Execute`; try `ElevationRequirement: elevationRequired`. |
| alirezagsm.trayy | #421521 | `Internal-Error-PR` plus `Needs-Attention`; a moderator must look. |
| ExpressLRS.ExpressLRS-Configurator | #420514 (`keep-open`) | `Internal-Error-Dynamic-Scan`, `Retry-1`; ask for a rerun. |

### Retire: identifier moved or renamed

| Package | Successor | Evidence |
|---|---|---|
| HiroSystems.Clarinet | StacksLabs.Clarinet | Validator matched the installer hash to `manifests/s/StacksLabs/Clarinet/3.23.1` (#422360). |
| Docker.ds | Docker.sbx | Validator matched the hash to `manifests/d/Docker/sbx/0.38.0` (#419942). |

### Fix mapping: wrong version stream, channel or spelling

| Package | Action | Evidence |
|---|---|---|
| OpenJS.Electron.39, OpenJS.Electron.41 (audit every OpenJS.Electron.N pipeline) | `fix-mapping`: only releases whose major equals N | Both received 43.4.0 (#418598, #418501); closed as duplicates of `OpenJS/Electron/43`. |
| LookupFoundation.RevitLookup.2021 (audit every RevitLookup.YYYY pipeline) | `fix-mapping`: only YYYY.x releases | Received 2027.0.3 (#418437). |
| PLFJY.ContextMenuMgrPlus.Beta | `fix-mapping`: beta channel must map to prerelease assets only | Closed as `Possible-Duplicate` (#418518). |
| SteamDatabase.SteamTokenDumper | `close-pr` #425659 (`2026.8.20`); keep #426111 (`2026.08.20`) | Same release submitted under two spellings; existing versions are zero-padded. |

### Delay: the publisher or another automation submits first

Wait 48 hours after a release (or drop the package) for: rizukirr.apic-gui
(#426747 by the publisher), raphamorim.rio (#413040), etcd-io.etcd (#413263),
JanProchazka.dbgate (#413274), GodotLauncher.Launcher (#419629),
futrime.lip (#421177), jj-vcs.jj (#413112), seerge.g-helper (#417102),
JohannesMillan.superProductivity (#413176/#413269),
sunfish-shogi.shogihome (#413241/#413283).

### Keep open: awaiting manual validation (pipeline passed)

Do not supersede these for a patch release; re-check weekly. The label means
"the test could not locate the primary application executable", which
moderators resolve by hand for portable CLI tools.

Shssoichiro.Oxipng #428402 · orhun.git-cliff #427816 ·
moneymanagerex.moneymanagerex #427338 · Google.Protobuf #427084 ·
Mickem.NSClient #426749 · RicardoCabral.icuvisor #426591 ·
AnInsomniacy.Aria2Next #426373 · CyrilPeng.VeneraNext #426246 ·
SteamDatabase.SteamTokenDumper #426111 · nu774.qaac #426110 ·
Docker.docker-credential-wincred #425879 · idleberg.ardent #425279 ·
binbat.whipinto #419500 · Google.OSVScanner #420885 ·
sorairolake.qrtool #423300 · Snesnopic.Flacoutcpp #423030 ·
Mozilla.mozregression #423027 · CargoLambda.CargoLambda #422446 ·
LuantiTeam.Luanti #422298 · garyttierney.me3 #422286 ·
Harmonoid.Harmonoid #420140 · Microsoft.Wassette #419355 ·
HongMinhee.Hongdown #421714 · binbat.whepfrom #419935 · Miller.Miller #418516 ·
binbat.liveman #418580 · Microsoft.OpenAPI.Kiota #419489 ·
binbat.net4mqtt #420402 · GDQuest.GDScript.Formatter #420498 ·
AdGuard.dnsproxy #420855 · CycloneDX.CLI #420574 · facebook.pyrefly #419499 ·
D2L.BMX #420850 · DamianH.BrowserWrangler #419931 · sisong.HDiffPatch #419186 ·
kagg886.Pixiv-MultiPlatform #418588 · KeeperSecurity.Commander #417572 ·
rsjaffe.MIDI2LR #418436 · Turin.tmuxw #420394 · bkryza.clang-uml #421091.

Optional experiment for this class: add
`InstallationMetadata.Files[{RelativeFilePath: <exe>, FileType: launch, InvocationParameter: --version}]`
to one portable manifest and watch whether the label disappears; a Microsoft
engineer resolved Kiota with the equivalent `@wingetbot installationmetadata
exe add kiota.exe --version` command.
