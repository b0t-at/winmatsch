using System.Collections.Immutable;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Rules;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.Operations;

public sealed class SubmissionRecoveryContractTests
{
    [Fact]
    public void Fingerprint_is_length_delimited_null_sensitive_and_ignores_created_timestamp()
    {
        LocalOperationPlan baseline = Plan();
        LocalOperationPlan timestampChanged = baseline with
        {
            Audit =
            [
                new("CREATED_AT", "2099-01-01T00:00:00Z", "workflow-clock"),
            ],
        };
        LocalOperationPlan nullValue = baseline with
        {
            Audit = [new("A", "ab", null), new("B", "c", null)],
        };
        LocalOperationPlan emptyValue = baseline with
        {
            Audit = [new("A", "ab", ""), new("B", "c", null)],
        };
        LocalOperationPlan ambiguousConcatenation = baseline with
        {
            Audit = [new("A", "a", null), new("B", "bc", null)],
        };

        Assert.Equal(baseline.Fingerprint, timestampChanged.Fingerprint);
        Assert.NotEqual(nullValue.Fingerprint, emptyValue.Fingerprint);
        Assert.NotEqual(nullValue.Fingerprint, ambiguousConcatenation.Fingerprint);
    }

    [Fact]
    public void Fingerprint_changes_for_rule_configuration_validation_and_artifact_evidence()
    {
        LocalOperationPlan baseline = Plan();
        LocalOperationPlan ruleChanged = baseline with
        {
            Rules = new(
                [new RuleExecution("RULE_X", RuleMode.Apply, RuleModeSource.CommandOverride)],
                [],
                [],
                [],
                []),
        };
        LocalOperationPlan validationChanged = baseline with
        {
            Validation = new ValidationReport(
                [new ValidationFinding("V1", ValidationSeverity.Warning, "warning")]),
        };
        LocalOperationPlan preflightChanged = baseline with
        {
            Preflight = baseline.Preflight with
            {
                Options = new PreflightOptions
                {
                    NetworkMode = NetworkValidationMode.Online,
                    WarningPolicy = WarningPolicy.TreatAsErrors,
                },
            },
        };
        LocalOperationPlan artifactChanged = baseline with
        {
            Preflight = baseline.Preflight with
            {
                InstallerArtifacts =
                [
                    new(
                        "https://example.test/app.exe",
                        new DownloadResult
                        {
                            FilePath = "ignored.exe",
                            FileName = "app.exe",
                            Sha256 = new Sha256Hash(
                                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                            SizeInBytes = 42,
                            RetrievedAt = DateTimeOffset.UtcNow,
                            InitialUrl = "https://example.test/app.exe",
                            FinalUrl = "https://example.test/app.exe",
                        }),
                ],
            },
        };

        Assert.NotEqual(baseline.Fingerprint, ruleChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, validationChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, preflightChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, artifactChanged.Fingerprint);
    }

    [Fact]
    public void Fingerprint_binds_preflight_documents_and_changes_to_exact_commit_bytes()
    {
        LocalOperationPlan baseline = Plan();
        WorkflowFileChange change = baseline.FileChanges[0];
        LocalOperationPlan documentChanged = baseline with
        {
            Preflight = baseline.Preflight with
            {
                AfterDocuments =
                [
                    new RawManifestDocument(change.RepositoryPath, "different document bytes"u8),
                ],
            },
        };
        LocalOperationPlan changeChanged = baseline with
        {
            Preflight = baseline.Preflight with
            {
                Changes =
                [
                    new WorkflowFileChange(
                        change.Kind,
                        change.RepositoryPath,
                        "different commit bytes"u8,
                        change.ExpectedState,
                        change.ExpectedSha256,
                        change.Provenance),
                ],
            },
        };

        Assert.NotEqual(
            LocalOperationPlanFingerprint.CreatePreflightFingerprint(baseline.Preflight),
            LocalOperationPlanFingerprint.CreatePreflightFingerprint(documentChanged.Preflight));
        Assert.NotEqual(
            LocalOperationPlanFingerprint.CreatePreflightFingerprint(baseline.Preflight),
            LocalOperationPlanFingerprint.CreatePreflightFingerprint(changeChanged.Preflight));
        Assert.NotEqual(baseline.Fingerprint, documentChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, changeChanged.Fingerprint);
    }

    [Fact]
    public async Task Verified_apply_rejects_a_plan_mutated_after_approval()
    {
        using var repository = new TemporaryDirectory();
        var source = new MutableSnapshotSource(Snapshot("one"));
        var transaction = new RecordingTransaction();
        var engine = new LocalWorkflowEngine(
            source,
            new EmptyRuleRunner(),
            new PassingPreflight(),
            transaction,
            planLocks: new ImmediateLocalLockProvider());
        var request = new RemoveOperationRequest
        {
            OutputDirectory = repository.Path,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PackageVersion = new PackageVersion("1.0.0"),
        };
        WorkflowOperationResult planned = await engine.RemoveAsync(request);
        source.Snapshot = Snapshot("two");

        WorkflowOperationResult result = await engine.ApplyVerifiedPlanAsync(
            request,
            planned.Plan.Fingerprint);

        Assert.Equal(WorkflowResultCode.StalePlan, result.Code);
        Assert.False(result.Applied);
        Assert.Equal(0, transaction.ApplyCount);
    }

    [Fact]
    public async Task Verified_apply_rechecks_final_preflight_inside_transaction_boundary()
    {
        using var repository = new TemporaryDirectory();
        var source = new MutableSnapshotSource(Snapshot("one"));
        var transaction = new RecordingTransaction();
        var engine = new LocalWorkflowEngine(
            source,
            new EmptyRuleRunner(),
            new ChangingVerifiedPreflight(),
            transaction,
            planLocks: new ImmediateLocalLockProvider());
        var request = new RemoveOperationRequest
        {
            OutputDirectory = repository.Path,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PackageVersion = new PackageVersion("1.0.0"),
        };
        WorkflowOperationResult planned = await engine.RemoveAsync(request);

        WorkflowOperationResult result = await engine.ApplyVerifiedPlanAsync(
            request,
            planned.Plan.Fingerprint);

        Assert.Equal(WorkflowResultCode.StalePlan, result.Code);
        Assert.False(result.Applied);
        Assert.Equal(0, transaction.ApplyCount);
    }

    private static LocalOperationPlan Plan()
    {
        var package = new PackageIdentifier("Example.App");
        var version = new PackageVersion("1.0.0");
        string path = $"{ManifestPaths.GetVersionDirectory(package, version)}/Example.App.yaml";
        var document = new RawManifestDocument(path, "PackageIdentifier: Example.App\n"u8);
        var change = new WorkflowFileChange(
            PlannedChangeKind.Add,
            path,
            document.Content.AsSpan());
        return new()
        {
            Operation = "new",
            PackageIdentifier = package,
            PackageVersion = version,
            OutputDirectory = Path.GetTempPath(),
            FileChanges = [change],
            BeforeDocuments = [],
            AfterDocuments = [document],
            Validation = new ValidationReport(),
            Preflight = new()
            {
                BeforeDocuments = [],
                AfterDocuments = [document],
                Changes = [change],
                Options = new PreflightOptions { NetworkMode = NetworkValidationMode.Skip },
            },
            Rules = RuleRunSummary.Empty,
        };
    }

    private static PackageSnapshot Snapshot(string value)
    {
        var package = new PackageIdentifier("Example.App");
        var version = new PackageVersion("1.0.0");
        string path = $"{ManifestPaths.GetVersionDirectory(package, version)}/Example.App.yaml";
        return new()
        {
            PackageIdentifier = package,
            PackageVersion = version,
            VersionDirectory = ManifestPaths.GetVersionDirectory(package, version),
            Manifests = new PackageManifests
            {
                Version = new VersionManifest
                {
                    PackageIdentifier = package,
                    PackageVersion = version,
                },
                Installer = new InstallerManifest
                {
                    PackageIdentifier = package,
                    PackageVersion = version,
                },
                DefaultLocale = new DefaultLocaleManifest
                {
                    PackageIdentifier = package,
                    PackageVersion = version,
                },
                Locales = [],
            },
            Documents = [new RawManifestDocument(path, Encoding.UTF8.GetBytes(value))],
        };
    }

    private sealed class MutableSnapshotSource(PackageSnapshot snapshot) : IManifestSnapshotSource
    {
        public PackageSnapshot Snapshot { get; set; } = snapshot;

        public Task<PackageSnapshot?> LoadAsync(
            string outputDirectory,
            PackageIdentifier packageIdentifier,
            PackageVersion packageVersion,
            CancellationToken cancellationToken)
            => Task.FromResult<PackageSnapshot?>(Snapshot);

        public Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
            string outputDirectory,
            PackageIdentifier packageIdentifier,
            CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray.Create(Snapshot));
    }

    private sealed class EmptyRuleRunner : IWorkflowRuleRunner
    {
        public WorkflowRuleResult Run(WorkflowRuleRequest request)
            => new(request.Manifests, RuleRunSummary.Empty);
    }

    private sealed class PassingPreflight : IWorkflowVerifiedPreflight
    {
        public Task<ValidationReport> ValidateAsync(
            WorkflowPreflightRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new ValidationReport());

        public async Task<ValidationReport> ExecuteAsync(
            WorkflowPreflightRequest request,
            Func<CancellationToken, Task> boundary,
            CancellationToken cancellationToken)
        {
            await boundary(cancellationToken);
            return new ValidationReport();
        }

        public async Task<ValidationReport> ExecuteVerifiedAsync(
            WorkflowPreflightRequest request,
            Func<ValidationReport, CancellationToken, Task> boundary,
            CancellationToken cancellationToken)
        {
            var report = new ValidationReport();
            await boundary(report, cancellationToken);
            return report;
        }
    }

    private sealed class ChangingVerifiedPreflight : IWorkflowVerifiedPreflight
    {
        public Task<ValidationReport> ValidateAsync(
            WorkflowPreflightRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new ValidationReport());

        public async Task<ValidationReport> ExecuteAsync(
            WorkflowPreflightRequest request,
            Func<CancellationToken, Task> boundary,
            CancellationToken cancellationToken)
        {
            await boundary(cancellationToken);
            return Changed();
        }

        public async Task<ValidationReport> ExecuteVerifiedAsync(
            WorkflowPreflightRequest request,
            Func<ValidationReport, CancellationToken, Task> boundary,
            CancellationToken cancellationToken)
        {
            ValidationReport report = Changed();
            await boundary(report, cancellationToken);
            return report;
        }

        private static ValidationReport Changed()
            => new(
                [new ValidationFinding("CHANGED", ValidationSeverity.Warning, "changed")]);
    }

    private sealed class RecordingTransaction : IWorkflowFileTransaction
    {
        public int ApplyCount { get; private set; }

        public Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateLocalLockProvider : ILocalOperationLockProvider
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(
            string outputDirectory,
            PackageIdentifier packageIdentifier,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IAsyncDisposable>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"winmatsch-submission-contract-{Guid.NewGuid():N}");
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
