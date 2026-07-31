using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.GitHub;

namespace WinMatsch.Workflows.Diagnostics;

public interface IRepositoryDiagnosticService
{
    public Task<PackageVersionResult> GetPackageVersionAsync(
        RepositoryCoordinates repository,
        PackageIdentifier identifier,
        PackageVersion version,
        bool normalize,
        CancellationToken cancellationToken = default);

    public Task<PackageVersionsResult> ListVersionsAsync(
        RepositoryCoordinates repository,
        PackageIdentifier identifier,
        int skip,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class RepositoryDiagnosticService : IRepositoryDiagnosticService
{
    private readonly IGitHubRepositoryClient _client;

    public RepositoryDiagnosticService(IGitHubRepositoryClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<PackageVersionResult> GetPackageVersionAsync(
        RepositoryCoordinates repository,
        PackageIdentifier identifier,
        PackageVersion version,
        bool normalize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(version);

        BranchState branch = await _client
            .GetDefaultBranchAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        string versionDirectory = ManifestPaths.GetVersionDirectory(identifier, version);
        IReadOnlyList<RepositoryTreeEntry> entries = await GetDirectoryEntriesAsync(
                repository,
                branch.HeadSha,
                versionDirectory,
                cancellationToken)
            .ConfigureAwait(false);

        RepositoryTreeEntry[] yamlFiles = entries
            .Where(static entry => entry.Type == RepositoryTreeEntryType.Blob
                && entry.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        if (yamlFiles.Length == 0)
        {
            throw new DiagnosticNotFoundException(
                $"Package '{identifier.Value}' version '{version.Value}' contains no manifest files.");
        }

        var files = new List<RepositoryManifestFile>(yamlFiles.Length);
        foreach (RepositoryTreeEntry entry in yamlFiles)
        {
            string fullPath = $"{versionDirectory}/{entry.Path}";
            RepositoryContent content = await _client
                .GetContentAsync(repository, fullPath, branch.HeadSha, cancellationToken)
                .ConfigureAwait(false);
            string text = content.GetText();
            VerifyIdentity(text, fullPath, identifier, version);
            files.Add(new RepositoryManifestFile(
                fullPath,
                normalize ? NormalizeManifest(text, fullPath) : text));
        }

        return new PackageVersionResult(
            repository,
            branch.Name,
            identifier,
            version,
            normalize,
            files);
    }

    public async Task<PackageVersionsResult> ListVersionsAsync(
        RepositoryCoordinates repository,
        PackageIdentifier identifier,
        int skip,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        BranchState branch = await _client
            .GetDefaultBranchAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        string packageDirectory = ManifestPaths.GetPackageDirectory(identifier);
        IReadOnlyList<RepositoryTreeEntry> entries = await GetDirectoryEntriesAsync(
                repository,
                branch.HeadSha,
                packageDirectory,
                cancellationToken)
            .ConfigureAwait(false);

        var versions = new List<PackageVersion>();
        foreach (RepositoryTreeEntry entry in entries
                     .Where(static entry => entry.Type == RepositoryTreeEntryType.Tree)
                     .OrderBy(static entry => entry.Path, StringComparer.Ordinal))
        {
            if (!PackageVersion.TryCreate(entry.Path, out PackageVersion? version))
            {
                throw new InvalidDataException(
                    $"Repository path '{packageDirectory}/{entry.Path}' is not a valid package version.");
            }

            versions.Add(version!);
        }

        versions.Sort(static (left, right) => right.CompareTo(left));
        PackageVersion[] page = [.. versions.Skip(skip).Take(limit)];
        return new PackageVersionsResult(
            repository,
            branch.Name,
            identifier,
            skip,
            limit,
            versions.Count,
            page);
    }

    private async Task<IReadOnlyList<RepositoryTreeEntry>> GetDirectoryEntriesAsync(
        RepositoryCoordinates repository,
        string rootTreeish,
        string directory,
        CancellationToken cancellationToken)
    {
        string treeish = rootTreeish;
        foreach (string segment in directory.Split('/'))
        {
            IReadOnlyList<RepositoryTreeEntry> entries = await _client
                .GetTreeAsync(repository, treeish, recursive: false, cancellationToken)
                .ConfigureAwait(false);
            RepositoryTreeEntry? exact = entries.FirstOrDefault(entry =>
                entry.Type == RepositoryTreeEntryType.Tree
                && string.Equals(entry.Path, segment, StringComparison.Ordinal));
            if (exact is null)
            {
                RepositoryTreeEntry? differentCase = entries.FirstOrDefault(entry =>
                    entry.Type == RepositoryTreeEntryType.Tree
                    && string.Equals(entry.Path, segment, StringComparison.OrdinalIgnoreCase));
                string detail = differentCase is null
                    ? "does not exist"
                    : $"uses exact casing '{differentCase.Path}'";
                throw new DiagnosticNotFoundException(
                    $"Repository directory '{directory}' {detail}.");
            }

            treeish = exact.Sha;
        }

        return await _client
            .GetTreeAsync(repository, treeish, recursive: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void VerifyIdentity(
        string content,
        string path,
        PackageIdentifier identifier,
        PackageVersion version)
    {
        ManifestHeader header;
        try
        {
            header = ManifestYamlReader.ReadHeader(content);
        }
        catch (Exception exception)
            when (exception is FormatException or ArgumentException or YamlDotNet.Core.YamlException)
        {
            throw new InvalidDataException($"Remote manifest '{path}' has an invalid header.", exception);
        }

        if (!string.Equals(header.PackageIdentifier, identifier.Value, StringComparison.Ordinal)
            || !string.Equals(header.PackageVersion, version.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Remote manifest '{path}' does not exactly match package '{identifier.Value}' version '{version.Value}'.");
        }
    }

    private static string NormalizeManifest(string content, string path)
    {
        try
        {
            return ManifestYamlReader.TryDetectType(content) switch
            {
                ManifestType.Installer => ManifestYamlWriter.Serialize(
                    ManifestYamlReader.ReadInstaller(content)),
                ManifestType.DefaultLocale => ManifestYamlWriter.Serialize(
                    ManifestYamlReader.ReadDefaultLocale(content)),
                ManifestType.Locale => ManifestYamlWriter.Serialize(
                    ManifestYamlReader.ReadLocale(content)),
                ManifestType.Version => ManifestYamlWriter.Serialize(
                    ManifestYamlReader.ReadVersion(content)),
                _ => throw new InvalidDataException(
                    $"Remote manifest '{path}' has an unsupported ManifestType."),
            };
        }
        catch (Exception exception)
            when (exception is FormatException
                or ArgumentException
                or InvalidOperationException
                or YamlDotNet.Core.YamlException)
        {
            throw new InvalidDataException(
                $"Remote manifest '{path}' cannot be normalized: {exception.Message}",
                exception);
        }
    }
}
