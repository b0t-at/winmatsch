using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;
using WinMatsch.Rules;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.Operations;

public sealed class LocalWorkflowEngineTests
{
    [Fact]
    public async Task New_plan_is_deterministic_and_does_not_publish()
    {
        using var temporary = new TemporaryDirectory();
        var transaction = new RecordingTransaction();
        LocalWorkflowEngine engine = CreateEngine(new DictionarySnapshotSource(), transaction);
        NewOperationRequest request = NewRequest(temporary.Path, WorkflowExecutionMode.Plan);

        WorkflowOperationResult first = await engine.NewAsync(request);
        WorkflowOperationResult second = await engine.NewAsync(request);

        Assert.Equal(WorkflowResultCode.Succeeded, first.Code);
        Assert.False(first.Applied);
        Assert.Equal(0, transaction.Calls);
        Assert.Equal(
            first.Plan.FileChanges.Select(static change => Convert.ToHexString(change.Content.AsSpan())),
            second.Plan.FileChanges.Select(static change => Convert.ToHexString(change.Content.AsSpan())));
        Assert.True(first.Plan.Audit.SequenceEqual(second.Plan.Audit));
        Assert.Contains(
            first.Plan.Audit,
            static entry => entry.Code == "CREATED_AT"
                && entry.Message == "2026-01-02T03:04:05.0000000+00:00");
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "manifests")));
    }

    [Fact]
    public async Task New_plan_uses_cleaned_scratch_instead_of_the_requested_artifact_directory()
    {
        using var temporary = new TemporaryDirectory();
        string requestedArtifacts = Path.Combine(temporary.Path, "requested-artifacts");
        var processor = new WritingArtifactProcessor();
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new PassThroughRuleRunner(),
            new CapturingPreflight(),
            new RecordingTransaction(),
            artifacts: processor,
            clock: new FixedClock());
        NewOperationRequest request = NewRequest(temporary.Path, WorkflowExecutionMode.Plan) with
        {
            Assets = [Asset("2.0.0", "A") with { Content = null, Analysis = null }],
            ArtifactDirectory = requestedArtifacts,
        };

        WorkflowOperationResult result = await engine.NewAsync(request);

        Assert.NotEqual(WorkflowResultCode.ApplyFailed, result.Code);
        Assert.False(Directory.Exists(requestedArtifacts));
        Assert.NotNull(processor.UsedDirectory);
        Assert.False(Directory.Exists(processor.UsedDirectory));
    }

    [Fact]
    public async Task New_apply_publishes_complete_valid_manifest_set()
    {
        using var temporary = new TemporaryDirectory();
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(),
            new AtomicWorkflowFileTransaction());
        NewOperationRequest request = NewRequest(temporary.Path, WorkflowExecutionMode.Apply);

        WorkflowOperationResult result = await engine.NewAsync(request);

        Assert.True(result.Applied);
        string directory = Path.Combine(
            temporary.Path,
            ManifestPaths.GetVersionDirectory(new PackageIdentifier("Example.App"), new PackageVersion("2.0.0"))
                .Replace('/', Path.DirectorySeparatorChar));
        PackageManifests manifests = PackageManifestIO.LoadDirectory(directory);
        Assert.Equal("Example.App", manifests.Version.PackageIdentifier!.Value);
        Assert.Equal("2.0.0", manifests.Version.PackageVersion!.Value);
        Assert.Equal(3, Directory.EnumerateFiles(directory, "*.yaml").Count());
    }

    [Fact]
    public async Task Remove_deletes_only_the_exact_version()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests oldPackage = CreatePackage("1.0.0", "A");
        PackageManifests retainedPackage = CreatePackage("2.0.0", "B");
        WritePackage(temporary.Path, oldPackage);
        WritePackage(temporary.Path, retainedPackage);
        var engine = new LocalWorkflowEngine(
            new LocalManifestSnapshotSource(),
            new PassThroughRuleRunner(),
            new PreflightGateWorkflowAdapter(new PreflightGate()),
            new AtomicWorkflowFileTransaction(),
            clock: new FixedClock());

        WorkflowOperationResult result = await engine.RemoveAsync(new RemoveOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PackageVersion = new PackageVersion("1.0.0"),
            NetworkValidationMode = NetworkValidationMode.Skip,
        });

        Assert.True(result.Applied);
        Assert.False(Directory.Exists(VersionDirectory(temporary.Path, "1.0.0")));
        Assert.True(Directory.Exists(VersionDirectory(temporary.Path, "2.0.0")));
    }

    [Fact]
    public async Task Submit_preserves_raw_bytes_when_normalization_is_not_requested()
    {
        using var temporary = new TemporaryDirectory();
        ImmutableArray<RawManifestDocument> documents = Documents(CreatePackage("1.0.0", "A"), "custom tool");
        var preflight = new CapturingPreflight();
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(),
            new RecordingTransaction(),
            preflight);

        WorkflowOperationResult result = await engine.SubmitAsync(new SubmitOperationRequest
        {
            OutputDirectory = temporary.Path,
            Documents = documents,
            Normalize = false,
        });

        Assert.Equal(WorkflowResultCode.Succeeded, result.Code);
        Assert.Equal(
            documents.Select(static document => Convert.ToHexString(document.Content.AsSpan())),
            preflight.Last!.AfterDocuments.Select(static document => Convert.ToHexString(document.Content.AsSpan())));
    }

    [Fact]
    public async Task New_locale_changes_one_file_and_preserves_unrelated_bytes()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        WritePackage(temporary.Path, package);
        string installerPath = Path.Combine(
            VersionDirectory(temporary.Path, "1.0.0"),
            ManifestPaths.GetInstallerFileName(new PackageIdentifier("Example.App")));
        byte[] installerBefore = await File.ReadAllBytesAsync(installerPath);
        var engine = new LocalWorkflowEngine(
            new LocalManifestSnapshotSource(),
            new PassThroughRuleRunner(),
            new PreflightGateWorkflowAdapter(new PreflightGate()),
            new AtomicWorkflowFileTransaction(),
            clock: new FixedClock());

        WorkflowOperationResult result = await engine.NewLocaleAsync(new NewLocaleOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PackageVersion = new PackageVersion("1.0.0"),
            Locale = new PackageLocaleMetadata
            {
                PackageLocale = new LanguageTag("de-DE"),
                Publisher = "Example",
                PackageName = "Anwendung",
                License = "MIT",
                ShortDescription = "Beschreibung",
            },
            NetworkValidationMode = NetworkValidationMode.Skip,
        });

        Assert.True(result.Applied);
        Assert.Single(result.Plan.FileChanges);
        Assert.Equal(installerBefore, await File.ReadAllBytesAsync(installerPath));
        Assert.True(File.Exists(Path.Combine(
            VersionDirectory(temporary.Path, "1.0.0"),
            "Example.App.locale.de-DE.yaml")));
    }

    [Fact]
    public async Task Update_locale_preserves_unspecified_fields_and_unrelated_files()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        package.Locales.Add(new LocaleManifest
        {
            PackageIdentifier = package.Version.PackageIdentifier,
            PackageVersion = package.Version.PackageVersion,
            PackageLocale = new LanguageTag("de-DE"),
            PackageName = "Alt",
            Description = "Hand-maintained description",
            Tags = ["eins", "zwei"],
        });
        WritePackage(temporary.Path, package);
        string installerPath = Path.Combine(
            VersionDirectory(temporary.Path, "1.0.0"),
            ManifestPaths.GetInstallerFileName(new PackageIdentifier("Example.App")));
        byte[] installerBefore = await File.ReadAllBytesAsync(installerPath);
        LocalWorkflowEngine engine = CreateEngine(
            new LocalManifestSnapshotSource(),
            new AtomicWorkflowFileTransaction());

        WorkflowOperationResult result = await engine.UpdateLocaleAsync(new UpdateLocaleOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PackageVersion = new PackageVersion("1.0.0"),
            Locale = new PackageLocaleMetadata
            {
                PackageLocale = new LanguageTag("de-DE"),
                PackageName = "Neu",
            },
            NetworkValidationMode = NetworkValidationMode.Skip,
        });

        PackageManifests updated = PackageManifestIO.LoadDirectory(VersionDirectory(temporary.Path, "1.0.0"));
        LocaleManifest locale = Assert.Single(updated.Locales);
        Assert.True(result.Applied);
        Assert.Equal("Neu", locale.PackageName);
        Assert.Equal("Hand-maintained description", locale.Description);
        Assert.Equal(["eins", "zwei"], locale.Tags);
        Assert.Equal(installerBefore, await File.ReadAllBytesAsync(installerPath));
    }

    [Fact]
    public async Task Local_source_rejects_non_exact_package_path_casing()
    {
        using var temporary = new TemporaryDirectory();
        WritePackage(temporary.Path, CreatePackage("1.0.0", "A"));
        string canonical = Path.Combine(temporary.Path, "manifests", "e", "Example");
        string intermediate = Path.Combine(temporary.Path, "manifests", "e", "rename-temp");
        string incorrect = Path.Combine(temporary.Path, "manifests", "e", "example");
        Directory.Move(canonical, intermediate);
        Directory.Move(intermediate, incorrect);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new LocalManifestSnapshotSource().LoadAsync(
                temporary.Path,
                new PackageIdentifier("Example.App"),
                new PackageVersion("1.0.0"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Local_snapshot_read_does_not_create_repository_lock_files()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        WritePackage(temporary.Path, package);

        PackageSnapshot? snapshot = await new LocalManifestSnapshotSource().LoadAsync(
                temporary.Path,
                package.Version.PackageIdentifier!,
                package.Version.PackageVersion!,
                CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, ".winmatsch-locks")));
    }

    [Fact]
    public async Task Snapshot_lock_contention_returns_a_structured_conflict()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        WritePackage(temporary.Path, package);
        using IDisposable held = AtomicWorkflowFileTransaction.AcquirePackageLock(
            temporary.Path,
            package.Version.PackageIdentifier!.Value);
        var engine = new LocalWorkflowEngine(
            new LocalManifestSnapshotSource(),
            new PassThroughRuleRunner(),
            new CapturingPreflight(),
            new AtomicWorkflowFileTransaction(),
            clock: new FixedClock());

        WorkflowOperationResult result = await engine.RemoveAsync(new RemoveOperationRequest
        {
            OutputDirectory = temporary.Path,
            PackageIdentifier = package.Version.PackageIdentifier!,
            PackageVersion = package.Version.PackageVersion!,
        });

        Assert.Equal(WorkflowResultCode.Conflict, result.Code);
        Assert.False(result.Applied);
        Assert.Contains(result.Plan.Validation.Findings, static finding => finding.Code == "WF_CONFLICT");
    }

    [Fact]
    public void Local_lock_also_coordinates_with_the_legacy_lock_location()
    {
        using var temporary = new TemporaryDirectory();
        string identity = DirectoryPin.GetLegacyIdentity(temporary.Path);
        string lockDirectory = Path.Combine(
            Path.GetTempPath(),
            "winmatsch-operation-locks",
            identity);
        string fileName =
            $"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("EXAMPLE.APP")))}.lock";
        Directory.CreateDirectory(lockDirectory);
        using var legacy = new FileStream(
            Path.Combine(lockDirectory, fileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        WorkflowOperationException exception = Assert.Throws<WorkflowOperationException>(
            () => AtomicWorkflowFileTransaction.AcquirePackageLock(
                temporary.Path,
                "Example.App"));

        Assert.Equal(WorkflowResultCode.Conflict, exception.Code);
        legacy.Dispose();
        Directory.Delete(lockDirectory, recursive: true);
    }

    [Fact]
    public async Task Snapshot_cancellation_remains_cancellation()
    {
        using var temporary = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(),
            new RecordingTransaction());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.NewAsync(
                NewRequest(temporary.Path, WorkflowExecutionMode.Plan),
                cancellation.Token));
    }

    [Fact]
    public void Non_windows_lock_identity_canonicalizes_symbolic_links()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string target = Path.Combine(temporary.Path, "target");
        string alias = Path.Combine(temporary.Path, "alias");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(alias, target);

        Assert.Equal(DirectoryPin.GetIdentity(target), DirectoryPin.GetIdentity(alias));
    }

    [Fact]
    public async Task Atomic_transaction_rolls_back_after_partial_install_failure()
    {
        using var temporary = new TemporaryDirectory();
        string original = Path.Combine(temporary.Path, "a.txt");
        await File.WriteAllTextAsync(original, "before");
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "blocked"), "not-a-directory");
        var transaction = new AtomicWorkflowFileTransaction();
        ImmutableArray<WorkflowFileChange> changes =
        [
            new(
                PlannedChangeKind.Update,
                "a.txt",
                "after"u8,
                ExpectedFileState.Present,
                WorkflowFileChange.Hash("before"u8)),
            new(PlannedChangeKind.Add, "blocked/file.txt", "new"u8),
        ];

        await Assert.ThrowsAnyAsync<IOException>(
            () => transaction.ApplyAsync(temporary.Path, "Example.App", changes, CancellationToken.None));

        Assert.Equal("before", await File.ReadAllTextAsync(original));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "blocked", "file.txt")));
        Assert.Empty(Directory.EnumerateDirectories(temporary.Path, ".winmatsch-transaction-*"));
    }

    [Fact]
    public async Task Transaction_preserves_apply_and_rollback_failures()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "a.txt"), "before-a");
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "b.txt"), "before-b");
        var fileSystem = new FaultingTransactionFileSystem
        {
            FailInstallFileName = "b.txt",
            FailRollback = true,
        };
        var transaction = new AtomicWorkflowFileTransaction(null, fileSystem);

        WorkflowRecoveryException exception = await Assert.ThrowsAsync<WorkflowRecoveryException>(
            () => transaction.ApplyAsync(
                temporary.Path,
                "Example.App",
                TwoFileChanges(),
                CancellationToken.None));

        Assert.Equal("Simulated apply failure.", exception.PrimaryException.Message);
        Assert.Contains(
            exception.RecoveryExceptions,
            static failure => failure.Message == "Simulated rollback failure.");
        Assert.True(exception.JournalRetained);
        Assert.IsType<AggregateException>(exception.InnerException);
    }

    [Fact]
    public async Task Transaction_preserves_apply_and_cleanup_failures()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "a.txt"), "before-a");
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "b.txt"), "before-b");
        var fileSystem = new FaultingTransactionFileSystem
        {
            FailInstallFileName = "b.txt",
            FailCleanup = true,
        };
        var transaction = new AtomicWorkflowFileTransaction(null, fileSystem);

        WorkflowRecoveryException exception = await Assert.ThrowsAsync<WorkflowRecoveryException>(
            () => transaction.ApplyAsync(
                temporary.Path,
                "Example.App",
                TwoFileChanges(),
                CancellationToken.None));

        Assert.Equal("Simulated apply failure.", exception.PrimaryException.Message);
        Assert.Contains(
            exception.RecoveryExceptions,
            static failure => failure.Message == "Simulated cleanup failure.");
        Assert.True(exception.JournalRetained);
        Assert.Equal("before-a", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "a.txt")));
        Assert.Equal("before-b", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "b.txt")));
    }

    [Fact]
    public async Task Transaction_rejects_traversal_without_writing()
    {
        using var temporary = new TemporaryDirectory();

        Assert.Throws<ArgumentException>(
            () => new WorkflowFileChange(PlannedChangeKind.Add, "../escape.txt", "bad"u8));
        await new AtomicWorkflowFileTransaction().ApplyAsync(
            temporary.Path,
            "Example.App",
            [],
            CancellationToken.None);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Fact]
    public async Task Update_with_unchanged_url_and_hash_is_a_no_op()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        PackageSnapshot snapshot = Snapshot(package) with { Documents = Documents(package, "winmatsch") };
        var source = new DictionarySnapshotSource(snapshot);
        LocalWorkflowEngine engine = CreateEngine(source, new RecordingTransaction());
        DiscoveredAsset asset = Asset("1.0.0", "A");

        WorkflowOperationResult result = await engine.UpdateAsync(new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "1.0.0",
            Assets = [asset],
            NetworkValidationMode = NetworkValidationMode.Skip,
        });

        Assert.Equal(WorkflowResultCode.NoChanges, result.Code);
        Assert.Empty(result.Plan.FileChanges);
    }

    [Fact]
    public async Task Blocking_rule_finding_stops_apply_at_the_transaction_boundary()
    {
        using var temporary = new TemporaryDirectory();
        var transaction = new RecordingTransaction();
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new FindingRuleRunner(
                new RuleRunSummary(
                    [],
                    [],
                    [new RuleFinding("TEST001", RuleSeverity.Error, "blocked")],
                    [],
                    [])),
            new CapturingPreflight(),
            transaction,
            clock: new FixedClock());

        WorkflowOperationResult result = await engine.NewAsync(
            NewRequest(temporary.Path, WorkflowExecutionMode.Apply));

        Assert.Equal(WorkflowResultCode.ValidationFailed, result.Code);
        Assert.Equal(0, transaction.Calls);
        Assert.Contains(result.Plan.Validation.Findings, static finding => finding.Code == "RULE_TEST001");
    }

    [Fact]
    public async Task Recovery_failure_is_returned_with_root_and_cleanup_details()
    {
        using var temporary = new TemporaryDirectory();
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new PassThroughRuleRunner(),
            new CapturingPreflight(),
            new RecoveryFailingTransaction(),
            clock: new FixedClock());

        WorkflowOperationResult result = await engine.NewAsync(
            NewRequest(temporary.Path, WorkflowExecutionMode.Apply));

        Assert.Equal(WorkflowResultCode.ApplyFailed, result.Code);
        Assert.Equal("root apply failure", result.ErrorMessage);
        WorkflowRecoveryDetails recovery = Assert.IsType<WorkflowRecoveryDetails>(result.Recovery);
        Assert.Equal("root apply failure", recovery.PrimaryError);
        Assert.Equal(["cleanup failure"], recovery.RecoveryErrors.ToArray());
        Assert.True(recovery.JournalRetained);
    }

    [Fact]
    public async Task Human_correction_review_requires_explicit_approval()
    {
        using var temporary = new TemporaryDirectory();
        var transaction = new RecordingTransaction();
        RuleRunSummary review = new(
            [],
            [],
            [],
            [new HumanCorrectionReview("installer.yaml", "Installers[0].Scope", "User", "Machine", "User")],
            []);
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new FindingRuleRunner(review),
            new CapturingPreflight(),
            transaction,
            clock: new FixedClock());

        WorkflowOperationResult blocked = await engine.NewAsync(
            NewRequest(temporary.Path, WorkflowExecutionMode.Apply));
        WorkflowOperationResult approved = await engine.NewAsync(
            NewRequest(temporary.Path, WorkflowExecutionMode.Apply) with { ApproveReview = true });

        Assert.Equal(WorkflowResultCode.ReviewRequired, blocked.Code);
        Assert.False(blocked.Applied);
        Assert.True(approved.Applied);
        Assert.Equal(1, transaction.Calls);
    }

    [Fact]
    public async Task Cancellation_before_transaction_keeps_destination_unchanged()
    {
        using var temporary = new TemporaryDirectory();
        string original = Path.Combine(temporary.Path, "a.txt");
        await File.WriteAllTextAsync(original, "before");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new AtomicWorkflowFileTransaction().ApplyAsync(
                temporary.Path,
                "Example.App",
                [
                    new WorkflowFileChange(
                        PlannedChangeKind.Update,
                        "a.txt",
                        "after"u8,
                        ExpectedFileState.Present,
                        WorkflowFileChange.Hash("before"u8)),
                ],
                cancellation.Token));

        Assert.Equal("before", await File.ReadAllTextAsync(original));
    }

    [Fact]
    public async Task Transaction_rejects_a_destination_changed_after_planning()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "a.txt");
        byte[] before = "before"u8.ToArray();
        await File.WriteAllBytesAsync(path, before);
        var change = new WorkflowFileChange(
            PlannedChangeKind.Update,
            "a.txt",
            "planned"u8,
            ExpectedFileState.Present,
            WorkflowFileChange.Hash(before));
        await File.WriteAllTextAsync(path, "concurrent");

        WorkflowOperationException exception = await Assert.ThrowsAsync<WorkflowOperationException>(
            () => new AtomicWorkflowFileTransaction().ApplyAsync(
                temporary.Path,
                "Example.App",
                [change],
                CancellationToken.None));

        Assert.Equal(WorkflowResultCode.Conflict, exception.Code);
        Assert.Equal("concurrent", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Local_snapshot_recovers_an_interrupted_transaction_before_reading()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        WritePackage(temporary.Path, package);
        string repositoryPath =
            $"{ManifestPaths.GetVersionDirectory(package.Version.PackageIdentifier!, package.Version.PackageVersion!)}/{ManifestPaths.GetInstallerFileName(package.Version.PackageIdentifier!)}";
        string destination = Path.Combine(
            temporary.Path,
            repositoryPath.Replace('/', Path.DirectorySeparatorChar));
        string packageKey = package.Version.PackageIdentifier!.Value.ToUpperInvariant();
        string prefix =
            $".winmatsch-transaction-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(packageKey)))[..16]}";
        string transaction = Path.Combine(temporary.Path, $"{prefix}-crash");
        string backup = Path.Combine(
            transaction,
            "backup",
            repositoryPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Move(destination, backup);
        string encodedPath = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(repositoryPath));
        await File.WriteAllTextAsync(
            Path.Combine(transaction, "journal"),
            $"prepared{Environment.NewLine}{PlannedChangeKind.Update}|1|{encodedPath}{Environment.NewLine}");

        PackageSnapshot? recovered = await new LocalManifestSnapshotSource().LoadAsync(
            temporary.Path,
            package.Version.PackageIdentifier!,
            package.Version.PackageVersion!,
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.True(File.Exists(destination));
        Assert.False(Directory.Exists(transaction));
    }

    [Fact]
    public async Task Legacy_manifests_committed_journal_finishes_without_creating_provenance()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        WritePackage(temporary.Path, package);
        string repositoryPath =
            $"{ManifestPaths.GetVersionDirectory(package.Version.PackageIdentifier!, package.Version.PackageVersion!)}/{ManifestPaths.GetInstallerFileName(package.Version.PackageIdentifier!)}";
        string packageKey = package.Version.PackageIdentifier!.Value.ToUpperInvariant();
        string prefix =
            $".winmatsch-transaction-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(packageKey)))[..16]}";
        string transaction = Path.Combine(temporary.Path, $"{prefix}-legacy");
        Directory.CreateDirectory(Path.Combine(transaction, "provenance"));
        string encodedPath = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(repositoryPath));
        await File.WriteAllTextAsync(
            Path.Combine(transaction, "journal"),
            $"manifests-committed{Environment.NewLine}"
            + $"{PlannedChangeKind.Update}|1|{encodedPath}{Environment.NewLine}");

        PackageSnapshot? recovered = await new LocalManifestSnapshotSource().LoadAsync(
            temporary.Path,
            package.Version.PackageIdentifier!,
            package.Version.PackageVersion!,
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Null(recovered.OriginalBotSubmission);
        Assert.False(Directory.Exists(transaction));
    }

    [Fact]
    public async Task Pre_enriched_asset_with_supplied_artifact_passes_production_preflight()
    {
        using var temporary = new TemporaryDirectory();
        DownloadResult download = Download("A", temporary.Path);
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new PassThroughRuleRunner(),
            new PreflightGateWorkflowAdapter(new PreflightGate(new StablePreflightNetwork(download))),
            new AtomicWorkflowFileTransaction(),
            clock: new FixedClock());
        NewOperationRequest request = NewRequest(temporary.Path, WorkflowExecutionMode.Apply) with
        {
            NetworkValidationMode = NetworkValidationMode.Online,
            InstallerArtifacts =
            [
                new InstallerArtifact("https://example.test/app-x64.exe", download),
            ],
        };

        WorkflowOperationResult result = await engine.NewAsync(request);

        Assert.True(result.Applied, result.Plan.Validation.ToText());
    }

    [Fact]
    public async Task Malformed_raw_submit_returns_a_stable_invalid_result()
    {
        using var temporary = new TemporaryDirectory();
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(),
            new RecordingTransaction());

        WorkflowOperationResult result = await engine.SubmitAsync(new SubmitOperationRequest
        {
            OutputDirectory = temporary.Path,
            Documents = [new RawManifestDocument("manifest.yaml", [0xFF])],
        });

        Assert.Equal(WorkflowResultCode.InvalidRequest, result.Code);
        Assert.Contains(result.Plan.Validation.Findings, static finding => finding.Code == "WF_INVALID");
    }

    [Fact]
    public async Task Replace_preflight_accepts_complete_prior_version_deletion()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests beforePackage = CreatePackage("1.0.0", "A");
        PackageManifests afterPackage = CreatePackage("2.0.0", "B");
        ImmutableArray<RawManifestDocument> before = Documents(beforePackage, "winmatsch");
        ImmutableArray<RawManifestDocument> after = Documents(afterPackage, "winmatsch");
        DownloadResult download = Download("B", temporary.Path);
        ImmutableArray<WorkflowFileChange> changes =
        [
            .. before.Select(static document => new WorkflowFileChange(
                PlannedChangeKind.Delete,
                document.RepositoryPath,
                expectedState: ExpectedFileState.Present,
                expectedSha256: WorkflowFileChange.Hash(document.Content.AsSpan()))),
            .. after.Select(static document => new WorkflowFileChange(
                PlannedChangeKind.Add,
                document.RepositoryPath,
                document.Content.AsSpan(),
                ExpectedFileState.Absent)),
        ];
        var adapter = new PreflightGateWorkflowAdapter(
            new PreflightGate(new StablePreflightNetwork(download)));

        ValidationReport report = await adapter.ValidateAsync(
            new WorkflowPreflightRequest
            {
                BeforeDocuments = before,
                AfterDocuments = after,
                Changes = changes,
                InstallerArtifacts =
                [
                    new InstallerArtifact("https://example.test/app-x64.exe", download),
                ],
            },
            CancellationToken.None);

        Assert.True(report.IsValid, report.ToText());
        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "VLD4003");
    }

    private static LocalWorkflowEngine CreateEngine(
        IManifestSnapshotSource source,
        IWorkflowFileTransaction transaction,
        IWorkflowPreflight? preflight = null)
        => new(
            source,
            new PassThroughRuleRunner(),
            preflight ?? new CapturingPreflight(),
            transaction,
            clock: new FixedClock());

    private static ImmutableArray<WorkflowFileChange> TwoFileChanges()
        =>
        [
            new(
                PlannedChangeKind.Update,
                "a.txt",
                "after-a"u8,
                ExpectedFileState.Present,
                WorkflowFileChange.Hash("before-a"u8)),
            new(
                PlannedChangeKind.Update,
                "b.txt",
                "after-b"u8,
                ExpectedFileState.Present,
                WorkflowFileChange.Hash("before-b"u8)),
        ];

    private static NewOperationRequest NewRequest(string output, WorkflowExecutionMode mode)
        => new()
        {
            OutputDirectory = output,
            ExecutionMode = mode,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PackageVersion = "2.0.0",
            Assets = [Asset("2.0.0", "A")],
            Locale = new PackageLocaleMetadata
            {
                PackageLocale = new LanguageTag("en-US"),
                Publisher = "Example",
                PackageName = "App",
                License = "MIT",
                ShortDescription = "Example application",
            },
            CreatedWith = "winmatsch test",
            NetworkValidationMode = NetworkValidationMode.Skip,
        };

    private static DiscoveredAsset Asset(string version, string hashSeed)
    {
        string hash = string.Concat(Enumerable.Repeat(hashSeed, 64));
        var identity = new DownloadContentIdentity(new Sha256Hash(hash), 42);
        return new DiscoveredAsset
        {
            ReleaseId = 1,
            ReleaseTag = $"v{version}",
            ReleaseName = version,
            ReleaseUri = new Uri($"https://example.test/releases/{version}"),
            IsPrerelease = false,
            ReleasePublishedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            AssetId = 2,
            AssetName = "app-x64.exe",
            DownloadUri = new Uri("https://example.test/app-x64.exe"),
            DeclaredContentType = "application/octet-stream",
            DeclaredSize = 42,
            AssetCreatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            Content = new(
                identity,
                "https://example.test/app-x64.exe",
                "https://example.test/app-x64.exe",
                "application/octet-stream",
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            Analysis = new AssetAnalysisEvidence
            {
                Format = DetectedInstallerFormat.GenericInstallerExe,
                AnalyzedContentIdentity = identity,
                AnalyzedUrl = "https://example.test/app-x64.exe",
                ProductVersion = version,
                IsProductVersionTrustworthy = true,
                InstallerShapes =
                [
                    new AnalyzedInstallerShape
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Exe,
                    },
                ],
            },
        };
    }

    private static PackageManifests CreatePackage(string versionValue, string hashSeed)
    {
        var identifier = new PackageIdentifier("Example.App");
        var version = new PackageVersion(versionValue);
        var locale = new LanguageTag("en-US");
        return new()
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
                        InstallerUrl = "https://example.test/app-x64.exe",
                        InstallerSha256 = new Sha256Hash(string.Concat(Enumerable.Repeat(hashSeed, 64))),
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
    }

    private static PackageSnapshot Snapshot(PackageManifests package)
        => new()
        {
            PackageIdentifier = package.Version.PackageIdentifier!,
            PackageVersion = package.Version.PackageVersion!,
            VersionDirectory = ManifestPaths.GetVersionDirectory(
                package.Version.PackageIdentifier!,
                package.Version.PackageVersion!),
            Manifests = package,
            Documents = Documents(package, null),
        };

    private static ImmutableArray<RawManifestDocument> Documents(
        PackageManifests package,
        string? createdWith)
    {
        string directory = ManifestPaths.GetVersionDirectory(
            package.Version.PackageIdentifier!,
            package.Version.PackageVersion!);
        return
        [
            .. PackageManifestIO.SerializeFiles(
                    package,
                    new ManifestWriteOptions { CreatedWith = createdWith })
                .Select(pair => new RawManifestDocument(
                    $"{directory}/{pair.Key}",
                    System.Text.Encoding.UTF8.GetBytes(pair.Value)))
                .OrderBy(static document => document.RepositoryPath, StringComparer.Ordinal),
        ];
    }

    private static void WritePackage(string root, PackageManifests package)
    {
        string directory = VersionDirectory(root, package.Version.PackageVersion!.Value);
        PackageManifestIO.WriteDirectory(directory, package);
    }

    private static string VersionDirectory(string root, string version)
        => Path.Combine(
            root,
            ManifestPaths.GetVersionDirectory(
                    new PackageIdentifier("Example.App"),
                    new PackageVersion(version))
                .Replace('/', Path.DirectorySeparatorChar));

    private static DownloadResult Download(string hashSeed, string directory)
        => new()
        {
            FilePath = Path.Combine(directory, "artifact.exe"),
            FileName = "artifact.exe",
            Sha256 = new Sha256Hash(string.Concat(Enumerable.Repeat(hashSeed, 64))),
            SizeInBytes = 42,
            RetrievedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            InitialUrl = "https://example.test/app-x64.exe",
            FinalUrl = "https://example.test/app-x64.exe",
        };

    private sealed class DictionarySnapshotSource(params PackageSnapshot[] snapshots) : IManifestSnapshotSource
    {
        private readonly IReadOnlyList<PackageSnapshot> _snapshots = snapshots;

        public Task<PackageSnapshot?> LoadAsync(
            string outputDirectory,
            PackageIdentifier packageIdentifier,
            PackageVersion packageVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots.SingleOrDefault(snapshot =>
                snapshot.PackageIdentifier.Equals(packageIdentifier)
                && snapshot.PackageVersion.Equals(packageVersion)));
        }

        public Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
            string outputDirectory,
            PackageIdentifier packageIdentifier,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _snapshots.Where(snapshot => snapshot.PackageIdentifier.Equals(packageIdentifier)).ToImmutableArray());
        }
    }

    private sealed class PassThroughRuleRunner : IWorkflowRuleRunner
    {
        public WorkflowRuleResult Run(WorkflowRuleRequest request)
            => new(request.Manifests, RuleRunSummary.Empty);
    }

    private sealed class FindingRuleRunner(RuleRunSummary summary) : IWorkflowRuleRunner
    {
        public WorkflowRuleResult Run(WorkflowRuleRequest request)
            => new(request.Manifests, summary);
    }

    private sealed class WritingArtifactProcessor : IWorkflowArtifactProcessor
    {
        public string? UsedDirectory { get; private set; }

        public async Task<ArtifactSnapshot> AcquireAsync(
            DiscoveredAsset asset,
            string artifactDirectory,
            CancellationToken cancellationToken)
        {
            UsedDirectory = artifactDirectory;
            Directory.CreateDirectory(artifactDirectory);
            string file = Path.Combine(artifactDirectory, asset.AssetName);
            await File.WriteAllBytesAsync(file, "installer"u8.ToArray(), cancellationToken);
            var sha = new Sha256Hash(
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            var download = new DownloadResult
            {
                FilePath = file,
                FileName = asset.AssetName,
                Sha256 = sha,
                SizeInBytes = 9,
                RetrievedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
                InitialUrl = asset.DownloadUri.AbsoluteUri,
                FinalUrl = asset.DownloadUri.AbsoluteUri,
            };
            var analysis = new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.GenericInstallerExe,
                ProductVersion = "2.0.0",
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Exe,
                    },
                ],
            };
            AssetContentEvidence content = AssetContentEvidence.FromDownload(download);
            return new()
            {
                Asset = asset with
                {
                    Content = content,
                    Analysis = AssetAnalysisEvidence.FromAnalysis(
                        analysis,
                        content,
                        isProductVersionTrustworthy: false),
                },
                Download = download,
                Analysis = analysis,
            };
        }
    }

    private sealed class CapturingPreflight : IWorkflowPreflight
    {
        public WorkflowPreflightRequest? Last { get; private set; }

        public Task<ValidationReport> ValidateAsync(
            WorkflowPreflightRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Last = request;
            return Task.FromResult(new ValidationReport());
        }

        public async Task<ValidationReport> ExecuteAsync(
            WorkflowPreflightRequest request,
            Func<CancellationToken, Task> boundary,
            CancellationToken cancellationToken)
        {
            Last = request;
            await boundary(cancellationToken);
            return new ValidationReport();
        }
    }

    private sealed class RecordingTransaction : IWorkflowFileTransaction
    {
        public int Calls { get; private set; }

        public Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingTransactionFileSystem : IWorkflowTransactionFileSystem
    {
        public string? FailInstallFileName { get; init; }

        public bool FailRollback { get; init; }

        public bool FailCleanup { get; init; }

        public void DeleteDirectory(string path, bool recursive)
        {
            if (FailCleanup
                && Path.GetFileName(path).StartsWith(
                    ".winmatsch-transaction-",
                    StringComparison.Ordinal))
            {
                throw new IOException("Simulated cleanup failure.");
            }

            Directory.Delete(path, recursive);
        }

        public void DeleteFile(string path) => File.Delete(path);

        public void MoveFile(string source, string destination)
        {
            string stageSegment = $"{Path.DirectorySeparatorChar}stage{Path.DirectorySeparatorChar}";
            string backupSegment = $"{Path.DirectorySeparatorChar}backup{Path.DirectorySeparatorChar}";
            if (FailInstallFileName is not null
                && source.Contains(stageSegment, StringComparison.Ordinal)
                && Path.GetFileName(destination) == FailInstallFileName)
            {
                throw new IOException("Simulated apply failure.");
            }

            if (FailRollback && source.Contains(backupSegment, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Simulated rollback failure.");
            }

            File.Move(source, destination);
        }
    }

    private sealed class RecoveryFailingTransaction : IWorkflowFileTransaction
    {
        public Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
            => throw new WorkflowRecoveryException(
                "recovery failed",
                new IOException("root apply failure"),
                rollbackException: null,
                cleanupException: new IOException("cleanup failure"),
                journalRetained: true);
    }

    private sealed class FixedClock : IWorkflowClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    }

    private sealed class StablePreflightNetwork(DownloadResult download) : IPreflightNetwork
    {
        public Task<DownloadProbeResult> ProbeAsync(
            string url,
            CancellationToken cancellationToken)
            => Task.FromResult(new DownloadProbeResult
            {
                InitialUrl = url,
                FinalUrl = url,
                Method = DownloadProbeMethod.Head,
            });

        public Task<DownloadRevalidationResult> RevalidateAsync(
            DownloadResult previous,
            CancellationToken cancellationToken)
            => Task.FromResult(new DownloadRevalidationResult
            {
                Status = DownloadRevalidationStatus.Unchanged,
                Result = download,
            });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "winmatsch-workflows-tests",
                Guid.NewGuid().ToString("N"));
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
