using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
using WinMatsch.Validation;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Configuration;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Cli.Commands.Mutations;

public sealed class ProductionMutationWorkflowFactory : IMutationWorkflowFactory
{
    public Task<IMutationWorkflow> CreateAsync(
        Hosting.CommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IMutationWorkflow>(
            new ProductionMutationWorkflow(
                context.Configuration,
                context.Tokens,
                context.GitHubOptions,
                context.Output.WriteDiagnostic));
    }
}

public sealed class ProductionSubmissionWorkflowFactory : ISubmissionWorkflowFactory
{
    public async Task<ISubmissionWorkflow> CreateAsync(
        Hosting.CommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ResolvedToken token = await context.Tokens.RequireAsync(cancellationToken).ConfigureAwait(false);
        return new ProductionSubmissionWorkflow(
            context.Configuration,
            token.Token,
            context.GitHubOptions);
    }
}

internal sealed class ProductionMutationWorkflow(
    WinMatschConfiguration configuration,
    Hosting.ITokenAccessor tokens,
    GitHubClientOptions gitHubOptions,
    Action<string>? cleanupWarning = null,
    Action<string>? deleteDirectory = null) : IVerifiedMutationWorkflow, IDisposable
{
    private WorkflowOperationRequest? _preparedRequest;
    private string? _artifactDirectory;
    private string? _submitCacheDirectory;
    private readonly Action<string> _deleteDirectory =
        deleteDirectory ?? (static path => Directory.Delete(path, recursive: true));

    public async Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool usePrepared = request.ExecutionMode == WorkflowExecutionMode.Apply
            && _preparedRequest is not null;
        if (usePrepared)
        {
            request = WithExecutionMode(_preparedRequest!, WorkflowExecutionMode.Apply);
        }
        else if (request.ExecutionMode == WorkflowExecutionMode.Plan)
        {
            CleanupArtifactDirectory();
            _preparedRequest = null;
        }

        using var downloader = new InstallerDownloader(
            DownloaderOptions(configuration, request is SubmitOperationRequest));
        using HttpClient? gitHubHttp = await CreateReleaseHttpClientAsync(request, cancellationToken)
            .ConfigureAwait(false);
        using IGitHubRepositoryClient? gitHub = gitHubHttp is null
            ? null
            : new Hosting.RedactingGitHubRepositoryClient(new GitHubRepositoryClient(
                gitHubHttp,
                (await tokens.RequireAsync(cancellationToken).ConfigureAwait(false)).Token.RevealValue(),
                gitHubOptions));
        IWorkflowReleaseSource? releases = CreateReleaseSource(request, gitHub, gitHubOptions);
        try
        {
            if (!usePrepared)
            {
                (request, _artifactDirectory) = await EnrichAssetsAsync(
                    request,
                    releases,
                    downloader,
                    cancellationToken).ConfigureAwait(false);
                request = await PrefetchSubmitAsync(
                    request,
                    downloader,
                    cancellationToken).ConfigureAwait(false);
                if (request.ExecutionMode == WorkflowExecutionMode.Plan)
                {
                    _preparedRequest = request;
                }
            }

            LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
                downloader,
                releases,
                overridePackStoreOptions: OverrideStoreOptions(configuration));
            WorkflowOperationResult result = await new LocalMutationWorkflow(engine)
                .ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return AttachTrustedReleaseProvenance(result, request, gitHubOptions);
        }
        catch (Exception primaryFailure)
        {
            try
            {
                if (!usePrepared)
                {
                    CleanupArtifactDirectory();
                }
            }
            catch (Exception cleanupFailure) when (
                cleanupFailure is IOException or UnauthorizedAccessException)
            {
                if (primaryFailure is OperationCanceledException
                    && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        $"{primaryFailure.Message} Cleanup of the mutation artifact directory "
                        + $"also failed: {cleanupFailure.Message}",
                        new AggregateException(primaryFailure, cleanupFailure),
                        cancellationToken);
                }

                throw new IOException(
                    $"{primaryFailure.Message} Cleanup of the mutation artifact directory also "
                    + $"failed: {cleanupFailure.Message}",
                    new AggregateException(primaryFailure, cleanupFailure));
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
    }

    public async Task<WorkflowOperationResult> ApplyVerifiedAsync(
        WorkflowOperationRequest request,
        string expectedPlanFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        WorkflowOperationRequest prepared = _preparedRequest is null
            ? request
            : WithExecutionMode(_preparedRequest, WorkflowExecutionMode.Apply);
        using var downloader = new InstallerDownloader(
            DownloaderOptions(configuration, prepared is SubmitOperationRequest));
        using HttpClient? gitHubHttp = await CreateReleaseHttpClientAsync(prepared, cancellationToken)
            .ConfigureAwait(false);
        using IGitHubRepositoryClient? gitHub = gitHubHttp is null
            ? null
            : new Hosting.RedactingGitHubRepositoryClient(new GitHubRepositoryClient(
                gitHubHttp,
                (await tokens.RequireAsync(cancellationToken).ConfigureAwait(false)).Token.RevealValue(),
                gitHubOptions));
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            CreateReleaseSource(prepared, gitHub, gitHubOptions),
            overridePackStoreOptions: OverrideStoreOptions(configuration));
        WorkflowOperationResult result = await engine.ApplyVerifiedPlanAsync(
            prepared,
            expectedPlanFingerprint,
            cancellationToken).ConfigureAwait(false);
        return AttachTrustedReleaseProvenance(result, prepared, gitHubOptions);
    }

    public void Dispose()
    {
        var failures = new List<Exception>();
        try
        {
            CleanupArtifactDirectory();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(exception);
        }

        try
        {
            if (_submitCacheDirectory is not null
                && Directory.Exists(_submitCacheDirectory))
            {
                _deleteDirectory(_submitCacheDirectory);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(exception);
        }
        GC.SuppressFinalize(this);
        foreach (Exception failure in failures)
        {
            cleanupWarning?.Invoke(
                $"Warning: a mutation temporary directory could not be removed: "
                + failure.Message);
        }
    }

    private static IWorkflowReleaseSource? CreateReleaseSource(
        WorkflowOperationRequest request,
        IGitHubRepositoryClient? gitHub,
        GitHubClientOptions gitHubOptions)
    {
        ReleaseRequest? release = request switch
        {
            NewOperationRequest value => value.Release,
            UpdateOperationRequest value => value.Release,
            _ => null,
        };
        if (release is null)
        {
            return null;
        }

        if (!release.ReleaseUrls.IsEmpty)
        {
            RepositoryCoordinates repository = ParseGitHubRepository(
                release.ReleaseUrls[0],
                gitHubOptions);
            IGitHubRepositoryClient requiredGitHub = gitHub
                ?? throw new InvalidOperationException(
                    "GitHub release discovery requires an invocation-scoped GitHub client.");
            return new GitHubWorkflowReleaseSource(
                requiredGitHub,
                repository,
                new GitHubRepositoryReleaseMetadataSource(requiredGitHub));
        }

        return release.InstallerUrls.IsEmpty ? null : new DirectWorkflowReleaseSource();
    }

    private async Task<HttpClient?> CreateReleaseHttpClientAsync(
        WorkflowOperationRequest request,
        CancellationToken cancellationToken)
    {
        ReleaseRequest? release = request switch
        {
            NewOperationRequest value => value.Release,
            UpdateOperationRequest value => value.Release,
            _ => null,
        };
        if (release is null || release.ReleaseUrls.IsEmpty)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = ParseGitHubRepository(release.ReleaseUrls[0], gitHubOptions);
        await Task.CompletedTask.ConfigureAwait(false);
        return new HttpClient();
    }

    private static RepositoryCoordinates ParseGitHubRepository(
        Uri releaseUri,
        GitHubClientOptions gitHubOptions)
    {
        string webHost = gitHubOptions.ApiBaseUri.Host.Equals(
            "api.github.com",
            StringComparison.OrdinalIgnoreCase)
            ? "github.com"
            : gitHubOptions.ApiBaseUri.Host;
        if (!releaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !releaseUri.Host.Equals(webHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(
                $"Release URL '{releaseUri}' must be an HTTPS {webHost} repository URL.");
        }

        string[] segments = releaseUri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            throw new FormatException(
                $"Release URL '{releaseUri}' does not identify a GitHub owner and repository.");
        }

        return new(segments[0], segments[1]);
    }

    private DownloaderOptions DownloaderOptions(
        WinMatschConfiguration configuration,
        bool submit)
        => new()
        {
            CacheDirectory = configuration.CacheEnabled
                ? configuration.CacheDirectory ?? DefaultCacheDirectory()
                : submit
                    ? _submitCacheDirectory ??= Directory.CreateTempSubdirectory(
                        "winmatsch-submit-cache-").FullName
                    : null,
        };

    private static OverridePackStoreOptions? OverrideStoreOptions(
        WinMatschConfiguration configuration)
        => configuration.OverrideStoreDirectory is null
            ? null
            : new OverridePackStoreOptions
            {
                RootDirectory = configuration.OverrideStoreDirectory,
            };

    private async Task<WorkflowOperationRequest> PrefetchSubmitAsync(
        WorkflowOperationRequest request,
        InstallerDownloader downloader,
        CancellationToken cancellationToken)
    {
        if (request is not SubmitOperationRequest submit)
        {
            return request;
        }

        ImmutableArray<string> urls = ExtractInstallerUrls(submit.Documents);
        if (urls.IsEmpty)
        {
            return request;
        }

        _artifactDirectory ??= Directory.CreateTempSubdirectory(
            "winmatsch-submit-artifacts-").FullName;
        _ = await downloader.DownloadManyAsync(
            urls,
            _artifactDirectory,
            configuration.ConcurrentDownloads,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return submit with { ArtifactDirectory = _artifactDirectory };
    }

    private static ImmutableArray<string> ExtractInstallerUrls(
        ImmutableArray<RawManifestDocument> documents)
    {
        var urls = ImmutableArray.CreateBuilder<string>();
        foreach (RawManifestDocument document in documents)
        {
            try
            {
                string yaml = new UTF8Encoding(false, true).GetString(document.Content.AsSpan());
                if (ManifestYamlReader.TryDetectType(yaml) != ManifestType.Installer)
                {
                    continue;
                }

                InstallerManifest manifest = ManifestYamlReader.ReadInstaller(yaml);
                urls.AddRange((manifest.Installers ?? [])
                    .Select(static installer => installer.InstallerUrl)
                    .OfType<string>()
                    .Where(static url => !string.IsNullOrWhiteSpace(url)));
            }
            catch (Exception exception) when (
                exception is DecoderFallbackException
                    or FormatException
                    or ArgumentException
                    or InvalidOperationException
                    or YamlDotNet.Core.YamlException)
            {
                // Prefetch is an optimization. Structured SubmitAsync validation owns malformed
                // input diagnostics and must remain the user-facing failure path.
                return [];
            }
        }

        return urls
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static WorkflowOperationResult AttachTrustedReleaseProvenance(
        WorkflowOperationResult result,
        WorkflowOperationRequest request,
        GitHubClientOptions options)
    {
        if (result.Plan.Release is not null)
        {
            return result;
        }

        ImmutableArray<DiscoveredAsset> assets = request switch
        {
            NewOperationRequest value => value.Assets,
            UpdateOperationRequest value => value.Assets,
            _ => [],
        };
        DiscoveredAsset[] releaseAssets =
        [
            .. assets.Where(static asset => asset.ReleaseId > 0),
        ];
        if (releaseAssets.Length == 0
            || releaseAssets.Select(static asset => asset.ReleaseId).Distinct().Count() != 1)
        {
            return result;
        }

        string trustedWebHost = options.ApiBaseUri.Host.Equals(
            "api.github.com",
            StringComparison.OrdinalIgnoreCase)
            ? "github.com"
            : options.ApiBaseUri.Host;
        Uri releaseUri = releaseAssets[0].ReleaseUri;
        if (!releaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !releaseUri.Host.Equals(trustedWebHost, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        string[] segments = releaseUri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DateTimeOffset[] timestamps =
        [
            .. releaseAssets.SelectMany(static asset => new DateTimeOffset?[]
                {
                    asset.ReleaseUpdatedAt,
                    asset.ReleasePublishedAt,
                    asset.AssetUpdatedAt,
                    asset.AssetCreatedAt,
                })
                .OfType<DateTimeOffset>(),
        ];
        if (segments.Length < 2 || timestamps.Length == 0)
        {
            return result;
        }

        return result with
        {
            Plan = result.Plan with
            {
                Release = new(
                    new RepositoryCoordinates(segments[0], segments[1]),
                    releaseAssets[0].ReleaseId,
                    timestamps.Max()),
            },
        };
    }

    private static WorkflowOperationRequest WithExecutionMode(
        WorkflowOperationRequest request,
        WorkflowExecutionMode mode)
        => request switch
        {
            NewOperationRequest value => value with { ExecutionMode = mode },
            UpdateOperationRequest value => value with { ExecutionMode = mode },
            RemoveOperationRequest value => value with { ExecutionMode = mode },
            SubmitOperationRequest value => value with { ExecutionMode = mode },
            NewLocaleOperationRequest value => value with { ExecutionMode = mode },
            UpdateLocaleOperationRequest value => value with { ExecutionMode = mode },
            _ => throw new ArgumentException("Unsupported mutation request.", nameof(request)),
        };

    private void CleanupArtifactDirectory()
    {
        if (_artifactDirectory is not null && Directory.Exists(_artifactDirectory))
        {
            _deleteDirectory(_artifactDirectory);
        }
        _artifactDirectory = null;
    }

    private async Task<(WorkflowOperationRequest Request, string? ArtifactDirectory)>
        EnrichAssetsAsync(
        WorkflowOperationRequest request,
        IWorkflowReleaseSource? releases,
        InstallerDownloader downloader,
        CancellationToken cancellationToken)
    {
        PackageIdentifier? identifier = request switch
        {
            NewOperationRequest value => value.PackageIdentifier,
            UpdateOperationRequest value => value.PackageIdentifier,
            _ => null,
        };
        if (identifier is null)
        {
            return (request, null);
        }

        ReleaseRequest release = request switch
        {
            NewOperationRequest value => value.Release,
            UpdateOperationRequest value => value.Release,
            _ => throw new InvalidOperationException(),
        };
        ImmutableArray<DiscoveredAsset> assets = request switch
        {
            NewOperationRequest value => value.Assets,
            UpdateOperationRequest value => value.Assets,
            _ => [],
        };
        if (assets.IsEmpty && releases is not null)
        {
            assets = await releases.DiscoverAsync(identifier, release, cancellationToken)
                .ConfigureAwait(false);
        }

        DiscoveredAsset[] ordered =
        [
            .. assets.OrderBy(
                static asset => asset.DownloadUri.AbsoluteUri,
                StringComparer.Ordinal),
        ];
        bool forceFresh = request.ExecutionMode == WorkflowExecutionMode.Apply;
        bool requiresAcquisition = ordered.Any(asset =>
            forceFresh || asset.Content is null || asset.Analysis is null);
        if (!requiresAcquisition)
        {
            return (request, null);
        }

        _artifactDirectory = Directory.CreateTempSubdirectory(
            "winmatsch-mutation-artifacts-").FullName;
        string artifactDirectory = _artifactDirectory;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                artifactDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var processor = new InstallerWorkflowArtifactProcessor(downloader);
        using var gate = new SemaphoreSlim(configuration.ConcurrentDownloads);
        ArtifactSnapshot?[] snapshots = new ArtifactSnapshot?[ordered.Length];
        await Task.WhenAll(ordered.Select(async (asset, index) =>
        {
            if (!forceFresh && asset.Content is not null && asset.Analysis is not null)
            {
                return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                snapshots[index] = await processor.AcquireAsync(
                    asset,
                    artifactDirectory,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        ImmutableArray<DiscoveredAsset> enriched =
        [
            .. ordered.Select((asset, index) => snapshots[index]?.Asset ?? asset),
        ];
        ImmutableArray<InstallerArtifact> existing = request switch
        {
            NewOperationRequest value => value.InstallerArtifacts,
            UpdateOperationRequest value => value.InstallerArtifacts,
            _ => [],
        };
        ImmutableArray<InstallerArtifact> installerArtifacts =
        [
            .. existing,
            .. snapshots.OfType<ArtifactSnapshot>().Select(snapshot =>
                new InstallerArtifact(snapshot.Asset.DownloadUri.AbsoluteUri, snapshot.Download)),
        ];
        return request switch
        {
            NewOperationRequest value => (value with
            {
                Assets = enriched,
                InstallerArtifacts = installerArtifacts,
                ArtifactDirectory = artifactDirectory,
            }, artifactDirectory),
            UpdateOperationRequest value => (value with
            {
                Assets = enriched,
                InstallerArtifacts = installerArtifacts,
                ArtifactDirectory = artifactDirectory,
            }, artifactDirectory),
            _ => (request, artifactDirectory),
        };
    }

    private static string DefaultCacheDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "winmatsch",
            "downloads");
}

internal sealed class ProductionSubmissionWorkflow : IJournaledSubmissionWorkflow
{
    private readonly WinMatschConfiguration _configuration;
    private readonly GitHubToken _token;
    private readonly GitHubClientOptions _gitHubOptions;
    private readonly ISubmissionJournalStore _journals;

    public ProductionSubmissionWorkflow(
        WinMatschConfiguration configuration,
        GitHubToken token,
        GitHubClientOptions gitHubOptions)
        : this(
            configuration,
            token,
            gitHubOptions,
            WorkflowProductionComposition.CreateSubmissionJournal(
                configuration.OverrideStoreDirectory is null
                    ? null
                    : new OverridePackStoreOptions
                    {
                        RootDirectory = configuration.OverrideStoreDirectory,
                    }))
    {
    }

    internal ProductionSubmissionWorkflow(
        WinMatschConfiguration configuration,
        GitHubToken token,
        GitHubClientOptions gitHubOptions,
        ISubmissionJournalStore journals)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _gitHubOptions = gitHubOptions ?? throw new ArgumentNullException(nameof(gitHubOptions));
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
    }

    public async Task<GitHubLifecycleResult> ExecuteAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();
        using var gitHub = new Hosting.RedactingGitHubRepositoryClient(
            new GitHubRepositoryClient(
                httpClient,
                _token.RevealValue(),
                _gitHubOptions));
        using var downloader = new InstallerDownloader(new DownloaderOptions
        {
            CacheDirectory = _configuration.CacheEnabled
                ? _configuration.CacheDirectory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "winmatsch",
                    "downloads")
                : null,
        });
        GitHubLifecycleWorkflow workflow =
            WorkflowProductionComposition.CreateGitHubLifecycle(gitHub, downloader);
        return await new LifecycleSubmissionWorkflow(workflow)
            .ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<SubmissionJournalHandle> PrepareAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken = default)
        => _journals.PrepareAsync(request, cancellationToken);

    public async Task<GitHubLifecycleResult> ExecutePreparedAsync(
        SubmissionJournalHandle handle,
        CancellationToken cancellationToken = default)
    {
        SubmissionJournalEntry entry = await _journals.ActivateAsync(
            handle,
            CancellationToken.None).ConfigureAwait(false);
        return await ExecuteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubLifecycleResult?> ResumePendingAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        RepositoryCoordinates upstreamRepository,
        CancellationToken cancellationToken = default)
    {
        SubmissionJournalRecoveryResult recovery = await _journals
            .RecoverAsync(outputDirectory, cancellationToken)
            .ConfigureAwait(false);
        SubmissionJournalCorruption? unknown = recovery.Corruptions.FirstOrDefault(corruption =>
            string.IsNullOrWhiteSpace(corruption.RepositoryFileSystemIdentity)
            || string.IsNullOrWhiteSpace(corruption.PackageIdentifier));
        if (unknown is not null)
        {
            throw new SubmissionJournalTamperedException(
                "A quarantined submission journal has no verified package scope, so pending "
                + "work cannot be proven unrelated. Inspect the preserved evidence "
                + $"'{unknown.EvidencePath}' before resuming submissions.");
        }

        if (!recovery.Corruptions.IsDefaultOrEmpty
            && recovery.Corruptions.Any(corruption =>
                string.Equals(
                    corruption.PackageIdentifier,
                    packageIdentifier.Value,
                    StringComparison.OrdinalIgnoreCase)))
        {
            SubmissionJournalCorruption matching = recovery.Corruptions.First(corruption =>
                string.Equals(
                    corruption.PackageIdentifier,
                    packageIdentifier.Value,
                    StringComparison.OrdinalIgnoreCase));
            throw new SubmissionJournalTamperedException(
                $"A quarantined submission journal may contain unfinished remote work for package "
                + $"'{packageIdentifier}'. Inspect the preserved evidence "
                + $"'{Path.GetFileName(matching.EvidencePath)}' before resuming this package.");
        }

        ImmutableArray<SubmissionJournalEntry> candidates =
        [
            .. (await _journals.ListPendingAsync(cancellationToken).ConfigureAwait(false))
                .Where(entry =>
                    string.Equals(
                        Path.GetFullPath(entry.Repository.CanonicalPath),
                        Path.GetFullPath(outputDirectory),
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal)
                    && entry.LocalPlan.PackageIdentifier == packageIdentifier
                    && entry.LocalPlan.PackageVersion == packageVersion
                    && entry.RemoteRequest.UpstreamRepository == upstreamRepository),
        ];
        if (candidates.IsEmpty)
        {
            return null;
        }

        if (candidates.Length != 1)
        {
            throw new SubmissionJournalConflictException(
                "Multiple pending submission journals match this package version.");
        }

        GitHubLifecycleResult result = await ExecuteEntryAsync(
            candidates[0],
            cancellationToken).ConfigureAwait(false);
        if (recovery.Diagnostics.IsDefaultOrEmpty)
        {
            return result;
        }

        return result with
        {
            Diagnostics =
            [
                .. result.Diagnostics,
                .. recovery.Diagnostics.Select(static diagnostic =>
                    new GitHubLifecycleDiagnostic("GH2042", diagnostic)),
            ],
        };
    }

    public Task<ImmutableArray<SubmissionJournalEntry>> ListPendingAsync(
        CancellationToken cancellationToken = default)
        => _journals.ListPendingAsync(cancellationToken);

    public Task CancelAsync(
        string id,
        long expectedRevision,
        CancellationToken cancellationToken = default)
        => _journals.CancelAsync(id, expectedRevision, cancellationToken);

    private async Task<GitHubLifecycleResult> ExecuteEntryAsync(
        SubmissionJournalEntry entry,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        using var gitHub = new Hosting.RedactingGitHubRepositoryClient(
            new GitHubRepositoryClient(
                httpClient,
                _token.RevealValue(),
                _gitHubOptions));
        using var downloader = new InstallerDownloader(new DownloaderOptions
        {
            CacheDirectory = _configuration.CacheEnabled
                ? _configuration.CacheDirectory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "winmatsch",
                    "downloads")
                : null,
        });
        VerifiedSubmissionRecoveryRequest recovery =
            await SubmissionJournalMaterializer.MaterializeVerifiedAsync(
            entry,
            gitHub,
            cancellationToken).ConfigureAwait(false);
        GitHubLifecycleWorkflow workflow =
            WorkflowProductionComposition.CreateGitHubLifecycle(gitHub, downloader);
        var progress = new JournalProgressSink(_journals, entry);
        GitHubLifecycleResult result = await workflow.ExecuteJournaledAsync(
            recovery,
            progress,
            cancellationToken).ConfigureAwait(false);

        SubmissionJournalEntry current = progress.Current;
        if (result.Applied)
        {
            if (current.State != SubmissionJournalState.PullRequestCreated)
            {
                current = await _journals.RecordRemoteStateAsync(
                    current.Id,
                    current.Revision,
                    result.RemoteState,
                    SubmissionJournalState.PullRequestCreated,
                    errorMessage: null,
                    CancellationToken.None).ConfigureAwait(false);
            }

            await _journals.CompleteAsync(
                current.Id,
                current.Revision,
                CancellationToken.None).ConfigureAwait(false);
            return result;
        }

        SubmissionJournalState next = result.Code == GitHubLifecycleResultCode.HumanEscalationRequired
                || result.RemoteState.RemoteOutcomeUncertain
            ? SubmissionJournalState.EscalationRequired
            : StateFor(result.RemoteState, current.State);
        _ = await _journals.RecordRemoteStateAsync(
            current.Id,
            current.Revision,
            result.RemoteState,
            next,
            string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)),
            CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private static SubmissionJournalState StateFor(
        RemoteMutationState state,
        SubmissionJournalState current)
        => state.PullRequestCreated
            ? SubmissionJournalState.PullRequestCreated
            : state.CommitCreated
                ? SubmissionJournalState.CommitCreated
                : state.BranchCreated || state.BranchAdopted
                    ? SubmissionJournalState.BranchCreated
                    : current;

    private sealed class JournalProgressSink(
        ISubmissionJournalStore journals,
        SubmissionJournalEntry entry) : ISubmissionProgressSink
    {
        public SubmissionJournalEntry Current { get; private set; } = entry;

        public async Task RecordAsync(
            RemoteMutationState remoteState,
            SubmissionJournalState state,
            CancellationToken cancellationToken)
        {
            Current = await journals.RecordRemoteStateAsync(
                Current.Id,
                Current.Revision,
                remoteState,
                state,
                errorMessage: null,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
