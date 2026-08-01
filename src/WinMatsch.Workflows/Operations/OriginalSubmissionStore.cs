using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using YamlDotNet.Core;

namespace WinMatsch.Workflows.Operations;

public interface IOriginalSubmissionStore
{
    public PackageManifests? Load(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion);

    public void PrepareCapture(
        string outputDirectory,
        string captureId,
        string snapshotDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes);

    public bool IsCapturePrepared(
        string outputDirectory,
        string captureId,
        string snapshotDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes);

    public void CaptureChangedVersions(
        string outputDirectory,
        string captureId,
        string snapshotDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes);

    public void CompleteCapture(
        string outputDirectory,
        string captureId,
        string snapshotDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes);
}

public sealed record CommittedWorkflowPath(
    PlannedChangeKind Kind,
    string RepositoryPath,
    WorkflowChangeProvenance Provenance);

/// <summary>
/// Persists the first tool-generated manifest set outside the repository so later updates can
/// distinguish moderator edits from the original generated submission.
/// </summary>
public sealed class FileOriginalSubmissionStore : IOriginalSubmissionStore
{
    private const string MetadataFileName = ".winmatsch-provenance-v1";
    private const string MetadataHeader = "winmatsch-original-submission-v1";
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
        if (!Directory.Exists(versionDirectory)
            || !ValidateRecord(
                versionDirectory,
                RepositoryIdentity(outputDirectory),
                packageIdentifier,
                packageVersion))
        {
            return null;
        }

        try
        {
            PackageManifests manifests = PackageManifestIO.LoadDirectory(versionDirectory);
            return manifests.Version.PackageIdentifier == packageIdentifier
                && manifests.Version.PackageVersion == packageVersion
                ? manifests
                : null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or FormatException
                or ArgumentException
                or YamlException)
        {
            return null;
        }
    }

    public void CaptureChangedVersions(
        string outputDirectory,
        string captureId,
        string snapshotDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ValidateCaptureId(captureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(changes);
        if (!IsCapturePrepared(
                outputDirectory,
                captureId,
                snapshotDirectory,
                changes))
        {
            throw new InvalidDataException("Original-submission capture is not backed by trusted transaction state.");
        }

        string root = Path.GetFullPath(outputDirectory);
        string snapshotRoot = Path.GetFullPath(snapshotDirectory);
        foreach (IGrouping<string, CommittedWorkflowPath> versionChanges in changes
                     .GroupBy(
                         static change => Path.GetDirectoryName(
                             WorkflowPath.NormalizeRepositoryPath(change.RepositoryPath)
                                 .Replace('/', Path.DirectorySeparatorChar)) ?? "",
                         StringComparer.Ordinal)
                     .Where(static group => !string.IsNullOrWhiteSpace(group.Key)))
        {
            string relativeDirectory = versionChanges.Key;
            string source = Path.GetFullPath(Path.Combine(snapshotRoot, relativeDirectory));
            if (!source.StartsWith(
                    snapshotRoot + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                continue;
            }

            string destination = Path.Combine(RepositoryStateDirectory(root), relativeDirectory);
            string[] manifestFiles = Directory.Exists(source)
                ?
                [
                    .. Directory.EnumerateFiles(source)
                        .Where(IsManifest)
                        .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal),
                ]
                : [];
            if (manifestFiles.Length == 0)
            {
                if (versionChanges.All(static change =>
                        change.Provenance == WorkflowChangeProvenance.ToolGenerated)
                    && Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }

                continue;
            }

            if (!IsCompleteToolCreation(manifestFiles, versionChanges))
            {
                continue;
            }

            PackageManifests manifests = PackageManifestIO.LoadDirectory(source);
            PackageIdentifier packageIdentifier = manifests.Version.PackageIdentifier
                ?? throw new InvalidDataException("Provenance source is missing PackageIdentifier.");
            PackageVersion packageVersion = manifests.Version.PackageVersion
                ?? throw new InvalidDataException("Provenance source is missing PackageVersion.");
            string repositoryIdentity = RepositoryIdentity(root);
            if (Directory.Exists(destination))
            {
                if (ValidateRecord(
                    destination,
                    repositoryIdentity,
                    packageIdentifier,
                    packageVersion))
                {
                    continue;
                }

                Directory.Delete(destination, recursive: true);
            }

            CaptureDirectory(
                source,
                destination,
                repositoryIdentity,
                packageIdentifier,
                packageVersion);
        }
    }

    public void PrepareCapture(
        string outputDirectory,
        string captureId,
        string snapshotDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ValidateCaptureId(captureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(changes);
        string marker = CaptureMarkerPath(outputDirectory, captureId);
        string fingerprint = CaptureFingerprint(outputDirectory, snapshotDirectory, changes);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        if (File.Exists(marker))
        {
            if (!string.Equals(File.ReadAllText(marker), fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Original-submission recovery marker does not match the transaction.");
            }

            return;
        }

        string temporary = $"{marker}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporary, fingerprint, new UTF8Encoding(false));
            FlushFile(temporary);
            try
            {
                File.Move(temporary, marker);
            }
            catch (IOException) when (File.Exists(marker))
            {
                File.Delete(temporary);
                if (!string.Equals(File.ReadAllText(marker), fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Original-submission recovery marker does not match the transaction.");
                }
            }
        }
        catch (Exception primaryException)
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception cleanupException)
                {
                    throw new IOException(
                        "Provenance marker creation and temporary cleanup both failed.",
                        new AggregateException(primaryException, cleanupException));
                }
            }

            throw;
        }
    }

    public bool IsCapturePrepared(
        string outputDirectory,
        string captureId,
        string snapshotDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes)
    {
        ValidateCaptureId(captureId);
        string marker = CaptureMarkerPath(outputDirectory, captureId);
        return File.Exists(marker)
            && string.Equals(
                File.ReadAllText(marker),
                CaptureFingerprint(outputDirectory, snapshotDirectory, changes),
                StringComparison.Ordinal);
    }

    public void CompleteCapture(
        string outputDirectory,
        string captureId,
        string snapshotDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes)
    {
        ValidateCaptureId(captureId);
        string marker = CaptureMarkerPath(outputDirectory, captureId);
        if (!File.Exists(marker))
        {
            return;
        }

        if (!IsCapturePrepared(outputDirectory, captureId, snapshotDirectory, changes))
        {
            throw new InvalidDataException("Original-submission recovery marker does not match the transaction.");
        }

        File.Delete(marker);
        string directory = Path.GetDirectoryName(marker)!;
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static void CaptureDirectory(
        string source,
        string destination,
        string repositoryIdentity,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion)
    {
        string temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (string file in Directory.EnumerateFiles(source)
                         .Where(IsManifest)
                         .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                string copied = Path.Combine(temporary, Path.GetFileName(file));
                File.Copy(file, copied);
                FlushFile(copied);
            }

            WriteMetadata(
                temporary,
                repositoryIdentity,
                packageIdentifier,
                packageVersion);
            FlushFile(Path.Combine(temporary, MetadataFileName));
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
        catch (Exception primaryException)
        {
            if (Directory.Exists(temporary))
            {
                try
                {
                    Directory.Delete(temporary, recursive: true);
                }
                catch (Exception cleanupException)
                {
                    throw new IOException(
                        "Provenance capture failed and temporary cleanup also failed.",
                        new AggregateException(primaryException, cleanupException));
                }
            }

            throw;
        }
    }

    private static bool IsCompleteToolCreation(
        IReadOnlyCollection<string> manifestFiles,
        IEnumerable<CommittedWorkflowPath> changes)
    {
        var expectedFiles = manifestFiles
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        var addedFiles = changes
            .Where(static change => change.Kind == PlannedChangeKind.Add)
            .Select(change => Path.GetFileName(
                WorkflowPath.NormalizeRepositoryPath(change.RepositoryPath)))
            .ToHashSet(StringComparer.Ordinal);
        return changes.All(static change => change.Kind == PlannedChangeKind.Add
                && change.Provenance == WorkflowChangeProvenance.ToolGenerated)
            && expectedFiles.SetEquals(addedFiles);
    }

    private static void WriteMetadata(
            string directory,
            string repositoryIdentity,
            PackageIdentifier packageIdentifier,
            PackageVersion packageVersion)
    {
        string[] manifestFiles =
        [
            .. Directory.EnumerateFiles(directory)
                    .Where(IsManifest)
                    .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal),
            ];
        string content = string.Join(
            '\n',
            [
                MetadataHeader,
                    $"repository={Encode(repositoryIdentity)}",
                    $"package={Encode(packageIdentifier.Value)}",
                    $"version={Encode(packageVersion.Value)}",
                    .. manifestFiles.Select(static file =>
                        $"file={Encode(Path.GetFileName(file))}|{HashFile(file)}"),
                    "",
            ]);
        File.WriteAllText(
            Path.Combine(directory, MetadataFileName),
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static bool ValidateRecord(
            string directory,
            string repositoryIdentity,
            PackageIdentifier packageIdentifier,
            PackageVersion packageVersion)
    {
        string metadataPath = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return false;
        }

        string[] lines = File.ReadAllLines(metadataPath);
        if (lines.Length < 5
            || !string.Equals(lines[0], MetadataHeader, StringComparison.Ordinal)
            || !TryReadValue(lines[1], "repository=", out string? recordedRepository)
            || !TryReadValue(lines[2], "package=", out string? recordedPackage)
            || !TryReadValue(lines[3], "version=", out string? recordedVersion)
            || !string.Equals(recordedRepository, repositoryIdentity, StringComparison.Ordinal)
            || !string.Equals(recordedPackage, packageIdentifier.Value, StringComparison.Ordinal)
            || !string.Equals(recordedVersion, packageVersion.Value, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in lines.Skip(4).Where(static line => line.Length > 0))
        {
            int separator = line.IndexOf('|');
            if (!line.StartsWith("file=", StringComparison.Ordinal)
                || separator <= 5
                || !TryDecode(line[5..separator], out string? fileName)
                || fileName is null
                || fileName != Path.GetFileName(fileName)
                || !expected.TryAdd(fileName, line[(separator + 1)..]))
            {
                return false;
            }
        }

        string[] manifestFiles =
        [
            .. Directory.EnumerateFiles(directory)
                    .Where(IsManifest)
                    .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal),
            ];
        return manifestFiles.Length > 0
            && expected.Count == manifestFiles.Length
            && manifestFiles.All(file =>
                expected.TryGetValue(Path.GetFileName(file), out string? hash)
                && string.Equals(hash, HashFile(file), StringComparison.Ordinal));
    }

    private string CaptureMarkerPath(string outputDirectory, string captureId)
        => Path.Combine(
            RepositoryStateDirectory(outputDirectory),
            ".pending-captures",
            captureId);

    private static string CaptureFingerprint(
        string outputDirectory,
        string snapshotDirectory,
        IReadOnlyList<CommittedWorkflowPath> changes)
    {
        string snapshotRoot = Path.GetFullPath(snapshotDirectory);
        var content = new StringBuilder()
            .AppendLine(RepositoryIdentity(outputDirectory));
        foreach (CommittedWorkflowPath change in changes
                     .OrderBy(static change => change.RepositoryPath, StringComparer.Ordinal)
                     .ThenBy(static change => change.Kind))
        {
            content
                .Append(change.Kind)
                .Append('|')
                .Append(change.Provenance)
                .Append('|')
                .AppendLine(WorkflowPath.NormalizeRepositoryPath(change.RepositoryPath));
        }

        foreach (string file in Directory.Exists(snapshotRoot)
                     ? Directory.EnumerateFiles(snapshotRoot, "*", SearchOption.AllDirectories)
                         .Where(IsManifest)
                         .Order(StringComparer.Ordinal)
                     : Enumerable.Empty<string>())
        {
            content
                .Append(Path.GetRelativePath(snapshotRoot, file).Replace('\\', '/'))
                .Append('|')
                .AppendLine(HashFile(file));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
    }

    private static void ValidateCaptureId(string captureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        if (!string.Equals(captureId, Path.GetFileName(captureId), StringComparison.Ordinal)
            || captureId is "." or "..")
        {
            throw new ArgumentException("Capture identifiers must be single path segments.", nameof(captureId));
        }
    }

    private static bool TryReadValue(
            string line,
            string prefix,
            out string? value)
    {
        value = null;
        return line.StartsWith(prefix, StringComparison.Ordinal)
            && TryDecode(line[prefix.Length..], out value);
    }

    private static bool TryDecode(string value, out string? decoded)
    {
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return true;
        }
        catch (FormatException)
        {
            decoded = null;
            return false;
        }
    }

    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void FlushFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static bool IsManifest(string path)
        => Path.GetExtension(path) is { } extension
            && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase));

    private string RepositoryStateDirectory(string outputDirectory)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            RepositoryIdentity(outputDirectory))));
        return Path.Combine(_stateDirectory, key);
    }

    private static string RepositoryIdentity(string outputDirectory)
    {
        string root = Path.GetFullPath(outputDirectory);
        return Directory.Exists(root)
            ? DirectoryPin.GetIdentity(root)
            : OperatingSystem.IsWindows()
                ? root.ToUpperInvariant()
                : root;
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
