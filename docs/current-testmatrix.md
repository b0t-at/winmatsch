# Current Test Matrix

**Generated:** 2026-08-02T21:26:12Z
**Analyzer:** `0.1.0+562aa9718011ccf99f139ebf3eec28637974d31a` (Debug, current dirty worktree at `562aa9718011ccf99f139ebf3eec28637974d31a`)
**Build fingerprint:** `B649B82932050C1298353F75D3A0CA25E3778D658440EBD29B29455F4EDC3B3A`
**Upstream sample:** 100 newest unique merged pull requests in [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs), ordered by `merged_at`
**Selection boundary:** [#411344](https://github.com/microsoft/winget-pkgs/pull/411344) at `2026-08-02T21:25:08Z` through [#411182](https://github.com/microsoft/winget-pkgs/pull/411182) at `2026-08-02T09:07:28Z`

## Current result

The current run contains **10 metadata discrepancies**, **0 SHA-256 mismatches**, **9 declaration-level analysis errors**, and **0 manifest retrieval/parsing errors**.

| Metric | Current count |
|---|---:|
| Merged PRs selected | 100 |
| PRs with testable installer declarations | 96 |
| PRs without a testable installer declaration | 4 |
| Installer declarations | 154 |
| Unique installer URLs attempted | 144 |
| Exact SHA/architecture/type matches | 129 |
| Compatible MSI/WiX refinements | 6 |
| Metadata discrepancies | 10 |
| SHA-256 mismatches | 0 |
| Analysis errors (declarations) | 9 |
| Analysis errors (unique URLs) | 6 |
| Successful analyses served from cache | 3 |

## Current findings

- All **10 metadata discrepancies** are declaration-level rows across **6 unique URLs**. Every downloaded SHA-256 matches its manifest, and every architecture matches; the difference is consistently manifest `nullsoft` versus analyzer `exe`.
- **5 unique URLs** affecting **8 declarations** fail with `The 7-Zip self-extractor contains more than 256 entries.`
- The remaining operational error is the 350.7 MB WPS installer, whose direct URL analysis exceeded the test harness's 20-minute limit.
- All **6 compatible refinements** are manifest `msi` packages that the analyzer identifies more specifically as `wix` with the same architecture.
- Counts are declaration-level. Repeated artifacts in the detail tables represent distinct entries in the source manifest and are intentionally retained.

## Method

1. Queried closed upstream pull requests and selected the newest 100 unique merged PRs by `merged_at`. Pagination continued until the 100th merge boundary was provably newer than every unvisited PR's `updated_at`.
2. Read each changed, non-removed `*.installer.yaml` from that PR's exact `merge_commit_sha`; no current-main manifest substitution was used.
3. Ran the rebuilt current-worktree CLI once per unique installer URL with JSON output and non-interactive mode. Persistent download-cache hits still rerun analysis with the current binaries.
4. Compared the downloaded SHA-256 and each declaration's effective architecture, installer type, and declared nested installer type against any installer entry emitted for that URL.
5. Counted `msi` versus `wix` as a compatible refinement; every other type or architecture difference is a discrepancy.

## Per-PR matrix

| # | Merged (UTC) | Pull request | Package(s) | Decl. | Exact | Compat. | Diff | Hash | Error | Current status |
|---:|---|---|---|---:|---:|---:|---:|---:|---:|---|
| 1 | `2026-08-02 21:25:08` | [#411344](https://github.com/microsoft/winget-pkgs/pull/411344) | — | 0 | 0 | 0 | 0 | 0 | 0 | **No testable installer manifest** |
| 2 | `2026-08-02 21:11:33` | [#411324](https://github.com/microsoft/winget-pkgs/pull/411324) | `SimonSchubert.Kai` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 3 | `2026-08-02 21:11:22` | [#411321](https://github.com/microsoft/winget-pkgs/pull/411321) | `Cloudstic.CLI` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 4 | `2026-08-02 21:10:58` | [#411320](https://github.com/microsoft/winget-pkgs/pull/411320) | — | 0 | 0 | 0 | 0 | 0 | 0 | **No testable installer manifest** |
| 5 | `2026-08-02 20:46:08` | [#411318](https://github.com/microsoft/winget-pkgs/pull/411318) | `DBeaver.DBeaver.Community` | 4 | 4 | 0 | 0 | 0 | 0 | **Match** |
| 6 | `2026-08-02 20:45:27` | [#410798](https://github.com/microsoft/winget-pkgs/pull/410798) | `Kiwix.KiwixJS.Electron` | 2 | 0 | 0 | 0 | 0 | 2 | **Error** |
| 7 | `2026-08-02 20:20:03` | [#411315](https://github.com/microsoft/winget-pkgs/pull/411315) | `twpayne.chezmoi` | 3 | 3 | 0 | 0 | 0 | 0 | **Match** |
| 8 | `2026-08-02 19:57:16` | [#410965](https://github.com/microsoft/winget-pkgs/pull/410965) | `SyntroSend.SyntroSend` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 9 | `2026-08-02 19:06:54` | [#411307](https://github.com/microsoft/winget-pkgs/pull/411307) | `hrzlgnm.mdns-browser` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 10 | `2026-08-02 19:06:41` | [#411305](https://github.com/microsoft/winget-pkgs/pull/411305) | `LuisPater.CLIProxyAPI` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 11 | `2026-08-02 19:06:27` | [#411304](https://github.com/microsoft/winget-pkgs/pull/411304) | `justhil.piDesktop` | 2 | 0 | 0 | 2 | 0 | 0 | **Discrepancy** |
| 12 | `2026-08-02 19:06:12` | [#411302](https://github.com/microsoft/winget-pkgs/pull/411302) | `Microsoft.SafetyScanner` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 13 | `2026-08-02 19:05:59` | [#411299](https://github.com/microsoft/winget-pkgs/pull/411299) | `ZedIndustries.Zed` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 14 | `2026-08-02 19:05:45` | [#411298](https://github.com/microsoft/winget-pkgs/pull/411298) | `blacktop.ipswd` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 15 | `2026-08-02 19:05:31` | [#411297](https://github.com/microsoft/winget-pkgs/pull/411297) | `blacktop.ipsw` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 16 | `2026-08-02 18:20:10` | [#411296](https://github.com/microsoft/winget-pkgs/pull/411296) | `Ledgerful.Ledgerful` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 17 | `2026-08-02 18:20:00` | [#411295](https://github.com/microsoft/winget-pkgs/pull/411295) | `NimbusAgent.Nimbus` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 18 | `2026-08-02 18:19:33` | [#411292](https://github.com/microsoft/winget-pkgs/pull/411292) | `tinyrack.dotweave` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 19 | `2026-08-02 18:19:20` | [#411288](https://github.com/microsoft/winget-pkgs/pull/411288) | `OO-Software.SafeErase.Professional` | 2 | 0 | 2 | 0 | 0 | 0 | **Compatible** |
| 20 | `2026-08-02 18:19:07` | [#411287](https://github.com/microsoft/winget-pkgs/pull/411287) | `XinTaoFei.Codeg` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 21 | `2026-08-02 18:18:53` | [#411285](https://github.com/microsoft/winget-pkgs/pull/411285) | `ZedIndustries.Zed.Preview` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 22 | `2026-08-02 18:18:39` | [#411284](https://github.com/microsoft/winget-pkgs/pull/411284) | `Sourcegraph.Amp` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 23 | `2026-08-02 18:18:24` | [#411282](https://github.com/microsoft/winget-pkgs/pull/411282) | `justhil.piDesktop` | 2 | 0 | 0 | 2 | 0 | 0 | **Discrepancy** |
| 24 | `2026-08-02 17:53:40` | [#411300](https://github.com/microsoft/winget-pkgs/pull/411300) | — | 0 | 0 | 0 | 0 | 0 | 0 | **No testable installer manifest** |
| 25 | `2026-08-02 17:53:25` | [#411289](https://github.com/microsoft/winget-pkgs/pull/411289) | `OO-Software.SafeErase.Server` | 2 | 0 | 2 | 0 | 0 | 0 | **Compatible** |
| 26 | `2026-08-02 17:53:03` | [#411279](https://github.com/microsoft/winget-pkgs/pull/411279) | `JanDeDobbeleer.OhMyPosh` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 27 | `2026-08-02 17:28:23` | [#411274](https://github.com/microsoft/winget-pkgs/pull/411274) | `ESEngine.ReasonixCLI` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 28 | `2026-08-02 17:28:07` | [#411259](https://github.com/microsoft/winget-pkgs/pull/411259) | `wuxiran.CC-Panes` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 29 | `2026-08-02 17:13:11` | [#411286](https://github.com/microsoft/winget-pkgs/pull/411286) | — | 0 | 0 | 0 | 0 | 0 | 0 | **No testable installer manifest** |
| 30 | `2026-08-02 17:12:55` | [#411276](https://github.com/microsoft/winget-pkgs/pull/411276) | `Vita3K.Vita3K` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 31 | `2026-08-02 17:12:41` | [#411275](https://github.com/microsoft/winget-pkgs/pull/411275) | `ESEngine.ReasonixDesktop` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 32 | `2026-08-02 17:12:25` | [#411273](https://github.com/microsoft/winget-pkgs/pull/411273) | `Alibaba.Qoder` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 33 | `2026-08-02 17:12:11` | [#411272](https://github.com/microsoft/winget-pkgs/pull/411272) | `Alochi.Monitoring` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 34 | `2026-08-02 17:11:57` | [#411269](https://github.com/microsoft/winget-pkgs/pull/411269) | `WinDirStat.WinDirStat` | 3 | 3 | 0 | 0 | 0 | 0 | **Match** |
| 35 | `2026-08-02 17:11:41` | [#411268](https://github.com/microsoft/winget-pkgs/pull/411268) | `Microsoft.SafetyScanner` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 36 | `2026-08-02 17:10:49` | [#410299](https://github.com/microsoft/winget-pkgs/pull/410299) | `0xJacky.nginx-ui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 37 | `2026-08-02 17:10:36` | [#410270](https://github.com/microsoft/winget-pkgs/pull/410270) | `0xJacky.nginx-ui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 38 | `2026-08-02 17:10:23` | [#410262](https://github.com/microsoft/winget-pkgs/pull/410262) | `0xJacky.nginx-ui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 39 | `2026-08-02 17:10:09` | [#410248](https://github.com/microsoft/winget-pkgs/pull/410248) | `0xJacky.nginx-ui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 40 | `2026-08-02 17:09:53` | [#409967](https://github.com/microsoft/winget-pkgs/pull/409967) | `RoxyBrowser.RoxyBrowser` | 4 | 0 | 0 | 4 | 0 | 0 | **Discrepancy** |
| 41 | `2026-08-02 16:54:24` | [#411214](https://github.com/microsoft/winget-pkgs/pull/411214) | `JanDeDobbeleer.OhMyPosh` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 42 | `2026-08-02 16:44:37` | [#411271](https://github.com/microsoft/winget-pkgs/pull/411271) | `szTheory.exifcleaner` | 1 | 0 | 0 | 0 | 0 | 1 | **Error** |
| 43 | `2026-08-02 16:19:30` | [#411261](https://github.com/microsoft/winget-pkgs/pull/411261) | `Gitea.tea` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 44 | `2026-08-02 15:57:41` | [#411266](https://github.com/microsoft/winget-pkgs/pull/411266) | `thedavidweng.OpenKara` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 45 | `2026-08-02 15:57:26` | [#411260](https://github.com/microsoft/winget-pkgs/pull/411260) | `jmrplens.gitlab-mcp-server` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 46 | `2026-08-02 15:57:04` | [#411257](https://github.com/microsoft/winget-pkgs/pull/411257) | `Alibaba.OpenCodeReview` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 47 | `2026-08-02 15:56:13` | [#408012](https://github.com/microsoft/winget-pkgs/pull/408012) | `misxzaiz.Polaris` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 48 | `2026-08-02 15:32:58` | [#411258](https://github.com/microsoft/winget-pkgs/pull/411258) | `Dais.Dais` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 49 | `2026-08-02 15:32:44` | [#411256](https://github.com/microsoft/winget-pkgs/pull/411256) | `zoharbabin.web-researcher-mcp` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 50 | `2026-08-02 15:32:32` | [#411255](https://github.com/microsoft/winget-pkgs/pull/411255) | `Alochi.Monitoring` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 51 | `2026-08-02 15:06:42` | [#411218](https://github.com/microsoft/winget-pkgs/pull/411218) | `Alochi.Monitoring` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 52 | `2026-08-02 14:43:50` | [#411253](https://github.com/microsoft/winget-pkgs/pull/411253) | `SunJary.NetAssistant` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 53 | `2026-08-02 14:43:37` | [#411251](https://github.com/microsoft/winget-pkgs/pull/411251) | `Peters.Horizon` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 54 | `2026-08-02 14:43:22` | [#411249](https://github.com/microsoft/winget-pkgs/pull/411249) | `walles.moor` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 55 | `2026-08-02 14:43:09` | [#411248](https://github.com/microsoft/winget-pkgs/pull/411248) | `InferenceHub.InferenceHub` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 56 | `2026-08-02 14:21:19` | [#411246](https://github.com/microsoft/winget-pkgs/pull/411246) | `hrzlgnm.mdns-tui-browser` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 57 | `2026-08-02 14:21:06` | [#411245](https://github.com/microsoft/winget-pkgs/pull/411245) | `yetone.Alma` | 2 | 0 | 0 | 0 | 0 | 2 | **Error** |
| 58 | `2026-08-02 14:20:54` | [#411244](https://github.com/microsoft/winget-pkgs/pull/411244) | `Sourcegraph.Amp` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 59 | `2026-08-02 14:20:40` | [#411242](https://github.com/microsoft/winget-pkgs/pull/411242) | `TopalaSoftwareSolutions.SIW` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 60 | `2026-08-02 14:20:25` | [#411236](https://github.com/microsoft/winget-pkgs/pull/411236) | `Nihitdev.yoo` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 61 | `2026-08-02 14:01:37` | [#411243](https://github.com/microsoft/winget-pkgs/pull/411243) | `SoftPerfect.NetworkScanner` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 62 | `2026-08-02 14:01:22` | [#411241](https://github.com/microsoft/winget-pkgs/pull/411241) | `Comfy.ComfyUI-Desktop` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 63 | `2026-08-02 14:01:07` | [#411240](https://github.com/microsoft/winget-pkgs/pull/411240) | `Oksion.XiControl` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 64 | `2026-08-02 14:00:55` | [#411239](https://github.com/microsoft/winget-pkgs/pull/411239) | `HarumiWeb.Xlflow` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 65 | `2026-08-02 14:00:42` | [#411238](https://github.com/microsoft/winget-pkgs/pull/411238) | `TX230.winproc-tui` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 66 | `2026-08-02 14:00:27` | [#411237](https://github.com/microsoft/winget-pkgs/pull/411237) | `Microsoft.SafetyScanner` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 67 | `2026-08-02 14:00:13` | [#411234](https://github.com/microsoft/winget-pkgs/pull/411234) | `eddacraft.anvil` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 68 | `2026-08-02 14:00:00` | [#411233](https://github.com/microsoft/winget-pkgs/pull/411233) | `Lunasans.MovieShelf` | 1 | 0 | 0 | 1 | 0 | 0 | **Discrepancy** |
| 69 | `2026-08-02 13:36:12` | [#411232](https://github.com/microsoft/winget-pkgs/pull/411232) | `TableClothProject.TableCloth` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 70 | `2026-08-02 13:35:59` | [#411226](https://github.com/microsoft/winget-pkgs/pull/411226) | `Google.Chrome.Canary` | 3 | 3 | 0 | 0 | 0 | 0 | **Match** |
| 71 | `2026-08-02 13:35:44` | [#411222](https://github.com/microsoft/winget-pkgs/pull/411222) | `ActiveDirectoryPro.365ProToolkit` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 72 | `2026-08-02 13:15:54` | [#411231](https://github.com/microsoft/winget-pkgs/pull/411231) | `yetone.Alma` | 2 | 0 | 0 | 0 | 0 | 2 | **Error** |
| 73 | `2026-08-02 13:15:41` | [#411229](https://github.com/microsoft/winget-pkgs/pull/411229) | `QMPlay2.QMPlay2` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 74 | `2026-08-02 13:15:27` | [#411228](https://github.com/microsoft/winget-pkgs/pull/411228) | `OpenRCT2.OpenRCT2` | 3 | 3 | 0 | 0 | 0 | 0 | **Match** |
| 75 | `2026-08-02 13:15:13` | [#411227](https://github.com/microsoft/winget-pkgs/pull/411227) | `nashsu.LLMWiki` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 76 | `2026-08-02 13:15:00` | [#411225](https://github.com/microsoft/winget-pkgs/pull/411225) | `HMCL.HMCL.Dev.CNB` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 77 | `2026-08-02 13:14:46` | [#411223](https://github.com/microsoft/winget-pkgs/pull/411223) | `HMCL.HMCL.Dev` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 78 | `2026-08-02 12:23:06` | [#411219](https://github.com/microsoft/winget-pkgs/pull/411219) | `goptics.vizb` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 79 | `2026-08-02 12:22:54` | [#411217](https://github.com/microsoft/winget-pkgs/pull/411217) | `MattJColes.lgtmaybe` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 80 | `2026-08-02 11:57:05` | [#411215](https://github.com/microsoft/winget-pkgs/pull/411215) | `MattJColes.lgtmaybe` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 81 | `2026-08-02 11:35:14` | [#411212](https://github.com/microsoft/winget-pkgs/pull/411212) | `Sourcegraph.Amp` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 82 | `2026-08-02 11:35:00` | [#411211](https://github.com/microsoft/winget-pkgs/pull/411211) | `StablyAI.Orca` | 1 | 0 | 0 | 0 | 0 | 1 | **Error** |
| 83 | `2026-08-02 11:34:46` | [#411208](https://github.com/microsoft/winget-pkgs/pull/411208) | `neelabo.NeeView` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 84 | `2026-08-02 11:34:31` | [#411203](https://github.com/microsoft/winget-pkgs/pull/411203) | `willibrandon.scout` | 2 | 0 | 2 | 0 | 0 | 0 | **Compatible** |
| 85 | `2026-08-02 11:34:17` | [#411199](https://github.com/microsoft/winget-pkgs/pull/411199) | `MaaAssistantArknights.MaaAssistantArknights` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 86 | `2026-08-02 11:10:27` | [#411201](https://github.com/microsoft/winget-pkgs/pull/411201) | `MattJColes.lgtmaybe` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 87 | `2026-08-02 11:10:14` | [#411200](https://github.com/microsoft/winget-pkgs/pull/411200) | `Microsoft.SafetyScanner` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 88 | `2026-08-02 11:10:01` | [#411183](https://github.com/microsoft/winget-pkgs/pull/411183) | `Kingsoft.WPSOffice.x64` | 1 | 0 | 0 | 0 | 0 | 1 | **Error** |
| 89 | `2026-08-02 10:45:13` | [#411197](https://github.com/microsoft/winget-pkgs/pull/411197) | `demkada.ArgyCode` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 90 | `2026-08-02 10:45:00` | [#411196](https://github.com/microsoft/winget-pkgs/pull/411196) | `NimbusAgent.Nimbus` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 91 | `2026-08-02 10:20:12` | [#411194](https://github.com/microsoft/winget-pkgs/pull/411194) | `MattJColes.lgtmaybe` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 92 | `2026-08-02 10:19:59` | [#411193](https://github.com/microsoft/winget-pkgs/pull/411193) | `xiaocang.EasydictforWindows` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 93 | `2026-08-02 09:57:10` | [#411192](https://github.com/microsoft/winget-pkgs/pull/411192) | `Lunasans.MovieShelf` | 1 | 0 | 0 | 1 | 0 | 0 | **Discrepancy** |
| 94 | `2026-08-02 09:56:57` | [#411190](https://github.com/microsoft/winget-pkgs/pull/411190) | `DBMobile.Resonance` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 95 | `2026-08-02 09:56:45` | [#411188](https://github.com/microsoft/winget-pkgs/pull/411188) | `RedEyeNetworks.LogivoreForVeeam` | 4 | 4 | 0 | 0 | 0 | 0 | **Match** |
| 96 | `2026-08-02 09:56:33` | [#411189](https://github.com/microsoft/winget-pkgs/pull/411189) | `RedEyeNetworks.Logivore` | 4 | 4 | 0 | 0 | 0 | 0 | **Match** |
| 97 | `2026-08-02 09:30:45` | [#411185](https://github.com/microsoft/winget-pkgs/pull/411185) | `Enter-tainer.typstyle` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 98 | `2026-08-02 09:07:54` | [#411187](https://github.com/microsoft/winget-pkgs/pull/411187) | `misxzaiz.Polaris` | 1 | 1 | 0 | 0 | 0 | 0 | **Match** |
| 99 | `2026-08-02 09:07:41` | [#411186](https://github.com/microsoft/winget-pkgs/pull/411186) | `lbjlaq.AntigravityTools` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |
| 100 | `2026-08-02 09:07:28` | [#411182](https://github.com/microsoft/winget-pkgs/pull/411182) | `gg.ai.ggcode-desktop` | 2 | 2 | 0 | 0 | 0 | 0 | **Match** |

## Current discrepancies

| PR | Package / artifact | Manifest | Analyzer | SHA-256 | Diagnostics |
|---:|---|---|---|---|---|
| [#411304](https://github.com/microsoft/winget-pkgs/pull/411304) | `justhil.piDesktop` 0.5.6 / pi.Desktop-Setup-0.5.6-x64.exe | `nullsoft/x64` | `exe/x64` | match | `SFX001`, `DEP002` |
| [#411304](https://github.com/microsoft/winget-pkgs/pull/411304) | `justhil.piDesktop` 0.5.6 / pi.Desktop-Setup-0.5.6-x64.exe | `nullsoft/x64` | `exe/x64` | match | `SFX001`, `DEP002` |
| [#411282](https://github.com/microsoft/winget-pkgs/pull/411282) | `justhil.piDesktop` 0.5.5 / pi.Desktop-Setup-0.5.5-x64.exe | `nullsoft/x64` | `exe/x64` | match | `SFX001`, `DEP002` |
| [#411282](https://github.com/microsoft/winget-pkgs/pull/411282) | `justhil.piDesktop` 0.5.5 / pi.Desktop-Setup-0.5.5-x64.exe | `nullsoft/x64` | `exe/x64` | match | `SFX001`, `DEP002` |
| [#409967](https://github.com/microsoft/winget-pkgs/pull/409967) | `RoxyBrowser.RoxyBrowser` 4.0.0 / RoxyBrowser_x86_4.0.0.exe | `nullsoft/x86` | `exe/x86` | match | `DEP002` |
| [#409967](https://github.com/microsoft/winget-pkgs/pull/409967) | `RoxyBrowser.RoxyBrowser` 4.0.0 / RoxyBrowser_x86_4.0.0.exe | `nullsoft/x86` | `exe/x86` | match | `DEP002` |
| [#409967](https://github.com/microsoft/winget-pkgs/pull/409967) | `RoxyBrowser.RoxyBrowser` 4.0.0 / RoxyBrowser_x64_4.0.0.exe | `nullsoft/x64` | `exe/x64` | match | `SFX001`, `DEP002` |
| [#409967](https://github.com/microsoft/winget-pkgs/pull/409967) | `RoxyBrowser.RoxyBrowser` 4.0.0 / RoxyBrowser_x64_4.0.0.exe | `nullsoft/x64` | `exe/x64` | match | `SFX001`, `DEP002` |
| [#411233](https://github.com/microsoft/winget-pkgs/pull/411233) | `Lunasans.MovieShelf` 0.24.0 / MovieShelf-Setup-0.24.0.exe | `nullsoft/x64` | `exe/x64` | match | `SFX001`, `DEP002` |
| [#411192](https://github.com/microsoft/winget-pkgs/pull/411192) | `Lunasans.MovieShelf` 0.23.0 / MovieShelf-Setup-0.23.0.exe | `nullsoft/x64` | `exe/x64` | match | `SFX001`, `DEP002` |

## Compatible refinements

| PR | Package / artifact | Manifest | Analyzer |
|---:|---|---|---|
| [#411288](https://github.com/microsoft/winget-pkgs/pull/411288) | `OO-Software.SafeErase.Professional` / oosafeerase_21.3.26142_pro_eng_x64.msi | `msi/x64` | `wix/x64` |
| [#411288](https://github.com/microsoft/winget-pkgs/pull/411288) | `OO-Software.SafeErase.Professional` / oosafeerase_21.3.26142_pro_deu_x64.msi | `msi/x64` | `wix/x64` |
| [#411289](https://github.com/microsoft/winget-pkgs/pull/411289) | `OO-Software.SafeErase.Server` / oosafeerase_21.3.26142_server_eng_x64.msi | `msi/x64` | `wix/x64` |
| [#411289](https://github.com/microsoft/winget-pkgs/pull/411289) | `OO-Software.SafeErase.Server` / oosafeerase_21.3.26142_server_deu_x64.msi | `msi/x64` | `wix/x64` |
| [#411203](https://github.com/microsoft/winget-pkgs/pull/411203) | `willibrandon.scout` / scout-win-x64.msi | `msi/x64` | `wix/x64` |
| [#411203](https://github.com/microsoft/winget-pkgs/pull/411203) | `willibrandon.scout` / scout-win-arm64.msi | `msi/arm64` | `wix/arm64` |

## Current operational errors

### Installer analysis

| PR | Package / artifact | Error |
|---:|---|---|
| [#410798](https://github.com/microsoft/winget-pkgs/pull/410798) | `Kiwix.KiwixJS.Electron` / Kiwix-JS-Electron-Setup-3.8.8-E.exe | Installer analysis failed: The 7-Zip self-extractor contains more than 256 entries. |
| [#410798](https://github.com/microsoft/winget-pkgs/pull/410798) | `Kiwix.KiwixJS.Electron` / Kiwix-JS-Electron-Setup-3.8.8-E.exe | Installer analysis failed: The 7-Zip self-extractor contains more than 256 entries. |
| [#411271](https://github.com/microsoft/winget-pkgs/pull/411271) | `szTheory.exifcleaner` / ExifCleaner.Setup.4.0.1.exe | Installer analysis failed: The 7-Zip self-extractor contains more than 256 entries. |
| [#411245](https://github.com/microsoft/winget-pkgs/pull/411245) | `yetone.Alma` / alma-0.0.928-win-x64.exe | Installer analysis failed: The 7-Zip self-extractor contains more than 256 entries. |
| [#411245](https://github.com/microsoft/winget-pkgs/pull/411245) | `yetone.Alma` / alma-0.0.928-win-x64.exe | Installer analysis failed: The 7-Zip self-extractor contains more than 256 entries. |
| [#411231](https://github.com/microsoft/winget-pkgs/pull/411231) | `yetone.Alma` / alma-0.0.927-win-x64.exe | Installer analysis failed: The 7-Zip self-extractor contains more than 256 entries. |
| [#411231](https://github.com/microsoft/winget-pkgs/pull/411231) | `yetone.Alma` / alma-0.0.927-win-x64.exe | Installer analysis failed: The 7-Zip self-extractor contains more than 256 entries. |
| [#411211](https://github.com/microsoft/winget-pkgs/pull/411211) | `StablyAI.Orca` / orca-windows-setup.exe | Installer analysis failed: The 7-Zip self-extractor contains more than 256 entries. |
| [#411183](https://github.com/microsoft/winget-pkgs/pull/411183) | `Kingsoft.WPSOffice.x64` / WPS_Setup_x64_28022.exe | Analysis exceeded the 20-minute harness limit. |

## PRs without a testable installer declaration

- [#411344](https://github.com/microsoft/winget-pkgs/pull/411344) — no added or modified installer manifest.
- [#411320](https://github.com/microsoft/winget-pkgs/pull/411320) — no added or modified installer manifest.
- [#411300](https://github.com/microsoft/winget-pkgs/pull/411300) — removed installer manifest(s): `manifests/z/ZedIndustries/Zed/1.10.3/ZedIndustries.Zed.installer.yaml`.
- [#411286](https://github.com/microsoft/winget-pkgs/pull/411286) — removed installer manifest(s): `manifests/z/ZedIndustries/Zed/Preview/1.11.2-pre/ZedIndustries.Zed.Preview.installer.yaml`.

## Diagnostics observed

| Code | Unique successful analyses |
|---|---:|
| `ARCH001` | 2 |
| `DEP001` | 24 |
| `DEP002` | 50 |
| `DEP003` | 10 |
| `INNO002` | 2 |
| `INNO004` | 7 |
| `INNO007` | 6 |
| `INNO009` | 9 |
| `JAVA001` | 2 |
| `NSIS004` | 5 |
| `SFX001` | 5 |
| `SQUIRREL001` | 2 |
| `SQUIRREL002` | 2 |

## Reproduction

Each artifact can be rerun with the same current build:

```text
src/WinMatsch.Cli/bin/Debug/net10.0/winmatsch.exe analyze <InstallerUrl> --format json --interaction never
```

The per-PR table is the complete selected sample. All results above describe only this run and this analyzer build.
