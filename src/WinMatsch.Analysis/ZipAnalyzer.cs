using System.IO.Compression;
using WinMatsch.Core;

namespace WinMatsch.Analysis;

/// <summary>
/// Analyzes .zip archives: finds nested installer candidates, auto-selects a single candidate
/// (refining it by analyzing the nested binary), and reports multiple candidates via
/// <see cref="ZipContents"/> so the interactive flow can prompt for a choice.
/// </summary>
public sealed class ZipAnalyzer : IInstallerAnalyzer
{
    // Folders that hold junk (macOS metadata) or support files rather than the actual payload.
    private static readonly string[] _skippedFolderNames = ["__MACOSX", "resources"];

    public bool CanAnalyze(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    public InstallerAnalysis Analyze(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        List<(string Path, ZipArchiveEntry Entry)> candidates = [];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = NormalizeAndValidatePath(entry.FullName);
            if (path.EndsWith('/'))
            {
                continue; // Directory entry.
            }

            if (MapNestedInstallerType(path) is null || IsInSkippedFolder(path))
            {
                continue;
            }

            candidates.Add((path, entry));
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException(
                "The archive contains no installable payloads (no .msi, .msix, .appx, .exe, .msixbundle or .appxbundle entries).");
        }

        if (candidates.Count > 1)
        {
            // Ambiguous: report every candidate and let the caller (interactive prompt or
            // explicit configuration) choose the nested installer file.
            return new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.Zip,
                Installers = [new Installer { InstallerType = InstallerType.Zip }],
                Zip = new ZipContents([.. candidates.Select(static c => c.Path)]),
            };
        }

        return AnalyzeSingleCandidate(candidates[0].Path, candidates[0].Entry);
    }

    private static InstallerAnalysis AnalyzeSingleCandidate(string path, ZipArchiveEntry entry)
    {
        InstallerType nestedType = MapNestedInstallerType(path)!.Value;

        // Architecture stays null when nothing inside the archive reveals it: the manifest
        // writer requires it, but rules/CLI fill it later (URL tokens, user input).
        Architecture? architecture = null;
        string? productName = null;
        string? publisher = null;
        string? productVersion = null;
        string? copyright = null;

        // Refine .exe and .msi payloads by analyzing the nested binary itself. The capability
        // check keeps .msi inert until an MSI analyzer is registered in a later wave.
        string extension = Path.GetExtension(path);
        bool refinable = string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase);
        if (refinable && FileAnalyzer.CanAnalyze(path))
        {
            using var buffer = new MemoryStream();
            using (Stream entryStream = entry.Open())
            {
                entryStream.CopyTo(buffer);
            }

            buffer.Position = 0;
            InstallerAnalysis inner = FileAnalyzer.Analyze(buffer, Path.GetFileName(path));
            architecture = inner.Installers[0].Architecture;
            productName = inner.ProductName;
            publisher = inner.Publisher;
            productVersion = inner.ProductVersion;
            copyright = inner.Copyright;
            if (inner.Format == DetectedInstallerFormat.PortableExe)
            {
                nestedType = InstallerType.Portable;
            }
        }

        var installer = new Installer
        {
            Architecture = architecture,
            InstallerType = InstallerType.Zip,
            NestedInstallerType = nestedType,
            NestedInstallerFiles = [new NestedInstallerFile { RelativeFilePath = path }],
        };

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.Zip,
            Installers = [installer],
            ProductName = productName,
            Publisher = publisher,
            ProductVersion = productVersion,
            Copyright = copyright,
            Zip = new ZipContents([path]),
        };
    }

    /// <summary>Maps a candidate's extension to its nested installer type, or null for non-candidates.</summary>
    private static InstallerType? MapNestedInstallerType(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.Msi;
        }

        if (string.Equals(extension, ".msix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".msixbundle", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.Msix;
        }

        if (string.Equals(extension, ".appx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".appxbundle", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.Appx;
        }

        if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Refined to Portable when the nested binary turns out not to be an installer.
            return InstallerType.Exe;
        }

        return null;
    }

    /// <summary>
    /// Normalizes an entry path to forward slashes and rejects zip-slip attempts: absolute
    /// paths and <c>..</c> segments mark a hostile archive.
    /// </summary>
    private static string NormalizeAndValidatePath(string entryName)
    {
        string normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            throw new InvalidDataException(
                $"The archive entry '{entryName}' uses an absolute path; refusing to analyze a hostile archive.");
        }

        foreach (string segment in normalized.Split('/'))
        {
            if (segment == "..")
            {
                throw new InvalidDataException(
                    $"The archive entry '{entryName}' contains a '..' segment; refusing to analyze a hostile archive.");
            }
        }

        return normalized;
    }

    /// <summary>Whether any folder (not the file name itself) on the path is a skipped folder.</summary>
    private static bool IsInSkippedFolder(string path)
    {
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            foreach (string skipped in _skippedFolderNames)
            {
                if (string.Equals(segments[i], skipped, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
