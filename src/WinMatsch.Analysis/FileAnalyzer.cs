using WinMatsch.Analysis.Msi;
using WinMatsch.Analysis.Msix;

namespace WinMatsch.Analysis;

/// <summary>
/// Entry point for installer analysis: dispatches a file to the first built-in analyzer that
/// handles its content. The file extension establishes capability, but magic bytes and required
/// archive manifests win during analysis so packaging changes are fully re-derived.
/// </summary>
public static class FileAnalyzer
{
    /// <summary>The built-in analyzers, probed in order; their extensions are disjoint.</summary>
    internal static IReadOnlyList<IInstallerAnalyzer> Analyzers { get; } =
    [
        new MsiAnalyzer(),
        new MsixAnalyzer(),
        new MsixBundleAnalyzer(),
        new ZipAnalyzer(),
        new ExeAnalyzer(),
    ];

    /// <summary>Whether any built-in analyzer handles the given file name (decided by extension).</summary>
    public static bool CanAnalyze(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        foreach (IInstallerAnalyzer analyzer in Analyzers)
        {
            if (analyzer.CanAnalyze(fileName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Analyzes the file content with the analyzer matching its outer packaging. The
    /// stream must be seekable and positioned at 0; it is left open.
    /// </summary>
    /// <exception cref="NotSupportedException">No analyzer is registered for the extension.</exception>
    public static InstallerAnalysis Analyze(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(fileName);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Installer analysis requires a seekable stream.", nameof(stream));
        }

        IInstallerAnalyzer? namedAnalyzer = null;
        foreach (IInstallerAnalyzer analyzer in Analyzers)
        {
            if (analyzer.CanAnalyze(fileName))
            {
                namedAnalyzer = analyzer;
                break;
            }
        }

        InstallerContentKind content = InstallerContentDetector.Detect(stream, fileName);
        IInstallerAnalyzer? contentAnalyzer = content switch
        {
            InstallerContentKind.PortableExecutable => FindAnalyzer<ExeAnalyzer>(),
            InstallerContentKind.CompoundFile => FindAnalyzer<MsiAnalyzer>(),
            InstallerContentKind.Zip => FindAnalyzer<ZipAnalyzer>(),
            InstallerContentKind.Msix => FindAnalyzer<MsixAnalyzer>(),
            InstallerContentKind.MsixBundle => FindAnalyzer<MsixBundleAnalyzer>(),
            _ => null,
        };

        if (contentAnalyzer is null)
        {
            if (namedAnalyzer is null)
            {
                throw new NotSupportedException(
                    $"No installer analyzer is registered for the file extension '{Path.GetExtension(fileName)}' (file '{fileName}'). Supported: .msi, .msix, .appx, .msixbundle, .appxbundle, .zip, .exe.");
            }

            throw new InvalidDataException(
                $"'{fileName}' does not contain recognized installer magic for its extension. Manual analysis is required.");
        }

        stream.Position = 0;
        return contentAnalyzer.Analyze(stream, fileName);
    }

    /// <summary>Opens the file and analyzes it; the file name decides the analyzer.</summary>
    public static InstallerAnalysis AnalyzeFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return Analyze(stream, Path.GetFileName(path));
    }

    private static T FindAnalyzer<T>()
        where T : class, IInstallerAnalyzer
        => Analyzers.OfType<T>().Single();
}
