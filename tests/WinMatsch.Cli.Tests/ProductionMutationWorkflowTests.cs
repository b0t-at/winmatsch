using System.Collections.Immutable;
using System.Net;
using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Tests.Maintenance;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
using WinMatsch.Validation;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Configuration;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Cli.Commands.Mutations;

public sealed class ProductionMutationWorkflowTests
{
    [Fact]
    public async Task Production_preparation_persists_completed_asset_evidence_without_redownload()
    {
        using var temporary = new TemporaryDirectory();
        byte[] executable = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Cli.Tests.dll"));
        var handler = new StaticResponseHandler(executable);
        using var downloader = new InstallerDownloader(handler);
        using ProductionMutationWorkflow workflow = CreateWorkflow(temporary.Path);
        DiscoveredAsset selected = ReleaseAsset("VCMI-Windows-x64.exe");
        DiscoveredAsset x86 = ReleaseAsset("VCMI-Windows-x86.exe");
        DiscoveredAsset arm64 = ReleaseAsset("VCMI-Windows-arm64.exe");
        ImmutableArray<PreviousInstallerEntry> previous =
        [
            PreviousInstaller(0, "VCMI-Windows-x64.exe", Architecture.X64),
            PreviousInstaller(1, "VCMI-Windows-x86.exe", Architecture.X86),
            PreviousInstaller(2, "VCMI-Windows-arm64.exe", Architecture.Arm64),
        ];
        var request = new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            PackageIdentifier = new PackageIdentifier("vcmi.vcmi"),
            PreviousVersion = new PackageVersion("1.7.3"),
            PackageVersion = "1.7.4",
            Release = new(null, [selected.DownloadUri], []),
            NetworkValidationMode = NetworkValidationMode.Skip,
        };
        var releases = new StaticReleaseSource(new([selected], [x86, arm64]));

        (WorkflowOperationRequest prepared, string? artifactDirectory) =
            await workflow.EnrichAssetsAsync(
                request,
                releases,
                downloader,
                previous,
                TestContext.Current.CancellationToken);

        var update = Assert.IsType<UpdateOperationRequest>(prepared);
        Assert.Equal(3, update.Assets.Length);
        Assert.Empty(update.ReleaseAssetCandidates);
        Assert.Equal(2, update.ReleaseAssetCompletions.Length);
        Assert.Equal(2, update.ReleaseAssetBindings.Length);
        Assert.Equal(3, update.InstallerArtifacts.Length);
        Assert.All(update.Assets, static asset =>
        {
            Assert.NotNull(asset.Content);
            Assert.NotNull(asset.Analysis);
        });
        Assert.NotNull(artifactDirectory);
        Assert.All(
            update.InstallerArtifacts,
            static artifact => Assert.True(File.Exists(artifact.Download.FilePath)));
        int requestsAfterPreparation = handler.Calls;

        (WorkflowOperationRequest reused, _) = await workflow.EnrichAssetsAsync(
            update,
            releases,
            downloader,
            previous,
            TestContext.Current.CancellationToken);

        Assert.Equal(requestsAfterPreparation, handler.Calls);
        Assert.Equal(
            update.ReleaseAssetCompletions,
            Assert.IsType<UpdateOperationRequest>(reused).ReleaseAssetCompletions);
        Assert.Equal(
            update.ReleaseAssetBindings,
            Assert.IsType<UpdateOperationRequest>(reused).ReleaseAssetBindings);
    }

    [Fact]
    public void Update_GitHub_asset_url_enables_optional_release_discovery()
    {
        using var gitHub = new FakeMaintenanceGitHubClient();
        var request = new UpdateOperationRequest
        {
            OutputDirectory = ".",
            PackageIdentifier = new PackageIdentifier("vcmi.vcmi"),
            PreviousVersion = new PackageVersion("1.7.3"),
            PackageVersion = "1.7.4",
            Release = new(
                null,
                [
                    new Uri(
                        "https://github.com/vcmi/vcmi/releases/download/1.7.4/VCMI-Windows-x64.exe"),
                ],
                []),
        };

        IWorkflowReleaseSource? source = ProductionMutationWorkflow.CreateReleaseSource(
            request,
            gitHub,
            new GitHubClientOptions());

        Assert.IsType<GitHubWorkflowReleaseSource>(source);
    }

    [Fact]
    public void Cross_repository_GitHub_asset_urls_keep_direct_release_source()
    {
        using var gitHub = new FakeMaintenanceGitHubClient();
        var request = new UpdateOperationRequest
        {
            OutputDirectory = ".",
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "2.0.0",
            Release = new(
                null,
                [
                    new Uri("https://github.com/one/app/releases/download/2.0.0/app-x64.exe"),
                    new Uri("https://github.com/two/app/releases/download/2.0.0/app-x86.exe"),
                ],
                []),
        };

        IWorkflowReleaseSource? source = ProductionMutationWorkflow.CreateReleaseSource(
            request,
            gitHub,
            new GitHubClientOptions());

        Assert.IsType<DirectWorkflowReleaseSource>(source);
    }

    [Fact]
    public async Task Verified_apply_uses_the_exact_native_plan_fingerprint()
    {
        using var temporary = new TemporaryDirectory();
        WritePackage(temporary.Path);
        string versionDirectory = VersionDirectory(temporary.Path);
        using var workflow = CreateWorkflow(temporary.Path);
        var request = new RemoveOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Plan,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PackageVersion = new PackageVersion("1.0.0"),
            NetworkValidationMode = NetworkValidationMode.Skip,
        };

        WorkflowOperationResult plan = await workflow.ExecuteAsync(request);
        WorkflowOperationResult applied = await workflow.ApplyVerifiedAsync(
            request,
            plan.Plan.Fingerprint);

        Assert.Equal(WorkflowResultCode.Succeeded, plan.Code);
        Assert.True(applied.Applied);
        Assert.False(Directory.Exists(versionDirectory));
    }

    [Fact]
    public async Task Verified_apply_rejects_a_different_fingerprint_without_mutating()
    {
        using var temporary = new TemporaryDirectory();
        WritePackage(temporary.Path);
        string versionDirectory = VersionDirectory(temporary.Path);
        using var workflow = CreateWorkflow(temporary.Path);
        var request = new RemoveOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Plan,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PackageVersion = new PackageVersion("1.0.0"),
            NetworkValidationMode = NetworkValidationMode.Skip,
        };

        WorkflowOperationResult plan = await workflow.ExecuteAsync(request);

        WorkflowOperationResult rejected = await workflow.ApplyVerifiedAsync(
            request,
            plan.Plan.Fingerprint + "00");

        Assert.Equal(WorkflowResultCode.StalePlan, rejected.Code);
        Assert.False(rejected.Applied);
        Assert.True(Directory.Exists(versionDirectory));
    }

    [Fact]
    public async Task Dispose_reports_cleanup_failure_without_reclassifying_completed_work()
    {
        using var temporary = new TemporaryDirectory();
        var warnings = new List<string>();
        string? retainedDirectory = null;
        var workflow = CreateWorkflow(
            temporary.Path,
            warnings.Add,
            path =>
            {
                retainedDirectory = path;
                throw new IOException("directory is locked");
            });
        try
        {
            _ = await workflow.ExecuteAsync(new SubmitOperationRequest
            {
                OutputDirectory = temporary.Path,
                ExecutionMode = WorkflowExecutionMode.Plan,
                Documents = [],
                NetworkValidationMode = NetworkValidationMode.Skip,
            });

            Exception? disposeFailure = Record.Exception(workflow.Dispose);

            Assert.Null(disposeFailure);
            Assert.Contains(
                warnings,
                warning => warning.Contains("directory is locked", StringComparison.Ordinal));
        }
        finally
        {
            if (retainedDirectory is not null && Directory.Exists(retainedDirectory))
            {
                Directory.Delete(retainedDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Resume_ignores_proven_unrelated_corruption_when_no_candidate_exists()
    {
        using var temporary = new TemporaryDirectory();
        const string evidencePath = "unrelated.journal.abc.corrupt";
        var journals = new DiagnosticOnlyJournalStore(new(
            [],
            [$"Quarantined journal evidence at '{evidencePath}'."])
        {
            Corruptions =
            [
                new(evidencePath, "other-repository", "Other.App"),
            ],
            RepositoryFileSystemIdentity = "current-repository",
        });
        ProductionSubmissionWorkflow workflow = CreateSubmissionWorkflow(
            temporary.Path,
            journals);

        GitHubLifecycleResult? result = await workflow.ResumePendingAsync(
            temporary.Path,
            new PackageIdentifier("Example.App"),
            new PackageVersion("2.0.0"),
            new RepositoryCoordinates("microsoft", "winget-pkgs"));

        Assert.Null(result);
    }

    [Fact]
    public async Task Resume_blocks_corruption_for_the_same_repository_and_package()
    {
        using var temporary = new TemporaryDirectory();
        const string evidencePath = "matching.journal.abc.corrupt";
        var journals = new DiagnosticOnlyJournalStore(new(
            [],
            [$"Quarantined journal evidence at '{evidencePath}'."])
        {
            Corruptions =
            [
                new(evidencePath, "current-repository", "Example.App"),
            ],
            RepositoryFileSystemIdentity = "current-repository",
        });
        ProductionSubmissionWorkflow workflow = CreateSubmissionWorkflow(
            temporary.Path,
            journals);

        SubmissionJournalTamperedException exception =
            await Assert.ThrowsAsync<SubmissionJournalTamperedException>(() =>
                workflow.ResumePendingAsync(
                    temporary.Path,
                    new PackageIdentifier("Example.App"),
                    new PackageVersion("2.0.0"),
                    new RepositoryCoordinates("microsoft", "winget-pkgs")));

        Assert.Contains(evidencePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_classifies_benign_recovery_diagnostics_as_conflict()
    {
        using var temporary = new TemporaryDirectory();
        var journals = new DiagnosticOnlyJournalStore(new(
            [],
            ["Discarded uncommitted submission intent 'abc'."]));
        ProductionSubmissionWorkflow workflow = CreateSubmissionWorkflow(
            temporary.Path,
            journals);

        SubmissionJournalConflictException exception =
            await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
                workflow.ResumePendingAsync(
                    temporary.Path,
                    new PackageIdentifier("Example.App"),
                    new PackageVersion("2.0.0"),
                    new RepositoryCoordinates("microsoft", "winget-pkgs")));

        Assert.Contains("Discarded uncommitted", exception.Message, StringComparison.Ordinal);
    }

    private static ProductionMutationWorkflow CreateWorkflow(
        string root,
        Action<string>? cleanupWarning = null,
        Action<string>? deleteDirectory = null)
        => new(
            new WinMatschConfiguration
            {
                Repository = new RepositoryCoordinates("microsoft", "winget-pkgs"),
                ConcurrentDownloads = 2,
                EnabledRules = [],
                DisabledRules = [],
                CacheEnabled = false,
                OverrideStoreDirectory = Path.Combine(root, "overrides"),
                FreshnessDelay = TimeSpan.FromHours(4),
                OutputFormat = OutputFormat.Text,
                OutputDirectory = root,
                Interaction = InteractionMode.Always,
            },
            new UnusedTokenAccessor(),
            new GitHubClientOptions(),
            cleanupWarning,
            deleteDirectory);

    private static ProductionSubmissionWorkflow CreateSubmissionWorkflow(
        string root,
        ISubmissionJournalStore journals)
        => new(
            new WinMatschConfiguration
            {
                Repository = new RepositoryCoordinates("microsoft", "winget-pkgs"),
                ConcurrentDownloads = 2,
                EnabledRules = [],
                DisabledRules = [],
                CacheEnabled = false,
                FreshnessDelay = TimeSpan.FromHours(4),
                OutputFormat = OutputFormat.Text,
                OutputDirectory = root,
                Interaction = InteractionMode.Always,
            },
            new GitHubToken("test-token"),
            new GitHubClientOptions(),
            journals);

    private static PreviousInstallerEntry PreviousInstaller(
        int position,
        string assetName,
        Architecture architecture)
        => new()
        {
            Position = position,
            Url = new(
                $"https://github.com/vcmi/vcmi/releases/download/1.7.3/{assetName}"),
            Architecture = architecture,
            InstallerType = InstallerType.Exe,
            PackageVersion = new("1.7.3"),
        };

    private static DiscoveredAsset ReleaseAsset(string assetName)
        => new()
        {
            ReleaseId = 174,
            ReleaseTag = "1.7.4",
            ReleaseName = "1.7.4",
            ReleaseUri = new("https://github.com/vcmi/vcmi/releases/tag/1.7.4"),
            IsPrerelease = false,
            ReleasePublishedAt = DateTimeOffset.UnixEpoch,
            AssetId = assetName.GetHashCode(StringComparison.Ordinal),
            AssetName = assetName,
            DownloadUri = new(
                $"https://github.com/vcmi/vcmi/releases/download/1.7.4/{assetName}"),
            DeclaredContentType = "application/octet-stream",
            DeclaredSize = 0,
            AssetCreatedAt = DateTimeOffset.UnixEpoch,
        };

    private sealed class StaticReleaseSource(WorkflowReleaseAssets assets)
        : IWorkflowReleaseSource
    {
        public Task<WorkflowReleaseAssets> DiscoverAsync(
            PackageIdentifier packageIdentifier,
            ReleaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(assets);
        }
    }

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }

    private static void WritePackage(string root)
    {
        var identifier = new PackageIdentifier("Example.App");
        var version = new PackageVersion("1.0.0");
        var locale = new LanguageTag("en-US");
        var manifests = new PackageManifests
        {
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = locale,
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Exe,
                        InstallerUrl = "https://example.test/app.exe",
                        InstallerSha256 = new Sha256Hash(new string('A', 64)),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                Publisher = "Example",
                PackageName = "App",
                License = "MIT",
                ShortDescription = "Example application",
            },
            Locales = [],
        };
        PackageManifestIO.WriteDirectory(VersionDirectory(root), manifests);
    }

    private static string VersionDirectory(string root)
        => Path.Combine(
            root,
            ManifestPaths.GetVersionDirectory(
                    new PackageIdentifier("Example.App"),
                    new PackageVersion("1.0.0"))
                .Replace('/', Path.DirectorySeparatorChar));

    private sealed class UnusedTokenAccessor : ITokenAccessor
    {
        public Task<ResolvedToken?> ResolveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ResolvedToken?>(null);

        public Task<ResolvedToken> RequireAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This local workflow must not request a token.");
    }

    private sealed class DiagnosticOnlyJournalStore(
        SubmissionJournalRecoveryResult recovery) : ISubmissionJournalStore
    {
        public Task<SubmissionJournalHandle> PrepareAsync(
            GitHubSubmissionRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SubmissionJournalEntry> ActivateAsync(
            SubmissionJournalHandle handle,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SubmissionJournalRecoveryResult> RecoverAsync(
            string outputDirectory,
            CancellationToken cancellationToken)
            => Task.FromResult(recovery);

        public Task<ImmutableArray<SubmissionJournalEntry>> ListPendingAsync(
            CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<SubmissionJournalEntry>.Empty);

        public Task<SubmissionJournalEntry?> GetAsync(
            string id,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SubmissionJournalEntry> RecordRemoteStateAsync(
            string id,
            long expectedRevision,
            RemoteMutationState remoteState,
            SubmissionJournalState state,
            string? errorMessage,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CancelAsync(
            string id,
            long expectedRevision,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CompleteAsync(
            string id,
            long expectedRevision,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"winmatsch-production-mutation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
