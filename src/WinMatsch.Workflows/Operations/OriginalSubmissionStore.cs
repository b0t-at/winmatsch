using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;

namespace WinMatsch.Workflows.Operations;

public interface IOriginalSubmissionStore
{
    public PackageManifests? Load(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion);

    public void CaptureChangedVersions(
        string outputDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes);
}

public sealed record CommittedWorkflowPath(
    PlannedChangeKind Kind,
    string RepositoryPath);

/// <summary>
/// Persists the first tool-generated manifest set outside the repository so later updates can
/// distinguish moderator edits from the original generated submission.
/// </summary>
public sealed class FileOriginalSubmissionStore : IOriginalSubmissionStore
{
    private readonly string _stateDirectory;

    public FileOriginalSubmissionStore(string? stateDirectory = null)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory ?? DefaultStateDirectory());
    }

    public PackageManifests? Load(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion)
    {
        string versionDirectory = Path.Combine(
            RepositoryStateDirectory(outputDirectory),
            ManifestPaths.GetVersionDirectory(packageIdentifier, packageVersion)
                .Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(versionDirectory)
            ? PackageManifestIO.LoadDirectory(versionDirectory)
            : null;
    }

    public void CaptureChangedVersions(
        string outputDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(changes);
        string root = Path.GetFullPath(outputDirectory);
        foreach (string relativeDirectory in changes
                     .Select(static change => Path.GetDirectoryName(
                         change.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)))
                     .Where(static directory => !string.IsNullOrWhiteSpace(directory))
                     .Select(static directory => directory!)
                     .Distinct(StringComparer.Ordinal))
        {
            string source = Path.GetFullPath(Path.Combine(root, relativeDirectory));
            if (!source.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                continue;
            }

            string destination = Path.Combine(RepositoryStateDirectory(root), relativeDirectory);
            bool hasManifestFiles = Directory.Exists(source)
                && Directory.EnumerateFiles(source).Any(static path =>
                    Path.GetExtension(path) is { } extension
                    && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)));
            if (!hasManifestFiles)
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }

                continue;
            }

            if (Directory.Exists(destination))
            {
                continue;
            }

            CaptureDirectory(source, destination);
        }
    }

    private static void CaptureDirectory(string source, string destination)
    {
        string temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (string file in Directory.EnumerateFiles(source)
                         .Where(static path => Path.GetExtension(path) is { } extension
                             && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                                 || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))))
            {
                File.Copy(file, Path.Combine(temporary, Path.GetFileName(file)));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                Directory.Move(temporary, destination);
            }
            catch (IOException) when (Directory.Exists(destination))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
        catch
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            throw;
        }
    }

    private string RepositoryStateDirectory(string outputDirectory)
    {
        string normalized = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_stateDirectory, key);
    }

    private static string DefaultStateDirectory()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return Path.Combine(localData, "winmatsch", "original-submissions");
    }
}
