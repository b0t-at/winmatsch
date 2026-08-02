using System.Collections.Immutable;
using WinMatsch.Cli.Hosting;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
using WinMatsch.Validation;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Configuration;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Cli.Commands.Mutations;

public sealed class ProductionMutationWorkflowTests
{
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
    public async Task Resume_surfaces_recovery_diagnostics_when_no_candidate_exists()
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
        });
        var workflow = new ProductionSubmissionWorkflow(
            new WinMatschConfiguration
            {
                Repository = new RepositoryCoordinates("microsoft", "winget-pkgs"),
                ConcurrentDownloads = 2,
                EnabledRules = [],
                DisabledRules = [],
                CacheEnabled = false,
                FreshnessDelay = TimeSpan.FromHours(4),
                OutputFormat = OutputFormat.Text,
                OutputDirectory = temporary.Path,
                Interaction = InteractionMode.Always,
            },
            new GitHubToken("test-token"),
            new GitHubClientOptions(),
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
