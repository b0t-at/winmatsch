using System.Collections.Immutable;
using System.Text;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Diagnostics;

namespace WinMatsch.Workflows.Operations;

public sealed class RepositoryManifestSnapshotSource : IManifestSnapshotSource
{
    private readonly IRepositoryDiagnosticService _diagnostics;
    private readonly RepositoryCoordinates _repository;
    private readonly Dictionary<(PackageIdentifier, PackageVersion), PackageSnapshot> _snapshots =
        [];

    public RepositoryManifestSnapshotSource(
        IRepositoryDiagnosticService diagnostics,
        RepositoryCoordinates repository)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<PackageSnapshot?> LoadAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken)
    {
        if (_snapshots.TryGetValue((packageIdentifier, packageVersion), out PackageSnapshot? cached))
        {
            return cached;
        }

        try
        {
            PackageVersionResult result = await _diagnostics.GetPackageVersionAsync(
                _repository,
                packageIdentifier,
                packageVersion,
                normalize: false,
                cancellationToken).ConfigureAwait(false);
            PackageSnapshot snapshot = await CreateSnapshotAsync(result, cancellationToken)
                .ConfigureAwait(false);
            _snapshots[(packageIdentifier, packageVersion)] = snapshot;
            return snapshot;
        }
        catch (DiagnosticNotFoundException)
        {
            return null;
        }
    }

    public async Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        try
        {
            PackageVersionsResult versions = await _diagnostics.ListVersionsAsync(
                _repository,
                packageIdentifier,
                skip: 0,
                limit: 1,
                cancellationToken).ConfigureAwait(false);
            if (versions.Versions.Count == 0)
            {
                return [];
            }

            PackageSnapshot? latest = await LoadAsync(
                outputDirectory,
                packageIdentifier,
                versions.Versions[0],
                cancellationToken).ConfigureAwait(false);
            return latest is null ? [] : [latest];
        }
        catch (DiagnosticNotFoundException)
        {
            return [];
        }
    }

    private static async Task<PackageSnapshot> CreateSnapshotAsync(
        PackageVersionResult result,
        CancellationToken cancellationToken)
    {
        string temporaryDirectory = Directory.CreateTempSubdirectory(
            "winmatsch-repository-manifests-").FullName;
        try
        {
            var fileNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (RepositoryManifestFile file in result.Files)
            {
                string fileName = Path.GetFileName(file.Path);
                if (string.IsNullOrWhiteSpace(fileName) || !fileNames.Add(fileName))
                {
                    throw new InvalidDataException(
                        $"Remote manifest path '{file.Path}' does not have a unique file name.");
                }

                await File.WriteAllTextAsync(
                    Path.Combine(temporaryDirectory, fileName),
                    file.Content,
                    Encoding.UTF8,
                    cancellationToken).ConfigureAwait(false);
            }

            PackageManifests manifests = PackageManifestIO.LoadDirectory(temporaryDirectory);
            return new()
            {
                PackageIdentifier = result.Identifier,
                PackageVersion = result.Version,
                VersionDirectory = ManifestPaths.GetVersionDirectory(
                    result.Identifier,
                    result.Version),
                Manifests = manifests,
                Documents =
                [
                    .. result.Files.Select(static file => new RawManifestDocument(
                        file.Path,
                        Encoding.UTF8.GetBytes(file.Content))),
                ],
            };
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}

public sealed class FallbackManifestSnapshotSource(
    IManifestSnapshotSource primary,
    IManifestSnapshotSource fallback) : IManifestSnapshotSource
{
    public async Task<PackageSnapshot?> LoadAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken)
        => await primary.LoadAsync(
                outputDirectory,
                packageIdentifier,
                packageVersion,
                cancellationToken)
            .ConfigureAwait(false)
            ?? await fallback.LoadAsync(
                outputDirectory,
                packageIdentifier,
                packageVersion,
                cancellationToken).ConfigureAwait(false);

    public async Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        ImmutableArray<PackageSnapshot> local = await primary.ListVersionsAsync(
            outputDirectory,
            packageIdentifier,
            cancellationToken).ConfigureAwait(false);
        if (!local.IsEmpty)
        {
            return [.. local.OrderByDescending(static snapshot => snapshot.PackageVersion)];
        }

        ImmutableArray<PackageSnapshot> remote = await fallback.ListVersionsAsync(
            outputDirectory,
            packageIdentifier,
            cancellationToken).ConfigureAwait(false);
        return [.. remote.OrderByDescending(static snapshot => snapshot.PackageVersion)];
    }
}
