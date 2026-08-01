using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Versioning;

namespace WinMatsch.Workflows.Operations;

public sealed class GitHubWorkflowReleaseSource(
    IGitHubRepositoryClient client,
    RepositoryCoordinates repository,
    IRepositoryReleaseMetadataSource? repositoryMetadataSource = null) :
    IWorkflowReleaseSource,
    IWorkflowReleaseMetadataSource
{
    private const int MaximumTopics = 16;
    private const int MaximumReleaseNotesLength = 10_000;
    private readonly IGitHubRepositoryClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private RepositoryReleaseMetadata? _cachedMetadata;

    public async Task<ImmutableArray<DiscoveredAsset>> DiscoverAsync(
        PackageIdentifier packageIdentifier,
        ReleaseRequest request,
        CancellationToken cancellationToken)
    {
        _ = packageIdentifier ?? throw new ArgumentNullException(nameof(packageIdentifier));
        IReadOnlyList<GitHubRelease> releases = await _client.GetReleasesAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        IEnumerable<GitHubRelease> selected = string.IsNullOrWhiteSpace(request.Release)
            ? releases
            : releases.Where(release =>
                string.Equals(release.TagName, request.Release, StringComparison.Ordinal)
                || string.Equals(release.Name, request.Release, StringComparison.Ordinal));
        ImmutableArray<DiscoveredAsset> discovered = ReleaseAssetDiscovery.Discover(selected);
        if (request.InstallerUrls.IsEmpty)
        {
            return discovered;
        }

        HashSet<string> urls = request.InstallerUrls
            .Select(static uri => uri.AbsoluteUri)
            .ToHashSet(StringComparer.Ordinal);
        var direct = request.InstallerUrls
            .Where(uri => !discovered.Any(asset =>
                string.Equals(asset.DownloadUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal)))
            .OrderBy(static uri => uri.AbsoluteUri, StringComparer.Ordinal)
            .Select((uri, index) => new DiscoveredAsset
            {
                ReleaseId = 0,
                ReleaseTag = request.Release ?? "",
                ReleaseName = request.Release ?? "direct URL",
                ReleaseUri = request.ReleaseUrls.FirstOrDefault() ?? uri,
                IsPrerelease = false,
                AssetId = index,
                AssetName = Path.GetFileName(uri.LocalPath),
                DownloadUri = uri,
                DeclaredContentType = "application/octet-stream",
                DeclaredSize = 0,
                AssetCreatedAt = DateTimeOffset.UnixEpoch,
            });
        return
        [
            .. discovered
                .Where(asset => urls.Contains(asset.DownloadUri.AbsoluteUri))
                .Concat(direct)
                .OrderBy(static asset => asset.DownloadUri.AbsoluteUri, StringComparer.Ordinal),
        ];
    }

    public async Task<WorkflowReleaseMetadata> DiscoverMetadataAsync(
        PackageIdentifier packageIdentifier,
        ReleaseRequest request,
        ImmutableArray<DiscoveredAsset> assets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        GitHubRelease? release = null;
        long[] releaseIds = assets
            .Where(static asset => asset.ReleaseId > 0)
            .Select(static asset => asset.ReleaseId)
            .Distinct()
            .ToArray();
        if (releaseIds.Length == 1)
        {
            IReadOnlyList<GitHubRelease> releases = await _client.GetReleasesAsync(
                repository,
                cancellationToken).ConfigureAwait(false);
            release = releases.SingleOrDefault(candidate => candidate.Id == releaseIds[0]);
        }

        RepositoryReleaseMetadata repositoryMetadata = await GetRepositoryMetadataAsync(
            repositoryMetadataSource,
            cancellationToken).ConfigureAwait(false);
        var provenance = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        string? rawReleaseNotes = string.IsNullOrWhiteSpace(release?.Body)
            ? null
            : release.Body.Trim();
        bool releaseNotesTruncated = rawReleaseNotes?.Length > MaximumReleaseNotesLength;
        string? releaseNotes = TruncateReleaseNotes(rawReleaseNotes);
        if (releaseNotes is not null)
        {
            provenance[nameof(PackageLocaleMetadata.ReleaseNotes)] = releaseNotesTruncated
                ? $"github-release:{release!.Id}:body:truncated={MaximumReleaseNotesLength}"
                : $"github-release:{release!.Id}:body";
        }

        string? releaseNotesUrl = release?.WebUri.AbsoluteUri;
        if (releaseNotesUrl is not null)
        {
            provenance[nameof(PackageLocaleMetadata.ReleaseNotesUrl)] = $"github-release:{release!.Id}:html_url";
        }

        if (repositoryMetadata.License is not null)
        {
            provenance[nameof(PackageLocaleMetadata.License)] = $"{repositoryMetadata.Provenance}:license";
        }

        if (repositoryMetadata.LicenseUrl is not null)
        {
            provenance[nameof(PackageLocaleMetadata.LicenseUrl)] = $"{repositoryMetadata.Provenance}:license_url";
        }

        if (!repositoryMetadata.Topics.IsEmpty)
        {
            provenance[nameof(PackageLocaleMetadata.Tags)] = $"{repositoryMetadata.Provenance}:topics";
        }

        if (repositoryMetadata.PublisherUrl is not null)
        {
            provenance[nameof(PackageLocaleMetadata.PublisherUrl)] = $"{repositoryMetadata.Provenance}:publisher_url";
        }

        Uri? repositoryUrl = repositoryMetadata.RepositoryUrl
            ?? TryRepositoryUri(release?.WebUri);
        if (repositoryUrl is not null)
        {
            provenance[nameof(PackageLocaleMetadata.PackageUrl)] = repositoryMetadata.RepositoryUrl is not null
                ? $"{repositoryMetadata.Provenance}:repository_url"
                : $"github-release:{release!.Id}:repository_url";
        }

        return new(
            new PackageLocaleMetadata
            {
                PackageLocale = new LanguageTag("und"),
                PublisherUrl = repositoryMetadata.PublisherUrl?.AbsoluteUri,
                PackageUrl = repositoryUrl?.AbsoluteUri,
                License = repositoryMetadata.License,
                LicenseUrl = repositoryMetadata.LicenseUrl?.AbsoluteUri,
                Tags =
                [
                    .. repositoryMetadata.Topics
                        .Where(IsSafeTopic)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Take(MaximumTopics),
                ],
                ReleaseNotes = releaseNotes,
                ReleaseNotesUrl = releaseNotesUrl,
                Provenance = provenance.ToImmutable(),
            },
            repositoryMetadata.Availability,
            repositoryMetadata.Diagnostic);
    }

    private async Task<RepositoryReleaseMetadata> GetRepositoryMetadataAsync(
        IRepositoryReleaseMetadataSource? source,
        CancellationToken cancellationToken)
    {
        if (_cachedMetadata is not null)
        {
            return _cachedMetadata;
        }

        if (source is null)
        {
            return new()
            {
                Availability = RepositoryMetadataAvailability.Unavailable,
                Provenance = "repository-metadata-source",
                Diagnostic = "No repository metadata source was configured.",
            };
        }

        RepositoryReleaseMetadata loaded = await source.GetAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        return Interlocked.CompareExchange(ref _cachedMetadata, loaded, comparand: null) ?? loaded;
    }

    private static Uri? TryRepositoryUri(Uri? releaseUri)
    {
        if (releaseUri is null
            || !releaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] segments = releaseUri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length < 2
            ? null
            : new Uri($"https://github.com/{segments[0]}/{segments[1]}");
    }

    private static bool IsSafeTopic(string topic)
        => !string.IsNullOrWhiteSpace(topic)
            && topic.Length <= 40
            && topic.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string? TruncateReleaseNotes(string? value)
    {
        if (value is null || value.Length <= MaximumReleaseNotesLength)
        {
            return value;
        }

        int length = MaximumReleaseNotesLength;
        if (char.IsHighSurrogate(value[length - 1])
            && length < value.Length
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length].TrimEnd();
    }
}

/// <summary>Creates release assets from explicit installer URLs without network discovery.</summary>
public sealed class DirectWorkflowReleaseSource : IWorkflowReleaseSource
{
    public Task<ImmutableArray<DiscoveredAsset>> DiscoverAsync(
        PackageIdentifier packageIdentifier,
        ReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            request.InstallerUrls
                .OrderBy(static uri => uri.AbsoluteUri, StringComparer.Ordinal)
                .Select((uri, index) => new DiscoveredAsset
                {
                    ReleaseId = 0,
                    ReleaseTag = request.Release ?? "",
                    ReleaseName = request.Release ?? "direct URL",
                    ReleaseUri = request.ReleaseUrls.FirstOrDefault() ?? uri,
                    IsPrerelease = false,
                    AssetId = index,
                    AssetName = Path.GetFileName(uri.LocalPath),
                    DownloadUri = uri,
                    DeclaredContentType = "application/octet-stream",
                    DeclaredSize = 0,
                    AssetCreatedAt = DateTimeOffset.UnixEpoch,
                })
                .ToImmutableArray());
    }
}

public sealed class InstallerWorkflowArtifactProcessor(
    InstallerDownloader downloader,
    PayloadDependencyAnalyzer? dependencyAnalyzer = null,
    InstallerVersionTrustPolicy? versionTrustPolicy = null) : IWorkflowArtifactProcessor
{
    private readonly InstallerDownloader _downloader =
        downloader ?? throw new ArgumentNullException(nameof(downloader));
    private readonly PayloadDependencyAnalyzer _dependencyAnalyzer =
        dependencyAnalyzer ?? new PayloadDependencyAnalyzer();
    private readonly InstallerVersionTrustPolicy _versionTrustPolicy =
        versionTrustPolicy ?? new InstallerVersionTrustPolicy();

    public async Task<ArtifactSnapshot> AcquireAsync(
        DiscoveredAsset asset,
        string artifactDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        DownloadResult download = await _downloader.DownloadAsync(
            asset.DownloadUri.AbsoluteUri,
            artifactDirectory,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        InstallerAnalysis analysis = await Task.Run(
            () => FileAnalyzer.AnalyzeFile(download.FilePath),
            cancellationToken).ConfigureAwait(false);
        PayloadDependencyAnalysis? dependencies = null;
        if (Path.GetExtension(download.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(download.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            await using FileStream stream = File.OpenRead(download.FilePath);
            dependencies = _dependencyAnalyzer.AnalyzeWithCancellation(
                stream,
                download.FileName,
                cancellationToken);
        }

        AssetContentEvidence content = AssetContentEvidence.FromDownload(download);
        InstallerVersionTrustDecision versionTrust = InstallerVersionTrustEvaluator.Evaluate(
            analysis,
            _versionTrustPolicy);
        InstallerVersionTrustDecision fileVersionTrust =
            InstallerVersionTrustEvaluator.EvaluateFileVersion(
                analysis,
                _versionTrustPolicy);
        AssetAnalysisEvidence analysisEvidence = AssetAnalysisEvidence.FromAnalysis(
            analysis,
            content,
            dependencies,
            isProductVersionTrustworthy: versionTrust.IsTrustworthy
                && versionTrust.UsesProductVersion,
            productVersionEvidenceKind: versionTrust.Kind,
            productVersionConfidence: versionTrust.UsesProductVersion
                ? versionTrust.Confidence
                : EvidenceConfidence.Low,
            isFileVersionTrustworthy: fileVersionTrust.IsTrustworthy,
            fileVersionEvidenceKind: fileVersionTrust.Kind,
            fileVersionConfidence: fileVersionTrust.Confidence);
        string[] versionDiagnostics =
        [
            .. new[]
                {
                    versionTrust.Diagnostic,
                    !versionTrust.IsTrustworthy
                        || !versionTrust.UsesProductVersion
                        || !string.IsNullOrWhiteSpace(analysis.FileVersion)
                        ? fileVersionTrust.Diagnostic
                        : null,
                }
                .Where(static diagnostic => diagnostic is not null)
                .Select(static diagnostic => diagnostic!)
                .Distinct(StringComparer.Ordinal),
        ];
        if (versionDiagnostics.Length > 0)
        {
            analysisEvidence = analysisEvidence with
            {
                Diagnostics =
                [
                    .. analysisEvidence.Diagnostics,
                    .. versionDiagnostics,
                ],
            };
        }
        return new()
        {
            Asset = asset with { Content = content, Analysis = analysisEvidence },
            Download = download,
            Analysis = analysis,
            DependencyAnalysis = dependencies,
        };
    }
}

internal sealed class DurableInstallerPreflightNetwork :
    IPreflightNetwork,
    IWorkflowPreflightDiagnosticSource
{
    private const int MaximumStaleArtifactsPerPass = 16;
    private const int MaximumNoStoreLeasesToInspect = 128;
    private const string NoStoreLeaseMarker = ".no-store-lease-v1";
    private static readonly TimeSpan _artifactRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan _noStoreLeaseDuration = TimeSpan.FromMinutes(5);
    private readonly InstallerDownloader _downloader;
    private readonly string _stateDirectory;
    private readonly IWorkflowScratchCleanup _scratchCleanup;
    private readonly ConcurrentQueue<ValidationFinding> _diagnostics = new();

    public DurableInstallerPreflightNetwork(InstallerDownloader downloader)
        : this(downloader, DefaultStateDirectory(), BoundedWorkflowScratchCleanup.Instance)
    {
    }

    internal DurableInstallerPreflightNetwork(
        InstallerDownloader downloader,
        string stateDirectory,
        IWorkflowScratchCleanup scratchCleanup)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _scratchCleanup = scratchCleanup ?? throw new ArgumentNullException(nameof(scratchCleanup));
    }

    public Task<DownloadProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
        => _downloader.ProbeAsync(url, cancellationToken);

    public async Task<DownloadRevalidationResult> RevalidateAsync(
        DownloadResult previous,
        CancellationToken cancellationToken)
    {
        if (File.Exists(previous.FilePath))
        {
            return await _downloader.RevalidateAsync(
                previous,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        PruneExpiredNoStoreLeases();
        string scratchDirectory = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-preflight-revalidation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDirectory);
        DownloadRevalidationResult? result = null;
        Exception? primaryFailure = null;
        bool retainScratchForNoStore = false;
        try
        {
            DownloadResult current = await _downloader.DownloadFreshAsync(
                previous.InitialUrl,
                scratchDirectory,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            retainScratchForNoStore = !current.MayBeStored;
            if (retainScratchForNoStore)
            {
                WriteNoStoreLease(
                    scratchDirectory,
                    DateTimeOffset.UtcNow.Add(_noStoreLeaseDuration));
            }

            string durablePath = retainScratchForNoStore
                ? current.FilePath
                : PreserveArtifact(current);
            result = new()
            {
                Status = current.ContentIdentity == previous.ContentIdentity
                    ? DownloadRevalidationStatus.Unchanged
                    : DownloadRevalidationStatus.ContentChanged,
                Result = CopyDownload(current, durablePath),
            };
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        WorkflowScratchCleanupState cleanup =
            retainScratchForNoStore && primaryFailure is null
                ? _scratchCleanup.Schedule(scratchDirectory, _noStoreLeaseDuration)
                : _scratchCleanup.Cleanup(scratchDirectory);
        if (cleanup.Scheduled)
        {
            _diagnostics.Enqueue(new(
                "WF_PREFLIGHT_SCRATCH_CLEANUP_SCHEDULED",
                ValidationSeverity.Info,
                cleanup.Diagnostic ?? "Scratch cleanup was scheduled."));
        }

        if (primaryFailure is not null)
        {
            if (primaryFailure is not OperationCanceledException && cleanup.Scheduled)
            {
                throw new WorkflowPreflightRecoveryException(primaryFailure, cleanup);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        return result!;
    }

    public ImmutableArray<ValidationFinding> DrainDiagnostics()
    {
        var findings = ImmutableArray.CreateBuilder<ValidationFinding>();
        while (_diagnostics.TryDequeue(out ValidationFinding? finding))
        {
            findings.Add(finding);
        }

        return findings.ToImmutable();
    }

    private string PreserveArtifact(DownloadResult current)
    {
        Directory.CreateDirectory(_stateDirectory);
        PruneStaleArtifacts();
        string path = Path.Combine(
            _stateDirectory,
            $"{current.Sha256}-{current.SizeInBytes}.bin");
        if (File.Exists(path))
        {
            VerifyArtifact(path, current.ContentIdentity);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return path;
        }

        string temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.Copy(current.FilePath, temporary);
            FlushFile(temporary);
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temporary);
                VerifyArtifact(path, current.ContentIdentity);
            }

            return path;
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
                        "Durable preflight artifact creation and temporary cleanup both failed.",
                        new AggregateException(primaryException, cleanupException));
                }
            }

            throw;
        }
    }

    private static void WriteNoStoreLease(
        string scratchDirectory,
        DateTimeOffset expiresAt)
    {
        string marker = Path.Combine(scratchDirectory, NoStoreLeaseMarker);
        File.WriteAllText(
            marker,
            expiresAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            new UTF8Encoding(false));
        FlushFile(marker);
    }

    private static void PruneExpiredNoStoreLeases()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string directory in Directory
                     .EnumerateDirectories(
                         Path.GetTempPath(),
                         "winmatsch-preflight-revalidation-*",
                         SearchOption.TopDirectoryOnly)
                     .Take(MaximumNoStoreLeasesToInspect)
                     .Select(static directory => new
                     {
                         Directory = directory,
                         Marker = Path.Combine(directory, NoStoreLeaseMarker),
                     })
                     .Where(static candidate => File.Exists(candidate.Marker))
                     .Select(static candidate => new
                     {
                         candidate.Directory,
                         ExpiresAt = TryReadLeaseExpiration(candidate.Marker),
                     })
                     .Where(candidate => candidate.ExpiresAt is { } expiration && expiration <= now)
                     .OrderBy(static candidate => candidate.ExpiresAt)
                     .Take(MaximumStaleArtifactsPerPass)
                     .Select(static candidate => candidate.Directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static DateTimeOffset? TryReadLeaseExpiration(string marker)
    {
        try
        {
            return long.TryParse(
                File.ReadAllText(marker),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out long milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void PruneStaleArtifacts()
    {
        DateTime cutoff = DateTime.UtcNow - _artifactRetention;
        foreach (string path in Directory.EnumerateFiles(_stateDirectory)
                     .Where(static path =>
                         path.EndsWith(".bin", StringComparison.Ordinal)
                         || Path.GetFileName(path).Contains(".bin.tmp-", StringComparison.Ordinal))
                     .Where(path => File.GetLastWriteTimeUtc(path) < cutoff)
                     .OrderBy(File.GetLastWriteTimeUtc)
                     .Take(MaximumStaleArtifactsPerPass))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void VerifyArtifact(string path, DownloadContentIdentity expected)
    {
        var info = new FileInfo(path);
        if (info.Length != expected.SizeInBytes)
        {
            throw new InvalidDataException("The durable preflight artifact has an unexpected size.");
        }

        using FileStream stream = File.OpenRead(path);
        var actual = new DownloadContentIdentity(
            new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream))),
            info.Length);
        if (actual != expected)
        {
            throw new InvalidDataException("The durable preflight artifact has an unexpected SHA-256.");
        }
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

    private static DownloadResult CopyDownload(DownloadResult source, string filePath)
        => new()
        {
            FilePath = filePath,
            FileName = source.FileName,
            Sha256 = source.Sha256,
            SizeInBytes = source.SizeInBytes,
            LastModified = source.LastModified,
            ETag = source.ETag,
            ResponseDate = source.ResponseDate,
            FreshUntil = source.FreshUntil,
            RetrievedAt = source.RetrievedAt,
            InitialUrl = source.InitialUrl,
            FinalUrl = source.FinalUrl,
            ContentType = source.ContentType,
            IsFromCache = source.IsFromCache,
            MayBeStored = source.MayBeStored,
        };

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

        return Path.Combine(localData, "winmatsch", "preflight-artifacts");
    }
}

internal readonly record struct WorkflowScratchCleanupState(
    bool Scheduled,
    string? Diagnostic = null)
{
    public static WorkflowScratchCleanupState Completed { get; } = new(Scheduled: false);
}

internal interface IWorkflowScratchCleanup
{
    public WorkflowScratchCleanupState Cleanup(string directory);

    public WorkflowScratchCleanupState Schedule(string directory, TimeSpan retention);
}

internal sealed class BoundedWorkflowScratchCleanup : IWorkflowScratchCleanup
{
    private static readonly TimeSpan[] _retryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
    ];

    public static BoundedWorkflowScratchCleanup Instance { get; } = new();

    public WorkflowScratchCleanupState Cleanup(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return WorkflowScratchCleanupState.Completed;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            return WorkflowScratchCleanupState.Completed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _ = Task.Run(() => RetryAsync(directory));
            return new(
                Scheduled: true,
                $"Scratch cleanup was scheduled after {exception.GetType().Name}.");
        }
    }

    public WorkflowScratchCleanupState Schedule(string directory, TimeSpan retention)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(retention).ConfigureAwait(false);
            await RetryAsync(directory).ConfigureAwait(false);
        });
        return new(
            Scheduled: true,
            $"Scratch cleanup was scheduled after a {retention.TotalMinutes:0}-minute no-store lease.");
    }

    private static async Task RetryAsync(string directory)
    {
        foreach (TimeSpan delay in _retryDelays)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

internal sealed class WorkflowPreflightRecoveryException : IOException
{
    public WorkflowPreflightRecoveryException(
        Exception primaryException,
        WorkflowScratchCleanupState cleanup)
        : base(
            $"Immediate installer revalidation failed: {primaryException.Message}",
            new AggregateException(
                primaryException,
                new IOException(cleanup.Diagnostic ?? "Scratch cleanup was scheduled.")))
    {
        PrimaryException = primaryException;
        Cleanup = cleanup;
    }

    public Exception PrimaryException { get; }

    public WorkflowScratchCleanupState Cleanup { get; }
}

public sealed class LocalManifestSnapshotSource : IManifestSnapshotSource
{
    private readonly IOriginalSubmissionStore _originalSubmissions;

    public LocalManifestSnapshotSource(IOriginalSubmissionStore? originalSubmissions = null)
    {
        _originalSubmissions = originalSubmissions ?? new FileOriginalSubmissionStore();
    }

    public async Task<PackageSnapshot?> LoadAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(root))
        {
            return null;
        }

        using IDisposable operationLock =
            AtomicWorkflowFileTransaction.AcquirePackageLock(root, packageIdentifier.Value);
        await AtomicWorkflowFileTransaction.RecoverPendingUnderLockAsync(
            root,
            packageIdentifier.Value,
            _originalSubmissions,
            cancellationToken).ConfigureAwait(false);
        return LoadCore(root, packageIdentifier, packageVersion, cancellationToken);
    }

    public async Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(root))
        {
            return ImmutableArray<PackageSnapshot>.Empty;
        }

        using IDisposable operationLock =
            AtomicWorkflowFileTransaction.AcquirePackageLock(root, packageIdentifier.Value);
        await AtomicWorkflowFileTransaction.RecoverPendingUnderLockAsync(
            root,
            packageIdentifier.Value,
            _originalSubmissions,
            cancellationToken).ConfigureAwait(false);
        string packageDirectory = ManifestPaths.GetPackageDirectory(packageIdentifier);
        string? fullPackageDirectory = SecurePath.ResolveExactExistingDirectory(root, packageDirectory);
        if (fullPackageDirectory is null)
        {
            return ImmutableArray<PackageSnapshot>.Empty;
        }

        var snapshots = ImmutableArray.CreateBuilder<PackageSnapshot>();
        foreach (string versionDirectory in Directory.EnumerateDirectories(fullPackageDirectory)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PackageVersion.TryCreate(Path.GetFileName(versionDirectory), out PackageVersion? version))
            {
                continue;
            }

            PackageSnapshot? snapshot = LoadCore(root, packageIdentifier, version!, cancellationToken);
            if (snapshot is not null)
            {
                snapshots.Add((PackageSnapshot)snapshot);
            }
        }

        return snapshots.ToImmutable();
    }

    private PackageSnapshot? LoadCore(
        string root,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken)
    {
        string relativeDirectory = ManifestPaths.GetVersionDirectory(packageIdentifier, packageVersion);
        string? versionDirectory = SecurePath.ResolveExactExistingDirectory(root, relativeDirectory);
        if (versionDirectory is null)
        {
            return null;
        }

        SecurePath.RejectReparsePoints(root, versionDirectory);
        PackageManifests manifests = PackageManifestIO.LoadDirectory(versionDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<RawManifestDocument> documents =
        [
            .. Directory.EnumerateFiles(versionDirectory)
                .Where(static path => Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => new RawManifestDocument(
                    $"{relativeDirectory}/{Path.GetFileName(path)}",
                    File.ReadAllBytes(path))),
        ];
        return new PackageSnapshot
        {
            PackageIdentifier = packageIdentifier,
            PackageVersion = packageVersion,
            VersionDirectory = relativeDirectory,
            Manifests = manifests,
            OriginalBotSubmission = _originalSubmissions.Load(root, packageIdentifier, packageVersion),
            Documents = documents,
        };
    }
}

public sealed class AtomicWorkflowFileTransaction :
    IWorkflowFileTransaction,
    IWorkflowFileTransactionRecovery,
    IWorkflowCoordinatedRecovery
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IOriginalSubmissionStore? _originalSubmissions;
    private readonly IWorkflowTransactionFileSystem _fileSystem;

    public AtomicWorkflowFileTransaction(IOriginalSubmissionStore? originalSubmissions = null)
        : this(originalSubmissions, WorkflowTransactionFileSystem.Instance)
    {
    }

    internal AtomicWorkflowFileTransaction(
        IOriginalSubmissionStore? originalSubmissions,
        IWorkflowTransactionFileSystem fileSystem)
    {
        _originalSubmissions = originalSubmissions;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public async Task RecoverAsync(
        string outputDirectory,
        string operationLockKey,
        CancellationToken cancellationToken)
    {
        using IDisposable lease = await RecoverAndHoldAsync(
            outputDirectory,
            operationLockKey,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IDisposable> RecoverAndHoldAsync(
        string outputDirectory,
        string operationLockKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationLockKey);
        string root = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(root))
        {
            return EmptyRecoveryLease.Instance;
        }

        string rootIdentity = DirectoryPin.GetIdentity(root);
        string normalizedLockKey = $"{rootIdentity}\u001f{operationLockKey.ToUpperInvariant()}";
        SemaphoreSlim gate = _locks.GetOrAdd(normalizedLockKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                "Another local operation is already running for this package.");
        }

        RepositoryOperationLock? processLock = null;
        try
        {
            SecurePath.ValidateOutputRoot(root);
            processLock = RepositoryOperationLock.Acquire(root, operationLockKey);
            await RecoverPendingUnderLockAsync(
                root,
                operationLockKey,
                _originalSubmissions,
                cancellationToken).ConfigureAwait(false);
            return new RecoveryLease(processLock, gate);
        }
        catch
        {
            processLock?.Dispose();
            gate.Release();
            throw;
        }
    }

    public async Task ApplyAsync(
        string outputDirectory,
        string operationLockKey,
        ImmutableArray<WorkflowFileChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationLockKey);
        if (changes.IsEmpty)
        {
            return;
        }

        string root = Path.GetFullPath(outputDirectory);
        string rootIdentity = Directory.Exists(root)
            ? DirectoryPin.GetIdentity(root)
            : Path.GetFullPath(root);
        string normalizedLockKey = $"{rootIdentity}\u001f{operationLockKey.ToUpperInvariant()}";
        SemaphoreSlim gate = _locks.GetOrAdd(normalizedLockKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                "Another local operation is already running for this package.");
        }

        string token = Guid.NewGuid().ToString("N");
        string transactionPrefix =
            $".winmatsch-transaction-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationLockKey.ToUpperInvariant())))[..16]}";
        string transactionRoot = Path.Combine(root, $"{transactionPrefix}-{token}");
        var installed = new List<TransactionEntry>(changes.Length);
        var directoryPins = new List<IDisposable>();
        var pinnedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RepositoryOperationLock? processLock = null;
        bool cleanupAllowed = false;
        Exception? committedCleanupFailure = null;
        Exception? committedProvenanceFailure = null;
        Exception? primaryFailure = null;
        Exception? rollbackFailure = null;
        Exception? uncommittedCleanupFailure = null;
        try
        {
            SecurePath.ValidateOutputRoot(root);
            Directory.CreateDirectory(root);
            processLock = RepositoryOperationLock.Acquire(root, operationLockKey);
            await RecoverAbandonedTransactionsAsync(
                root,
                transactionPrefix,
                currentTransaction: "",
                _originalSubmissions,
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(transactionRoot);
            string stageRoot = Path.Combine(transactionRoot, "stage");
            string backupRoot = Path.Combine(transactionRoot, "backup");
            Directory.CreateDirectory(stageRoot);
            Directory.CreateDirectory(backupRoot);

            foreach (WorkflowFileChange change in changes.OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = SecurePath.Resolve(root, change.RepositoryPath, requireExistingLeaf: false);
                SecurePath.RejectReparsePoints(root, Path.GetDirectoryName(destination)!);
                string stage = SecurePath.Resolve(stageRoot, change.RepositoryPath, requireExistingLeaf: false);
                string backup = SecurePath.Resolve(backupRoot, change.RepositoryPath, requireExistingLeaf: false);
                if (change.Kind != PlannedChangeKind.Delete)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
                    await File.WriteAllBytesAsync(stage, change.Content.ToArray(), cancellationToken)
                        .ConfigureAwait(false);
                }

                installed.Add(new(change, destination, stage, backup));
            }

            foreach (TransactionEntry entry in installed)
            {
                PinExistingDirectoryChain(root, entry.Destination, directoryPins, pinnedDirectories);
                VerifyPrecondition(entry);
            }

            WriteJournal(transactionRoot, "prepared", installed);
            foreach (TransactionEntry entry in installed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SecurePath.RejectReparsePoints(root, Path.GetDirectoryName(entry.Destination)!);
                if (entry.HadDestination)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Backup)!);
                    _fileSystem.MoveFile(entry.Destination, entry.Backup);
                    entry.BackupCreated = true;
                    VerifyCapturedBackup(entry);
                }

                if (entry.Change.Kind != PlannedChangeKind.Delete)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
                    SecurePath.RejectReparsePoints(root, Path.GetDirectoryName(entry.Destination)!);
                    PinDirectoryChain(
                        root,
                        Path.GetDirectoryName(entry.Destination)!,
                        directoryPins,
                        pinnedDirectories);
                    _fileSystem.MoveFile(entry.Stage, entry.Destination);
                    entry.DestinationInstalled = true;
                    FlushFile(entry.Destination);
                }
            }

            DisposePins(directoryPins);
            DeleteEmptyManifestDirectories(root, installed);
            if (_originalSubmissions is null)
            {
                WriteJournal(transactionRoot, "committed", installed);
                cleanupAllowed = true;
            }
            else
            {
                string provenanceRoot = Path.Combine(transactionRoot, "provenance");
                StageProvenanceSnapshots(root, provenanceRoot, changes);
                string captureId = Path.GetFileName(transactionRoot);
                CommittedWorkflowPath[] committedPaths = ToCommittedPaths(changes);
                _originalSubmissions.PrepareCapture(
                    root,
                    captureId,
                    provenanceRoot,
                    committedPaths);
                WriteJournal(transactionRoot, "manifests-committed", installed);
                try
                {
                    await _originalSubmissions.CaptureChangedVersionsAsync(
                        root,
                        captureId,
                        provenanceRoot,
                        committedPaths,
                        cancellationToken).ConfigureAwait(false);
                    WriteJournal(transactionRoot, "committed", installed);
                    _originalSubmissions.CompleteCapture(
                        root,
                        captureId,
                        provenanceRoot,
                        committedPaths);
                    cleanupAllowed = true;
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or OperationCanceledException)
                {
                    committedProvenanceFailure = exception;
                }
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            try
            {
                RollBack(installed);
                cleanupAllowed = true;
            }
            catch (Exception recoveryException)
            {
                cleanupAllowed = false;
                rollbackFailure = recoveryException;
            }
        }
        finally
        {
            if (cleanupAllowed && Directory.Exists(transactionRoot))
            {
                try
                {
                    _fileSystem.DeleteDirectory(transactionRoot, recursive: true);
                }
                catch (Exception exception)
                {
                    RecordCleanupFailure(exception);
                }
            }

            try
            {
                DisposePins(directoryPins);
            }
            catch (Exception exception)
            {
                RecordCleanupFailure(exception);
            }

            try
            {
                processLock?.Dispose();
            }
            catch (Exception exception)
            {
                RecordCleanupFailure(exception);
            }

            try
            {
                gate.Release();
            }
            catch (Exception exception)
            {
                RecordCleanupFailure(exception);
            }
        }

        if (primaryFailure is not null)
        {
            if (rollbackFailure is not null || uncommittedCleanupFailure is not null)
            {
                throw new WorkflowRecoveryException(
                    "The local manifest transaction failed and recovery was incomplete.",
                    primaryFailure,
                    rollbackFailure,
                    uncommittedCleanupFailure,
                    Directory.Exists(transactionRoot));
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (committedCleanupFailure is not null && committedProvenanceFailure is not null)
        {
            throw new WorkflowCommittedProvenanceException(
                "The manifest transaction committed, but provenance capture and recovery-directory cleanup failed.",
                new AggregateException(committedProvenanceFailure, committedCleanupFailure));
        }

        if (committedProvenanceFailure is not null)
        {
            throw new WorkflowCommittedProvenanceException(
                "The manifest transaction committed, but its original-submission provenance could not be recorded.",
                committedProvenanceFailure);
        }

        if (committedCleanupFailure is not null)
        {
            throw new WorkflowCommittedCleanupException(
                "The manifest transaction committed, but its recovery directory could not be removed.",
                committedCleanupFailure);
        }

        void RecordCleanupFailure(Exception exception)
        {
            if (primaryFailure is not null)
            {
                uncommittedCleanupFailure = CombineFailures(uncommittedCleanupFailure, exception);
            }
            else
            {
                committedCleanupFailure = CombineFailures(committedCleanupFailure, exception);
            }
        }
    }

    private void RollBack(IReadOnlyList<TransactionEntry> entries)
    {
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            TransactionEntry entry = entries[index];
            if (entry.DestinationInstalled && File.Exists(entry.Destination))
            {
                _fileSystem.DeleteFile(entry.Destination);
            }

            if (entry.BackupCreated && File.Exists(entry.Backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
                _fileSystem.MoveFile(entry.Backup, entry.Destination);
            }
        }
    }

    private static Exception CombineFailures(Exception? current, Exception next)
        => current is null ? next : new AggregateException(current, next);

    private static void VerifyPrecondition(TransactionEntry entry)
    {
        bool exists = File.Exists(entry.Destination);
        entry.HadDestination = exists;
        if (entry.Change.ExpectedState == ExpectedFileState.Absent && exists)
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                $"Destination '{entry.Change.RepositoryPath}' was created after planning.");
        }

        if (entry.Change.ExpectedState != ExpectedFileState.Present)
        {
            return;
        }

        if (!exists)
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                $"Destination '{entry.Change.RepositoryPath}' was removed after planning.");
        }

    }

    private static void VerifyCapturedBackup(TransactionEntry entry)
    {
        if (entry.Change.ExpectedState != ExpectedFileState.Present)
        {
            return;
        }

        using FileStream stream = File.OpenRead(entry.Backup);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, entry.Change.ExpectedSha256, StringComparison.Ordinal))
        {
            throw new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                $"Destination '{entry.Change.RepositoryPath}' changed after planning.");
        }
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

    private static void DisposePins(List<IDisposable> pins)
    {
        foreach (IDisposable pin in pins)
        {
            pin.Dispose();
        }

        pins.Clear();
    }

    private static void PinExistingDirectoryChain(
        string root,
        string destination,
        ICollection<IDisposable> pins,
        ISet<string> pinnedDirectories)
    {
        string? current = Path.GetDirectoryName(destination);
        while (current is not null && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        current ??= root;
        string relative = Path.GetRelativePath(root, current);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Destination parent escapes the output root.");
        }

        PinDirectoryChain(root, current, pins, pinnedDirectories);
    }

    private static void PinDirectoryChain(
        string root,
        string path,
        ICollection<IDisposable> pins,
        ISet<string> pinnedDirectories)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Pinned directory escapes the output root.");
        }

        PinOne(fullRoot);
        string current = fullRoot;
        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                break;
            }

            PinOne(current);
        }

        void PinOne(string directory)
        {
            if (pinnedDirectories.Add(directory))
            {
                pins.Add(DirectoryPin.Acquire(directory));
            }
        }
    }

    internal static IDisposable AcquirePackageLock(string root, string operationLockKey)
        => RepositoryOperationLock.Acquire(root, operationLockKey);

    internal static Task RecoverPendingUnderLockAsync(
        string root,
        string operationLockKey,
        IOriginalSubmissionStore? originalSubmissions,
        CancellationToken cancellationToken)
    {
        string transactionPrefix =
            $".winmatsch-transaction-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationLockKey.ToUpperInvariant())))[..16]}";
        if (!Directory.EnumerateDirectories(root, $"{transactionPrefix}-*").Any())
        {
            return Task.CompletedTask;
        }

        return RecoverAbandonedTransactionsAsync(
            root,
            transactionPrefix,
            currentTransaction: "",
            originalSubmissions,
            cancellationToken);
    }

    private static void WriteJournal(
        string transactionRoot,
        string status,
        IReadOnlyList<TransactionEntry> entries)
    {
        string journalPath = Path.Combine(transactionRoot, "journal");
        string temporaryPath = $"{journalPath}.tmp";
        string content = string.Join(
            '\n',
            [
                status,
                .. entries.Select(static entry => string.Join(
                    '|',
                    entry.Change.Kind,
                    entry.HadDestination ? "1" : "0",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Change.RepositoryPath)),
                    entry.Change.Provenance)),
                "",
            ]);
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
        using (FileStream stream = new(
                   temporaryPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.Read,
                   bufferSize: 1,
                   FileOptions.WriteThrough))
        {
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    private static async Task RecoverAbandonedTransactionsAsync(
        string root,
        string transactionPrefix,
        string currentTransaction,
        IOriginalSubmissionStore? originalSubmissions,
        CancellationToken cancellationToken)
    {
        foreach (string transaction in Directory.EnumerateDirectories(root, $"{transactionPrefix}-*")
                     .Where(path => !string.Equals(path, currentTransaction, StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pins = new List<IDisposable>();
            var pinnedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PinDirectoryChain(root, transaction, pins, pinnedDirectories);
            try
            {
                string journalPath = Path.Combine(transaction, "journal");
                if (!File.Exists(journalPath))
                {
                    DisposePins(pins);
                    DeleteRecoveredTransaction(transaction);
                    continue;
                }

                string[] lines = File.ReadAllLines(journalPath);
                string status = lines.FirstOrDefault() ?? "";
                string captureId = Path.GetFileName(transaction);
                List<CommittedWorkflowPath> committedPaths = ParseCommittedPaths(lines, journalPath);
                bool legacyJournal = lines
                    .Skip(1)
                    .Where(static line => line.Length > 0)
                    .All(static line => line.Split('|').Length == 3);
                bool committed = string.Equals(status, "committed", StringComparison.Ordinal);
                if (string.Equals(status, "manifests-committed", StringComparison.Ordinal))
                {
                    if (legacyJournal)
                    {
                        WriteRecoveredJournalStatus(journalPath, lines, "committed");
                        committed = true;
                    }
                    else
                    {
                        if (originalSubmissions is null)
                        {
                            throw new InvalidDataException(
                                $"Transaction journal '{journalPath}' requires provenance recovery.");
                        }

                        if (!originalSubmissions.IsCapturePrepared(
                                root,
                                captureId,
                                Path.Combine(transaction, "provenance"),
                                committedPaths))
                        {
                            throw new InvalidDataException(
                                $"Transaction journal '{journalPath}' has no trusted provenance recovery marker.");
                        }

                        await originalSubmissions.CaptureChangedVersionsAsync(
                            root,
                            captureId,
                            Path.Combine(transaction, "provenance"),
                            committedPaths,
                            cancellationToken).ConfigureAwait(false);
                        WriteRecoveredJournalStatus(journalPath, lines, "committed");
                        committed = true;
                    }
                }

                if (!committed)
                {
                    foreach (string line in lines.Skip(1).Where(static line => line.Length > 0).Reverse())
                    {
                        string[] parts = line.Split('|');
                        if (parts.Length is not (3 or 4)
                            || !Enum.TryParse(parts[0], out PlannedChangeKind kind))
                        {
                            throw new InvalidDataException($"Invalid transaction journal '{journalPath}'.");
                        }

                        bool hadDestination = parts[1] == "1";
                        string repositoryPath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                        string destination = SecurePath.Resolve(root, repositoryPath, requireExistingLeaf: false);
                        string backup = SecurePath.Resolve(
                            Path.Combine(transaction, "backup"),
                            repositoryPath,
                            requireExistingLeaf: false);
                        PinExistingDirectoryChain(root, destination, pins, pinnedDirectories);
                        PinDirectoryChain(
                            root,
                            Path.GetDirectoryName(backup)!,
                            pins,
                            pinnedDirectories);
                        if (hadDestination && File.Exists(backup))
                        {
                            if (File.Exists(destination))
                            {
                                File.Delete(destination);
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            PinDirectoryChain(
                                root,
                                Path.GetDirectoryName(destination)!,
                                pins,
                                pinnedDirectories);
                            File.Move(backup, destination);
                        }
                        else if (!hadDestination && kind != PlannedChangeKind.Delete && File.Exists(destination))
                        {
                            File.Delete(destination);
                        }
                    }
                }

                if (originalSubmissions is not null)
                {
                    originalSubmissions.CompleteCapture(
                        root,
                        captureId,
                        Path.Combine(transaction, "provenance"),
                        committedPaths);
                }

                DisposePins(pins);
                DeleteRecoveredTransaction(transaction);
            }
            catch
            {
                DisposePins(pins);
                throw;
            }
        }
    }

    private static void WriteRecoveredJournalStatus(
        string journalPath,
        IReadOnlyList<string> lines,
        string status)
    {
        string temporary = $"{journalPath}.tmp";
        File.WriteAllLines(temporary, [status, .. lines.Skip(1)], new UTF8Encoding(false));
        using (FileStream stream = new(
                   temporary,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.Read,
                   bufferSize: 1,
                   FileOptions.WriteThrough))
        {
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, journalPath, overwrite: true);
    }

    private static void DeleteRecoveredTransaction(string transaction)
    {
        TimeSpan[] delays =
        [
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250),
        ];
        for (int attempt = 0; attempt < delays.Length; attempt++)
        {
            if (delays[attempt] > TimeSpan.Zero)
            {
                Thread.Sleep(delays[attempt]);
            }

            try
            {
                Directory.Delete(transaction, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < delays.Length - 1
                && exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static List<CommittedWorkflowPath> ParseCommittedPaths(
        IReadOnlyList<string> lines,
        string journalPath)
    {
        var paths = new List<CommittedWorkflowPath>();
        foreach (string line in lines.Skip(1).Where(static line => line.Length > 0))
        {
            string[] parts = line.Split('|');
            if (parts.Length is not (3 or 4)
                || !Enum.TryParse(parts[0], out PlannedChangeKind kind)
                || (parts.Length == 4
                    && !Enum.TryParse(parts[3], out WorkflowChangeProvenance _)))
            {
                throw new InvalidDataException($"Invalid transaction journal '{journalPath}'.");
            }

            paths.Add(new(
                kind,
                Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])),
                parts.Length == 4
                    ? Enum.Parse<WorkflowChangeProvenance>(parts[3])
                    : WorkflowChangeProvenance.Untrusted));
        }

        return paths;
    }

    private static CommittedWorkflowPath[] ToCommittedPaths(
        IEnumerable<WorkflowFileChange> changes)
        => changes
            .Select(static change => new CommittedWorkflowPath(
                change.Kind,
                change.RepositoryPath,
                change.Provenance))
            .ToArray();

    private static void StageProvenanceSnapshots(
        string root,
        string provenanceRoot,
        IEnumerable<WorkflowFileChange> changes)
    {
        foreach (string relativeDirectory in changes
                     .Select(static change => Path.GetDirectoryName(
                         change.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)))
                     .Where(static directory => !string.IsNullOrWhiteSpace(directory))
                     .Select(static directory => directory!)
                     .Distinct(StringComparer.Ordinal))
        {
            string source = SecurePath.Resolve(root, relativeDirectory, requireExistingLeaf: false);
            if (!Directory.Exists(source))
            {
                continue;
            }

            string[] manifests =
            [
                .. Directory.EnumerateFiles(source)
                    .Where(static path => Path.GetExtension(path) is { } extension
                        && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)))
                    .Order(StringComparer.Ordinal),
            ];
            if (manifests.Length == 0)
            {
                continue;
            }

            string destination = SecurePath.Resolve(
                provenanceRoot,
                relativeDirectory,
                requireExistingLeaf: false);
            Directory.CreateDirectory(destination);
            foreach (string manifest in manifests)
            {
                string staged = Path.Combine(destination, Path.GetFileName(manifest));
                File.Copy(manifest, staged);
                FlushFile(staged);
            }
        }
    }

    private static void DeleteEmptyManifestDirectories(string root, IEnumerable<TransactionEntry> entries)
    {
        foreach (string directory in entries
                     .Where(static entry => entry.Change.Kind == PlannedChangeKind.Delete)
                     .Select(static entry => Path.GetDirectoryName(entry.Destination)!)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(static path => path.Length))
        {
            string current = directory;
            while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(current)
                   && !Directory.EnumerateFileSystemEntries(current).Any())
            {
                Directory.Delete(current);
                current = Path.GetDirectoryName(current)!;
            }
        }
    }

    private sealed class TransactionEntry(
        WorkflowFileChange change,
        string destination,
        string stage,
        string backup)
    {
        public WorkflowFileChange Change { get; } = change;
        public string Destination { get; } = destination;
        public string Stage { get; } = stage;
        public string Backup { get; } = backup;
        public bool HadDestination { get; set; }
        public bool BackupCreated { get; set; }
        public bool DestinationInstalled { get; set; }
    }

    private sealed class RecoveryLease(
        IDisposable processLock,
        SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                processLock.Dispose();
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private sealed class EmptyRecoveryLease : IDisposable
    {
        public static EmptyRecoveryLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class RepositoryOperationLock : IDisposable
    {
        private const int MaximumStaleFilesPerAcquire = 16;
        private readonly FileStream _currentStream;
        private readonly FileStream _legacyStream;
        private readonly IDisposable _rootPin;
        private readonly IDisposable _currentLockDirectoryPin;
        private readonly IDisposable _legacyLockDirectoryPin;
        private readonly string _lockDirectory;
        private readonly string _lockPath;

        private RepositoryOperationLock(
            FileStream currentStream,
            FileStream legacyStream,
            IDisposable rootPin,
            IDisposable currentLockDirectoryPin,
            IDisposable legacyLockDirectoryPin,
            string lockDirectory,
            string lockPath)
        {
            _currentStream = currentStream;
            _legacyStream = legacyStream;
            _rootPin = rootPin;
            _currentLockDirectoryPin = currentLockDirectoryPin;
            _legacyLockDirectoryPin = legacyLockDirectoryPin;
            _lockDirectory = lockDirectory;
            _lockPath = lockPath;
        }

        public static RepositoryOperationLock Acquire(string root, string key)
        {
            SecurePath.RejectReparsePoints(root, root);
            IDisposable rootPin = DirectoryPin.Acquire(root);
            string lockDirectory = Path.Combine(
                ExternalLockRoot(),
                "winmatsch-operation-locks",
                DirectoryPin.GetIdentity(root));
            string legacyLockDirectory = Path.Combine(
                Path.GetTempPath(),
                "winmatsch-operation-locks",
                DirectoryPin.GetLegacyIdentity(root));
            string fileName =
                $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))}.lock";
            IDisposable? currentLockDirectoryPin = null;
            IDisposable? legacyLockDirectoryPin = null;
            FileStream? currentStream = null;
            FileStream? legacyStream = null;
            string lockPath = Path.Combine(lockDirectory, fileName);
            try
            {
                Directory.CreateDirectory(lockDirectory);
                Directory.CreateDirectory(legacyLockDirectory);
                currentLockDirectoryPin = DirectoryPin.Acquire(lockDirectory);
                legacyLockDirectoryPin = DirectoryPin.Acquire(legacyLockDirectory);
                using FileStream coordinator = AcquireCleanupCoordinator(lockDirectory);
                CleanupStaleFiles(lockDirectory, MaximumStaleFilesPerAcquire);
                currentStream = OpenLock(lockPath);
                legacyStream = OpenLock(Path.Combine(legacyLockDirectory, fileName));
                return new(
                    currentStream,
                    legacyStream,
                    rootPin,
                    currentLockDirectoryPin,
                    legacyLockDirectoryPin,
                    lockDirectory,
                    lockPath);
            }
            catch (IOException exception)
            {
                legacyStream?.Dispose();
                currentStream?.Dispose();
                TryDeleteReleasedLock(lockDirectory, lockPath);
                legacyLockDirectoryPin?.Dispose();
                currentLockDirectoryPin?.Dispose();
                rootPin.Dispose();
                throw new WorkflowOperationException(
                    WorkflowResultCode.Conflict,
                    "Another process is already running a local operation for this package.",
                    exception);
            }
            catch
            {
                legacyStream?.Dispose();
                currentStream?.Dispose();
                TryDeleteReleasedLock(lockDirectory, lockPath);
                legacyLockDirectoryPin?.Dispose();
                currentLockDirectoryPin?.Dispose();
                rootPin.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            var failures = new List<Exception>();
            DisposeOne(_legacyStream);
            DisposeOne(_currentStream);
            try
            {
                TryDeleteReleasedLock(_lockDirectory, _lockPath);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            DisposeOne(_legacyLockDirectoryPin);
            DisposeOne(_currentLockDirectoryPin);
            DisposeOne(_rootPin);
            if (failures.Count > 0)
            {
                throw new IOException(
                    "One or more local operation lock resources could not be released.",
                    new AggregateException(failures));
            }

            void DisposeOne(IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }

        private static FileStream OpenLock(string path)
            => new(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);

        private static FileStream AcquireCleanupCoordinator(string lockDirectory)
        {
            string path = Path.Combine(lockDirectory, ".cleanup");
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return new FileStream(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.WriteThrough);
                }
                catch (IOException) when (timeout.Elapsed < TimeSpan.FromSeconds(1))
                {
                    Thread.Sleep(10);
                }
            }
        }

        private static void CleanupStaleFiles(string lockDirectory, int maximumFiles)
        {
            foreach (string path in Directory.EnumerateFiles(lockDirectory, "*.lock")
                         .OrderBy(File.GetLastWriteTimeUtc)
                         .Take(maximumFiles))
            {
                TryDeleteUnlocked(path);
            }
        }

        private static void TryDeleteReleasedLock(string lockDirectory, string lockPath)
        {
            try
            {
                using FileStream coordinator = AcquireCleanupCoordinator(lockDirectory);
                TryDeleteUnlocked(lockPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteUnlocked(string path)
        {
            try
            {
                using (new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.None,
                           bufferSize: 1,
                           FileOptions.WriteThrough))
                {
                }

                File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static string ExternalLockRoot()
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
            {
                localData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share");
            }

            return Path.Combine(localData, "winmatsch");
        }
    }
}

internal static class DirectoryPin
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static IDisposable Acquire(string path)
        => OperatingSystem.IsWindows() ? OpenAndValidate(path) : NoopDisposable.Instance;

    public static string GetIdentity(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return GetUnixIdentity(path);
        }

        using SafeFileHandle handle = OpenAndValidate(path);
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"Unable to identify directory '{path}' (Win32 error {error}).");
        }

        return $"{information.VolumeSerialNumber:X8}-{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
    }

    public static string GetLegacyIdentity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return GetIdentity(path);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
    }

    private static string GetUnixIdentity(string path)
    {
        nint buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (Stat(Path.GetFullPath(path), buffer) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new IOException($"Unable to identify directory '{path}' (errno {error}).");
            }

            ulong device = OperatingSystem.IsMacOS()
                ? unchecked((uint)Marshal.ReadInt32(buffer, 0))
                : unchecked((ulong)Marshal.ReadInt64(buffer, 0));
            ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            return $"UNIX-{device:X16}-{inode:X16}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeFileHandle OpenAndValidate(string path)
    {
        SafeFileHandle handle = CreateFile(
            path,
            FileListDirectory,
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Unable to pin directory '{path}' against replacement (Win32 error {error}).");
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out FileAttributeTagInformation information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Unable to inspect pinned directory '{path}' (Win32 error {error}).");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidDataException($"Pinned directory '{path}' is a reparse point.");
        }

        return handle;
    }

#pragma warning disable SYSLIB1054 // Source-generated interop would require enabling unsafe blocks project-wide.
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    private const int FileAttributeTagInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat(string path, nint buffer);
#pragma warning restore SYSLIB1054

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal static class SecurePath
{
    public static string? ResolveExactExistingDirectory(string root, string repositoryPath)
    {
        string normalized = WorkflowPath.NormalizeRepositoryPath(repositoryPath);
        string current = Path.GetFullPath(root);
        foreach (string expectedSegment in normalized.Split('/'))
        {
            string? actualSegment = Directory.EnumerateFileSystemEntries(current)
                .Select(Path.GetFileName)
                .SingleOrDefault(name => string.Equals(name, expectedSegment, StringComparison.OrdinalIgnoreCase));
            if (actualSegment is null)
            {
                return null;
            }

            if (!string.Equals(actualSegment, expectedSegment, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Package path segment casing is '{actualSegment}', expected '{expectedSegment}'.");
            }

            current = Path.Combine(current, actualSegment);
            if (!Directory.Exists(current))
            {
                return null;
            }
        }

        return current;
    }

    public static void ValidateOutputRoot(string root)
    {
        string? existing = root;
        while (existing is not null && !Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
        }

        if (existing is not null)
        {
            string pathRoot = Path.GetPathRoot(existing)
                ?? throw new InvalidDataException("The output path has no filesystem root.");
            string current = pathRoot;
            foreach (string segment in Path.GetRelativePath(pathRoot, existing).Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Output path '{current}' contains a reparse point.");
                }
            }
        }
    }

    public static string Resolve(string root, string repositoryPath, bool requireExistingLeaf)
    {
        string normalized = WorkflowPath.NormalizeRepositoryPath(repositoryPath);
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Path '{repositoryPath}' escapes the output directory.");
        }

        if (requireExistingLeaf && !File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException($"Path '{repositoryPath}' does not exist.", fullPath);
        }

        return fullPath;
    }

    public static void RejectReparsePoints(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Path is outside the trusted output root.");
        }

        string current = fullRoot;
        if (Directory.Exists(current)
            && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Output path '{current}' is a reparse point.");
        }

        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Output path '{current}' contains a reparse point.");
            }
        }
    }
}

internal interface IWorkflowTransactionFileSystem
{
    public void DeleteDirectory(string path, bool recursive);

    public void DeleteFile(string path);

    public void MoveFile(string source, string destination);
}

internal sealed class WorkflowTransactionFileSystem : IWorkflowTransactionFileSystem
{
    public static WorkflowTransactionFileSystem Instance { get; } = new();

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    public void DeleteFile(string path) => File.Delete(path);

    public void MoveFile(string source, string destination) => File.Move(source, destination);
}

public sealed class WorkflowOperationException : Exception
{
    public WorkflowOperationException(WorkflowResultCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public WorkflowResultCode Code { get; }
}

public sealed class WorkflowRecoveryException : IOException
{
    public WorkflowRecoveryException(
        string message,
        Exception primaryException,
        Exception? rollbackException,
        Exception? cleanupException,
        bool journalRetained)
        : base(
            message,
            new AggregateException(
            [
                primaryException,
                .. rollbackException is null ? [] : new[] { rollbackException },
                .. cleanupException is null ? [] : new[] { cleanupException },
            ]))
    {
        PrimaryException = primaryException;
        RecoveryExceptions =
        [
            .. rollbackException is null ? [] : new[] { rollbackException },
            .. cleanupException is null ? [] : new[] { cleanupException },
        ];
        JournalRetained = journalRetained;
    }

    public Exception PrimaryException { get; }

    public ImmutableArray<Exception> RecoveryExceptions { get; }

    public bool JournalRetained { get; }
}

public abstract class WorkflowCommittedException : IOException
{
    protected WorkflowCommittedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WorkflowCommittedCleanupException : WorkflowCommittedException
{
    public WorkflowCommittedCleanupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WorkflowCommittedProvenanceException : WorkflowCommittedException
{
    public WorkflowCommittedProvenanceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
