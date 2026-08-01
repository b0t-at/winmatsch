# Analyzer guide

`winmatsch analyze` (and every mutation workflow) inspects installer binaries
to extract manifest evidence. All analyzers are clean-room implementations
based on public format documentation; no third-party parser source was
copied. Parsing is defensive throughout — see [limits](#limits-and-safety).

## Support matrix

Detection is deterministic: files are dispatched by extension, then verified
by magic bytes; executables are probed by format-specific probes in a fixed
order (Advanced Installer → Burn → Inno Setup → NSIS → Squirrel) before
falling back to keyword heuristics.

| Format | Detected as | Evidence extracted |
|---|---|---|
| Windows Installer (`.msi`) | `Msi` | Product code, product name, product version, manufacturer, upgrade code, product language, ALLUSERS (scope), target architecture, authoring tool (WiX vs generic), Apps & Features entries. Read from the compound-file Property table and summary information. |
| MSIX / AppX (`.msix`, `.appx`) | `Msix` | Package identity (name, publisher, version), architecture from the app manifest. |
| MSIX / AppX bundle (`.msixbundle`, `.appxbundle`) | `MsixBundle` | Bundle identity and per-architecture packages. |
| ZIP archive (`.zip`) | `Zip` | Nested installer/payload candidates (`.msi`, `.msix`, `.appx`, `.exe`, `.msixbundle`, `.appxbundle`), verified by magic bytes; resolves single-type multi-architecture archives; portable payload detection. |
| WiX Burn bundle (`.exe`) | `Burn` | Bundle metadata and chained packages. |
| NSIS (`.exe`) | `Nullsoft` | NSIS structures plus PE version info. |
| Inno Setup (`.exe`) | `InnoSetup` | Inno structures plus PE version info. |
| Advanced Installer (`.exe`) | `AdvancedInstaller` | 7-Zip SFX-wrapped Advanced Installer payload metadata. |
| Squirrel / Clowd.Squirrel (`.exe`) | `Squirrel` | Squirrel package metadata. |
| Generic installer (`.exe`) | `GenericInstallerExe` | PE version info (product name, company, product version, copyright, original filename, description), architecture, elevation requirement. Chosen when installer keywords (`installer`, `setup`, 7z SFX markers) match but no specific format probe does. |
| Portable executable (`.exe`) | `PortableExe` | Same PE metadata; classified as portable when no installer signals are present. |

Every extracted value is attached to the manifest as *evidence* with a source
description; rule changes backed by installer analysis carry `high`
confidence, values derived from a raw download alone carry `medium`
confidence, and low-confidence situations surface as questions or human
escalation rather than silent guesses.

## Manual-analysis behavior

When analysis cannot classify an installer confidently — an unknown EXE
layout, a ZIP whose candidates fail magic-byte validation, an archive
exceeding limits — the workflow does not fabricate values. Depending on
context it asks an interactive question, reports a validation finding, or
requires an explicit override (`--url url|arch|scope|displayVersion`, or an
[override pack](rules.md#override-packs) with `forcedArchitectures` /
`assetMappings`).

## Limits and safety

Untrusted binaries are parsed with hard bounds (all enforced centrally):

| Limit | Value |
|---|---|
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
- **Self-extracting archives** other than the recognized Advanced Installer
  7z-SFX layout fall back to generic EXE classification.
- **Signature/certificate validation** is out of scope; hash integrity is
  handled by the download pipeline.
