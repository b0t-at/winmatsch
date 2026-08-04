using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Testing.Infrastructure;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using WinMatsch.Workflows.Tests.GitHub;
using Xunit;

namespace WinMatsch.Workflows.Tests.Operations;

public sealed class WorkflowProductionCompositionTests
{
    private const string NestedZipInstallerUrl =
        "https://example.test/RoslynPad-windows-x64.zip";

    [Fact]
    public async Task Direct_release_source_deduplicates_identical_installer_urls()
    {
        var source = new DirectWorkflowReleaseSource();
        var url = new Uri("https://example.test/setup.exe");

        ImmutableArray<DiscoveredAsset> assets = await source.DiscoverAsync(
            new PackageIdentifier("Example.Composed"),
            new ReleaseRequest(null, [url, url], []),
            TestContext.Current.CancellationToken);

        Assert.Equal(url, Assert.Single(assets).DownloadUri);
    }

    [Fact]
    public async Task Production_update_preserves_same_url_scope_twin_switches()
    {
        byte[] executable = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Workflows.Tests.dll"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(executable),
        });
        using var downloader = new InstallerDownloader(handler);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            new DirectWorkflowReleaseSource());
        string output = CreateDirectory();
        var url = new Uri("https://example.test/setup.exe");
        try
        {
            WritePrevious(output, scopedTwins: true);

            WorkflowOperationResult result = await engine.UpdateAsync(new UpdateOperationRequest
            {
                OutputDirectory = output,
                PackageIdentifier = new PackageIdentifier("Example.Composed"),
                PreviousVersion = new PackageVersion("1.0.0"),
                PackageVersion = "2.0.0",
                AllowStableUrlContentChange = true,
                Release = new(null, [url, url], []),
            });

            Assert.Equal(WorkflowResultCode.Succeeded, result.Code);
            RawManifestDocument installer = Assert.Single(
                result.Plan.AfterDocuments,
                static document => document.RepositoryPath.EndsWith(".installer.yaml", StringComparison.Ordinal));
            string yaml = Encoding.UTF8.GetString(installer.Content.AsSpan());
            Assert.Contains("Custom: /CURRENTUSER", yaml, StringComparison.Ordinal);
            Assert.Contains("Custom: /ALLUSERS", yaml, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Production_update_clears_stale_nested_state_from_direct_executables()
    {
        byte[] executable = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Workflows.Tests.dll"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(executable),
        });
        using var downloader = new InstallerDownloader(handler);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            new DirectWorkflowReleaseSource());
        string output = CreateDirectory();
        try
        {
            WritePrevious(output, staleDirectNestedState: true);

            WorkflowOperationResult result = await engine.UpdateAsync(new UpdateOperationRequest
            {
                OutputDirectory = output,
                PackageIdentifier = new PackageIdentifier("Example.Composed"),
                PreviousVersion = new PackageVersion("1.0.0"),
                PackageVersion = "2.0.0",
                AllowStructuralRewrite = true,
                AllowStableUrlContentChange = true,
                Release = new(null, [new Uri("https://example.test/setup.exe")], []),
            });

            Assert.Equal(WorkflowResultCode.Succeeded, result.Code);
            RawManifestDocument installer = Assert.Single(
                result.Plan.AfterDocuments,
                static document => document.RepositoryPath.EndsWith(".installer.yaml", StringComparison.Ordinal));
            string yaml = Encoding.UTF8.GetString(installer.Content.AsSpan());
            Assert.DoesNotContain("NestedInstallerType:", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("NestedInstallerFiles:", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("ArchiveBinariesDependOnPath:", yaml, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Production_local_engine_runs_a_full_new_plan_with_direct_urls()
    {
        byte[] executable = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Workflows.Tests.dll"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(executable),
            Headers = { ETag = new("\"fixture\"") },
        });
        using var downloader = new InstallerDownloader(handler);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            new DirectWorkflowReleaseSource());
        string output = CreateDirectory();
        try
        {
            WorkflowOperationResult result = await engine.NewAsync(new NewOperationRequest
            {
                OutputDirectory = output,
                PackageIdentifier = new PackageIdentifier("Example.Composed"),
                PackageVersion = "1.0.0",
                Release = new(
                    null,
                    [new Uri("https://example.test/setup.exe")],
                    []),
                Locale = Locale(),
            });

            Assert.True(
                result.Code == WorkflowResultCode.Succeeded,
                string.Join(
                    Environment.NewLine,
                    [$"Code: {result.Code}", .. result.Plan.Validation.Findings.Select(static finding =>
                        $"{finding.Code}: {finding.Message}"), .. result.Plan.Questions.Select(static question =>
                        $"{question.Code}: {question.Prompt}")]));
            Assert.False(result.Applied);
            Assert.NotEmpty(result.Plan.FileChanges);
            Assert.NotEmpty(result.Plan.Rules.Executions);
            Assert.Contains(
                result.Plan.Rules.Executions,
                static execution => execution.RuleId == Rules.RuleCatalogueIds.Pipe1);
            Assert.True(handler.Requests.Count >= 3);
            Assert.Empty(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Production_local_engine_carries_github_release_provenance()
    {
        byte[] executable = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Workflows.Tests.dll"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(executable),
        });
        using var downloader = new InstallerDownloader(handler);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(downloader);
        string output = CreateDirectory();
        var releaseUpdated = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var assetUpdated = releaseUpdated.AddMinutes(5);
        try
        {
            WorkflowOperationResult result = await engine.NewAsync(new NewOperationRequest
            {
                OutputDirectory = output,
                PackageIdentifier = new PackageIdentifier("Example.Composed"),
                PackageVersion = "1.0.0",
                Assets =
                [
                    new DiscoveredAsset
                    {
                        ReleaseId = 42,
                        ReleaseTag = "v1.0.0",
                        ReleaseName = "1.0.0",
                        ReleaseUri = new Uri("https://github.com/vendor/app/releases/tag/v1.0.0"),
                        IsPrerelease = false,
                        ReleasePublishedAt = releaseUpdated.AddDays(-1),
                        ReleaseUpdatedAt = releaseUpdated,
                        AssetId = 7,
                        AssetName = "setup.exe",
                        DownloadUri = new Uri("https://example.test/setup.exe"),
                        DeclaredContentType = "application/octet-stream",
                        DeclaredSize = executable.Length,
                        AssetCreatedAt = releaseUpdated,
                        AssetUpdatedAt = assetUpdated,
                    },
                ],
                Locale = Locale(),
            });

            WorkflowReleaseProvenance provenance = Assert.IsType<WorkflowReleaseProvenance>(
                result.Plan.Release);
            Assert.Equal(new RepositoryCoordinates("vendor", "app"), provenance.Repository);
            Assert.Equal(42, provenance.ReleaseId);
            Assert.Equal(assetUpdated, provenance.UpdatedAt);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Theory]
    [InlineData("1.4.0")]
    [InlineData("1.9.0")]
    [InlineData("1.10.0")]
    public async Task Production_local_engine_passes_previous_manifest_on_update(string sourceManifestVersion)
    {
        byte[] executable = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Workflows.Tests.dll"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(executable),
            Headers = { ETag = new("\"fixture\"") },
        });
        using var downloader = new InstallerDownloader(handler);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            new DirectWorkflowReleaseSource());
        string output = CreateDirectory();
        try
        {
            WritePrevious(output);
            RewriteManifestVersion(output, sourceManifestVersion);
            WorkflowOperationResult result = await engine.UpdateAsync(new UpdateOperationRequest
            {
                OutputDirectory = output,
                PackageIdentifier = new PackageIdentifier("Example.Composed"),
                PreviousVersion = new PackageVersion("1.0.0"),
                PackageVersion = "2.0.0",
                AllowStructuralRewrite = true,
                AllowStableUrlContentChange = true,
                Release = new(
                    null,
                    [new Uri("https://example.test/setup.exe")],
                    []),
            });

            Assert.True(
                result.Code == WorkflowResultCode.Succeeded,
                string.Join(
                    Environment.NewLine,
                    [$"Code: {result.Code}", .. result.Plan.Validation.Findings.Select(static finding =>
                            $"{finding.Code}: {finding.Message}"), .. result.Plan.Questions.Select(static question =>
                            $"{question.Code}: {question.Prompt}")]));
            Assert.Contains(
                result.Plan.Rules.Executions,
                static execution => execution.RuleId == Rules.RuleIds.PreserveOnUpdate);
            Assert.All(
                result.Plan.AfterDocuments,
                static document => Assert.Contains(
                    "ManifestVersion: 1.12.0",
                    Encoding.UTF8.GetString(document.Content.AsSpan()),
                    StringComparison.Ordinal));
            Assert.NotEmpty(result.Plan.Rules.Changes);
            Assert.False(result.Applied);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Composed_github_plan_performs_no_network_or_remote_mutation()
    {
        var client = new FakeGitHubClient();
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Plan-only GitHub composition must not use artifact network."));
        using var downloader = new InstallerDownloader(handler);
        GitHubLifecycleWorkflow workflow = WorkflowProductionComposition.CreateGitHubLifecycle(
            client,
            downloader);

        GitHubLifecycleResult result = await workflow.ExecuteAsync(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.Equal(GitHubLifecycleResultCode.Planned, result.Code);
        Assert.Empty(client.Mutations);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Composed_github_apply_uses_pinned_repository_submission_evidence()
    {
        var client = new FakeGitHubClient();
        client.SetContent(
            GitHubLifecycleTestSupport.Upstream,
            GitHubRepositorySubmissionEvidenceProvider.PolicyPath,
            GitHubLifecycleTestSupport.UpstreamSha,
            Encoding.UTF8.GetBytes(
                """{"retiredIdentifiers":["Example.App"]}"""));
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException(
                "Retired repository evidence must block before artifact network."));
        using var downloader = new InstallerDownloader(handler);
        GitHubLifecycleWorkflow workflow = WorkflowProductionComposition.CreateGitHubLifecycle(
            client,
            downloader);

        GitHubLifecycleResult result = await workflow.ExecuteAsync(
            GitHubLifecycleTestSupport.Request());

        Assert.Equal(GitHubLifecycleResultCode.InvalidPlan, result.Code);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "GH1013");
        Assert.Empty(client.Mutations);
        Assert.Empty(handler.Requests);
        Assert.Contains(
            client.ContentRequests,
            request => request.Path == GitHubRepositorySubmissionEvidenceProvider.PolicyPath
                && request.Reference == GitHubLifecycleTestSupport.UpstreamSha);
    }

    [Fact]
    public async Task Composed_github_apply_revalidates_deleted_local_artifacts_before_mutation()
    {
        byte[] executable = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Workflows.Tests.dll"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(executable),
            Headers = { ETag = new("\"fixture\"") },
        });
        LocalOperationPlan localPlan;
        string output = CreateDirectory();
        using (var localDownloader = new InstallerDownloader(handler))
        {
            LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
                localDownloader,
                new DirectWorkflowReleaseSource());
            WorkflowOperationResult local = await engine.NewAsync(new NewOperationRequest
            {
                OutputDirectory = output,
                PackageIdentifier = new PackageIdentifier("Example.Composed"),
                PackageVersion = "1.0.0",
                Release = new(
                    null,
                    [new Uri("https://example.test/setup.exe")],
                    []),
                Locale = Locale(),
            });
            Assert.Equal(WorkflowResultCode.Succeeded, local.Code);
            localPlan = local.Plan;
        }

        try
        {
            Assert.All(
                localPlan.Preflight.InstallerArtifacts,
                static artifact => Assert.False(File.Exists(artifact.Download.FilePath)));
            var client = new FakeGitHubClient();
            using var remoteDownloader = new InstallerDownloader(handler);
            GitHubLifecycleWorkflow workflow = WorkflowProductionComposition.CreateGitHubLifecycle(
                client,
                remoteDownloader);
            GitHubLifecycleResult result = await workflow.ExecuteAsync(new GitHubSubmissionRequest
            {
                LocalPlan = localPlan,
                UpstreamRepository = GitHubLifecycleTestSupport.Upstream,
                TargetRepository = GitHubLifecycleTestSupport.Fork,
                ExecutionMode = WorkflowExecutionMode.Apply,
                Policy = new() { MinimumReleaseFreshness = TimeSpan.Zero },
                IdempotencyKey = "production-deleted-artifact",
            });

            Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
            Assert.Equal(["branch", "commit", "pull-request"], client.Mutations);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Journaled_raw_nested_zip_submit_reacquires_artifacts_before_remote_lifecycle()
    {
        byte[] archive = CreateZipArchive("RoslynPad.exe", "harmless canary"u8);
        string output = CreateDirectory();
        string journalState = CreateDirectory();
        string overrideState = CreateDirectory();
        string remoteLocks = CreateDirectory();
        string artifactRoot = CreateDirectory();
        const string approvedFinalUrl =
            "https://cdn.example.test/releases/RoslynPad.zip?sig=approved&expires=1";
        const string refreshedFinalUrl =
            "https://cdn.example.test/releases/RoslynPad.zip?sig=refreshed&expires=2";
        const string sourceArtifactPath = "RoslynPad-windows-x64.zip";
        Assert.False(Path.IsPathFullyQualified(sourceArtifactPath));
        Assert.Equal(string.Empty, Path.GetDirectoryName(sourceArtifactPath));
        try
        {
            GitHubSubmissionRequest request = RawNestedZipSubmissionRequest(
                output,
                sourceArtifactPath,
                archive,
                approvedFinalUrl);
            var journals = new FileSubmissionJournalStore(new SubmissionJournalOptions
            {
                RootDirectory = journalState,
                OverrideStoreDirectory = overrideState,
            });
            SubmissionJournalHandle handle = await journals.PrepareAsync(
                request,
                TestContext.Current.CancellationToken);
            WriteCommittedChanges(request.LocalPlan);
            SubmissionJournalEntry entry = await journals.ActivateAsync(
                handle,
                TestContext.Current.CancellationToken);
            SubmissionJournalArtifactIdentity artifactIdentity = Assert.Single(
                entry.LocalPlan.InstallerArtifacts);
            Assert.Equal(
                SubmissionJournalArtifactIdentity.CurrentFormatVersion,
                artifactIdentity.FormatVersion);
            Assert.Equal(
                DownloadRedirectIdentity.ComputeSha256(approvedFinalUrl),
                artifactIdentity.ApprovedFinalUrlSha256);
            var client = new FakeGitHubClient();
            using var downloader = new InstallerDownloader(new StubHttpMessageHandler(request =>
                RedirectingArchiveResponse(request, archive, refreshedFinalUrl)));
            string recoveredPath;
            await using (VerifiedSubmissionRecoveryRequest recovery =
                         await SubmissionJournalMaterializer.MaterializeVerifiedAsync(
                             entry,
                             client,
                             downloader,
                             artifactRoot,
                             BoundedSubmissionArtifactDirectoryCleanup.Instance,
                             TestContext.Current.CancellationToken))
            {
                DownloadResult recoveredArtifact = Assert.Single(
                    recovery.Request.LocalPlan.Preflight.InstallerArtifacts).Download;
                recoveredPath = recoveredArtifact.FilePath;
                Assert.False(string.IsNullOrWhiteSpace(recoveredPath));
                Assert.True(Path.IsPathFullyQualified(recoveredPath));
                Assert.NotEqual(sourceArtifactPath, recoveredPath);
                Assert.True(File.Exists(recoveredPath));
                Assert.False(recoveredArtifact.MayBeStored);
                GitHubLifecycleWorkflow workflow =
                    WorkflowProductionComposition.CreateGitHubLifecycle(
                        client,
                        downloader,
                        lockOptions: new RemoteOperationLockOptions
                        {
                            RootDirectory = remoteLocks,
                        });

                GitHubLifecycleResult result = await workflow.ExecuteJournaledAsync(
                    recovery,
                    new FakeSubmissionProgressSink(),
                    TestContext.Current.CancellationToken);

                Assert.True(File.Exists(recoveredPath));
                Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
                Assert.Equal(["branch", "commit", "pull-request"], client.Mutations);
            }

            Assert.False(File.Exists(recoveredPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(artifactRoot));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(journalState, recursive: true);
            Directory.Delete(overrideState, recursive: true);
            Directory.Delete(remoteLocks, recursive: true);
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Journaled_artifact_reacquisition_cleans_rejected_and_failed_downloads()
    {
        byte[] plannedArchive = CreateZipArchive("RoslynPad.exe", "planned canary"u8);
        byte[] changedArchive = CreateZipArchive("RoslynPad.exe", "changed canary"u8);
        string output = CreateDirectory();
        string journalState = CreateDirectory();
        string overrideState = CreateDirectory();
        string artifactRoot = CreateDirectory();
        string cache = CreateDirectory();
        string cacheSentinel = Path.Combine(cache, "shared-cache-sentinel");
        await File.WriteAllTextAsync(cacheSentinel, "keep");
        try
        {
            GitHubSubmissionRequest request = RawNestedZipSubmissionRequest(
                output,
                "RoslynPad-windows-x64.zip",
                plannedArchive);
            var journals = new FileSubmissionJournalStore(new SubmissionJournalOptions
            {
                RootDirectory = journalState,
                OverrideStoreDirectory = overrideState,
            });
            SubmissionJournalHandle handle = await journals.PrepareAsync(
                request,
                TestContext.Current.CancellationToken);
            WriteCommittedChanges(request.LocalPlan);
            SubmissionJournalEntry entry = await journals.ActivateAsync(
                handle,
                TestContext.Current.CancellationToken);
            var client = new FakeGitHubClient();
            using var downloader = new InstallerDownloader(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(changedArchive),
                }), new DownloaderOptions { CacheDirectory = cache });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SubmissionJournalMaterializer.MaterializeVerifiedAsync(
                    entry,
                    client,
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
                SubmissionJournalMaterializer.MaterializeVerifiedAsync(
                    entry,
                    client,
                    downloader,
                    artifactRoot,
                    BoundedSubmissionArtifactDirectoryCleanup.Instance,
                    TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(artifactRoot));
            Assert.True(File.Exists(cacheSentinel));
            using var failingDownloader = new InstallerDownloader(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.NotFound)));
            await Assert.ThrowsAsync<IOException>(() =>
                SubmissionJournalMaterializer.MaterializeVerifiedAsync(
                    entry,
                    client,
                    failingDownloader,
                    artifactRoot,
                    BoundedSubmissionArtifactDirectoryCleanup.Instance,
                    TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(artifactRoot));
            Assert.True(File.Exists(cacheSentinel));
            Assert.Empty(client.Mutations);
            SubmissionJournalEntry retained = Assert.IsType<SubmissionJournalEntry>(
                await journals.GetAsync(handle.Id, TestContext.Current.CancellationToken));
            Assert.Equal(SubmissionJournalState.Pending, retained.State);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(journalState, recursive: true);
            Directory.Delete(overrideState, recursive: true);
            Directory.Delete(artifactRoot, recursive: true);
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task Journaled_artifact_reacquisition_blocks_redirect_drift_and_legacy_identity()
    {
        byte[] archive = CreateZipArchive("RoslynPad.exe", "redirect canary"u8);
        string output = CreateDirectory();
        string journalState = CreateDirectory();
        string overrideState = CreateDirectory();
        string artifactRoot = CreateDirectory();
        const string approvedFinalUrl =
            "https://cdn.example.test/releases/RoslynPad.zip?sig=approved";
        const string changedFinalUrl =
            "https://cdn2.example.test/releases/RoslynPad.zip?sig=approved";
        try
        {
            GitHubSubmissionRequest request = RawNestedZipSubmissionRequest(
                output,
                "RoslynPad-windows-x64.zip",
                archive,
                approvedFinalUrl);
            var journals = new FileSubmissionJournalStore(new SubmissionJournalOptions
            {
                RootDirectory = journalState,
                OverrideStoreDirectory = overrideState,
            });
            SubmissionJournalHandle handle = await journals.PrepareAsync(
                request,
                TestContext.Current.CancellationToken);
            WriteCommittedChanges(request.LocalPlan);
            SubmissionJournalEntry entry = await journals.ActivateAsync(
                handle,
                TestContext.Current.CancellationToken);
            var client = new FakeGitHubClient();
            using var downloader = new InstallerDownloader(new StubHttpMessageHandler(request =>
                RedirectingArchiveResponse(request, archive, changedFinalUrl)));

            await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
                SubmissionJournalMaterializer.MaterializeVerifiedAsync(
                    entry,
                    client,
                    downloader,
                    artifactRoot,
                    BoundedSubmissionArtifactDirectoryCleanup.Instance,
                    TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(artifactRoot));
            SubmissionJournalArtifactIdentity identity = Assert.Single(
                entry.LocalPlan.InstallerArtifacts);
            SubmissionJournalEntry legacy = entry with
            {
                LocalPlan = entry.LocalPlan with
                {
                    InstallerArtifacts =
                    [
                        identity with
                        {
                            FormatVersion = 0,
                            ApprovedFinalUrlSha256 = null,
                        },
                    ],
                },
            };
            await Assert.ThrowsAsync<SubmissionJournalConflictException>(() =>
                SubmissionJournalMaterializer.MaterializeVerifiedAsync(
                    legacy,
                    client,
                    downloader,
                    artifactRoot,
                    BoundedSubmissionArtifactDirectoryCleanup.Instance,
                    TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(artifactRoot));
            Assert.Empty(client.Mutations);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(journalState, recursive: true);
            Directory.Delete(overrideState, recursive: true);
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Final_revalidation_redownloads_when_the_planned_file_no_longer_exists()
    {
        byte[] content = "stable installer"u8.ToArray();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        });
        using var downloader = new InstallerDownloader(handler);
        var revalidator = new DownloaderFinalArtifactRevalidator(downloader);
        GitHubSubmissionRequest request = RequestWithArtifact(
            "https://example.test/setup.exe",
            content,
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"));

        FinalArtifactRevalidationResult result = await revalidator.RevalidateAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Final_revalidation_redacts_signed_urls_when_content_changes()
    {
        byte[] planned = "planned"u8.ToArray();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("changed"u8.ToArray()),
        });
        using var downloader = new InstallerDownloader(handler);
        var revalidator = new DownloaderFinalArtifactRevalidator(downloader);
        GitHubSubmissionRequest request = RequestWithArtifact(
            "https://example.test/setup.exe?sig=TOPSECRET&token=ALSOSECRET",
            planned,
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"));

        FinalArtifactRevalidationResult result = await revalidator.RevalidateAsync(
            request,
            CancellationToken.None);

        GitHubLifecycleDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.False(result.IsValid);
        Assert.DoesNotContain("TOPSECRET", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ALSOSECRET", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_preflight_revalidation_returns_durable_bytes_and_cleanup_diagnostics()
    {
        byte[] content = "stable installer"u8.ToArray();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        });
        using var downloader = new InstallerDownloader(handler);
        string state = CreateDirectory();
        string crashedCopy = Path.Combine(state, "stale.bin.tmp-crash");
        await File.WriteAllTextAsync(crashedCopy, "stale");
        File.SetLastWriteTimeUtc(crashedCopy, DateTime.UtcNow.AddDays(-31));
        var cleanup = new RecordingWorkflowScratchCleanup(delete: false);
        var network = new DurableInstallerPreflightNetwork(downloader, state, cleanup);
        var previous = new DownloadResult
        {
            FilePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"),
            FileName = "setup.exe",
            Sha256 = new Sha256Hash(Convert.ToHexString(SHA256.HashData(content))),
            SizeInBytes = content.Length,
            InitialUrl = "https://example.test/setup.exe",
            FinalUrl = "https://example.test/setup.exe",
        };
        try
        {
            DownloadRevalidationResult result = await network.RevalidateAsync(
                previous,
                CancellationToken.None);

            Assert.Equal(DownloadRevalidationStatus.Unchanged, result.Status);
            Assert.False(File.Exists(crashedCopy));
            Assert.True(File.Exists(result.Result.FilePath));
            Assert.Equal(content, await File.ReadAllBytesAsync(result.Result.FilePath));
            Assert.NotEqual(cleanup.Directory, Path.GetDirectoryName(result.Result.FilePath));
            var before = new RawManifestDocument("manifests/e/Example/1.0.0/example.yaml", "x"u8);
            ValidationReport report = await new PreflightGateWorkflowAdapter(
                    new PreflightGate(),
                    network)
                .ValidateAsync(
                    new WorkflowPreflightRequest
                    {
                        BeforeDocuments = [before],
                        AfterDocuments = [],
                        Changes =
                        [
                            new WorkflowFileChange(
                                PlannedChangeKind.Delete,
                                before.RepositoryPath,
                                expectedState: ExpectedFileState.Present,
                                expectedSha256: WorkflowFileChange.Hash(before.Content.AsSpan())),
                        ],
                        Options = new PreflightOptions
                        {
                            WarningPolicy = WarningPolicy.TreatAsErrors,
                            NetworkMode = NetworkValidationMode.Skip,
                        },
                    },
                    CancellationToken.None);
            ValidationFinding diagnostic = Assert.Single(
                report.Findings,
                static finding => finding.Code == "WF_PREFLIGHT_SCRATCH_CLEANUP_SCHEDULED");
            Assert.Equal("WF_PREFLIGHT_SCRATCH_CLEANUP_SCHEDULED", diagnostic.Code);
            Assert.Equal(ValidationSeverity.Info, diagnostic.Severity);
            Assert.True(report.CanProceed(WarningPolicy.TreatAsErrors), report.ToText());
        }
        finally
        {
            if (cleanup.Directory is not null && Directory.Exists(cleanup.Directory))
            {
                Directory.Delete(cleanup.Directory, recursive: true);
            }

            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Workflow_preflight_no_store_payload_uses_a_bounded_ephemeral_lease()
    {
        byte[] content = "no-store installer"u8.ToArray();
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return response;
        });
        using var downloader = new InstallerDownloader(handler);
        string state = CreateDirectory();
        string expiredLease = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-preflight-revalidation-expired-{Guid.NewGuid():N}");
        Directory.CreateDirectory(expiredLease);
        await File.WriteAllTextAsync(
            Path.Combine(expiredLease, ".no-store-lease-v1"),
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        var cleanup = new RecordingWorkflowScratchCleanup(delete: true);
        var network = new DurableInstallerPreflightNetwork(downloader, state, cleanup);
        var previous = new DownloadResult
        {
            FilePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"),
            FileName = "setup.exe",
            Sha256 = new Sha256Hash(Convert.ToHexString(SHA256.HashData(content))),
            SizeInBytes = content.Length,
            InitialUrl = "https://example.test/setup.exe",
            FinalUrl = "https://example.test/setup.exe",
        };
        try
        {
            DownloadRevalidationResult result = await network.RevalidateAsync(
                previous,
                CancellationToken.None);

            Assert.False(result.Result.MayBeStored);
            Assert.True(File.Exists(result.Result.FilePath));
            Assert.Equal(content, await File.ReadAllBytesAsync(result.Result.FilePath));
            Assert.True(cleanup.WasScheduled);
            Assert.Empty(Directory.EnumerateFiles(state));
            Assert.False(Directory.Exists(expiredLease));
        }
        finally
        {
            if (cleanup.Directory is not null && Directory.Exists(cleanup.Directory))
            {
                Directory.Delete(cleanup.Directory, recursive: true);
            }

            Directory.Delete(state, recursive: true);
            if (Directory.Exists(expiredLease))
            {
                Directory.Delete(expiredLease, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Workflow_preflight_preserves_origin_failure_when_cleanup_is_scheduled()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("Simulated origin failure."));
        using var downloader = new InstallerDownloader(handler);
        string state = CreateDirectory();
        var cleanup = new RecordingWorkflowScratchCleanup(delete: false);
        var network = new DurableInstallerPreflightNetwork(downloader, state, cleanup);
        var previous = new DownloadResult
        {
            FilePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"),
            FileName = "setup.exe",
            Sha256 = new Sha256Hash(
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
            SizeInBytes = 1,
            InitialUrl = "https://example.test/setup.exe",
            FinalUrl = "https://example.test/setup.exe",
        };
        try
        {
            WorkflowPreflightRecoveryException exception =
                await Assert.ThrowsAsync<WorkflowPreflightRecoveryException>(
                    () => network.RevalidateAsync(previous, CancellationToken.None));

            Assert.Contains(
                "Simulated origin failure",
                exception.PrimaryException.GetBaseException().Message,
                StringComparison.Ordinal);
            AggregateException aggregate = Assert.IsType<AggregateException>(exception.InnerException);
            Assert.Same(exception.PrimaryException, aggregate.InnerExceptions[0]);
            Assert.True(exception.Cleanup.Scheduled);
        }
        finally
        {
            if (cleanup.Directory is not null && Directory.Exists(cleanup.Directory))
            {
                Directory.Delete(cleanup.Directory, recursive: true);
            }

            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Provenance_transaction_preserves_original_before_later_human_edits()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        try
        {
            var store = new FileOriginalSubmissionStore(state);
            var transaction = new AtomicWorkflowFileTransaction(store);
            (PackageManifests manifests, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");
            await transaction.ApplyAsync(output, "Example.Composed", changes, CancellationToken.None);

            string localePath = Path.Combine(
                output,
                ManifestPaths.GetVersionDirectory(
                        new PackageIdentifier("Example.Composed"),
                        new PackageVersion("1.0.0"))
                    .Replace('/', Path.DirectorySeparatorChar),
                "Example.Composed.locale.en-US.yaml");
            File.WriteAllText(
                localePath,
                File.ReadAllText(localePath).Replace(
                    "Publisher: Original Publisher",
                    "Publisher: Human Publisher",
                    StringComparison.Ordinal));

            PackageSnapshot snapshot = Assert.IsType<PackageSnapshot>(
                await new LocalManifestSnapshotSource(store).LoadAsync(
                    output,
                    new PackageIdentifier("Example.Composed"),
                    new PackageVersion("1.0.0"),
                    CancellationToken.None));

            Assert.Equal("Human Publisher", snapshot.Manifests.DefaultLocale.Publisher);
            Assert.Equal("Original Publisher", snapshot.OriginalBotSubmission!.DefaultLocale.Publisher);
            Assert.NotSame(manifests, snapshot.OriginalBotSubmission);

            System.Collections.Immutable.ImmutableArray<WorkflowFileChange> deletions =
            [
                .. Directory.EnumerateFiles(
                        Path.GetDirectoryName(localePath)!,
                        "*.yaml")
                    .Select(path =>
                    {
                        byte[] content = File.ReadAllBytes(path);
                        return new WorkflowFileChange(
                            PlannedChangeKind.Delete,
                            Path.GetRelativePath(output, path).Replace('\\', '/'),
                            expectedState: ExpectedFileState.Present,
                            expectedSha256: WorkflowFileChange.Hash(content),
                            provenance: WorkflowChangeProvenance.ToolGenerated);
                    }),
            ];
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(localePath)!, ".gitkeep"), "");
            await transaction.ApplyAsync(output, "Example.Composed", deletions, CancellationToken.None);
            Assert.Null(store.Load(
                output,
                new PackageIdentifier("Example.Composed"),
                new PackageVersion("1.0.0")));

            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> recreated) =
                CreateInitialChanges("Recreated Publisher");
            await transaction.ApplyAsync(output, "Example.Composed", recreated, CancellationToken.None);
            Assert.Equal(
                "Recreated Publisher",
                store.Load(
                    output,
                    new PackageIdentifier("Example.Composed"),
                    new PackageVersion("1.0.0"))!.DefaultLocale.Publisher);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Locale_edit_never_captures_pre_existing_human_manifests_as_bot_original()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        try
        {
            WritePrevious(output);
            var identifier = new PackageIdentifier("Example.Composed");
            var version = new PackageVersion("1.0.0");
            string versionDirectory = Path.Combine(
                output,
                ManifestPaths.GetVersionDirectory(identifier, version)
                    .Replace('/', Path.DirectorySeparatorChar));
            PackageManifests manifests = PackageManifestIO.LoadDirectory(versionDirectory);
            manifests.Locales.Add(new LocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = new LanguageTag("de-DE"),
                PackageName = "Zusammengesetzt",
                ShortDescription = "Beschreibung",
            });
            KeyValuePair<string, string> locale = PackageManifestIO.SerializeFiles(
                    manifests,
                    new ManifestWriteOptions { CreatedWith = "winmatsch test" })
                .Single(static file => file.Key.EndsWith(".locale.de-DE.yaml", StringComparison.Ordinal));
            string repositoryDirectory = ManifestPaths.GetVersionDirectory(identifier, version);
            var store = new FileOriginalSubmissionStore(state);

            await new AtomicWorkflowFileTransaction(store).ApplyAsync(
                output,
                identifier.Value,
                [
                    new WorkflowFileChange(
                        PlannedChangeKind.Add,
                        $"{repositoryDirectory}/{locale.Key}",
                        Encoding.UTF8.GetBytes(locale.Value)),
                ],
                CancellationToken.None);

            Assert.Null(store.Load(output, identifier, version));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Provenance_hash_metadata_rejects_tampered_and_ambiguous_records()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        string snapshot = CreateDirectory();
        try
        {
            var identifier = new PackageIdentifier("Example.Composed");
            var version = new PackageVersion("1.0.0");
            var store = new FileOriginalSubmissionStore(state);
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");
            await new AtomicWorkflowFileTransaction(store).ApplyAsync(
                output,
                identifier.Value,
                changes,
                CancellationToken.None);
            Assert.NotNull(store.Load(output, identifier, version));

            string storedManifest = Directory.EnumerateFiles(state, "*.yaml", SearchOption.AllDirectories)
                .First();
            await File.AppendAllTextAsync(storedManifest, "# tampered");
            Assert.Null(store.Load(output, identifier, version));

            string metadata = Directory.EnumerateFiles(state, "*", SearchOption.AllDirectories)
                .Single(static path => Path.GetFileName(path).StartsWith(
                    ".winmatsch-provenance-",
                    StringComparison.Ordinal));
            File.Delete(metadata);
            Assert.Null(store.Load(output, identifier, version));

            CommittedWorkflowPath[] committedPaths = StageProvenanceSnapshot(snapshot, changes);
            const string CaptureId = "tampered-record-recapture";
            store.PrepareCapture(output, CaptureId, snapshot, committedPaths);
            OriginalSubmissionConflictException exception =
                await Assert.ThrowsAsync<OriginalSubmissionConflictException>(
                    () => store.CaptureChangedVersionsAsync(
                        output,
                        CaptureId,
                        snapshot,
                        committedPaths,
                        CancellationToken.None));

            Assert.Equal(OriginalSubmissionConflictKind.CorruptExistingRecord, exception.Kind);
            Assert.Contains("# tampered", await File.ReadAllTextAsync(storedManifest), StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateDirectories(state, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
            Directory.Delete(snapshot, recursive: true);
        }
    }

    [Fact]
    public async Task Provenance_capture_coalesces_a_forced_same_content_publication_collision()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        string snapshot = CreateDirectory();
        int forcedCollision = 0;
        var hooks = new OriginalSubmissionCaptureHooks
        {
            BeforeFinalizeAsync = (temporary, destination, _) =>
            {
                if (Interlocked.CompareExchange(ref forcedCollision, 1, 0) == 0)
                {
                    CopyDirectory(temporary, destination);
                }

                return Task.CompletedTask;
            },
        };
        try
        {
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");
            CommittedWorkflowPath[] committedPaths = StageProvenanceSnapshot(snapshot, changes);
            var store = new FileOriginalSubmissionStore(state, hooks);
            const string CaptureId = "forced-same-content-collision";
            store.PrepareCapture(output, CaptureId, snapshot, committedPaths);

            await store.CaptureChangedVersionsAsync(
                output,
                CaptureId,
                snapshot,
                committedPaths,
                CancellationToken.None);
            store.CompleteCapture(output, CaptureId, snapshot, committedPaths);

            Assert.Equal(1, forcedCollision);
            Assert.Equal(
                "Original Publisher",
                store.Load(
                    output,
                    new PackageIdentifier("Example.Composed"),
                    new PackageVersion("1.0.0"))!.DefaultLocale.Publisher);
            Assert.Empty(Directory.EnumerateDirectories(state, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
            Directory.Delete(snapshot, recursive: true);
        }
    }

    [Fact]
    public async Task Forced_conflicting_provenance_collision_retains_committed_recovery_state()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        int forcedCollision = 0;
        var hooks = new OriginalSubmissionCaptureHooks
        {
            BeforeFinalizeAsync = async (temporary, destination, cancellationToken) =>
            {
                if (Interlocked.CompareExchange(ref forcedCollision, 1, 0) == 0)
                {
                    CopyDirectory(temporary, destination);
                    string manifest = Directory.EnumerateFiles(destination, "*.yaml").First();
                    await File.AppendAllTextAsync(
                        manifest,
                        "# conflicting publication",
                        cancellationToken);
                }
            },
        };
        try
        {
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");

            WorkflowCommittedProvenanceException exception =
                await Assert.ThrowsAsync<WorkflowCommittedProvenanceException>(
                    () => new AtomicWorkflowFileTransaction(
                            new FileOriginalSubmissionStore(state, hooks))
                        .ApplyAsync(
                            output,
                            "Example.Composed",
                            changes,
                            CancellationToken.None));

            OriginalSubmissionConflictException conflict = Assert.Single(
                EnumerateExceptions(exception).OfType<OriginalSubmissionConflictException>());
            Assert.Equal(OriginalSubmissionConflictKind.CorruptExistingRecord, conflict.Kind);
            Assert.NotEmpty(Directory.EnumerateFiles(output, "*.yaml", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.EnumerateDirectories(output, ".winmatsch-transaction-*"));
            Assert.Contains(
                "# conflicting publication",
                await File.ReadAllTextAsync(
                    Directory.EnumerateFiles(state, "*.yaml", SearchOption.AllDirectories).First()),
                StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateDirectories(state, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Provenance_capture_retries_a_windows_held_handle_without_overwriting()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string output = CreateDirectory();
        string state = CreateDirectory();
        FileStream? heldHandle = null;
        int handleCreated = 0;
        int retryCount = 0;
        var hooks = new OriginalSubmissionCaptureHooks
        {
            BeforeFinalizeAsync = (temporary, _, _) =>
            {
                if (Interlocked.CompareExchange(ref handleCreated, 1, 0) == 0)
                {
                    heldHandle = new FileStream(
                        Directory.EnumerateFiles(temporary, "*.yaml").First(),
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }

                return Task.CompletedTask;
            },
            BeforeTransientFinalizationRetryAsync = (_, retry, _) =>
            {
                retryCount = retry;
                heldHandle?.Dispose();
                heldHandle = null;
                return Task.CompletedTask;
            },
        };
        try
        {
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");

            await new AtomicWorkflowFileTransaction(new FileOriginalSubmissionStore(state, hooks))
                .ApplyAsync(output, "Example.Composed", changes, CancellationToken.None);

            Assert.True(retryCount >= 1);
            Assert.Empty(Directory.EnumerateDirectories(state, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            heldHandle?.Dispose();
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Cancelled_provenance_finalization_preserves_committed_recovery_state()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string output = CreateDirectory();
        string state = CreateDirectory();
        FileStream? heldHandle = null;
        int handleCreated = 0;
        var retryObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new OriginalSubmissionCaptureHooks
        {
            BeforeFinalizeAsync = (temporary, _, _) =>
            {
                if (Interlocked.CompareExchange(ref handleCreated, 1, 0) == 0)
                {
                    heldHandle = new FileStream(
                        Directory.EnumerateFiles(temporary, "*.yaml").First(),
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }

                return Task.CompletedTask;
            },
            BeforeTransientFinalizationRetryAsync = async (_, _, cancellationToken) =>
            {
                retryObserved.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };
        using var cancellation = new CancellationTokenSource();
        try
        {
            var store = new FileOriginalSubmissionStore(state, hooks);
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");
            Task apply = new AtomicWorkflowFileTransaction(store)
                .ApplyAsync(output, "Example.Composed", changes, cancellation.Token);
            await retryObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            WorkflowCommittedProvenanceException exception =
                await Assert.ThrowsAsync<WorkflowCommittedProvenanceException>(() => apply);
            Assert.Contains(
                EnumerateExceptions(exception),
                static failure => failure is OperationCanceledException);
            Assert.NotEmpty(Directory.EnumerateFiles(output, "*.yaml", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.EnumerateDirectories(output, ".winmatsch-transaction-*"));

            heldHandle!.Dispose();
            heldHandle = null;
            PackageSnapshot recovered = Assert.IsType<PackageSnapshot>(
                await new LocalManifestSnapshotSource(store).LoadAsync(
                    output,
                    new PackageIdentifier("Example.Composed"),
                    new PackageVersion("1.0.0"),
                    CancellationToken.None));

            Assert.Equal("Original Publisher", recovered.OriginalBotSubmission!.DefaultLocale.Publisher);
            Assert.Empty(Directory.EnumerateDirectories(output, ".winmatsch-transaction-*"));
            Assert.Empty(Directory.EnumerateDirectories(state, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            heldHandle?.Dispose();
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_same_content_provenance_captures_coalesce()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        string snapshot = CreateDirectory();
        var firstAtFinalize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStore = new FileOriginalSubmissionStore(
            state,
            new OriginalSubmissionCaptureHooks
            {
                BeforeFinalizeAsync = async (_, _, cancellationToken) =>
                {
                    firstAtFinalize.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                },
            });
        var secondStore = new FileOriginalSubmissionStore(
            state,
            new OriginalSubmissionCaptureHooks
            {
                BeforeDestinationLockWait = _ => secondWaiting.TrySetResult(),
            });
        try
        {
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");
            CommittedWorkflowPath[] committedPaths = StageProvenanceSnapshot(snapshot, changes);
            const string CaptureId = "concurrent-same-content";
            firstStore.PrepareCapture(output, CaptureId, snapshot, committedPaths);

            Task first = firstStore.CaptureChangedVersionsAsync(
                output,
                CaptureId,
                snapshot,
                committedPaths,
                CancellationToken.None);
            await firstAtFinalize.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task second = secondStore.CaptureChangedVersionsAsync(
                output,
                CaptureId,
                snapshot,
                committedPaths,
                CancellationToken.None);
            await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(second.IsCompleted);

            releaseFirst.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            firstStore.CompleteCapture(output, CaptureId, snapshot, committedPaths);

            Assert.Equal(
                "Original Publisher",
                firstStore.Load(
                    output,
                    new PackageIdentifier("Example.Composed"),
                    new PackageVersion("1.0.0"))!.DefaultLocale.Publisher);
            Assert.Empty(Directory.EnumerateDirectories(state, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            releaseFirst.TrySetResult();
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
            Directory.Delete(snapshot, recursive: true);
        }
    }

    [Fact]
    public async Task Waiting_provenance_capture_honors_cancellation()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        string snapshot = CreateDirectory();
        var firstAtFinalize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStore = new FileOriginalSubmissionStore(
            state,
            new OriginalSubmissionCaptureHooks
            {
                BeforeFinalizeAsync = async (_, _, cancellationToken) =>
                {
                    firstAtFinalize.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                },
            });
        var secondStore = new FileOriginalSubmissionStore(
            state,
            new OriginalSubmissionCaptureHooks
            {
                BeforeDestinationLockWait = _ => secondWaiting.TrySetResult(),
            });
        using var cancellation = new CancellationTokenSource();
        try
        {
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");
            CommittedWorkflowPath[] committedPaths = StageProvenanceSnapshot(snapshot, changes);
            const string CaptureId = "cancelled-waiter";
            firstStore.PrepareCapture(output, CaptureId, snapshot, committedPaths);

            Task first = firstStore.CaptureChangedVersionsAsync(
                output,
                CaptureId,
                snapshot,
                committedPaths,
                CancellationToken.None);
            await firstAtFinalize.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task second = secondStore.CaptureChangedVersionsAsync(
                output,
                CaptureId,
                snapshot,
                committedPaths,
                cancellation.Token);
            await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            releaseFirst.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            firstStore.CompleteCapture(output, CaptureId, snapshot, committedPaths);

            Assert.Empty(Directory.EnumerateDirectories(state, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            releaseFirst.TrySetResult();
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
            Directory.Delete(snapshot, recursive: true);
        }
    }

    [Fact]
    public async Task Conflicting_provenance_capture_fails_without_overwriting_the_original()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        string firstSnapshot = CreateDirectory();
        string conflictingSnapshot = CreateDirectory();
        try
        {
            var store = new FileOriginalSubmissionStore(state);
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> originalChanges) =
                CreateInitialChanges("Original Publisher");
            CommittedWorkflowPath[] originalPaths = StageProvenanceSnapshot(firstSnapshot, originalChanges);
            store.PrepareCapture(output, "original-content", firstSnapshot, originalPaths);
            await store.CaptureChangedVersionsAsync(
                output,
                "original-content",
                firstSnapshot,
                originalPaths,
                CancellationToken.None);
            store.CompleteCapture(output, "original-content", firstSnapshot, originalPaths);

            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> conflictingChanges) =
                CreateInitialChanges("Conflicting Publisher");
            CommittedWorkflowPath[] conflictingPaths =
                StageProvenanceSnapshot(conflictingSnapshot, conflictingChanges);
            store.PrepareCapture(output, "conflicting-content", conflictingSnapshot, conflictingPaths);

            OriginalSubmissionConflictException exception =
                await Assert.ThrowsAsync<OriginalSubmissionConflictException>(
                    () => store.CaptureChangedVersionsAsync(
                        output,
                        "conflicting-content",
                        conflictingSnapshot,
                        conflictingPaths,
                        CancellationToken.None));

            Assert.Equal(OriginalSubmissionConflictKind.ContentMismatch, exception.Kind);
            Assert.Equal(
                "Original Publisher",
                store.Load(
                    output,
                    new PackageIdentifier("Example.Composed"),
                    new PackageVersion("1.0.0"))!.DefaultLocale.Publisher);
            Assert.Empty(Directory.EnumerateDirectories(state, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
            Directory.Delete(firstSnapshot, recursive: true);
            Directory.Delete(conflictingSnapshot, recursive: true);
        }
    }

    [Fact]
    public async Task Provenance_accepts_custom_tool_attribution_when_transaction_attestation_is_valid()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        try
        {
            var identifier = new PackageIdentifier("Example.Composed");
            var version = new PackageVersion("1.0.0");
            var store = new FileOriginalSubmissionStore(state);
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher", "custom generator");

            await new AtomicWorkflowFileTransaction(store).ApplyAsync(
                output,
                identifier.Value,
                changes,
                CancellationToken.None);

            Assert.Equal(
                "Original Publisher",
                store.Load(output, identifier, version)!.DefaultLocale.Publisher);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Forged_repository_journal_cannot_delete_trusted_provenance()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        try
        {
            var identifier = new PackageIdentifier("Example.Composed");
            var version = new PackageVersion("1.0.0");
            var store = new FileOriginalSubmissionStore(state);
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");
            await new AtomicWorkflowFileTransaction(store).ApplyAsync(
                output,
                identifier.Value,
                changes,
                CancellationToken.None);
            Assert.NotNull(store.Load(output, identifier, version));

            string packageKey = identifier.Value.ToUpperInvariant();
            string prefix =
                $".winmatsch-transaction-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageKey)))[..16]}";
            string forged = Path.Combine(output, $"{prefix}-forged");
            string repositoryPath =
                $"{ManifestPaths.GetVersionDirectory(identifier, version)}/{ManifestPaths.GetVersionFileName(identifier)}";
            Directory.CreateDirectory(Path.Combine(forged, "provenance"));
            await File.WriteAllTextAsync(
                Path.Combine(forged, "journal"),
                $"manifests-committed{Environment.NewLine}"
                + $"Delete|1|{Convert.ToBase64String(Encoding.UTF8.GetBytes(repositoryPath))}|ToolGenerated"
                + Environment.NewLine);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new LocalManifestSnapshotSource(store).LoadAsync(
                    output,
                    identifier,
                    version,
                    CancellationToken.None));

            Assert.NotNull(store.Load(output, identifier, version));
            Directory.Delete(forged, recursive: true);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Provenance_failure_reports_committed_state_and_is_recovered_from_journal()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        try
        {
            var transaction = new AtomicWorkflowFileTransaction(new ThrowingOriginalSubmissionStore(state));
            (_, System.Collections.Immutable.ImmutableArray<WorkflowFileChange> changes) =
                CreateInitialChanges("Original Publisher");

            WorkflowCommittedProvenanceException exception =
                await Assert.ThrowsAsync<WorkflowCommittedProvenanceException>(() =>
                    transaction.ApplyAsync(
                        output,
                        "Example.Composed",
                        changes,
                        CancellationToken.None));

            Assert.Contains("committed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(Directory.EnumerateFiles(output, "*.yaml", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.EnumerateDirectories(output, ".winmatsch-transaction-*"));
            string localePath = Path.Combine(
                output,
                ManifestPaths.GetVersionDirectory(
                        new PackageIdentifier("Example.Composed"),
                        new PackageVersion("1.0.0"))
                    .Replace('/', Path.DirectorySeparatorChar),
                "Example.Composed.locale.en-US.yaml");
            File.WriteAllText(
                localePath,
                File.ReadAllText(localePath).Replace(
                    "Publisher: Original Publisher",
                    "Publisher: Human After Commit",
                    StringComparison.Ordinal));

            PackageSnapshot recovered = Assert.IsType<PackageSnapshot>(
                await new LocalManifestSnapshotSource(new FileOriginalSubmissionStore(state)).LoadAsync(
                    output,
                    new PackageIdentifier("Example.Composed"),
                    new PackageVersion("1.0.0"),
                    CancellationToken.None));
            Assert.Equal("Human After Commit", recovered.Manifests.DefaultLocale.Publisher);
            Assert.Equal("Original Publisher", recovered.OriginalBotSubmission!.DefaultLocale.Publisher);
            Assert.Empty(Directory.EnumerateDirectories(output, ".winmatsch-transaction-*"));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
        }
    }

    private static PackageLocaleMetadata Locale() => new()
    {
        PackageLocale = new LanguageTag("en-US"),
        Publisher = "Example",
        PackageName = "Composed",
        License = "MIT",
        ShortDescription = "Composition test",
    };

    private static void WritePrevious(
        string output,
        bool scopedTwins = false,
        bool staleDirectNestedState = false)
    {
        var identifier = new PackageIdentifier("Example.Composed");
        var version = new PackageVersion("1.0.0");
        var manifests = new PackageManifests
        {
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = new LanguageTag("en-US"),
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                InstallerType = InstallerType.Portable,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerUrl = "https://example.test/setup.exe",
                        InstallerSha256 = new Sha256Hash(
                            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = new LanguageTag("en-US"),
                Publisher = "Example",
                PackageName = "Composed",
                License = "MIT",
                ShortDescription = "Composition test",
            },
            Locales = [],
        };
        if (scopedTwins)
        {
            Installer first = Assert.Single(manifests.Installer.Installers!);
            first.Scope = Scope.User;
            first.InstallerSwitches = new InstallerSwitches { Custom = "/CURRENTUSER" };
            manifests.Installer.Installers.Add(new Installer
            {
                Architecture = Architecture.X64,
                Scope = Scope.Machine,
                InstallerUrl = first.InstallerUrl,
                InstallerSha256 = first.InstallerSha256,
                InstallerSwitches = new InstallerSwitches { Custom = "/ALLUSERS" },
            });
        }

        if (staleDirectNestedState)
        {
            manifests.Installer.NestedInstallerType = InstallerType.Portable;
            manifests.Installer.NestedInstallerFiles =
            [
                new NestedInstallerFile
                {
                    RelativeFilePath = "setup.exe",
                    PortableCommandAlias = "setup",
                },
            ];
            manifests.Installer.ArchiveBinariesDependOnPath = true;
        }

        string directory = Path.Combine(
            output,
            ManifestPaths.GetVersionDirectory(identifier, version).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        foreach ((string fileName, string content) in PackageManifestIO.SerializeFiles(manifests))
        {
            File.WriteAllText(Path.Combine(directory, fileName), content);
        }
    }

    private static void RewriteManifestVersion(string output, string version)
    {
        foreach (string path in Directory.EnumerateFiles(output, "*.yaml", SearchOption.AllDirectories))
        {
            File.WriteAllText(
                path,
                File.ReadAllText(path).Replace(
                    ManifestVersion.Default.Value,
                    version,
                    StringComparison.Ordinal));
        }
    }

    private static GitHubSubmissionRequest RequestWithArtifact(
            string url,
            byte[] content,
            string missingPath)
    {
        var download = new DownloadResult
        {
            FilePath = missingPath,
            FileName = "setup.exe",
            Sha256 = new Sha256Hash(Convert.ToHexString(SHA256.HashData(content))),
            SizeInBytes = content.Length,
            InitialUrl = url,
            FinalUrl = url,
        };
        LocalOperationPlan plan = GitHubLifecycleTestSupport.Plan();
        WorkflowPreflightRequest preflight = new()
        {
            BeforeDocuments = plan.Preflight.BeforeDocuments,
            AfterDocuments = plan.Preflight.AfterDocuments,
            Changes = plan.Preflight.Changes,
            InstallerArtifacts = [new InstallerArtifact(url, download)],
            Options = plan.Preflight.Options,
        };
        return GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan) with
        {
            LocalPlan = plan with { Preflight = preflight },
        };
    }

    private static GitHubSubmissionRequest RawNestedZipSubmissionRequest(
        string output,
        string sourceArtifactPath,
        byte[] archive,
        string? approvedFinalUrl = null)
    {
        var identifier = new PackageIdentifier("RoslynPad.RoslynPad");
        var version = new PackageVersion("22.1");
        var manifests = new PackageManifests
        {
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = new LanguageTag("en-US"),
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                InstallerType = InstallerType.Zip,
                NestedInstallerType = InstallerType.Portable,
                NestedInstallerFiles =
                [
                    new NestedInstallerFile
                    {
                        RelativeFilePath = "RoslynPad.exe",
                        PortableCommandAlias = "RoslynPad",
                    },
                ],
                ArchiveBinariesDependOnPath = true,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerUrl = NestedZipInstallerUrl,
                        InstallerSha256 = new Sha256Hash(
                            Convert.ToHexString(SHA256.HashData(archive))),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = new LanguageTag("en-US"),
                Publisher = "RoslynPad",
                PackageName = "RoslynPad",
                License = "MIT",
                ShortDescription = "Production-shaped journal recovery regression.",
            },
            Locales = [],
        };
        string directory = ManifestPaths.GetVersionDirectory(identifier, version);
        ImmutableArray<RawManifestDocument> documents =
        [
            .. PackageManifestIO.SerializeFiles(manifests)
                .Select(file => new RawManifestDocument(
                    $"{directory}/{file.Key}",
                    Encoding.UTF8.GetBytes(file.Value))),
        ];
        ImmutableArray<WorkflowFileChange> changes =
        [
            .. documents.Select(document => new WorkflowFileChange(
                PlannedChangeKind.Add,
                document.RepositoryPath,
                document.Content.AsSpan(),
                provenance: WorkflowChangeProvenance.Untrusted)),
        ];
        var download = new DownloadResult
        {
            FilePath = sourceArtifactPath,
            FileName = sourceArtifactPath,
            Sha256 = new Sha256Hash(Convert.ToHexString(SHA256.HashData(archive))),
            SizeInBytes = archive.LongLength,
            InitialUrl = NestedZipInstallerUrl,
            FinalUrl = approvedFinalUrl ?? NestedZipInstallerUrl,
        };
        var preflight = new WorkflowPreflightRequest
        {
            BeforeDocuments = [],
            AfterDocuments = documents,
            Changes = changes,
            InstallerArtifacts = [new InstallerArtifact(NestedZipInstallerUrl, download)],
            Options = new PreflightOptions
            {
                NetworkMode = NetworkValidationMode.Online,
            },
        };
        var plan = new LocalOperationPlan
        {
            Operation = "submit",
            PackageIdentifier = identifier,
            PackageVersion = version,
            OutputDirectory = output,
            FileChanges = changes,
            BeforeDocuments = [],
            AfterDocuments = documents,
            Validation = new ValidationReport(),
            Preflight = preflight,
            Rules = RuleRunSummary.Empty,
        };
        return new GitHubSubmissionRequest
        {
            LocalPlan = plan,
            UpstreamRepository = GitHubLifecycleTestSupport.Upstream,
            TargetRepository = GitHubLifecycleTestSupport.Fork,
            ExecutionMode = WorkflowExecutionMode.Apply,
            Operation = GitHubManifestOperation.Add,
            Policy = new GitHubSubmissionPolicy
            {
                MinimumReleaseFreshness = TimeSpan.Zero,
            },
            IdempotencyKey = "raw-submit:RoslynPad.RoslynPad:22.1",
            CreatedWith = "winmatsch regression test",
        };
    }

    private static HttpResponseMessage RedirectingArchiveResponse(
        HttpRequestMessage request,
        byte[] archive,
        string finalUrl)
    {
        if (string.Equals(
                request.RequestUri?.AbsoluteUri,
                NestedZipInstallerUrl,
                StringComparison.Ordinal))
        {
            return new(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri(finalUrl) },
            };
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive),
        };
        response.Headers.CacheControl = new() { NoStore = true };
        return response;
    }

    private static byte[] CreateZipArchive(string path, ReadOnlySpan<byte> content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(path);
            using Stream stream = entry.Open();
            stream.Write(content);
        }

        return buffer.ToArray();
    }

    private static void WriteCommittedChanges(LocalOperationPlan plan)
    {
        foreach (WorkflowFileChange change in plan.FileChanges)
        {
            string path = Path.Combine(
                plan.OutputDirectory,
                change.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, change.Content.ToArray());
        }
    }

    private static (
            PackageManifests Manifests,
            System.Collections.Immutable.ImmutableArray<WorkflowFileChange> Changes)
            CreateInitialChanges(
                string publisher,
                string createdWith = "winmatsch test")
    {
        var identifier = new PackageIdentifier("Example.Composed");
        var version = new PackageVersion("1.0.0");
        PackageManifests manifests = new()
        {
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = new LanguageTag("en-US"),
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                InstallerType = InstallerType.Exe,
                Installers =
                [
                    new Installer
                        {
                            Architecture = Architecture.X64,
                            InstallerUrl = "https://example.test/setup.exe",
                            InstallerSha256 = new Sha256Hash(
                                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                        },
                    ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = new LanguageTag("en-US"),
                Publisher = publisher,
                PackageName = "Composed",
                License = "MIT",
                ShortDescription = "Composition test",
            },
            Locales = [],
        };
        string directory = ManifestPaths.GetVersionDirectory(identifier, version);
        return (
            manifests,
            [
                .. PackageManifestIO.SerializeFiles(
                        manifests,
                        new ManifestWriteOptions { CreatedWith = createdWith })
                    .Select(file => new WorkflowFileChange(
                        PlannedChangeKind.Add,
                        $"{directory}/{file.Key}",
                        Encoding.UTF8.GetBytes(file.Value),
                        provenance: WorkflowChangeProvenance.ToolGenerated)),
            ]);
    }

    private static CommittedWorkflowPath[] StageProvenanceSnapshot(
        string snapshot,
        IEnumerable<WorkflowFileChange> changes)
    {
        WorkflowFileChange[] materialized = [.. changes];
        foreach (WorkflowFileChange change in materialized.Where(static change =>
                     change.Kind != PlannedChangeKind.Delete))
        {
            string path = Path.Combine(
                snapshot,
                change.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, change.Content.ToArray());
        }

        return
        [
            .. materialized.Select(static change => new CommittedWorkflowPath(
                change.Kind,
                change.RepositoryPath,
                change.Provenance)),
        ];
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions.SelectMany(EnumerateExceptions))
            {
                yield return inner;
            }
        }
        else if (exception.InnerException is { } inner)
        {
            foreach (Exception nested in EnumerateExceptions(inner))
            {
                yield return nested;
            }
        }
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "winmatsch-composition-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ThrowingOriginalSubmissionStore(string stateDirectory) : IOriginalSubmissionStore
    {
        private readonly FileOriginalSubmissionStore _inner = new(stateDirectory);

        public PackageManifests? Load(
            string outputDirectory,
            PackageIdentifier packageIdentifier,
            PackageVersion packageVersion)
            => _inner.Load(outputDirectory, packageIdentifier, packageVersion);

        public void PrepareCapture(
            string outputDirectory,
            string captureId,
            string snapshotDirectory,
            IReadOnlyList<CommittedWorkflowPath> changes)
            => _inner.PrepareCapture(outputDirectory, captureId, snapshotDirectory, changes);

        public bool IsCapturePrepared(
            string outputDirectory,
            string captureId,
            string snapshotDirectory,
            IReadOnlyList<CommittedWorkflowPath> changes)
            => _inner.IsCapturePrepared(outputDirectory, captureId, snapshotDirectory, changes);

        public void CaptureChangedVersions(
            string outputDirectory,
            string captureId,
            string snapshotDirectory,
            IReadOnlyList<CommittedWorkflowPath> changes)
            => throw new IOException("Simulated provenance write failure.");

        public void CompleteCapture(
            string outputDirectory,
            string captureId,
            string snapshotDirectory,
            IReadOnlyList<CommittedWorkflowPath> changes)
            => _inner.CompleteCapture(outputDirectory, captureId, snapshotDirectory, changes);
    }

    private sealed class RecordingWorkflowScratchCleanup(bool delete) : IWorkflowScratchCleanup
    {
        public string? Directory { get; private set; }

        public bool WasScheduled { get; private set; }

        public WorkflowScratchCleanupState Cleanup(string directory)
        {
            Directory = directory;
            if (delete && System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
                return WorkflowScratchCleanupState.Completed;
            }

            return new(
                Scheduled: true,
                "Scratch cleanup was scheduled for the test.");
        }

        public WorkflowScratchCleanupState Schedule(string directory, TimeSpan retention)
        {
            Directory = directory;
            WasScheduled = true;
            return new(
                Scheduled: true,
                $"Scratch cleanup was scheduled after {retention.TotalMinutes:0} minutes.");
        }
    }
}
