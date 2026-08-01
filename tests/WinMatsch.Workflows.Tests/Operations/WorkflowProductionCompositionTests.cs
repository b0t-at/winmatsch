using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
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

    [Fact]
    public async Task Production_local_engine_passes_previous_manifest_on_update()
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
                            expectedSha256: WorkflowFileChange.Hash(content));
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
    public async Task Provenance_failure_reports_committed_state_and_is_recovered_from_journal()
    {
        string output = CreateDirectory();
        string state = CreateDirectory();
        try
        {
            var transaction = new AtomicWorkflowFileTransaction(new ThrowingOriginalSubmissionStore());
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

            PackageSnapshot recovered = Assert.IsType<PackageSnapshot>(
                await new LocalManifestSnapshotSource(new FileOriginalSubmissionStore(state)).LoadAsync(
                    output,
                    new PackageIdentifier("Example.Composed"),
                    new PackageVersion("1.0.0"),
                    CancellationToken.None));
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

    private static void WritePrevious(string output)
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
        string directory = Path.Combine(
            output,
            ManifestPaths.GetVersionDirectory(identifier, version).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        foreach ((string fileName, string content) in PackageManifestIO.SerializeFiles(manifests))
        {
            File.WriteAllText(Path.Combine(directory, fileName), content);
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

    private static (
            PackageManifests Manifests,
            System.Collections.Immutable.ImmutableArray<WorkflowFileChange> Changes)
            CreateInitialChanges(string publisher)
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
                .. PackageManifestIO.SerializeFiles(manifests).Select(file => new WorkflowFileChange(
                        PlannedChangeKind.Add,
                        $"{directory}/{file.Key}",
                        Encoding.UTF8.GetBytes(file.Value))),
            ]);
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

    private sealed class ThrowingOriginalSubmissionStore : IOriginalSubmissionStore
    {
        public PackageManifests? Load(
            string outputDirectory,
            PackageIdentifier packageIdentifier,
            PackageVersion packageVersion)
            => null;

        public void CaptureChangedVersions(
            string outputDirectory,
            IReadOnlyList<CommittedWorkflowPath> changes)
            => throw new IOException("Simulated provenance write failure.");
    }
}
