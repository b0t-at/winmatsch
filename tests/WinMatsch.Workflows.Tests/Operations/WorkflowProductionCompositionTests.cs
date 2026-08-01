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
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            Directory.Delete(state, recursive: true);
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
