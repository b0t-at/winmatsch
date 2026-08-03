# Current Test Matrix

**Generated:** 2026-08-02T22:52:13Z
**Analyzer:** `0.1.0+440e4a8f6fea324ac6988470b778b1097b15bd85` (Debug, current dirty worktree at `440e4a8f6fea324ac6988470b778b1097b15bd85`)
**Build fingerprint:** `0CCD7D206A494E3C0F73BBFE0CB3B7659B7F95BAA4060FBD5EEA1A44716CD822`
**Upstream sample:** 100 newest unique merged pull requests in [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs), ordered by `merged_at`
**Selection boundary:** [#411339](https://github.com/microsoft/winget-pkgs/pull/411339) at `2026-08-02T22:19:30Z` through [#411199](https://github.com/microsoft/winget-pkgs/pull/411199) at `2026-08-02T11:34:17Z`

## Current result

The current analyzer matched every testable installer declaration in this 100-PR sample.

| Metric | Current count |
|---|---:|
| Merged PRs selected | 100 |
| PRs with testable installer declarations | 95 |
| PRs without a testable installer declaration | 5 |
| Installer declarations | 149 |
| Unique installer URLs analyzed | 138 |
| Exact SHA/architecture/type matches | 143 |
| Compatible MSI/WiX refinements | 6 |
| Metadata discrepancies | 0 |
| SHA-256 mismatches | 0 |
| Analysis errors (declarations) | 0 |
| Analysis errors (unique URLs) | 0 |
| Per-URL wall-clock budget | 10 minutes |

## Method

1. Queried closed upstream pull requests and selected the newest 100 unique merged PRs by `merged_at`. Pagination continued until the 100th merge boundary was provably newer than every unvisited PR's `updated_at`.
2. Read each changed, non-removed `*.installer.yaml` from that PR's exact `merge_commit_sha`; no current-main manifest substitution was used.
3. Ran the rebuilt current-worktree CLI once per unique installer URL with JSON output, non-interactive mode, no persistent cache and a 10-minute wall-clock budget.
4. Compared the downloaded SHA-256 and each declaration's effective architecture, installer type, and declared nested installer type against any installer entry emitted for that URL.
5. Counted `msi` versus `wix` as a compatible refinement; every other type or architecture difference is a discrepancy.

## Per-PR matrix

| # | Merged (UTC) | Pull request | Package(s) | Decl. | Exact | Compat. | Diff | Hash | Error | Current status |
|---:|---|---|---|---:|---:|---:|---:|---:|---:|---|
| 1 | `2026-08-02 22:19:30` | [#411339](https://github.com/microsoft/winget-pkgs/pull/411339) | `agentty.agentty` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 2 | `2026-08-02 22:19:18` | [#411337](https://github.com/microsoft/winget-pkgs/pull/411337) | `LouisHinchliffe.HardwareMon` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 3 | `2026-08-02 22:18:24` | [#409626](https://github.com/microsoft/winget-pkgs/pull/409626) | `KDE.Skrooge` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 4 | `2026-08-02 21:57:11` | [#411335](https://github.com/microsoft/winget-pkgs/pull/411335) | `LouisHinchliffe.HardwareMon` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 5 | `2026-08-02 21:56:58` | [#411334](https://github.com/microsoft/winget-pkgs/pull/411334) | `ActualBudget.ActualBudget` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 6 | `2026-08-02 21:56:46` | [#411333](https://github.com/microsoft/winget-pkgs/pull/411333) | `alagrede.znote` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 7 | `2026-08-02 21:56:33` | [#411332](https://github.com/microsoft/winget-pkgs/pull/411332) | `LunarWerxs.SageThumbs2K` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 8 | `2026-08-02 21:56:21` | [#411327](https://github.com/microsoft/winget-pkgs/pull/411327) | `OlimoffDev.DevProjex` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 9 | `2026-08-02 21:36:33` | [#411340](https://github.com/microsoft/winget-pkgs/pull/411340) | — | 0 | 0 | 0 | 0 | 0 | 0 | **No testable installer manifest** |
| 10 | `2026-08-02 21:36:22` | [#411329](https://github.com/microsoft/winget-pkgs/pull/411329) | `RemcoStoeten.Skriuw` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 11 | `2026-08-02 21:36:10` | [#411328](https://github.com/microsoft/winget-pkgs/pull/411328) | `rvben.rumdl` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 12 | `2026-08-02 21:36:06` | [#411326](https://github.com/microsoft/winget-pkgs/pull/411326) | `Microsoft.APM` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 13 | `2026-08-02 21:35:47` | [#411325](https://github.com/microsoft/winget-pkgs/pull/411325) | `AdamGell.CMTraceOpen` | 4 | 4 | 0 | 0 | 0 | 0 | **Match** |
| 14 | `2026-08-02 21:35:41` | [#411323](https://github.com/microsoft/winget-pkgs/pull/411323) | `DollarLint.DollarLint` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 15 | `2026-08-02 21:34:38` | [#406524](https://github.com/microsoft/winget-pkgs/pull/406524) | `Waytech.CloudDrive2` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 16 | `2026-08-02 21:25:08` | [#411344](https://github.com/microsoft/winget-pkgs/pull/411344) | — | 0 | 0 | 0 | 0 | 0 | 0 | **No testable installer manifest** |
| 17 | `2026-08-02 21:11:33` | [#411324](https://github.com/microsoft/winget-pkgs/pull/411324) | `SimonSchubert.Kai` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 18 | `2026-08-02 21:11:22` | [#411321](https://github.com/microsoft/winget-pkgs/pull/411321) | `Cloudstic.CLI` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 19 | `2026-08-02 21:10:58` | [#411320](https://github.com/microsoft/winget-pkgs/pull/411320) | — | 0 | 0 | 0 | 0 | 0 | 0 | **No testable installer manifest** |
| 20 | `2026-08-02 20:46:08` | [#411318](https://github.com/microsoft/winget-pkgs/pull/411318) | `DBeaver.DBeaver.Community` | 4 | 4 | 0 | 0 | 0 | 0 | **Match** |
| 21 | `2026-08-02 20:45:27` | [#410798](https://github.com/microsoft/winget-pkgs/pull/410798) | `Kiwix.KiwixJS.Electron` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 22 | `2026-08-02 20:20:03` | [#411315](https://github.com/microsoft/winget-pkgs/pull/411315) | `twpayne.chezmoi` | 3 | 3 | 0 | 0 | 0 | 0 | **Match** |
| 23 | `2026-08-02 19:57:16` | [#410965](https://github.com/microsoft/winget-pkgs/pull/410965) | `SyntroSend.SyntroSend` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 24 | `2026-08-02 19:06:54` | [#411307](https://github.com/microsoft/winget-pkgs/pull/411307) | `hrzlgnm.mdns-browser` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 25 | `2026-08-02 19:06:41` | [#411305](https://github.com/microsoft/winget-pkgs/pull/411305) | `LuisPater.CLIProxyAPI` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 26 | `2026-08-02 19:06:27` | [#411304](https://github.com/microsoft/winget-pkgs/pull/411304) | `justhil.piDesktop` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 27 | `2026-08-02 19:06:12` | [#411302](https://github.com/microsoft/winget-pkgs/pull/411302) | `Microsoft.SafetyScanner` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 28 | `2026-08-02 19:05:59` | [#411299](https://github.com/microsoft/winget-pkgs/pull/411299) | `ZedIndustries.Zed` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 29 | `2026-08-02 19:05:45` | [#411298](https://github.com/microsoft/winget-pkgs/pull/411298) | `blacktop.ipswd` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 30 | `2026-08-02 19:05:31` | [#411297](https://github.com/microsoft/winget-pkgs/pull/411297) | `blacktop.ipsw` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 31 | `2026-08-02 18:20:10` | [#411296](https://github.com/microsoft/winget-pkgs/pull/411296) | `Ledgerful.Ledgerful` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 32 | `2026-08-02 18:20:00` | [#411295](https://github.com/microsoft/winget-pkgs/pull/411295) | `NimbusAgent.Nimbus` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 33 | `2026-08-02 18:19:33` | [#411292](https://github.com/microsoft/winget-pkgs/pull/411292) | `tinyrack.dotweave` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 34 | `2026-08-02 18:19:20` | [#411288](https://github.com/microsoft/winget-pkgs/pull/411288) | `OO-Software.SafeErase.Professional` | 2 | 0 | 2 | 0 | 0 | 0 | **Compatible** |
| 35 | `2026-08-02 18:19:07` | [#411287](https://github.com/microsoft/winget-pkgs/pull/411287) | `XinTaoFei.Codeg` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 36 | `2026-08-02 18:18:53` | [#411285](https://github.com/microsoft/winget-pkgs/pull/411285) | `ZedIndustries.Zed.Preview` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 37 | `2026-08-02 18:18:39` | [#411284](https://github.com/microsoft/winget-pkgs/pull/411284) | `Sourcegraph.Amp` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 38 | `2026-08-02 18:18:24` | [#411282](https://github.com/microsoft/winget-pkgs/pull/411282) | `justhil.piDesktop` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 39 | `2026-08-02 17:53:40` | [#411300](https://github.com/microsoft/winget-pkgs/pull/411300) | — | 0 | 0 | 0 | 0 | 0 | 0 | **No testable installer manifest** |
| 40 | `2026-08-02 17:53:25` | [#411289](https://github.com/microsoft/winget-pkgs/pull/411289) | `OO-Software.SafeErase.Server` | 2 | 0 | 2 | 0 | 0 | 0 | **Compatible** |
| 41 | `2026-08-02 17:53:03` | [#411279](https://github.com/microsoft/winget-pkgs/pull/411279) | `JanDeDobbeleer.OhMyPosh` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 42 | `2026-08-02 17:28:23` | [#411274](https://github.com/microsoft/winget-pkgs/pull/411274) | `ESEngine.ReasonixCLI` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 43 | `2026-08-02 17:28:07` | [#411259](https://github.com/microsoft/winget-pkgs/pull/411259) | `wuxiran.CC-Panes` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 44 | `2026-08-02 17:13:11` | [#411286](https://github.com/microsoft/winget-pkgs/pull/411286) | — | 0 | 0 | 0 | 0 | 0 | 0 | **No testable installer manifest** |
| 45 | `2026-08-02 17:12:55` | [#411276](https://github.com/microsoft/winget-pkgs/pull/411276) | `Vita3K.Vita3K` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 46 | `2026-08-02 17:12:41` | [#411275](https://github.com/microsoft/winget-pkgs/pull/411275) | `ESEngine.ReasonixDesktop` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 47 | `2026-08-02 17:12:25` | [#411273](https://github.com/microsoft/winget-pkgs/pull/411273) | `Alibaba.Qoder` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 48 | `2026-08-02 17:12:11` | [#411272](https://github.com/microsoft/winget-pkgs/pull/411272) | `Alochi.Monitoring` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 49 | `2026-08-02 17:11:57` | [#411269](https://github.com/microsoft/winget-pkgs/pull/411269) | `WinDirStat.WinDirStat` | 3 | 3 | 0 | 0 | 0 | 0 | **Match** |
| 50 | `2026-08-02 17:11:41` | [#411268](https://github.com/microsoft/winget-pkgs/pull/411268) | `Microsoft.SafetyScanner` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 51 | `2026-08-02 17:10:49` | [#410299](https://github.com/microsoft/winget-pkgs/pull/410299) | `0xJacky.nginx-ui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 52 | `2026-08-02 17:10:36` | [#410270](https://github.com/microsoft/winget-pkgs/pull/410270) | `0xJacky.nginx-ui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 53 | `2026-08-02 17:10:23` | [#410262](https://github.com/microsoft/winget-pkgs/pull/410262) | `0xJacky.nginx-ui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 54 | `2026-08-02 17:10:09` | [#410248](https://github.com/microsoft/winget-pkgs/pull/410248) | `0xJacky.nginx-ui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 55 | `2026-08-02 17:09:53` | [#409967](https://github.com/microsoft/winget-pkgs/pull/409967) | `RoxyBrowser.RoxyBrowser` | 4 | 4 | 0 | 0 | 0 | 0 | **Match** |
| 56 | `2026-08-02 16:54:24` | [#411214](https://github.com/microsoft/winget-pkgs/pull/411214) | `JanDeDobbeleer.OhMyPosh` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 57 | `2026-08-02 16:44:37` | [#411271](https://github.com/microsoft/winget-pkgs/pull/411271) | `szTheory.exifcleaner` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 58 | `2026-08-02 16:19:30` | [#411261](https://github.com/microsoft/winget-pkgs/pull/411261) | `Gitea.tea` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 59 | `2026-08-02 15:57:41` | [#411266](https://github.com/microsoft/winget-pkgs/pull/411266) | `thedavidweng.OpenKara` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 60 | `2026-08-02 15:57:26` | [#411260](https://github.com/microsoft/winget-pkgs/pull/411260) | `jmrplens.gitlab-mcp-server` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 61 | `2026-08-02 15:57:04` | [#411257](https://github.com/microsoft/winget-pkgs/pull/411257) | `Alibaba.OpenCodeReview` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 62 | `2026-08-02 15:56:13` | [#408012](https://github.com/microsoft/winget-pkgs/pull/408012) | `misxzaiz.Polaris` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 63 | `2026-08-02 15:32:58` | [#411258](https://github.com/microsoft/winget-pkgs/pull/411258) | `Dais.Dais` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 64 | `2026-08-02 15:32:44` | [#411256](https://github.com/microsoft/winget-pkgs/pull/411256) | `zoharbabin.web-researcher-mcp` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 65 | `2026-08-02 15:32:32` | [#411255](https://github.com/microsoft/winget-pkgs/pull/411255) | `Alochi.Monitoring` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 66 | `2026-08-02 15:06:42` | [#411218](https://github.com/microsoft/winget-pkgs/pull/411218) | `Alochi.Monitoring` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 67 | `2026-08-02 14:43:50` | [#411253](https://github.com/microsoft/winget-pkgs/pull/411253) | `SunJary.NetAssistant` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 68 | `2026-08-02 14:43:37` | [#411251](https://github.com/microsoft/winget-pkgs/pull/411251) | `Peters.Horizon` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 69 | `2026-08-02 14:43:22` | [#411249](https://github.com/microsoft/winget-pkgs/pull/411249) | `walles.moor` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 70 | `2026-08-02 14:43:09` | [#411248](https://github.com/microsoft/winget-pkgs/pull/411248) | `InferenceHub.InferenceHub` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 71 | `2026-08-02 14:21:19` | [#411246](https://github.com/microsoft/winget-pkgs/pull/411246) | `hrzlgnm.mdns-tui-browser` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 72 | `2026-08-02 14:21:06` | [#411245](https://github.com/microsoft/winget-pkgs/pull/411245) | `yetone.Alma` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 73 | `2026-08-02 14:20:54` | [#411244](https://github.com/microsoft/winget-pkgs/pull/411244) | `Sourcegraph.Amp` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 74 | `2026-08-02 14:20:40` | [#411242](https://github.com/microsoft/winget-pkgs/pull/411242) | `TopalaSoftwareSolutions.SIW` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 75 | `2026-08-02 14:20:25` | [#411236](https://github.com/microsoft/winget-pkgs/pull/411236) | `Nihitdev.yoo` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 76 | `2026-08-02 14:01:37` | [#411243](https://github.com/microsoft/winget-pkgs/pull/411243) | `SoftPerfect.NetworkScanner` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 77 | `2026-08-02 14:01:22` | [#411241](https://github.com/microsoft/winget-pkgs/pull/411241) | `Comfy.ComfyUI-Desktop` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 78 | `2026-08-02 14:01:07` | [#411240](https://github.com/microsoft/winget-pkgs/pull/411240) | `Oksion.XiControl` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 79 | `2026-08-02 14:00:55` | [#411239](https://github.com/microsoft/winget-pkgs/pull/411239) | `HarumiWeb.Xlflow` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 80 | `2026-08-02 14:00:42` | [#411238](https://github.com/microsoft/winget-pkgs/pull/411238) | `TX230.winproc-tui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 81 | `2026-08-02 14:00:27` | [#411237](https://github.com/microsoft/winget-pkgs/pull/411237) | `Microsoft.SafetyScanner` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 82 | `2026-08-02 14:00:13` | [#411234](https://github.com/microsoft/winget-pkgs/pull/411234) | `eddacraft.anvil` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 83 | `2026-08-02 14:00:00` | [#411233](https://github.com/microsoft/winget-pkgs/pull/411233) | `Lunasans.MovieShelf` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 84 | `2026-08-02 13:36:12` | [#411232](https://github.com/microsoft/winget-pkgs/pull/411232) | `TableClothProject.TableCloth` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 85 | `2026-08-02 13:35:59` | [#411226](https://github.com/microsoft/winget-pkgs/pull/411226) | `Google.Chrome.Canary` | 3 | 3 | 0 | 0 | 0 | 0 | **Match** |
| 86 | `2026-08-02 13:35:44` | [#411222](https://github.com/microsoft/winget-pkgs/pull/411222) | `ActiveDirectoryPro.365ProToolkit` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 87 | `2026-08-02 13:15:54` | [#411231](https://github.com/microsoft/winget-pkgs/pull/411231) | `yetone.Alma` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 88 | `2026-08-02 13:15:41` | [#411229](https://github.com/microsoft/winget-pkgs/pull/411229) | `QMPlay2.QMPlay2` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 89 | `2026-08-02 13:15:27` | [#411228](https://github.com/microsoft/winget-pkgs/pull/411228) | `OpenRCT2.OpenRCT2` | 3 | 3 | 0 | 0 | 0 | 0 | **Match** |
| 90 | `2026-08-02 13:15:13` | [#411227](https://github.com/microsoft/winget-pkgs/pull/411227) | `nashsu.LLMWiki` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 91 | `2026-08-02 13:15:00` | [#411225](https://github.com/microsoft/winget-pkgs/pull/411225) | `HMCL.HMCL.Dev.CNB` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 92 | `2026-08-02 13:14:46` | [#411223](https://github.com/microsoft/winget-pkgs/pull/411223) | `HMCL.HMCL.Dev` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 93 | `2026-08-02 12:23:06` | [#411219](https://github.com/microsoft/winget-pkgs/pull/411219) | `goptics.vizb` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 94 | `2026-08-02 12:22:54` | [#411217](https://github.com/microsoft/winget-pkgs/pull/411217) | `MattJColes.lgtmaybe` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 95 | `2026-08-02 11:57:05` | [#411215](https://github.com/microsoft/winget-pkgs/pull/411215) | `MattJColes.lgtmaybe` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 96 | `2026-08-02 11:35:14` | [#411212](https://github.com/microsoft/winget-pkgs/pull/411212) | `Sourcegraph.Amp` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 97 | `2026-08-02 11:35:00` | [#411211](https://github.com/microsoft/winget-pkgs/pull/411211) | `StablyAI.Orca` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 98 | `2026-08-02 11:34:46` | [#411208](https://github.com/microsoft/winget-pkgs/pull/411208) | `neelabo.NeeView` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 99 | `2026-08-02 11:34:31` | [#411203](https://github.com/microsoft/winget-pkgs/pull/411203) | `willibrandon.scout` | 2 | 0 | 2 | 0 | 0 | 0 | **Compatible** |
| 100 | `2026-08-02 11:34:17` | [#411199](https://github.com/microsoft/winget-pkgs/pull/411199) | `MaaAssistantArknights.MaaAssistantArknights` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |

## Current discrepancies

None.

## Compatible refinements

These rows are not failures. Every artifact is a valid MSI database, while its
SummaryInformation `Creating Application` field identifies the more-specific
authoring technology: O&O SafeErase reports `Windows Installer XML Toolset
(3.14.1.8722)`, and Scout reports `WiX Toolset (5.0.2.0)`. Both `msi` and `wix`
are valid WinGet installer types; the WinGet client places them in the same MSI
compatibility set and applies MSI properties to both. WinMatsch therefore keeps
the specific `wix` evidence but treats an existing generic `msi` declaration as
structurally compatible rather than forcing a manifest rewrite.

| PR | Package / artifact | Manifest | Analyzer |
|---:|---|---|---|
| [#411288](https://github.com/microsoft/winget-pkgs/pull/411288) | `OO-Software.SafeErase.Professional` / oosafeerase_21.3.26142_pro_eng_x64.msi | `msi/x64` | `wix/x64` |
| [#411288](https://github.com/microsoft/winget-pkgs/pull/411288) | `OO-Software.SafeErase.Professional` / oosafeerase_21.3.26142_pro_deu_x64.msi | `msi/x64` | `wix/x64` |
| [#411289](https://github.com/microsoft/winget-pkgs/pull/411289) | `OO-Software.SafeErase.Server` / oosafeerase_21.3.26142_server_eng_x64.msi | `msi/x64` | `wix/x64` |
| [#411289](https://github.com/microsoft/winget-pkgs/pull/411289) | `OO-Software.SafeErase.Server` / oosafeerase_21.3.26142_server_deu_x64.msi | `msi/x64` | `wix/x64` |
| [#411203](https://github.com/microsoft/winget-pkgs/pull/411203) | `willibrandon.scout` / scout-win-x64.msi | `msi/x64` | `wix/x64` |
| [#411203](https://github.com/microsoft/winget-pkgs/pull/411203) | `willibrandon.scout` / scout-win-arm64.msi | `msi/arm64` | `wix/arm64` |

## Current operational errors

None.

## PRs without a testable installer declaration

- [#411340](https://github.com/microsoft/winget-pkgs/pull/411340) — removed installer manifest(s): `manifests/d/DenisZhitnyakov/DolphinAnty/2026.196.258/DenisZhitnyakov.DolphinAnty.installer.yaml`.
- [#411344](https://github.com/microsoft/winget-pkgs/pull/411344) — no added or modified installer manifest.
- [#411320](https://github.com/microsoft/winget-pkgs/pull/411320) — no added or modified installer manifest.
- [#411300](https://github.com/microsoft/winget-pkgs/pull/411300) — removed installer manifest(s): `manifests/z/ZedIndustries/Zed/1.10.3/ZedIndustries.Zed.installer.yaml`.
- [#411286](https://github.com/microsoft/winget-pkgs/pull/411286) — removed installer manifest(s): `manifests/z/ZedIndustries/Zed/Preview/1.11.2-pre/ZedIndustries.Zed.Preview.installer.yaml`.

## Diagnostics observed

| Code | Unique successful analyses |
|---|---:|
| `ARCH001` | 3 |
| `DEP001` | 22 |
| `DEP002` | 51 |
| `DEP003` | 9 |
| `INNO002` | 6 |
| `INNO004` | 9 |
| `INNO007` | 5 |
| `INNO009` | 1 |
| `INNO013` | 3 |
| `JAVA001` | 2 |
| `NSIS001` | 2 |
| `NSIS004` | 4 |
| `SQUIRREL001` | 2 |
| `SQUIRREL002` | 2 |

## Reproduction

Each artifact can be rerun with the same current build:

```text
src/WinMatsch.Cli/bin/Debug/net10.0/winmatsch.exe analyze <InstallerUrl> --format json --interaction never
```

The per-PR table is the complete selected sample. All results above describe only this run and this analyzer build.
