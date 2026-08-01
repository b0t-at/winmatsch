using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Analysis.Tests;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Validation;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.E2E.Tests;

public sealed class GoldenLocalWorkflowTests
{
    private static readonly PackageIdentifier _package = new("Example.Golden");
    private static readonly PackageVersion _version = new("1.0.0");

    [Fact]
    public async Task Production_composition_runs_golden_new_locale_update_and_remove_lifecycle()
    {
        byte[] installer = BuildMsix();
        byte[] updatedInstaller = BuildMsix("2.0.0.0");
        var handler = new StaticHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(
                request.RequestUri!.AbsolutePath.EndsWith("golden-v2.msix", StringComparison.Ordinal)
                    ? updatedInstaller
                    : installer),
            Headers = { ETag = new("\"golden-msix\"") },
        });
        using var downloader = new InstallerDownloader(handler);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            new DirectWorkflowReleaseSource(),
            new FixedClock());
        using var temporary = new TemporaryDirectory();
        NewOperationRequest request = NewRequest(temporary.Path);

        WorkflowOperationResult plan = await engine.NewAsync(request);

        Assert.True(
            plan.Code == WorkflowResultCode.Succeeded,
            Describe(plan));
        Assert.False(plan.Applied);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
        Assert.NotEmpty(plan.Plan.Rules.Executions);
        Assert.NotEmpty(plan.Plan.Audit);
        Assert.All(
            plan.Plan.AfterDocuments,
            static document => Assert.Contains(
                "ManifestVersion: 1.12.0",
                Encoding.UTF8.GetString(document.Content.AsSpan()),
                StringComparison.Ordinal));
        Assert.Equal(
            [
                "manifests/e/Example/Golden/1.0.0/Example.Golden.installer.yaml",
                "manifests/e/Example/Golden/1.0.0/Example.Golden.locale.en-US.yaml",
                "manifests/e/Example/Golden/1.0.0/Example.Golden.yaml",
            ],
            plan.Plan.FileChanges.Select(static change => change.RepositoryPath));
        string[] goldenHashes =
        [
            .. plan.Plan.FileChanges.Select(static change =>
                Convert.ToHexString(SHA256.HashData(change.Content.AsSpan()))),
        ];
        Assert.True(
            goldenHashes.SequenceEqual(
            [
                "DD4D9C75636E44CBBAFB997136597FF192D955EC8EE255203156A3FD346A00C1",
                "6C1436D543C4B0BB1DAE98B957AE7CE38FD779E193361F8C3B1A3C936FAAC749",
                "A512F7720F1E618BFE5449CFFC67BD42905616EE00CC9DFD837DFBD513FCAA44",
            ],
            StringComparer.Ordinal),
            string.Join(Environment.NewLine, goldenHashes));

        WorkflowOperationResult applied = await engine.NewAsync(
            request with { ExecutionMode = WorkflowExecutionMode.Apply });

        Assert.True(applied.Applied, Describe(applied));
        Assert.Equal(
            plan.Plan.FileChanges.Select(static change => change.RepositoryPath),
            applied.Plan.FileChanges.Select(static change => change.RepositoryPath));
        AssertPlanMatchesDisk(temporary.Path, plan.Plan);
        Dictionary<string, byte[]> firstBytes = SnapshotFiles(temporary.Path);

        WorkflowOperationResult repeated = await engine.NewAsync(
            request with { ExecutionMode = WorkflowExecutionMode.Apply });

        Assert.False(repeated.Applied);
        Assert.Equal(WorkflowResultCode.Conflict, repeated.Code);
        Assert.Equal(firstBytes, SnapshotFiles(temporary.Path));

        WorkflowOperationResult updatePlan = await engine.UpdateAsync(new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            PackageIdentifier = _package,
            PreviousVersion = _version,
            PackageVersion = "2.0.0",
            Release = new ReleaseRequest(
                null,
                [new Uri("https://fixtures.invalid/golden-v2.msix")],
                []),
            AllowStructuralRewrite = true,
        });
        Assert.True(updatePlan.Code == WorkflowResultCode.Succeeded, Describe(updatePlan));
        Assert.False(updatePlan.Applied);
        Assert.True(Directory.Exists(VersionDirectory(temporary.Path)));

        WorkflowOperationResult updated = await engine.UpdateAsync(new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = _package,
            PreviousVersion = _version,
            PackageVersion = "2.0.0",
            Release = new ReleaseRequest(
                null,
                [new Uri("https://fixtures.invalid/golden-v2.msix")],
                []),
            AllowStructuralRewrite = true,
        });
        Assert.True(updated.Applied, Describe(updated));
        Assert.True(Directory.Exists(VersionDirectory(temporary.Path, new PackageVersion("2.0.0"))));

        WorkflowOperationResult newLocale = await engine.NewLocaleAsync(new NewLocaleOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = _package,
            PackageVersion = _version,
            Locale = Locale("de-DE", "Goldene Anwendung"),
            NetworkValidationMode = NetworkValidationMode.Skip,
        });
        Assert.True(newLocale.Applied);
        Assert.Single(newLocale.Plan.FileChanges);

        WorkflowOperationResult updateLocale = await engine.UpdateLocaleAsync(
            new UpdateLocaleOperationRequest
            {
                OutputDirectory = temporary.Path,
                ExecutionMode = WorkflowExecutionMode.Apply,
                PackageIdentifier = _package,
                PackageVersion = _version,
                Locale = new PackageLocaleMetadata
                {
                    PackageLocale = new LanguageTag("de-DE"),
                    PackageName = "Goldene Anwendung 2",
                },
                ApproveReview = true,
                NetworkValidationMode = NetworkValidationMode.Skip,
            });
        Assert.True(updateLocale.Applied, Describe(updateLocale));
        PackageManifests localized = PackageManifestIO.LoadDirectory(VersionDirectory(temporary.Path));
        Assert.Equal("Goldene Anwendung 2", Assert.Single(localized.Locales).PackageName);

        WorkflowOperationResult removePlan = await engine.RemoveAsync(new RemoveOperationRequest
        {
            OutputDirectory = temporary.Path,
            PackageIdentifier = _package,
            PackageVersion = _version,
            NetworkValidationMode = NetworkValidationMode.Skip,
        });
        Assert.False(removePlan.Applied);
        Assert.True(Directory.Exists(VersionDirectory(temporary.Path)));

        WorkflowOperationResult removed = await engine.RemoveAsync(new RemoveOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = _package,
            PackageVersion = _version,
            NetworkValidationMode = NetworkValidationMode.Skip,
        });
        Assert.True(removed.Applied);
        Assert.False(Directory.Exists(VersionDirectory(temporary.Path)));
    }

    [Fact]
    public async Task Production_submit_preserves_exact_validated_bytes()
    {
        using var source = new TemporaryDirectory();
        using var destination = new TemporaryDirectory();
        byte[] installer = BuildMsix();
        var handler = new StaticHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(installer),
        });
        using var downloader = new InstallerDownloader(handler);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            new DirectWorkflowReleaseSource(),
            new FixedClock());
        WorkflowOperationResult newPlan = await engine.NewAsync(NewRequest(source.Path));
        ImmutableArray<RawManifestDocument> raw =
        [
            .. newPlan.Plan.AfterDocuments.Select(static document => new RawManifestDocument(
                document.RepositoryPath,
                document.Content.AsSpan())),
        ];

        WorkflowOperationResult submitPlan = await engine.SubmitAsync(new SubmitOperationRequest
        {
            OutputDirectory = destination.Path,
            Documents = raw,
            Normalize = false,
        });
        Assert.Equal(
            raw.Select(static document => Convert.ToHexString(document.Content.AsSpan())),
            submitPlan.Plan.AfterDocuments.Select(static document => Convert.ToHexString(document.Content.AsSpan())));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination.Path));

        WorkflowOperationResult applied = await engine.SubmitAsync(new SubmitOperationRequest
        {
            OutputDirectory = destination.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            Documents = raw,
            Normalize = false,
        });
        Assert.True(applied.Applied, Describe(applied));
        AssertPlanMatchesDisk(destination.Path, submitPlan.Plan);
    }

    [Fact]
    public async Task Atomic_transaction_precondition_failure_and_cancellation_leave_no_partial_output()
    {
        using var temporary = new TemporaryDirectory();
        string existingPath = Path.Combine(temporary.Path, "existing.yaml");
        await File.WriteAllTextAsync(existingPath, "original");
        var transaction = new AtomicWorkflowFileTransaction();
        ImmutableArray<WorkflowFileChange> changes =
        [
            new(PlannedChangeKind.Add, "added.yaml", "new"u8, ExpectedFileState.Absent),
            new(
                PlannedChangeKind.Update,
                "existing.yaml",
                "changed"u8,
                ExpectedFileState.Present,
                new string('A', 64)),
        ];

        await Assert.ThrowsAsync<WorkflowOperationException>(
            () => transaction.ApplyAsync(temporary.Path, "Example.Golden", changes, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(temporary.Path, "added.yaml")));
        Assert.Equal("original", await File.ReadAllTextAsync(existingPath));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(temporary.Path),
            static path => Path.GetFileName(path).StartsWith(".winmatsch-transaction-", StringComparison.Ordinal));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.ApplyAsync(
                temporary.Path,
                "Example.Cancelled",
                [new(PlannedChangeKind.Add, "cancelled.yaml", "x"u8)],
                cancelled.Token));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "cancelled.yaml")));
    }

    [Fact]
    public async Task Concurrent_package_transactions_have_one_winner_and_consistent_bytes()
    {
        using var temporary = new TemporaryDirectory();
        var transaction = new AtomicWorkflowFileTransaction();
        ImmutableArray<WorkflowFileChange> changes =
        [
            new(PlannedChangeKind.Add, "manifests/e/Example/Golden/1.0.0/value.yaml", "winner"u8),
        ];
        Task[] attempts =
        [
            .. Enumerable.Range(0, 16).Select(async _ =>
            {
                try
                {
                    await transaction.ApplyAsync(
                        temporary.Path,
                        "Example.Golden",
                        changes,
                        CancellationToken.None);
                }
                catch (WorkflowOperationException exception)
                    when (exception.Code == WorkflowResultCode.Conflict)
                {
                }
            }),
        ];

        await Task.WhenAll(attempts);

        string path = Path.Combine(
            temporary.Path,
            "manifests",
            "e",
            "Example",
            "Golden",
            "1.0.0",
            "value.yaml");
        Assert.Equal("winner", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.EnumerateFiles(temporary.Path, "*.yaml", SearchOption.AllDirectories));
    }

    [Fact]
    public void Repository_paths_reject_traversal_before_touching_the_filesystem()
    {
        Assert.Throws<ArgumentException>(
            () => new WorkflowFileChange(PlannedChangeKind.Add, "../escape.yaml", "x"u8));
        Assert.Throws<ArgumentException>(
            () => new RawManifestDocument("manifests/../../escape.yaml", "x"u8));
    }

    private static NewOperationRequest NewRequest(string outputDirectory) => new()
    {
        OutputDirectory = outputDirectory,
        PackageIdentifier = _package,
        PackageVersion = _version.Value,
        Release = new ReleaseRequest(
            null,
            [new Uri("https://fixtures.invalid/golden.msix")],
            []),
        Locale = Locale("en-US", "Golden App"),
    };

    private static PackageLocaleMetadata Locale(string language, string packageName) => new()
    {
        PackageLocale = new LanguageTag(language),
        Publisher = "Example Publisher",
        PackageName = packageName,
        License = "MIT",
        ShortDescription = "A deterministic golden fixture.",
    };

    private static byte[] BuildMsix(string version = "1.0.0.0")
        => MsixFixtures.BuildPackage(MsixFixtures.PackageManifest(
            identityName: "Example.Golden",
            publisher: "CN=Example Publisher, O=Example Publisher, C=US",
            version: version,
            displayName: "Golden App",
            publisherDisplayName: "Example Publisher")).ToArray();

    private static string VersionDirectory(string root)
        => VersionDirectory(root, _version);

    private static string VersionDirectory(string root, PackageVersion version)
        => Path.Combine(
            root,
            ManifestPaths.GetVersionDirectory(_package, version)
                .Replace('/', Path.DirectorySeparatorChar));

    private static void AssertPlanMatchesDisk(string root, LocalOperationPlan plan)
    {
        foreach (WorkflowFileChange change in plan.FileChanges)
        {
            string path = Path.Combine(
                root,
                change.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
            if (change.Kind == PlannedChangeKind.Delete)
            {
                Assert.False(File.Exists(path));
            }
            else
            {
                Assert.Equal(change.Content.ToArray(), File.ReadAllBytes(path));
            }
        }
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static path => !Path.GetFileName(path).StartsWith(".winmatsch-", StringComparison.Ordinal))
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static string Describe(WorkflowOperationResult result)
        => string.Join(
            Environment.NewLine,
            [
                $"Code: {result.Code}",
                $"Error: {result.ErrorMessage}",
                .. result.Plan.Validation.Findings.Select(static finding =>
                    $"{finding.Code}: {finding.Message} ({finding.Path})"),
                .. result.Plan.Questions.Select(static question =>
                    $"{question.Code}: {question.Prompt} ({question.Path})"),
            ]);

    private sealed class FixedClock : IWorkflowClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 8, 1, 6, 0, 0, TimeSpan.Zero);
    }
}
