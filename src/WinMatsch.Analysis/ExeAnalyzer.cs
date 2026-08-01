using WinMatsch.Analysis.Advanced;
using WinMatsch.Analysis.Burn;
using WinMatsch.Analysis.Inno;
using WinMatsch.Analysis.Nsis;
using WinMatsch.Analysis.Pe;
using WinMatsch.Analysis.Squirrel;
using WinMatsch.Core;

namespace WinMatsch.Analysis;

/// <summary>
/// Analyzes .exe files. Format-specific probes run first; when no probe claims the file, a
/// generic fallback classifies it as an installer or a portable executable based on keywords
/// in its version strings.
/// </summary>
public sealed class ExeAnalyzer : IInstallerAnalyzer
{
    private static readonly IReadOnlyList<IExeFormatProbe> _probes =
    [
        new AdvancedInstallerProbe(),
        new BurnProbe(),
        new InnoProbe(),
        new NsisProbe(),
        new SquirrelProbe(),
    ];

    // An EXE whose OriginalFilename or FileDescription contains one of these is treated as an
    // installer; everything else is portable. "7zs.sfx"/"7zsd.sfx" are 7-Zip self-extractor stubs.
    private static readonly string[] _installerKeywords = ["installer", "setup", "7zs.sfx", "7zsd.sfx"];

    /// <summary>The explicit production probe order, exposed internally for registry tests.</summary>
    internal static IReadOnlyList<IExeFormatProbe> Probes => _probes;

    public bool CanAnalyze(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    public InstallerAnalysis Analyze(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var peFile = new PeFile(stream);
        VersionInfo version = peFile.VersionInfo;
        foreach (IExeFormatProbe probe in _probes)
        {
            stream.Position = 0;
            InstallerAnalysis? probed = probe.Probe(peFile, stream);
            if (probed is not null)
            {
                return new InstallerAnalysis
                {
                    Format = probed.Format,
                    Installers = probed.Installers,
                    ProductName = probed.ProductName,
                    Publisher = probed.Publisher,
                    ProductVersion = probed.ProductVersion,
                    FileVersion = probed.FileVersion ?? version.FileVersion,
                    Copyright = probed.Copyright,
                    Zip = probed.Zip,
                    Diagnostics = probed.Diagnostics,
                };
            }
        }

        bool isInstaller = ContainsInstallerKeyword(version.OriginalFilename)
            || ContainsInstallerKeyword(version.FileDescription);

        return new InstallerAnalysis
        {
            Format = isInstaller ? DetectedInstallerFormat.GenericInstallerExe : DetectedInstallerFormat.PortableExe,
            Installers = [isInstaller ? CreateGenericInstaller(peFile, version) : CreatePortableInstaller(peFile)],
            ProductName = version.ProductName,
            Publisher = version.CompanyName,
            ProductVersion = version.ProductVersion,
            FileVersion = version.FileVersion,
            Copyright = version.LegalCopyright,
        };
    }

    private static Installer CreateGenericInstaller(PeFile peFile, VersionInfo version)
    {
        var installer = new Installer
        {
            Architecture = peFile.Architecture,
            InstallerType = InstallerType.Exe,
            ElevationRequirement = peFile.RequestedElevation,
        };

        if (version.ProductName is not null || version.CompanyName is not null || version.ProductVersion is not null)
        {
            installer.AppsAndFeaturesEntries =
            [
                new AppsAndFeaturesEntry
                {
                    DisplayName = version.ProductName,
                    Publisher = version.CompanyName,
                    DisplayVersion = version.ProductVersion,
                },
            ];
        }

        return installer;
    }

    // Deliberately minimal: no command alias is derived here; rules decide aliases later.
    private static Installer CreatePortableInstaller(PeFile peFile) => new()
    {
        Architecture = peFile.Architecture,
        InstallerType = InstallerType.Portable,
    };

    private static bool ContainsInstallerKeyword(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (string keyword in _installerKeywords)
        {
            if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
