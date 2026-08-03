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
/// in its version strings and its file name.
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
        // Generic container signatures run last: specific installers such as NSIS may embed
        // a valid JAR or 7z payload that does not redefine their outer executable format.
        new JavaArchiveProbe(),
        new SevenZipSfxProbe(),
    ];

    // An EXE whose OriginalFilename or FileDescription contains one of these is treated as an
    // installer; everything else is portable. For installers shipped without any version
    // resource (e.g. Google's uncompressed Chrome installer) the actual file name is the only
    // signal, so it is consulted as a fallback. The 7-Zip SFX module names are self-extractor
    // stubs.
    private static readonly string[] _sevenZipSfxModuleNames =
        ["7z.sfx", "7zCon.sfx", "7zS.sfx", "7zSD.sfx", "7zS2.sfx", "7zS2con.sfx"];

    private static readonly string[] _installerKeywords =
        ["installer", "setup", .. _sevenZipSfxModuleNames];

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
                return CompleteProbedAnalysis(probed, version, fileName);
            }
        }

        bool isInstaller = ContainsInstallerKeyword(version.OriginalFilename)
            || ContainsInstallerKeyword(version.FileDescription)
            || (!HasVersionEvidence(version) && ContainsInstallerKeyword(GetFileName(fileName)));
        bool isSelfExtractorStub = IsSelfExtractorStub(version);

        InstallerAnalysis fallback = new()
        {
            Format = isInstaller ? DetectedInstallerFormat.GenericInstallerExe : DetectedInstallerFormat.PortableExe,
            Installers = [isInstaller ? CreateGenericInstaller(peFile, version) : CreatePortableInstaller(peFile)],
            ProductName = version.ProductName,
            Publisher = version.CompanyName,
            ProductVersion = version.ProductVersion,
            FileVersion = version.FileVersion,
            IsSelfExtractorStub = isSelfExtractorStub,
            Copyright = version.LegalCopyright,
        };
        return ApplyFilenameArchitectureHint(fallback, fileName, "ARCH001");
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

    private static InstallerAnalysis CompleteProbedAnalysis(
        InstallerAnalysis probed,
        VersionInfo version,
        string fileName)
    {
        IReadOnlyList<AnalysisDiagnostic> diagnostics = probed.Diagnostics;
        if (probed.Format == DetectedInstallerFormat.Nullsoft
            && UrlArchitectureDetector.Detect(GetFileName(fileName)) == Architecture.Arm64
            && probed.Installers.All(static installer => installer.Architecture == Architecture.X64))
        {
            foreach (Installer installer in probed.Installers)
            {
                installer.Architecture = Architecture.Arm64;
            }

            diagnostics =
            [
                .. diagnostics,
                new AnalysisDiagnostic(
                    "NSIS004",
                    "The asset filename identifies ARM64, overriding generic 64-bit NSIS script evidence. Verify the package manually.",
                    RequiresManualAnalysis: true),
            ];
        }

        InstallerAnalysis completed = new()
        {
            Format = probed.Format,
            Installers = probed.Installers,
            ProductName = probed.ProductName,
            Publisher = probed.Publisher,
            ProductVersion = probed.ProductVersion,
            FileVersion = probed.FileVersion ?? version.FileVersion,
            IsSelfExtractorStub = probed.IsSelfExtractorStub,
            Copyright = probed.Copyright,
            Zip = probed.Zip,
            Diagnostics = diagnostics,
        };
        return ApplyFilenameArchitectureHint(completed, fileName, "ARCH001");
    }

    private static InstallerAnalysis ApplyFilenameArchitectureHint(
        InstallerAnalysis analysis,
        string fileName,
        string diagnosticCode)
    {
        Architecture? hint = UrlArchitectureDetector.Detect(GetFileName(fileName));
        bool hasOnlyMissingOrStubEvidence = analysis.Installers.All(
            static installer => installer.Architecture is null or Architecture.X86);
        bool hasManualWrapperConflict = analysis.Diagnostics.Any(static diagnostic => diagnostic.RequiresManualAnalysis)
            && analysis.Format is DetectedInstallerFormat.InnoSetup
                or DetectedInstallerFormat.Nullsoft
                or DetectedInstallerFormat.GenericInstallerExe;
        if (hint is null
            || (!hasOnlyMissingOrStubEvidence && !hasManualWrapperConflict)
            || analysis.Installers.All(installer => installer.Architecture == hint))
        {
            return analysis;
        }

        foreach (Installer installer in analysis.Installers)
        {
            installer.Architecture = hint;
        }

        return new InstallerAnalysis
        {
            Format = analysis.Format,
            Installers = analysis.Installers,
            ProductName = analysis.ProductName,
            Publisher = analysis.Publisher,
            ProductVersion = analysis.ProductVersion,
            FileVersion = analysis.FileVersion,
            IsSelfExtractorStub = analysis.IsSelfExtractorStub,
            Copyright = analysis.Copyright,
            Zip = analysis.Zip,
            Diagnostics =
            [
                .. analysis.Diagnostics,
                new AnalysisDiagnostic(
                    diagnosticCode,
                    $"The asset filename identifies {hint}, overriding missing or x86 wrapper architecture evidence. Verify the package manually.",
                    RequiresManualAnalysis: true),
            ],
        };
    }

    private static bool HasVersionEvidence(VersionInfo version)
        => version.ProductName is not null
            || version.CompanyName is not null
            || version.LegalCopyright is not null
            || version.ProductVersion is not null
            || version.FileVersion is not null
            || version.OriginalFilename is not null
            || version.FileDescription is not null;

    private static string GetFileName(string path)
        => Path.GetFileName(path.Replace('\\', '/'));

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

    private static bool IsSelfExtractorStub(VersionInfo version)
        => IsSelfExtractorStubName(version.OriginalFilename)
            || IsSelfExtractorStubName(version.FileDescription);

    private static bool IsSelfExtractorStubName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && _sevenZipSfxModuleNames.Contains(value, StringComparer.OrdinalIgnoreCase);
}
