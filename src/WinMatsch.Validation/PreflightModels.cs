using WinMatsch.Core;
using WinMatsch.Downloads;

namespace WinMatsch.Validation;

/// <summary>A manifest file and its repository-relative path.</summary>
public sealed record ManifestDocument(string RepositoryPath, string Content);

/// <summary>A repository path changed by the proposed submission.</summary>
public sealed record RepositoryFileChange(string RepositoryPath, RepositoryChangeKind Kind);

public enum RepositoryChangeKind
{
    Added,
    Modified,
    Deleted,
}

/// <summary>ARP identity values already published for another package version.</summary>
public sealed record ExistingVersionSnapshot(
    string PackageVersion,
    IReadOnlyCollection<string> DisplayVersions);

/// <summary>An installer artifact downloaded while generating the proposed manifests.</summary>
public sealed record InstallerArtifact(string InstallerUrl, DownloadResult Download);

public enum NetworkValidationMode
{
    Online,
    Offline,
    Skip,
}

public sealed class PreflightOptions
{
    public WarningPolicy WarningPolicy { get; init; }

    public NetworkValidationMode NetworkMode { get; init; } = NetworkValidationMode.Online;
}

/// <summary>All immutable inputs required to validate one package version submission.</summary>
public sealed class PreflightRequest
{
    public required IReadOnlyList<ManifestDocument> Documents { get; init; }

    public required IReadOnlyList<RepositoryFileChange> Changes { get; init; }

    public IReadOnlyList<ExistingVersionSnapshot> ExistingVersions { get; init; } = [];

    public IReadOnlyList<InstallerArtifact> InstallerArtifacts { get; init; } = [];

    public PreflightOptions Options { get; init; } = new();

    /// <summary>Reads all YAML manifests below one version directory using repository-relative paths.</summary>
    public static PreflightRequest FromDirectory(
        string repositoryRoot,
        string versionDirectory,
        IReadOnlyList<RepositoryFileChange> changes,
        IReadOnlyList<ExistingVersionSnapshot>? existingVersions = null,
        IReadOnlyList<InstallerArtifact>? installerArtifacts = null,
        PreflightOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDirectory);
        ArgumentNullException.ThrowIfNull(changes);

        string fullRoot = Path.GetFullPath(repositoryRoot);
        string fullDirectory = Path.GetFullPath(versionDirectory);
        string relativeDirectory = Path.GetRelativePath(fullRoot, fullDirectory);
        if (relativeDirectory.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeDirectory))
        {
            throw new ArgumentException("The version directory must be inside the repository root.", nameof(versionDirectory));
        }

        ManifestDocument[] documents =
        [
            .. Directory.EnumerateFiles(fullDirectory)
                .Where(static path => Path.GetExtension(path) is { } extension
                    && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Select(path => new ManifestDocument(
                    NormalizeRepositoryPath(Path.GetRelativePath(fullRoot, path)),
                    File.ReadAllText(path))),
        ];

        return new PreflightRequest
        {
            Documents = documents,
            Changes = changes,
            ExistingVersions = existingVersions ?? [],
            InstallerArtifacts = installerArtifacts ?? [],
            Options = options ?? new PreflightOptions(),
        };
    }

    internal static string NormalizeRepositoryPath(string path) => path.Replace('\\', '/');
}

/// <summary>The only operation allowed to create a commit or pull request after validation.</summary>
public interface IPreflightBoundary
{
    public Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>Network operations used by preflight, abstracted for deterministic tests.</summary>
public interface IPreflightNetwork
{
    public Task<DownloadProbeResult> ProbeAsync(string url, CancellationToken cancellationToken);

    public Task<DownloadRevalidationResult> RevalidateAsync(
        DownloadResult previous,
        CancellationToken cancellationToken);
}

/// <summary>Adapts the Downloads project to the preflight network contract.</summary>
public sealed class InstallerDownloaderPreflightNetwork(InstallerDownloader downloader) : IPreflightNetwork
{
    private readonly InstallerDownloader _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));

    public Task<DownloadProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
        => _downloader.ProbeAsync(url, cancellationToken);

    public Task<DownloadRevalidationResult> RevalidateAsync(
        DownloadResult previous,
        CancellationToken cancellationToken)
        => _downloader.RevalidateAsync(previous, cancellationToken: cancellationToken);
}
