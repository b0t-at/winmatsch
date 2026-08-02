using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Nsis;

/// <summary>
/// Detects NSIS (Nullsoft Scriptable Install System) installers: executables whose PE overlay
/// carries the <c>0xDEADBEEF</c> + "NullsoftInst" first header (<see cref="NsisFirstHeader"/>).
/// The installer header is decompressed (<see cref="NsisCompression"/>), its instructions are
/// scanned for the registry writes NSIS scripts use to register their Apps &amp; Features
/// entry — <c>WriteRegStr ... "...\CurrentVersion\Uninstall\..." "DisplayName|DisplayVersion|
/// Publisher" ...</c> (<c>EW_WRITEREG</c>, opcode 51 in NSIS 3 release builds) — and the
/// header's default install directory decides the scope: <c>$PROGRAMFILES</c>-family folders
/// mean a per-machine install, the user-profile folders a per-user one. NSIS stubs are always
/// x86; the architecture is promoted to x64 when the script targets the 64-bit Program Files
/// (<c>$PROGRAMFILES64</c>/<c>$COMMONFILES64</c>) or switches the registry view to 64-bit
/// (<c>SetRegView 64</c>: <c>EW_SETFLAG</c>, opcode 13, on exec flag 12, <c>alter_reg_view</c>,
/// with value <c>KEY_WOW64_64KEY</c>). ARM64 is not detectable: NSIS has no ARM64 stub and no
/// conventional marker, so ARM64-targeting installers are reported as their stub's machine.
/// The installer locale is the first language table's LCID — the script's default language.
/// </summary>
public sealed partial class NsisProbe : IExeFormatProbe
{
    private const int EwSetFlag = 13;
    private const int EwWriteReg = 51;
    private const int RegSz = 1;
    private const int AlterRegViewFlagIndex = 12;
    private const int KeyWow6464Key = 0x0100;

    private const string UninstallKeyFragment = @"\CurrentVersion\Uninstall\";

    /// <summary>
    /// Returns the installer's analysis, or null when the executable's overlay has no NSIS
    /// first header. An installer that is positively NSIS but truncated, corrupt, or using
    /// an unsupported compressor (NSIS-modified bzip2, BCJ-filtered LZMA) yields a degraded
    /// analysis carrying diagnostic NSIS003 instead of header-derived metadata.
    /// </summary>
    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            NsisFirstHeader? firstHeader = NsisFirstHeader.Find(stream);
            return firstHeader is null ? null : Analyze(peFile, stream, firstHeader);
        }
        catch (InvalidDataException exception)
        {
            return CreateDegradedAnalysis(peFile, exception);
        }
    }

    private static InstallerAnalysis Analyze(PeFile peFile, Stream stream, NsisFirstHeader firstHeader)
    {
        NsisHeader header = NsisHeader.Parse(NsisCompression.ReadHeaderData(stream, firstHeader));
        var strings = new NsisStringReader(header);

        string? installDirectory = strings.Read(header.InstallDirectoryPtr);
        (Dictionary<string, string> arpValues, bool regView64) = ScanEntries(header, strings);
        Architecture architecture = GetArchitecture(
            peFile,
            installDirectory,
            regView64,
            DetectPayloadArchitectures(header),
            out AnalysisDiagnostic? architectureDiagnostic);

        string? displayName = StripUnresolvedVariables(arpValues.GetValueOrDefault("DisplayName"));
        string? displayVersion = StripUnresolvedVariables(arpValues.GetValueOrDefault("DisplayVersion"));
        string? publisher = StripUnresolvedVariables(arpValues.GetValueOrDefault("Publisher"));
        VersionInfo version = peFile.VersionInfo;

        var installer = new Installer
        {
            Architecture = architecture,
            InstallerType = InstallerType.Nullsoft,
            Scope = GetScope(installDirectory),
            ElevationRequirement = peFile.RequestedElevation,
            InstallerLocale = header.GetFirstLangTable() is { } langTable
                ? Lcid.ToLanguageTag(langTable.LanguageId)
                : null,
        };

        if (arpValues.Count > 0)
        {
            installer.AppsAndFeaturesEntries =
            [
                new AppsAndFeaturesEntry
                {
                    DisplayName = displayName,
                    Publisher = publisher,
                    DisplayVersion = displayVersion,
                },
            ];
        }

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.Nullsoft,
            Installers = [installer],
            ProductName = displayName ?? version.ProductName,
            Publisher = publisher ?? version.CompanyName,
            ProductVersion = displayVersion ?? version.ProductVersion,
            Copyright = version.LegalCopyright,
            Diagnostics = architectureDiagnostic is null ? [] : [architectureDiagnostic],
        };
    }

    private static InstallerAnalysis CreateDegradedAnalysis(PeFile peFile, InvalidDataException exception)
    {
        VersionInfo version = peFile.VersionInfo;
        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.Nullsoft,
            Installers =
            [
                new Installer
                {
                    // The always-x86 stub says nothing about the payload; only a non-x86 stub is evidence.
                    Architecture = peFile.Architecture == Architecture.X86 ? null : peFile.Architecture,
                    InstallerType = InstallerType.Nullsoft,
                    ElevationRequirement = peFile.RequestedElevation,
                },
            ],
            ProductName = version.ProductName,
            Publisher = version.CompanyName,
            ProductVersion = version.ProductVersion,
            Copyright = version.LegalCopyright,
            Diagnostics =
            [
                new AnalysisDiagnostic(
                    "NSIS003",
                    $"{exception.Message} Header metadata was not interpreted; verify the installer manually.",
                    RequiresManualAnalysis: true),
            ],
        };
    }

    /// <summary>
    /// Removes unresolved user-variable tokens ($0–$9, $R0–$R9, $__VARn__) that scripts
    /// interpolate into ARP values at run time (Tauri writes "DisplayName" as "...$1").
    /// </summary>
    private static string? StripUnresolvedVariables(string? value)
    {
        if (value is null || !value.Contains('$'))
        {
            return value;
        }

        string stripped = UnresolvedVariablePattern().Replace(value, string.Empty);
        stripped = WhitespaceRunPattern().Replace(stripped, " ").Trim();
        return stripped.Length == 0 ? null : stripped;
    }

    [GeneratedRegex(@"\$(?:R[0-9](?![0-9])|[0-9](?![0-9])|__VAR[0-9]+__)")]
    private static partial Regex UnresolvedVariablePattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRunPattern();

    /// <summary>
    /// Scans the instructions for REG_SZ writes to an uninstall key, collecting the values
    /// WinGet matches ARP entries on (a later write wins, like execution would), and for
    /// <c>SetRegView 64</c>. Registry root and view are ignored for harvesting: HKLM, HKCU
    /// and SHCTX uninstall keys all feed Apps &amp; Features.
    /// </summary>
    private static (Dictionary<string, string> ArpValues, bool RegView64) ScanEntries(
        NsisHeader header,
        NsisStringReader strings)
    {
        var arpValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool regView64 = false;
        for (int i = 0; i < header.EntryCount; i++)
        {
            NsisEntry entry = header.GetEntry(i);
            switch (entry.Which)
            {
                case EwSetFlag:
                    // parm0 = exec-flag index, parm1 = the value as a string reference.
                    if (entry.Parm0 == AlterRegViewFlagIndex
                        && int.TryParse(strings.Read(entry.Parm1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int view)
                        && view == KeyWow6464Key)
                    {
                        regView64 = true;
                    }

                    break;

                case EwWriteReg:
                    // parm0 = root key, parm1 = key, parm2 = value name, parm3 = value,
                    // parm4 = data type (REG_SZ for WriteRegStr and WriteRegExpandStr).
                    if (entry.Parm4 != RegSz)
                    {
                        break;
                    }

                    string? key = strings.Read(entry.Parm1);
                    if (key is null || !key.Contains(UninstallKeyFragment, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    string? valueName = strings.Read(entry.Parm2);
                    if (valueName is "DisplayName" or "DisplayVersion" or "Publisher"
                        && strings.Read(entry.Parm3) is { } value)
                    {
                        arpValues[valueName] = value;
                    }

                    break;

                default:
                    break;
            }
        }

        return (arpValues, regView64);
    }

    /// <summary>
    /// The stub's machine (x86 for every NSIS release), promoted to x64 when the default
    /// install directory targets a 64-bit folder or the script switches to the 64-bit
    /// registry view.
    /// </summary>
    private static Architecture GetArchitecture(
        PeFile peFile,
        string? installDirectory,
        bool regView64,
        List<Architecture> payloadArchitectures,
        out AnalysisDiagnostic? diagnostic)
    {
        Architecture scriptArchitecture = regView64
            || (installDirectory is not null
                && (installDirectory.Contains("$PROGRAMFILES64", StringComparison.OrdinalIgnoreCase)
                    || installDirectory.Contains("$COMMONFILES64", StringComparison.OrdinalIgnoreCase)))
            ? Architecture.X64
            : peFile.Architecture;

        if (payloadArchitectures.Count == 0)
        {
            diagnostic = null;
            return scriptArchitecture;
        }

        if (payloadArchitectures.Count > 1)
        {
            diagnostic = new AnalysisDiagnostic(
                "NSIS001",
                $"The NSIS string table references payloads for {string.Join(", ", payloadArchitectures)}. "
                    + $"This appears to be a universal installer; architecture requires manual analysis.",
                RequiresManualAnalysis: true);
            return scriptArchitecture;
        }

        Architecture payloadArchitecture = payloadArchitectures[0];
        diagnostic = payloadArchitecture != scriptArchitecture && scriptArchitecture != Architecture.X86
            ? new AnalysisDiagnostic(
                "NSIS002",
                $"The NSIS script implies {scriptArchitecture}, but its Electron-style payload name implies "
                    + $"{payloadArchitecture}. The payload architecture was selected; verify the package manually.",
                RequiresManualAnalysis: true)
            : null;
        return payloadArchitecture;
    }

    /// <summary>
    /// electron-builder stores payload names such as app-64.7z, app-32.7z and app-arm64.7z
    /// in the NSIS string table. Those names describe the installed binaries, unlike the
    /// always-x86 NSIS stub.
    /// </summary>
    private static List<Architecture> DetectPayloadArchitectures(NsisHeader header)
    {
        ReadOnlySpan<byte> bytes = header.Strings;
        List<Architecture> architectures = [];
        AddIfPresent(
            architectures,
            Architecture.Arm64,
            bytes,
            "app-arm64.7z",
            "app-aarch64.7z",
            "win-arm64",
            "win-aarch64");
        AddIfPresent(
            architectures,
            Architecture.X64,
            bytes,
            "app-64.7z",
            "app-x64.7z",
            "app-amd64.7z",
            "win-x64",
            "win-amd64");
        AddIfPresent(
            architectures,
            Architecture.X86,
            bytes,
            "app-32.7z",
            "app-ia32.7z",
            "app-x86.7z",
            "win-ia32",
            "win-x86");
        return architectures;
    }

    private static void AddIfPresent(
        List<Architecture> architectures,
        Architecture architecture,
        ReadOnlySpan<byte> searchable,
        params string[] conventions)
    {
        foreach (string convention in conventions)
        {
            string upper = convention.ToUpperInvariant();
            if (searchable.IndexOf(Encoding.ASCII.GetBytes(convention)) >= 0
                || searchable.IndexOf(Encoding.ASCII.GetBytes(upper)) >= 0
                || searchable.IndexOf(Encoding.Unicode.GetBytes(convention)) >= 0
                || searchable.IndexOf(Encoding.Unicode.GetBytes(upper)) >= 0)
            {
                architectures.Add(architecture);
                return;
            }
        }
    }

    /// <summary>
    /// Machine scope for Program Files / Common Files defaults, user scope for user-profile
    /// folders; no claim otherwise ($INSTDIR set at runtime, drive-rooted paths, ...).
    /// </summary>
    private static Scope? GetScope(string? installDirectory)
    {
        if (installDirectory is null)
        {
            return null;
        }

        if (installDirectory.Contains("$PROGRAMFILES", StringComparison.OrdinalIgnoreCase)
            || installDirectory.Contains("$COMMONFILES", StringComparison.OrdinalIgnoreCase))
        {
            return Scope.Machine;
        }

        if (installDirectory.Contains("$LOCALAPPDATA", StringComparison.OrdinalIgnoreCase)
            || installDirectory.Contains("$APPDATA", StringComparison.OrdinalIgnoreCase)
            || installDirectory.Contains("$PROFILE", StringComparison.OrdinalIgnoreCase))
        {
            return Scope.User;
        }

        return null;
    }
}
