using System.Collections.Immutable;
using System.Text;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Diagnostics;

namespace WinMatsch.Workflows.Operations;

public sealed class RepositoryManifestSnapshotSource :
    IManifestSnapshotSource,
    IManifestSnapshotSourceDiagnosticSource
{
    private const int LatestVersionCandidateLimit = 100;
    private readonly IRepositoryDiagnosticService _diagnostics;
    private readonly RepositoryCoordinates _repository;
    private readonly Dictionary<(string Identifier, PackageVersion Version), string>
        _missingDiagnostics = [];
    private readonly Dictionary<(string Identifier, PackageVersion Version), PackageSnapshot> _snapshots =
        [];
    private readonly HashSet<(string Identifier, PackageVersion Version)> _missing = [];
    private readonly PackageVersion? _configuredSourceVersion;
    private readonly Dictionary<string, PackageVersion> _sourceVersions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableArray<PackageSnapshot>> _versions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _versionDiagnostics =
        new(StringComparer.Ordinal);

    public RepositoryManifestSnapshotSource(
        IRepositoryDiagnosticService diagnostics,
        RepositoryCoordinates repository,
        PackageVersion? sourceVersion = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configuredSourceVersion = sourceVersion;
    }

    public async Task<PackageSnapshot?> LoadAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken)
    {
        string identifier = packageIdentifier.Value;
        PackageVersion? sourceVersion = GetSourceVersion(identifier);
        if (sourceVersion is not null && !sourceVersion.Equals(packageVersion))
        {
            return null;
        }

        if (_snapshots.TryGetValue((identifier, packageVersion), out PackageSnapshot? cached))
        {
            return cached;
        }

        if (_missing.Contains((identifier, packageVersion)))
        {
            return null;
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
            _sourceVersions[identifier] = packageVersion;
            _snapshots[(identifier, packageVersion)] = snapshot;
            return snapshot;
        }
        catch (DiagnosticNotFoundException exception)
        {
            _missing.Add((identifier, packageVersion));
            _missingDiagnostics[(identifier, packageVersion)] = exception.Message;
            return null;
        }
    }

    public async Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        string identifier = packageIdentifier.Value;
        if (_versions.TryGetValue(identifier, out ImmutableArray<PackageSnapshot> cached))
        {
            return cached;
        }

        try
        {
            PackageVersion? sourceVersion = GetSourceVersion(identifier);
            if (sourceVersion is not null)
            {
                PackageSnapshot? source = await LoadAsync(
                    outputDirectory,
                    packageIdentifier,
                    sourceVersion,
                    cancellationToken).ConfigureAwait(false);
                ImmutableArray<PackageSnapshot> configured = source is null ? [] : [source];
                _versions[identifier] = configured;
                return configured;
            }

            PackageVersionsResult versions = await _diagnostics.ListVersionsAsync(
                _repository,
                packageIdentifier,
                skip: 0,
                limit: LatestVersionCandidateLimit,
                cancellationToken).ConfigureAwait(false);
            if (versions.Versions.Count == 0)
            {
                _versions[identifier] = [];
                return [];
            }

            foreach (PackageVersion candidate in versions.Versions)
            {
                PackageSnapshot? latest = await LoadAsync(
                    outputDirectory,
                    packageIdentifier,
                    candidate,
                    cancellationToken).ConfigureAwait(false);
                if (latest is null)
                {
                    continue;
                }

                ImmutableArray<PackageSnapshot> resolved = [latest];
                _versions[identifier] = resolved;
                return resolved;
            }

            string candidates = string.Join(
                ", ",
                versions.Versions.Select(static version => version.Value));
            _versionDiagnostics[identifier] =
                $"Package '{identifier}' has {versions.Total} version directories, but none of "
                + $"the {versions.Versions.Count} newest candidates checked contained a manifest "
                + $"set. Candidates checked: {candidates}.";
            _versions[identifier] = [];
            return [];
        }
        catch (DiagnosticNotFoundException)
        {
            _versions[identifier] = [];
            return [];
        }
    }

    string? IManifestSnapshotSourceDiagnosticSource.GetListVersionsDiagnostic(
        PackageIdentifier packageIdentifier)
        => _versionDiagnostics.GetValueOrDefault(packageIdentifier.Value);

    string? IManifestSnapshotSourceDiagnosticSource.GetLoadDiagnostic(
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion)
        => _missingDiagnostics.GetValueOrDefault((packageIdentifier.Value, packageVersion));

    private PackageVersion? GetSourceVersion(string packageIdentifier)
        => _sourceVersions.TryGetValue(packageIdentifier, out PackageVersion? sourceVersion)
            ? sourceVersion
            : _configuredSourceVersion;

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
                IsRemote = true,
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
    IManifestSnapshotSource fallback) :
    IManifestSnapshotSource,
    IManifestSnapshotSourceDiagnosticSource
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

    string? IManifestSnapshotSourceDiagnosticSource.GetListVersionsDiagnostic(
        PackageIdentifier packageIdentifier)
        => DiagnosticSource(fallback)?.GetListVersionsDiagnostic(packageIdentifier)
            ?? DiagnosticSource(primary)?.GetListVersionsDiagnostic(packageIdentifier);

    string? IManifestSnapshotSourceDiagnosticSource.GetLoadDiagnostic(
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion)
        => DiagnosticSource(fallback)?.GetLoadDiagnostic(packageIdentifier, packageVersion)
            ?? DiagnosticSource(primary)?.GetLoadDiagnostic(packageIdentifier, packageVersion);

    private static IManifestSnapshotSourceDiagnosticSource? DiagnosticSource(
        IManifestSnapshotSource source)
        => source as IManifestSnapshotSourceDiagnosticSource;
}
