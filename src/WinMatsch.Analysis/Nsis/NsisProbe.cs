using System.Globalization;
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
public sealed class NsisProbe : IExeFormatProbe
{
    private const int EwSetFlag = 13;
    private const int EwWriteReg = 51;
    private const int RegSz = 1;
    private const int AlterRegViewFlagIndex = 12;
    private const int KeyWow6464Key = 0x0100;

    private const string UninstallKeyFragment = @"\CurrentVersion\Uninstall\";

    /// <summary>
    /// Returns the installer's analysis, or null when the executable's overlay has no NSIS
    /// first header.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file is positively an NSIS installer but is truncated, corrupt, or uses an
    /// unsupported compressor (NSIS-modified bzip2, BCJ-filtered LZMA).
    /// </exception>
    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        NsisFirstHeader? firstHeader = NsisFirstHeader.Find(stream);
        if (firstHeader is null)
        {
            return null;
        }

        NsisHeader header = NsisHeader.Parse(NsisCompression.ReadHeaderData(stream, firstHeader));
        var strings = new NsisStringReader(header);

        string? installDirectory = strings.Read(header.InstallDirectoryPtr);
        (Dictionary<string, string> arpValues, bool regView64) = ScanEntries(header, strings);

        string? displayName = arpValues.GetValueOrDefault("DisplayName");
        string? displayVersion = arpValues.GetValueOrDefault("DisplayVersion");
        string? publisher = arpValues.GetValueOrDefault("Publisher");
        VersionInfo version = peFile.VersionInfo;

        var installer = new Installer
        {
            Architecture = GetArchitecture(peFile, installDirectory, regView64),
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
        };
    }

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
    private static Architecture GetArchitecture(PeFile peFile, string? installDirectory, bool regView64)
    {
        bool x64 = regView64
            || (installDirectory is not null
                && (installDirectory.Contains("$PROGRAMFILES64", StringComparison.OrdinalIgnoreCase)
                    || installDirectory.Contains("$COMMONFILES64", StringComparison.OrdinalIgnoreCase)));
        return x64 && peFile.Architecture == Architecture.X86 ? Architecture.X64 : peFile.Architecture;
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
