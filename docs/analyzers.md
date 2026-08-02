# Analyzer guide

`winmatsch analyze` (and every mutation workflow) inspects installer binaries
to extract manifest evidence. All analyzers are clean-room implementations
based on public format documentation; no third-party parser source was
copied. Parsing is defensive throughout — see [limits](#limits-and-safety).

## Support matrix

Detection is deterministic: files are dispatched by extension, then verified
by magic bytes; executables are probed by format-specific probes in a fixed
order (Advanced Installer → Java archive → 7-Zip SFX → Burn → Inno Setup →
NSIS → Squirrel) before falling back to keyword heuristics.

| Format | Detected as | Evidence extracted |
| --- | --- | --- |
| Windows Installer (`.msi`) | `Msi` | Product code, product name, product version, manufacturer, upgrade code, product language, ALLUSERS (scope), target architecture, authoring tool (WiX vs generic), Apps & Features entries. Read from the compound-file Property table and summary information. |
| MSIX / AppX (`.msix`, `.appx`) | `Msix` | Package identity (name, publisher, version), architecture from the app manifest. |
| MSIX / AppX bundle (`.msixbundle`, `.appxbundle`) | `MsixBundle` | Bundle identity and per-architecture packages. |
| ZIP archive (`.zip`) | `Zip` | Nested installer/payload candidates (`.msi`, `.msix`, `.appx`, `.exe`, `.msixbundle`, `.appxbundle`), verified by magic bytes; resolves single-type multi-architecture archives; portable payload detection. |
| WiX Burn bundle (`.exe`) | `Burn` | Bundle metadata and chained packages. |
| NSIS (`.exe`) | `Nullsoft` | NSIS structures plus PE version info. |
| Inno Setup (`.exe`) | `InnoSetup` | Inno 5.5.7–7.0.0.3 structures (through Inno Setup 7.0.2), including loader revisions 1/2, architecture expressions/lists, privileges, languages, encryption mode and PE payload evidence, plus PE version info. |
| Advanced Installer (`.exe`) | `AdvancedInstaller` | 7-Zip SFX-wrapped Advanced Installer payload metadata. |
| Java executable archive (`.exe`) | `PortableExe` | Detects PE/JAR polyglots and reports neutral when bundled native libraries cover multiple architectures. |
| 7-Zip SFX (`.exe`) | `GenericInstallerExe` | Bounded embedded PE inspection with installed-application architecture voting; excludes setup/uninstall helper executables. |
| Squirrel / Clowd.Squirrel (`.exe`) | `Squirrel` | Squirrel package metadata. |
| Generic installer (`.exe`) | `GenericInstallerExe` | PE version info (product name, company, product version, file version, copyright, original filename, description), architecture, elevation requirement. Chosen when installer keywords (`installer`, `setup`, 7z SFX markers) match but no specific format probe does. |
| Portable executable (`.exe`) | `PortableExe` | Same PE metadata; classified as portable when no installer signals are present. |

Every extracted value is attached to the manifest as *evidence* with a source
description; rule changes backed by installer analysis carry `high`
confidence, values derived from a raw download alone carry `medium`
confidence, and low-confidence situations surface as questions or human
escalation rather than silent guesses.

For generic/portable EXEs and a ZIP with exactly one analyzed nested payload,
version resolution considers a trustworthy PE `ProductVersion` first and then
the separately captured `FileVersion`. Placeholder, internal, all-zero, and
mixed product/build text is rejected rather than normalized into a version.

## Manual-analysis behavior

When analysis cannot classify an installer confidently — an unknown EXE
layout, a ZIP whose candidates fail magic-byte validation, an archive
exceeding limits — the workflow does not fabricate values. Depending on
context it asks an interactive question, reports a validation finding, or
requires an explicit override (`--url url`, `--url url|arch`,
`--url url|arch|scope`, or `--url url|arch|scope|displayVersion`), or an
[override pack](rules.md#override-packs) with `forcedArchitectures` /
`assetMappings`).

## Limits and safety

Untrusted binaries are parsed with hard bounds (all enforced centrally):

| Limit | Value |
| --- | --- |
| Max archive entries | 10,000 |
| Max archive path depth / length | 64 segments / 2,048 chars |
| Max bytes per archive entry | 256 MB |
| Max total expanded archive bytes | 1 GB |
| Max nested archive depth | 4 |
| Max PE sections | 96 |
| Max PE resource bytes | 16 MB |
| Max MSI stream bytes | 64 MB |
| Max NSIS header bytes | 64 MB |

Additional guards: declared entry sizes are verified against actual stream
lengths (zip-bomb defense with overflow detection), nested archive depth is
tracked per call chain, and known junk folders (`__MACOSX`, `resources`) are
skipped in ZIP scans.

## Known unsupported variants and non-goals

- **Executing installers.** winmatsch never runs an installer, in a sandbox
  or otherwise; only static analysis is performed.
- **Encrypted or password-protected archives** are not extracted.
- **ZIP members that look installable but fail magic-byte validation** are
  rejected rather than trusted by extension.
- **Archive formats other than ZIP** (7z, RAR, cab-as-container) are not
  scanned for nested installers.
- **Fully encrypted Inno headers** are identified as Inno but require manual
  analysis (`INNO013`) because header metadata cannot be decrypted without the
  setup password. File-only encryption does not prevent header inspection.
- **Unknown self-extracting archive formats** fall back to generic EXE
  classification; recognized 7-Zip SFX and Advanced Installer wrappers receive
  bounded payload inspection.
- **Signature/certificate validation** is out of scope; hash integrity is
  handled by the download pipeline.
