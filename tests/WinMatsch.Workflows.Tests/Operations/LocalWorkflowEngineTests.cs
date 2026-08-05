using System.Collections.Immutable;
using System.IO.Compression;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Rules;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Validation;
using WinMatsch.Workflows.Diagnostics;
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
    public async Task Release_metadata_fills_only_missing_fields_with_explicit_provenance()
    {
        using var temporary = new TemporaryDirectory();
        var releaseSource = new MetadataReleaseSource();
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new PassThroughRuleRunner(),
            new CapturingPreflight(),
            new RecordingTransaction(),
            releases: releaseSource,
            clock: new FixedClock());
        NewOperationRequest baseline = NewRequest(temporary.Path, WorkflowExecutionMode.Plan);
        NewOperationRequest request = baseline with
        {
            Locale = baseline.Locale with
            {
                License = null,
                PublisherUrl = "https://human.example.test",
            },
        };

        WorkflowOperationResult result = await engine.NewAsync(request);

        RawManifestDocument locale = Assert.Single(
            result.Plan.AfterDocuments,
            document => document.RepositoryPath.Contains(".locale.", StringComparison.Ordinal));
        string yaml = System.Text.Encoding.UTF8.GetString(locale.Content.AsSpan());
        Assert.Contains("License: Apache-2.0", yaml, StringComparison.Ordinal);
        Assert.Contains("PublisherUrl: https://human.example.test", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("https://generated.example.test", yaml, StringComparison.Ordinal);
        Assert.Contains(
            result.Plan.Audit,
            entry => entry.Code == "RELEASE_METADATA"
                && entry.Message == nameof(PackageLocaleMetadata.License)
                && entry.Provenance == "fixture:license");
        Assert.Equal(1, releaseSource.MetadataCalls);
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
    public async Task Submit_reuses_supplied_installer_artifacts()
    {
        using var temporary = new TemporaryDirectory();
        DownloadResult download = Download("A", temporary.Path);
        await File.WriteAllBytesAsync(download.FilePath, new byte[42]);
        var processor = new WritingArtifactProcessor();
        var preflight = new CapturingPreflight();
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new PassThroughRuleRunner(),
            preflight,
            new RecordingTransaction(),
            artifacts: processor,
            clock: new FixedClock());

        WorkflowOperationResult result = await engine.SubmitAsync(new SubmitOperationRequest
        {
            OutputDirectory = temporary.Path,
            Documents = Documents(CreatePackage("1.0.0", "A"), "custom tool"),
            InstallerArtifacts =
            [
                new InstallerArtifact("https://example.test/app-x64.exe", download),
            ],
        });

        Assert.Equal(WorkflowResultCode.Succeeded, result.Code);
        Assert.Null(processor.UsedDirectory);
        InstallerArtifact artifact = Assert.Single(result.Plan.Preflight.InstallerArtifacts);
        Assert.Equal(download.FilePath, artifact.Download.FilePath);
        Assert.True(File.Exists(artifact.Download.FilePath));
    }

    [Fact]
    public async Task Verified_submit_keeps_supplied_zip_available_for_final_preflight()
    {
        using var temporary = new TemporaryDirectory();
        string artifactPath = Path.Combine(temporary.Path, "artifact.zip");
        using (ZipArchive archive = ZipFile.Open(artifactPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("tool.exe");
            await using Stream stream = entry.Open();
            await stream.WriteAsync("portable"u8.ToArray());
        }

        byte[] artifactBytes = await File.ReadAllBytesAsync(artifactPath);
        var artifactHash = new Sha256Hash(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(artifactBytes)));
        PackageManifests package = CreatePackage("1.0.0", "A");
        Installer installer = Assert.Single(package.Installer.Installers!);
        installer.InstallerType = InstallerType.Zip;
        installer.NestedInstallerType = InstallerType.Portable;
        installer.NestedInstallerFiles =
        [
            new NestedInstallerFile
            {
                RelativeFilePath = "tool.exe",
                PortableCommandAlias = "example",
            },
        ];
        installer.InstallerSha256 = artifactHash;
        var download = new DownloadResult
        {
            FilePath = artifactPath,
            FileName = "artifact.zip",
            Sha256 = artifactHash,
            SizeInBytes = artifactBytes.Length,
            RetrievedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            InitialUrl = installer.InstallerUrl!,
            FinalUrl = installer.InstallerUrl!,
        };
        var transaction = new RecordingTransaction();
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new PassThroughRuleRunner(),
            new PreflightGateWorkflowAdapter(
                new PreflightGate(new StablePreflightNetwork(download))),
            transaction,
            artifacts: new WritingArtifactProcessor(),
            clock: new FixedClock());
        var request = new SubmitOperationRequest
        {
            OutputDirectory = temporary.Path,
            Documents = Documents(package, "custom tool"),
            InstallerArtifacts =
            [
                new InstallerArtifact(installer.InstallerUrl!, download),
            ],
        };
        WorkflowOperationResult planned = await engine.SubmitAsync(request);

        WorkflowOperationResult result = await engine.ApplyVerifiedPlanAsync(
            request,
            planned.Plan.Fingerprint);

        Assert.True(result.Applied, result.Plan.Validation.ToText());
        Assert.DoesNotContain(
            result.Plan.Validation.Findings,
            static finding => finding.Code == "VLD3012");
        Assert.True(File.Exists(artifactPath));
        Assert.Equal(1, transaction.Calls);
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

    [Theory]
    [InlineData("\n", "winmatsch")]
    [InlineData("\r\n", "winmatsch")]
    [InlineData("\n", "custom tool")]
    public async Task Update_with_unchanged_url_and_hash_is_a_no_op(
        string lineEnding,
        string createdWith)
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        PackageSnapshot snapshot = Snapshot(package) with
        {
            Documents = Documents(package, createdWith, lineEnding),
        };
        var source = new DictionarySnapshotSource(snapshot);
        LocalWorkflowEngine engine = CreateEngine(
            new FallbackManifestSnapshotSource(new DictionarySnapshotSource(), source),
            new RecordingTransaction());
        DiscoveredAsset asset = Asset("1.0.0", "A");

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, asset, createdWith));

        Assert.Empty(result.Plan.FileChanges);
        Assert.Equal(WorkflowResultCode.NoChanges, result.Code);
    }

    [Fact]
    public async Task Update_without_a_source_version_selects_latest_by_winget_order()
    {
        using var temporary = new TemporaryDirectory();
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(
                Snapshot(CreatePackage("1.9", "A")),
                Snapshot(CreatePackage("1.10", "A")),
                Snapshot(CreatePackage("1.2.0", "A"))),
            new RecordingTransaction());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.10", "A")) with
            {
                PreviousVersion = null,
                PackageVersion = "1.10",
            });

        Assert.NotEqual(WorkflowResultCode.InvalidRequest, result.Code);
        Assert.NotEqual(WorkflowResultCode.NotFound, result.Code);
        Assert.Contains(
            result.Plan.Audit,
            static entry => entry.Code == "UPDATE_SOURCE_VERSION"
                && entry.Message == "1.10");
    }

    [Fact]
    public async Task Update_skips_empty_latest_repository_version_and_uses_next_candidate()
    {
        using var temporary = new TemporaryDirectory();
        PackageVersionResult valid = RepositoryVersion(CreatePackage("1.23.1", "A"));
        var diagnostics = new CandidateRepositoryDiagnosticService(
            valid.Identifier,
            [new PackageVersion("2"), valid.Version],
            valid);
        var source = new RepositoryManifestSnapshotSource(
            diagnostics,
            new RepositoryCoordinates("microsoft", "winget-pkgs"));
        LocalWorkflowEngine engine = CreateEngine(
            new FallbackManifestSnapshotSource(new DictionarySnapshotSource(), source),
            new RecordingTransaction());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.24.0", "A")) with
            {
                PreviousVersion = null,
                PackageVersion = "1.24.0",
            });

        Assert.NotEqual(WorkflowResultCode.InvalidRequest, result.Code);
        Assert.NotEqual(WorkflowResultCode.NotFound, result.Code);
        Assert.Contains(
            result.Plan.Audit,
            static entry => entry.Code == "UPDATE_SOURCE_VERSION"
                && entry.Message == "1.23.1");
        Assert.Equal(["2", "1.23.1"], diagnostics.RequestedVersions.Select(static value => value.Value));
    }

    [Fact]
    public async Task Update_reports_version_directories_when_all_repository_candidates_are_empty()
    {
        using var temporary = new TemporaryDirectory();
        var identifier = new PackageIdentifier("Example.App");
        var diagnostics = new CandidateRepositoryDiagnosticService(
            identifier,
            [new PackageVersion("2"), new PackageVersion("1.23.1")]);
        var source = new RepositoryManifestSnapshotSource(
            diagnostics,
            new RepositoryCoordinates("microsoft", "winget-pkgs"));
        LocalWorkflowEngine engine = CreateEngine(
            new FallbackManifestSnapshotSource(new DictionarySnapshotSource(), source),
            new RecordingTransaction());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.24.0", "B")) with
            {
                PreviousVersion = null,
                PackageVersion = "1.24.0",
            });

        Assert.Equal(WorkflowResultCode.InvalidRequest, result.Code);
        string diagnostic = result.Plan.Validation.ToText();
        Assert.Contains("has 2 version directories", diagnostic, StringComparison.Ordinal);
        Assert.Contains(
            "none of the 2 newest candidates checked contained a manifest set",
            diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("Candidates checked: 2, 1.23.1.", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_with_explicit_empty_repository_version_reports_missing_manifests()
    {
        using var temporary = new TemporaryDirectory();
        var identifier = new PackageIdentifier("Example.App");
        var emptyVersion = new PackageVersion("2");
        var diagnostics = new CandidateRepositoryDiagnosticService(
            identifier,
            [emptyVersion]);
        var source = new RepositoryManifestSnapshotSource(
            diagnostics,
            new RepositoryCoordinates("microsoft", "winget-pkgs"),
            emptyVersion);
        LocalWorkflowEngine engine = CreateEngine(source, new RecordingTransaction());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.24.0", "B")) with
            {
                PreviousVersion = emptyVersion,
                PackageVersion = "1.24.0",
            });

        Assert.Equal(WorkflowResultCode.NotFound, result.Code);
        Assert.Contains(
            "Package 'Example.App' version '2' contains no manifest files.",
            result.Plan.Validation.ToText(),
            StringComparison.Ordinal);
        Assert.Equal(0, diagnostics.ListCalls);
        Assert.Equal(["2"], diagnostics.RequestedVersions.Select(static value => value.Value));
    }

    [Fact]
    public async Task Remote_source_rejects_replace_without_local_baseline()
    {
        using var temporary = new TemporaryDirectory();
        PackageSnapshot remote = Snapshot(CreatePackage("1.0.0", "A")) with
        {
            IsRemote = true,
        };
        var transaction = new RecordingTransaction();
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(remote),
            transaction);

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("2.0.0", "B")) with
            {
                PackageVersion = "2.0.0",
                ReplacePreviousVersion = true,
            });

        Assert.Equal(WorkflowResultCode.InvalidRequest, result.Code);
        Assert.Contains("read-only", result.Plan.Validation.ToText(), StringComparison.Ordinal);
        Assert.Equal(0, transaction.Calls);
    }

    [Fact]
    public async Task Remote_source_rejects_same_version_update_without_local_baseline()
    {
        using var temporary = new TemporaryDirectory();
        PackageSnapshot remote = Snapshot(CreatePackage("1.0.0", "A")) with
        {
            IsRemote = true,
        };
        var transaction = new RecordingTransaction();
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(remote),
            transaction);

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.0.0", "B")) with
            {
                AllowStableUrlContentChange = true,
            });

        Assert.Equal(WorkflowResultCode.InvalidRequest, result.Code);
        Assert.Contains("read-only", result.Plan.Validation.ToText(), StringComparison.Ordinal);
        Assert.Equal(0, transaction.Calls);
    }

    [Fact]
    public async Task Update_preserves_explicit_empty_installer_collections_for_a_no_op()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        Installer installer = Assert.Single(package.Installer.Installers!);
        installer.NestedInstallerFiles = [];
        installer.AppsAndFeaturesEntries = [];
        PackageSnapshot snapshot = Snapshot(package) with { Documents = Documents(package, "winmatsch") };
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(snapshot),
            new RecordingTransaction());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.0.0", "A")));

        Assert.Equal(WorkflowResultCode.NoChanges, result.Code);
        Assert.Empty(result.Plan.FileChanges);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("custom tool")]
    public async Task Update_does_not_hide_created_with_attribution_changes(string? previousCreatedWith)
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        PackageSnapshot snapshot = Snapshot(package) with
        {
            Documents = Documents(package, previousCreatedWith),
        };
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(snapshot),
            new RecordingTransaction());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.0.0", "A")));

        Assert.Equal(WorkflowResultCode.Succeeded, result.Code);
        Assert.Equal(snapshot.Documents.Length, result.Plan.FileChanges.Length);
        Assert.All(result.Plan.FileChanges, static change =>
        {
            Assert.Equal(WorkflowChangeProvenance.ToolGenerated, change.Provenance);
            Assert.Contains(
                "# Created with winmatsch\n",
                System.Text.Encoding.UTF8.GetString(change.Content.AsSpan()),
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Update_does_not_hide_header_order_changes()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        PackageSnapshot snapshot = Snapshot(package) with
        {
            Documents = TransformDocuments(
                Documents(package, "winmatsch"),
                static yaml =>
                {
                    int firstLineEnd = yaml.IndexOf('\n');
                    int secondLineEnd = yaml.IndexOf('\n', firstLineEnd + 1);
                    return yaml[(firstLineEnd + 1)..(secondLineEnd + 1)]
                        + yaml[..(firstLineEnd + 1)]
                        + yaml[(secondLineEnd + 1)..];
                }),
        };
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(snapshot),
            new RecordingTransaction());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.0.0", "A")));

        Assert.Equal(WorkflowResultCode.Succeeded, result.Code);
        Assert.Equal(snapshot.Documents.Length, result.Plan.FileChanges.Length);
    }

    [Fact]
    public async Task Update_normalizes_mixed_line_endings_instead_of_hiding_the_style_change()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        PackageSnapshot snapshot = Snapshot(package) with
        {
            Documents = TransformDocuments(
                Documents(package, "winmatsch"),
                static yaml =>
                {
                    int firstLineEnd = yaml.IndexOf('\n');
                    return yaml[..firstLineEnd] + "\r\n" + yaml[(firstLineEnd + 1)..];
                }),
        };
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(snapshot),
            new RecordingTransaction());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.0.0", "A")));

        Assert.Equal(WorkflowResultCode.Succeeded, result.Code);
        Assert.Equal(snapshot.Documents.Length, result.Plan.FileChanges.Length);
        Assert.All(result.Plan.FileChanges, static change =>
        {
            string yaml = System.Text.Encoding.UTF8.GetString(change.Content.AsSpan());
            Assert.DoesNotContain("\n", yaml.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Update_keeps_changed_metadata_byte_exact_through_preflight()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        PackageSnapshot snapshot = Snapshot(package) with
        {
            Documents = Documents(package, "winmatsch", "\r\n"),
        };
        var preflight = new CapturingPreflight();
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(snapshot),
            new MutatingRuleRunner(static manifests => manifests.DefaultLocale.PackageName = "Changed App"),
            preflight,
            new RecordingTransaction(),
            clock: new FixedClock());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.0.0", "A")));

        WorkflowFileChange change = Assert.Single(result.Plan.FileChanges);
        RawManifestDocument before = snapshot.Documents.Single(document =>
            document.RepositoryPath == change.RepositoryPath);
        Assert.Equal(WorkflowResultCode.Succeeded, result.Code);
        Assert.Equal(WorkflowFileChange.Hash(before.Content.AsSpan()), change.ExpectedSha256);
        Assert.Equal(WorkflowChangeProvenance.ToolGenerated, change.Provenance);
        string yaml = System.Text.Encoding.UTF8.GetString(change.Content.AsSpan());
        Assert.Contains("PackageName: Changed App\r\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", yaml.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        RawManifestDocument preflightDocument = preflight.Last!.AfterDocuments.Single(document =>
            document.RepositoryPath == change.RepositoryPath);
        Assert.True(change.Content.AsSpan().SequenceEqual(preflightDocument.Content.AsSpan()));
    }

    [Fact]
    public async Task Update_does_not_hide_changed_installer_hash()
    {
        using var temporary = new TemporaryDirectory();
        PackageManifests package = CreatePackage("1.0.0", "A");
        PackageSnapshot snapshot = Snapshot(package) with { Documents = Documents(package, "winmatsch") };
        LocalWorkflowEngine engine = CreateEngine(
            new DictionarySnapshotSource(snapshot),
            new RecordingTransaction());

        WorkflowOperationResult result = await engine.UpdateAsync(
            UpdateRequest(temporary.Path, Asset("1.0.0", "B")) with
            {
                AllowStableUrlContentChange = true,
            });

        WorkflowFileChange change = Assert.Single(result.Plan.FileChanges);
        RawManifestDocument before = snapshot.Documents.Single(document =>
            document.RepositoryPath == change.RepositoryPath);
        Assert.Equal(WorkflowResultCode.Succeeded, result.Code);
        Assert.EndsWith(".installer.yaml", change.RepositoryPath, StringComparison.Ordinal);
        Assert.Equal(WorkflowFileChange.Hash(before.Content.AsSpan()), change.ExpectedSha256);
        Assert.Equal(WorkflowChangeProvenance.ToolGenerated, change.Provenance);
        Assert.Contains(
            "InstallerSha256: BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
            System.Text.Encoding.UTF8.GetString(change.Content.AsSpan()),
            StringComparison.Ordinal);
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
    public async Task Startup_recovery_io_failure_returns_structured_diagnostics()
    {
        using var temporary = new TemporaryDirectory();
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new PassThroughRuleRunner(),
            new CapturingPreflight(),
            new IoRecoveryFailingTransaction(),
            clock: new FixedClock());

        WorkflowOperationResult result = await engine.NewAsync(
            NewRequest(temporary.Path, WorkflowExecutionMode.Apply));

        Assert.Equal(WorkflowResultCode.ApplyFailed, result.Code);
        Assert.Equal("malformed recovery journal", result.ErrorMessage);
        Assert.True(Assert.IsType<WorkflowRecoveryDetails>(result.Recovery).JournalRetained);
    }

    [Fact]
    public async Task Startup_recovery_lock_contention_returns_conflict()
    {
        using var temporary = new TemporaryDirectory();
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(),
            new PassThroughRuleRunner(),
            new CapturingPreflight(),
            new ConflictingRecoveryTransaction(),
            clock: new FixedClock());

        WorkflowOperationResult result = await engine.NewAsync(
            NewRequest(temporary.Path, WorkflowExecutionMode.Apply));

        Assert.Equal(WorkflowResultCode.Conflict, result.Code);
        Assert.Contains(
            result.Plan.Validation.Findings,
            static finding => finding.Code == "WF_CONFLICT");
    }

    [Fact]
    public async Task Human_correction_approval_persists_and_next_run_consumes_learned_value()
    {
        using var temporary = new TemporaryDirectory();
        string storeDirectory = Path.Combine(temporary.Path, "override-store");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = storeDirectory,
        });
        var transaction = new WritingRecordingTransaction();
        PackageManifests original = CreatePackage("1.0.0", "A");
        original.Installer.Installers![0].Scope = Scope.User;
        PackageManifests merged = ManifestSnapshotForTest.Clone(original);
        merged.Installer.Installers![0].Scope = Scope.Machine;
        PackageSnapshot previous = Snapshot(merged) with
        {
            OriginalBotSubmission = original,
            Documents = Documents(merged, "winmatsch"),
        };
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new LearningRuleRunner(),
            new CapturingPreflight(),
            transaction,
            clock: new FixedClock(),
            overridePackStore: store);
        UpdateOperationRequest baseline = new()
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "2.0.0",
            Assets = [Asset("2.0.0", "A")],
            NetworkValidationMode = NetworkValidationMode.Skip,
        };
        UpdateOperationRequest disabledBaseline = baseline with
        {
            RuleRuntime = new RuleRuntimeConfiguration(
                commandOverrides: new Dictionary<string, RuleMode>
                {
                    [RuleIds.ApplyOverridePackFields] = RuleMode.Disabled,
                }),
        };

        WorkflowOperationResult preview = await engine.UpdateAsync(
            disabledBaseline with { ExecutionMode = WorkflowExecutionMode.Plan });
        UpdateOperationRequest plannedRequest = Assert.IsType<UpdateOperationRequest>(
            ReviewApproval.Bind(
                disabledBaseline with { ExecutionMode = WorkflowExecutionMode.Plan },
                preview.Plan));
        WorkflowOperationResult planned = await engine.UpdateAsync(plannedRequest);
        Assert.False(Directory.Exists(storeDirectory));
        WorkflowOperationResult blocked = await engine.UpdateAsync(disabledBaseline);
        WorkflowOperationResult changedApproval = await engine.UpdateAsync(
            disabledBaseline with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = [new string('F', 64)],
            });
        UpdateOperationRequest approvedRequest = Assert.IsType<UpdateOperationRequest>(
            ReviewApproval.Bind(disabledBaseline, blocked.Plan));
        WorkflowOperationResult replayedApproval = await engine.UpdateAsync(
            approvedRequest with
            {
                PackageVersion = "3.0.0",
                Assets = [Asset("3.0.0", "A")],
            });
        WorkflowOperationResult approved = await engine.UpdateAsync(approvedRequest);
        WorkflowOperationResult subsequent = await engine.UpdateAsync(baseline);
        var changedGeneratorEngine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new ChangedGeneratorRuleRunner(),
            new CapturingPreflight(),
            new RecordingTransaction(),
            clock: new FixedClock(),
            overridePackStore: store);
        WorkflowOperationResult changedGenerator = await changedGeneratorEngine.UpdateAsync(
            baseline with
            {
                ExecutionMode = WorkflowExecutionMode.Plan,
                PackageVersion = "4.0.0",
                Assets = [Asset("4.0.0", "A")],
            });

        Assert.Equal(WorkflowResultCode.ReviewRequired, blocked.Code);
        Assert.False(blocked.Applied);
        Assert.Equal(WorkflowResultCode.ReviewRequired, changedApproval.Code);
        Assert.Equal(WorkflowResultCode.ReviewRequired, replayedApproval.Code);
        Assert.False(replayedApproval.Applied);
        Assert.Equal(WorkflowResultCode.Succeeded, planned.Code);
        Assert.False(planned.Applied);
        Assert.True(approved.Applied);
        Assert.NotNull(approved.Plan.LearnedOverride);
        Assert.Contains(
            approved.Plan.Audit,
            static entry => entry.Code == "LEARNED_OVERRIDE_PERSISTED"
                && entry.Message.EndsWith(".Scope", StringComparison.Ordinal));
        PackageManifests committed = PackageManifestIO.LoadDirectory(Path.Combine(
            temporary.Path,
            ManifestPaths.GetVersionDirectory(
                baseline.PackageIdentifier,
                new PackageVersion("2.0.0")).Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(Scope.Machine, committed.Installer.Installers![0].Scope);
        Assert.True(
            subsequent.Code == WorkflowResultCode.Succeeded,
            string.Join(
                Environment.NewLine,
                [
                    $"Code: {subsequent.Code}",
                    .. subsequent.Plan.Rules.Findings.Select(static finding =>
                        $"{finding.RuleId}:{finding.Path}:{finding.Message}"),
                    .. subsequent.Plan.Rules.Trace.Select(static entry =>
                        $"{entry.RuleId}:{entry.Message}"),
                ]));
        Assert.False(subsequent.Plan.RequiresReview);
        Assert.Contains(
            changedGenerator.Plan.Validation.Findings,
            static finding => finding.Message.Contains(
                "raw generated value no longer matches",
                StringComparison.Ordinal));
        InstallerManifest changedInstaller = ManifestYamlReader.ReadInstaller(
            System.Text.Encoding.UTF8.GetString(
                changedGenerator.Plan.AfterDocuments.Single(document =>
                        ManifestYamlReader.TryDetectType(
                            System.Text.Encoding.UTF8.GetString(document.Content.AsSpan()))
                        == ManifestType.Installer)
                    .Content.AsSpan()));
        Assert.Null(changedInstaller.Installers![0].Scope);
        Assert.Equal(2, transaction.Calls);
    }

    [Fact]
    public async Task Failed_manifest_transaction_restores_learned_override_store()
    {
        using var temporary = new TemporaryDirectory();
        string storeDirectory = Path.Combine(temporary.Path, "override-store");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = storeDirectory,
        });
        PackageManifests original = CreatePackage("1.0.0", "A");
        original.Installer.Installers![0].Scope = Scope.User;
        PackageManifests merged = ManifestSnapshotForTest.Clone(original);
        merged.Installer.Installers![0].Scope = Scope.Machine;
        PackageSnapshot previous = Snapshot(merged) with { OriginalBotSubmission = original };
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new LearningRuleRunner(),
            new CapturingPreflight(),
            new RejectingTransaction(),
            clock: new FixedClock(),
            overridePackStore: store);
        var request = new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "2.0.0",
            Assets = [Asset("2.0.0", "A")],
            NetworkValidationMode = NetworkValidationMode.Skip,
        };
        WorkflowOperationResult preview = await engine.UpdateAsync(
            request with { ExecutionMode = WorkflowExecutionMode.Plan });
        UpdateOperationRequest approved = Assert.IsType<UpdateOperationRequest>(
            ReviewApproval.Bind(request, preview.Plan));

        WorkflowOperationResult failed = await engine.UpdateAsync(approved);
        OverridePackStoreSnapshot restored = await store.LoadAsync(
            request.PackageIdentifier,
            allowRecoveryWrites: false,
            CancellationToken.None);

        Assert.Equal(WorkflowResultCode.ApplyFailed, failed.Code);
        Assert.Null(restored.Pack);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(storeDirectory),
            path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Manifest_commit_with_pack_failure_recovers_learning_on_next_run()
    {
        using var temporary = new TemporaryDirectory();
        string storeDirectory = Path.Combine(temporary.Path, "override-store");
        string activePackPath = Path.Combine(storeDirectory, "EXAMPLE.APP.yaml");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = storeDirectory,
        });
        PackageManifests original = CreatePackage("1.0.0", "A");
        original.Installer.Installers![0].Scope = Scope.User;
        PackageManifests merged = ManifestSnapshotForTest.Clone(original);
        merged.Installer.Installers![0].Scope = Scope.Machine;
        PackageSnapshot previous = Snapshot(merged) with { OriginalBotSubmission = original };
        var firstEngine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new LearningRuleRunner(),
            new CapturingPreflight(),
            new ManifestWritingPackBlockingTransaction(activePackPath),
            clock: new FixedClock(),
            overridePackStore: store);
        var request = new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "2.0.0",
            Assets = [Asset("2.0.0", "A")],
            NetworkValidationMode = NetworkValidationMode.Skip,
        };
        WorkflowOperationResult preview = await firstEngine.UpdateAsync(
            request with { ExecutionMode = WorkflowExecutionMode.Plan });
        UpdateOperationRequest approved = Assert.IsType<UpdateOperationRequest>(
            ReviewApproval.Bind(request, preview.Plan));

        WorkflowOperationResult committed = await firstEngine.UpdateAsync(approved);

        Assert.True(committed.Applied);
        Assert.Contains("retained for automatic recovery", committed.ErrorMessage, StringComparison.Ordinal);
        Assert.True(File.Exists($"{activePackPath}.pending"));
        Assert.True(File.Exists($"{activePackPath}.transaction"));
        Directory.Delete(activePackPath);

        var nextEngine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new LearningRuleRunner(),
            new CapturingPreflight(),
            new WritingRecordingTransaction(),
            clock: new FixedClock(),
            overridePackStore: store);
        WorkflowOperationResult next = await nextEngine.UpdateAsync(request);

        Assert.False(next.Plan.RequiresReview);
        Assert.Contains(
            next.Plan.Audit,
            static entry => entry.Code == "LEARNED_OVERRIDE_ACTIVE"
                && entry.Provenance?.Contains("durable", StringComparison.Ordinal) == true);
        Assert.False(File.Exists($"{activePackPath}.pending"));
        Assert.False(File.Exists($"{activePackPath}.transaction"));
    }

    [Fact]
    public async Task Provenance_failure_keeps_learning_inactive_until_recovery_finishes()
    {
        using var temporary = new TemporaryDirectory();
        string storeDirectory = Path.Combine(temporary.Path, "override-store");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = storeDirectory,
        });
        PackageManifests original = CreatePackage("1.0.0", "A");
        original.Installer.Installers![0].Scope = Scope.User;
        PackageManifests merged = ManifestSnapshotForTest.Clone(original);
        merged.Installer.Installers![0].Scope = Scope.Machine;
        PackageSnapshot previous = Snapshot(merged) with { OriginalBotSubmission = original };
        var firstEngine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new LearningRuleRunner(),
            new CapturingPreflight(),
            new ManifestWritingProvenanceFailingTransaction(),
            clock: new FixedClock(),
            overridePackStore: store);
        var request = new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "2.0.0",
            Assets = [Asset("2.0.0", "A")],
            NetworkValidationMode = NetworkValidationMode.Skip,
        };
        WorkflowOperationResult preview = await firstEngine.UpdateAsync(
            request with { ExecutionMode = WorkflowExecutionMode.Plan });
        UpdateOperationRequest approved = Assert.IsType<UpdateOperationRequest>(
            ReviewApproval.Bind(request, preview.Plan));

        WorkflowOperationResult failedProvenance = await firstEngine.UpdateAsync(approved);
        OverridePackStoreSnapshot pending = await store.LoadAsync(
            request.PackageIdentifier,
            allowRecoveryWrites: true,
            CancellationToken.None);

        Assert.True(failedProvenance.Applied);
        Assert.Null(pending.Pack);
        Assert.True(pending.PendingActivation);

        var recoveredEngine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new LearningRuleRunner(),
            new CapturingPreflight(),
            new WritingRecordingTransaction(),
            clock: new FixedClock(),
            overridePackStore: store);
        WorkflowOperationResult recovered = await recoveredEngine.UpdateAsync(request);

        Assert.False(recovered.Plan.RequiresReview);
        Assert.Contains(
            recovered.Plan.Audit,
            static entry => entry.Code == "LEARNED_OVERRIDE_ACTIVE"
                && entry.Provenance?.Contains("durable", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Non_update_operation_finalizes_pending_learning_before_mutation()
    {
        using var temporary = new TemporaryDirectory();
        string storeDirectory = Path.Combine(temporary.Path, "override-store");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = storeDirectory,
        });
        PackageIdentifier package = new("Example.App");
        string marker = Path.Combine(temporary.Path, "marker.yaml");
        await File.WriteAllTextAsync(marker, "before");
        await using IOverridePackWriteStage stage = await store.StageAsync(
            new(
                package,
                new OverridePack
                {
                    PackageIdentifier = package,
                    PreservedFields = ["DefaultLocale.PublisherUrl"],
                },
                ExpectedContentSha256: null,
                ExpectedFormatVersion: null,
                OutputDirectory: temporary.Path,
                ManifestChanges:
                [
                    new WorkflowFileChange(
                        PlannedChangeKind.Update,
                        "marker.yaml",
                        "after"u8,
                        ExpectedFileState.Present,
                        WorkflowFileChange.Hash("before"u8)),
                ]),
            CancellationToken.None);
        await File.WriteAllTextAsync(marker, "after");
        await stage.RetainForRecoveryAsync();
        PackageSnapshot snapshot = Snapshot(CreatePackage("1.0.0", "A"));
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(snapshot),
            new PassThroughRuleRunner(),
            new CapturingPreflight(),
            new RecoveryAwareRecordingTransaction(),
            clock: new FixedClock(),
            overridePackStore: store);

        WorkflowOperationResult removed = await engine.RemoveAsync(new RemoveOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = package,
            PackageVersion = new PackageVersion("1.0.0"),
            NetworkValidationMode = NetworkValidationMode.Skip,
        });
        OverridePackStoreSnapshot active = await store.LoadAsync(
            package,
            allowRecoveryWrites: false,
            CancellationToken.None);

        Assert.True(
            removed.Applied,
            $"{removed.Code}: {removed.ErrorMessage} {removed.Plan.Validation.ToText()}");
        Assert.NotNull(active.Pack);
        Assert.False(active.PendingActivation);
    }

    [Fact]
    public async Task Root_scope_value_correction_is_learned_without_false_layout_change()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = Path.Combine(temporary.Path, "override-store"),
        });
        PackageManifests original = CreatePackage("1.0.0", "A");
        original.Installer.Scope = Scope.User;
        PackageManifests merged = ManifestSnapshotForTest.Clone(original);
        merged.Installer.Scope = Scope.Machine;
        PackageSnapshot previous = Snapshot(merged) with { OriginalBotSubmission = original };
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new RootScopeLearningRuleRunner(),
            new CapturingPreflight(),
            new WritingRecordingTransaction(),
            clock: new FixedClock(),
            overridePackStore: store);
        var request = new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "2.0.0",
            Assets = [Asset("2.0.0", "A")],
            NetworkValidationMode = NetworkValidationMode.Skip,
        };
        WorkflowOperationResult preview = await engine.UpdateAsync(
            request with { ExecutionMode = WorkflowExecutionMode.Plan });
        UpdateOperationRequest approved = Assert.IsType<UpdateOperationRequest>(
            ReviewApproval.Bind(request, preview.Plan));

        WorkflowOperationResult persisted = await engine.UpdateAsync(approved);
        WorkflowOperationResult subsequent = await engine.UpdateAsync(request);

        Assert.True(persisted.Applied);
        LearnedOverridePlan learning = Assert.IsType<LearnedOverridePlan>(persisted.Plan.LearnedOverride);
        Assert.Null(learning.Pack.ScopeLayout);
        LearnedFieldOverride rootScope = Assert.Single(learning.Pack.LearnedFields);
        Assert.Equal("Scope", rootScope.SemanticPath);
        Assert.Null(rootScope.InstallerSelectorSha256);
        Assert.False(subsequent.Plan.RequiresReview);
    }

    [Fact]
    public async Task Newly_approved_value_supersedes_stale_active_learning()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = Path.Combine(temporary.Path, "override-store"),
        });
        PackageIdentifier package = new("Example.App");
        const string BotUrl = "https://bot.example.test";
        const string OldHumanUrl = "https://old-human.example.test";
        const string NewHumanUrl = "https://new-human.example.test";
        _ = await store.WriteAsync(
            new(
                package,
                new OverridePack
                {
                    PackageIdentifier = package,
                    LearnedFields =
                    [
                        new()
                        {
                            DocumentKey = "defaultLocale",
                            SemanticPath = "PublisherUrl",
                            Value = OldHumanUrl,
                            ValueSha256 = HashText(OldHumanUrl),
                            BotValueSha256 = HashText(BotUrl),
                            SourceFingerprint = new string('A', 64),
                            Source = "manifest:PublisherUrl",
                        },
                    ],
                },
                ExpectedContentSha256: null,
                ExpectedFormatVersion: null),
            CancellationToken.None);
        PackageManifests original = CreatePackage("1.0.0", "A");
        original.DefaultLocale.PublisherUrl = BotUrl;
        PackageManifests merged = ManifestSnapshotForTest.Clone(original);
        merged.DefaultLocale.PublisherUrl = NewHumanUrl;
        PackageSnapshot previous = Snapshot(merged) with { OriginalBotSubmission = original };
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new PublisherLearningRuleRunner(BotUrl),
            new CapturingPreflight(),
            new WritingRecordingTransaction(),
            clock: new FixedClock(),
            overridePackStore: store);
        var request = new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = package,
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "2.0.0",
            Assets = [Asset("2.0.0", "A")],
            NetworkValidationMode = NetworkValidationMode.Skip,
        };
        WorkflowOperationResult preview = await engine.UpdateAsync(
            request with { ExecutionMode = WorkflowExecutionMode.Plan });
        UpdateOperationRequest approved = Assert.IsType<UpdateOperationRequest>(
            ReviewApproval.Bind(request, preview.Plan));

        WorkflowOperationResult applied = await engine.UpdateAsync(approved);

        Assert.True(applied.Applied, applied.Plan.Validation.ToText());
        DefaultLocaleManifest locale = ManifestYamlReader.ReadDefaultLocale(
            System.Text.Encoding.UTF8.GetString(
                applied.Plan.AfterDocuments.Single(document =>
                        ManifestYamlReader.TryDetectType(
                            System.Text.Encoding.UTF8.GetString(document.Content.AsSpan()))
                        == ManifestType.DefaultLocale)
                    .Content.AsSpan()));
        Assert.Equal(NewHumanUrl, locale.PublisherUrl);
        OverridePack active = Assert.IsType<OverridePack>(
            (await store.LoadAsync(package, false, CancellationToken.None)).Pack);
        Assert.Equal(NewHumanUrl, Assert.Single(active.LearnedFields).Value);
    }

    [Fact]
    public async Task No_change_approval_persists_learning_with_forced_apply_mode()
    {
        using var temporary = new TemporaryDirectory();
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = Path.Combine(temporary.Path, "override-store"),
        });
        PackageManifests original = CreatePackage("1.0.0", "A");
        original.Installer.Installers![0].Scope = Scope.User;
        PackageManifests merged = ManifestSnapshotForTest.Clone(original);
        merged.Installer.Installers![0].Scope = Scope.Machine;
        PackageSnapshot previous = Snapshot(merged) with
        {
            OriginalBotSubmission = original,
            Documents = Documents(merged, "winmatsch"),
        };
        var engine = new LocalWorkflowEngine(
            new DictionarySnapshotSource(previous),
            new NoChangeLearningRuleRunner(),
            new CapturingPreflight(),
            new RecordingTransaction(),
            clock: new FixedClock(),
            overridePackStore: store);
        var request = new UpdateOperationRequest
        {
            OutputDirectory = temporary.Path,
            ExecutionMode = WorkflowExecutionMode.Apply,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "1.0.0",
            Assets = [Asset("1.0.0", "A")],
            NetworkValidationMode = NetworkValidationMode.Skip,
            RuleRuntime = new RuleRuntimeConfiguration(
                commandOverrides: new Dictionary<string, RuleMode>
                {
                    [RuleIds.ApplyOverridePackFields] = RuleMode.Disabled,
                }),
        };
        WorkflowOperationResult preview = await engine.UpdateAsync(
            request with { ExecutionMode = WorkflowExecutionMode.Plan });
        UpdateOperationRequest approved = Assert.IsType<UpdateOperationRequest>(
            ReviewApproval.Bind(request, preview.Plan));

        WorkflowOperationResult applied = await engine.UpdateAsync(approved);

        Assert.Empty(applied.Plan.FileChanges);
        Assert.Equal(WorkflowResultCode.NoChanges, applied.Code);
        Assert.True(applied.Applied);
        Assert.NotNull(
            (await store.LoadAsync(
                request.PackageIdentifier,
                allowRecoveryWrites: false,
                CancellationToken.None)).Pack);
    }

    [Fact]
    public void Output_root_canonicalization_does_not_follow_a_user_controlled_unix_symlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string token = Guid.NewGuid().ToString("N");
        string target = Path.Combine(Path.GetTempPath(), $"winmatsch-output-target-{token}");
        string alias = Path.Combine(Path.GetTempPath(), $"winmatsch-output-alias-{token}");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(alias, target);
        try
        {
            string requested = Path.Combine(alias, "nested", "output");

            string canonical = SecurePath.CanonicalizeOutputRoot(requested);

            Assert.Equal(
                Path.Combine(SecurePath.CanonicalizeOutputRoot(alias), "nested", "output"),
                canonical);
            Assert.Throws<InvalidDataException>(() => SecurePath.ValidateOutputRoot(canonical));
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }

            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void Output_root_canonicalization_resolves_the_known_macos_var_alias()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string requested = Path.Combine("/var", "folders", $"winmatsch-{Guid.NewGuid():N}");

        string canonical = SecurePath.CanonicalizeOutputRoot(requested);

        Assert.StartsWith("/private/var/folders/", canonical, StringComparison.Ordinal);
        SecurePath.ValidateOutputRoot(canonical);
    }

    [Fact]
    public async Task Atomic_transaction_accepts_the_known_macos_var_alias()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string root = Path.Combine("/var", "tmp", $"winmatsch-{Guid.NewGuid():N}");
        try
        {
            var transaction = new AtomicWorkflowFileTransaction();

            await transaction.ApplyAsync(
                root,
                "Example.MacAlias",
                [new(PlannedChangeKind.Add, "proof.yaml", "ok"u8)],
                CancellationToken.None);

            Assert.Equal("ok", await File.ReadAllTextAsync(Path.Combine(root, "proof.yaml")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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

    private static UpdateOperationRequest UpdateRequest(
        string output,
        DiscoveredAsset asset,
        string createdWith = "winmatsch")
        => new()
        {
            OutputDirectory = output,
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreviousVersion = new PackageVersion("1.0.0"),
            PackageVersion = "1.0.0",
            Assets = [asset],
            CreatedWith = createdWith,
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

    private static PackageVersionResult RepositoryVersion(PackageManifests package)
    {
        string directory = ManifestPaths.GetVersionDirectory(
            package.Version.PackageIdentifier!,
            package.Version.PackageVersion!);
        RepositoryManifestFile[] files =
        [
            .. PackageManifestIO.SerializeFiles(package).Select(pair =>
                new RepositoryManifestFile($"{directory}/{pair.Key}", pair.Value)),
        ];
        return new(
            new RepositoryCoordinates("microsoft", "winget-pkgs"),
            "master",
            package.Version.PackageIdentifier!,
            package.Version.PackageVersion!,
            Normalized: false,
            files);
    }

    private static ImmutableArray<RawManifestDocument> Documents(
        PackageManifests package,
        string? createdWith,
        string lineEnding = "\n")
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
                    System.Text.Encoding.UTF8.GetBytes(
                        lineEnding == "\n"
                            ? pair.Value
                            : pair.Value.Replace("\n", lineEnding, StringComparison.Ordinal))))
                .OrderBy(static document => document.RepositoryPath, StringComparer.Ordinal),
        ];
    }

    private static ImmutableArray<RawManifestDocument> TransformDocuments(
        ImmutableArray<RawManifestDocument> documents,
        Func<string, string> transform)
        =>
        [
            .. documents.Select(document => new RawManifestDocument(
                document.RepositoryPath,
                System.Text.Encoding.UTF8.GetBytes(
                    transform(System.Text.Encoding.UTF8.GetString(document.Content.AsSpan()))))),
        ];

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

    private static string HashText(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));

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

    private sealed class CandidateRepositoryDiagnosticService(
        PackageIdentifier identifier,
        IReadOnlyList<PackageVersion> candidates,
        params PackageVersionResult[] available) : IRepositoryDiagnosticService
    {
        private readonly Dictionary<PackageVersion, PackageVersionResult> _available =
            available.ToDictionary(static result => result.Version);

        public int ListCalls { get; private set; }

        public List<PackageVersion> RequestedVersions { get; } = [];

        public Task<PackageVersionResult> GetPackageVersionAsync(
            RepositoryCoordinates repository,
            PackageIdentifier requestedIdentifier,
            PackageVersion version,
            bool normalize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedVersions.Add(version);
            return requestedIdentifier.Equals(identifier)
                && _available.TryGetValue(version, out PackageVersionResult? result)
                    ? Task.FromResult(result)
                    : Task.FromException<PackageVersionResult>(
                        new DiagnosticNotFoundException(
                            $"Package '{requestedIdentifier.Value}' version '{version.Value}' "
                            + "contains no manifest files."));
        }

        public Task<PackageVersionsResult> ListVersionsAsync(
            RepositoryCoordinates repository,
            PackageIdentifier requestedIdentifier,
            int skip,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            return Task.FromResult(new PackageVersionsResult(
                repository,
                "master",
                requestedIdentifier,
                skip,
                limit,
                candidates.Count,
                [.. candidates.Skip(skip).Take(limit)]));
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

    private sealed class MutatingRuleRunner(Action<PackageManifests> mutate) : IWorkflowRuleRunner
    {
        public WorkflowRuleResult Run(WorkflowRuleRequest request)
        {
            mutate(request.Manifests);
            return new(request.Manifests, RuleRunSummary.Empty);
        }
    }

    private sealed class LearningRuleRunner : IWorkflowRuleRunner
    {
        public WorkflowRuleResult Run(WorkflowRuleRequest request)
        {
            request.Manifests.Installer.Installers![0].Scope = Scope.User;
            var context = new ManifestContext
            {
                Manifests = request.Manifests,
                Previous = request.Previous,
                OriginalBotSubmission = request.OriginalBotSubmission,
                Evidence = request.InstallerEvidence,
                Options = request.Options,
            };
            RulePipeline pipeline = RulePipeline.Create(
                [new ApplyOverridePackFieldsRule(request.OverridePacks)],
                request.Runtime,
                request.OverridePacks);
            _ = pipeline.Run(context);
            return new(
                context.Manifests,
                new(
                    [.. context.Executions],
                    [.. context.Changes],
                    [.. context.Findings],
                    [.. context.HumanCorrectionReviews],
                    [.. context.Trace]));
        }
    }

    private sealed class ChangedGeneratorRuleRunner : IWorkflowRuleRunner
    {
        public WorkflowRuleResult Run(WorkflowRuleRequest request)
        {
            request.Manifests.Installer.Installers![0].Scope = null;
            var context = new ManifestContext
            {
                Manifests = request.Manifests,
                Previous = request.Previous,
                OriginalBotSubmission = request.OriginalBotSubmission,
                Evidence = request.InstallerEvidence,
                Options = request.Options,
            };
            RulePipeline pipeline = RulePipeline.Create(
                [new ApplyOverridePackFieldsRule(request.OverridePacks)],
                request.Runtime,
                request.OverridePacks);
            _ = pipeline.Run(context);
            return new(
                context.Manifests,
                new(
                    [.. context.Executions],
                    [.. context.Changes],
                    [.. context.Findings],
                    [.. context.HumanCorrectionReviews],
                    [.. context.Trace]));
        }
    }

    private sealed class PublisherLearningRuleRunner(
        string generatedPublisherUrl) : IWorkflowRuleRunner
    {
        public WorkflowRuleResult Run(WorkflowRuleRequest request)
        {
            request.Manifests.DefaultLocale.PublisherUrl = generatedPublisherUrl;
            var context = new ManifestContext
            {
                Manifests = request.Manifests,
                Previous = request.Previous,
                OriginalBotSubmission = request.OriginalBotSubmission,
                Evidence = request.InstallerEvidence,
                Options = request.Options,
            };
            RulePipeline pipeline = RulePipeline.Create(
                [new ApplyOverridePackFieldsRule(request.OverridePacks)],
                request.Runtime,
                request.OverridePacks);
            _ = pipeline.Run(context);
            return new(
                context.Manifests,
                new(
                    [.. context.Executions],
                    [.. context.Changes],
                    [.. context.Findings],
                    [.. context.HumanCorrectionReviews],
                    [.. context.Trace]));
        }
    }

    private sealed class NoChangeLearningRuleRunner : IWorkflowRuleRunner
    {
        public WorkflowRuleResult Run(WorkflowRuleRequest request)
        {
            request.Manifests.Installer.Installers![0].Scope = Scope.User;
            var context = new ManifestContext
            {
                Manifests = request.Manifests,
                Previous = request.Previous,
                OriginalBotSubmission = request.OriginalBotSubmission,
                Evidence = request.InstallerEvidence,
                Options = request.Options,
            };
            RulePipeline pipeline = RulePipeline.Create(
                [new ApplyOverridePackFieldsRule(request.OverridePacks)],
                request.Runtime,
                request.OverridePacks);
            _ = pipeline.Run(context);
            context.Manifests.Installer.Installers![0].Scope = Scope.Machine;
            return new(
                context.Manifests,
                new(
                    [.. context.Executions],
                    [.. context.Changes],
                    [.. context.Findings],
                    [.. context.HumanCorrectionReviews],
                    [.. context.Trace]));
        }
    }

    private sealed class RootScopeLearningRuleRunner : IWorkflowRuleRunner
    {
        public WorkflowRuleResult Run(WorkflowRuleRequest request)
        {
            request.Manifests.Installer.Scope = Scope.User;
            var context = new ManifestContext
            {
                Manifests = request.Manifests,
                Previous = request.Previous,
                OriginalBotSubmission = request.OriginalBotSubmission,
                Evidence = request.InstallerEvidence,
                Options = request.Options,
            };
            RulePipeline pipeline = RulePipeline.Create(
                [new ApplyOverridePackFieldsRule(request.OverridePacks)],
                request.Runtime,
                request.OverridePacks);
            _ = pipeline.Run(context);
            return new(
                context.Manifests,
                new(
                    [.. context.Executions],
                    [.. context.Changes],
                    [.. context.Findings],
                    [.. context.HumanCorrectionReviews],
                    [.. context.Trace]));
        }
    }

    private static class ManifestSnapshotForTest
    {
        public static PackageManifests Clone(PackageManifests source)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                $"winmatsch-clone-{Guid.NewGuid():N}");
            try
            {
                PackageManifestIO.WriteDirectory(directory, source);
                return PackageManifestIO.LoadDirectory(directory);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    private sealed class MetadataReleaseSource : IWorkflowReleaseSource, IWorkflowReleaseMetadataSource
    {
        public int MetadataCalls { get; private set; }

        public Task<ImmutableArray<DiscoveredAsset>> DiscoverAsync(
            PackageIdentifier packageIdentifier,
            ReleaseRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<DiscoveredAsset>.Empty);

        public Task<WorkflowReleaseMetadata> DiscoverMetadataAsync(
            PackageIdentifier packageIdentifier,
            ReleaseRequest request,
            ImmutableArray<DiscoveredAsset> assets,
            CancellationToken cancellationToken)
        {
            MetadataCalls++;
            return Task.FromResult(new WorkflowReleaseMetadata(
                new PackageLocaleMetadata
                {
                    PackageLocale = new LanguageTag("und"),
                    PublisherUrl = "https://generated.example.test",
                    PackageUrl = "https://github.com/example/app",
                    License = "Apache-2.0",
                    Tags = ["windows", "utility"],
                    ReleaseNotes = "Release notes",
                    ReleaseNotesUrl = "https://github.com/example/app/releases/tag/v2.0.0",
                    Provenance = ImmutableDictionary.CreateRange(
                    [
                        KeyValuePair.Create(nameof(PackageLocaleMetadata.PublisherUrl), "fixture:publisher_url"),
                        KeyValuePair.Create(nameof(PackageLocaleMetadata.PackageUrl), "fixture:repository_url"),
                        KeyValuePair.Create(nameof(PackageLocaleMetadata.License), "fixture:license"),
                        KeyValuePair.Create(nameof(PackageLocaleMetadata.Tags), "fixture:topics"),
                        KeyValuePair.Create(nameof(PackageLocaleMetadata.ReleaseNotes), "fixture:release_body"),
                        KeyValuePair.Create(nameof(PackageLocaleMetadata.ReleaseNotesUrl), "fixture:release_url"),
                    ]),
                },
                RepositoryMetadataAvailability.Available,
                null));
        }
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

    private sealed class WritingRecordingTransaction :
        IWorkflowFileTransaction,
        IWorkflowFileTransactionRecovery
    {
        public int Calls { get; private set; }

        public async Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
        {
            Calls++;
            foreach (WorkflowFileChange change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(
                    outputDirectory,
                    change.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
                if (change.Kind == PlannedChangeKind.Delete)
                {
                    File.Delete(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(
                    path,
                    change.Content.ToArray(),
                    cancellationToken);
            }
        }

        public Task RecoverAsync(
            string outputDirectory,
            string operationLockKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveryAwareRecordingTransaction :
        IWorkflowFileTransaction,
        IWorkflowFileTransactionRecovery
    {
        public Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RecoverAsync(
            string outputDirectory,
            string operationLockKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ManifestWritingPackBlockingTransaction(
        string activePackPath) : IWorkflowFileTransaction
    {
        public async Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
        {
            foreach (WorkflowFileChange change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(
                    outputDirectory,
                    change.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
                if (change.Kind == PlannedChangeKind.Delete)
                {
                    File.Delete(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(
                    path,
                    change.Content.ToArray(),
                    cancellationToken);
            }

            Directory.CreateDirectory(activePackPath);
        }
    }

    private sealed class ManifestWritingProvenanceFailingTransaction : IWorkflowFileTransaction
    {
        public async Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
        {
            foreach (WorkflowFileChange change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(
                    outputDirectory,
                    change.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
                if (change.Kind == PlannedChangeKind.Delete)
                {
                    File.Delete(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, change.Content.ToArray(), cancellationToken);
            }

            throw new WorkflowCommittedProvenanceException(
                "Manifest committed but provenance failed.",
                new IOException("provenance failure"));
        }
    }

    private sealed class RejectingTransaction : IWorkflowFileTransaction
    {
        public Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
            => throw new IOException("Simulated manifest transaction failure.");
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

    private sealed class IoRecoveryFailingTransaction :
        IWorkflowFileTransaction,
        IWorkflowFileTransactionRecovery
    {
        public Task RecoverAsync(
            string outputDirectory,
            string operationLockKey,
            CancellationToken cancellationToken)
            => Task.FromException(new InvalidDataException("malformed recovery journal"));

        public Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Apply must not run after recovery failure.");
    }

    private sealed class ConflictingRecoveryTransaction :
        IWorkflowFileTransaction,
        IWorkflowFileTransactionRecovery
    {
        public Task RecoverAsync(
            string outputDirectory,
            string operationLockKey,
            CancellationToken cancellationToken)
            => Task.FromException(new WorkflowOperationException(
                WorkflowResultCode.Conflict,
                "Another operation owns the package lock."));

        public Task ApplyAsync(
            string outputDirectory,
            string operationLockKey,
            ImmutableArray<WorkflowFileChange> changes,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Apply must not run after recovery conflict.");
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
