using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.Operations;

public sealed class OverridePackStoreTests
{
    [Fact]
    public async Task Concurrent_stale_write_is_rejected_deterministically()
    {
        using var temporary = new StoreDirectory();
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = temporary.Path,
        });
        PackageIdentifier package = new("Example.App");
        OverridePackStoreSnapshot first = await store.LoadAsync(package, true, CancellationToken.None);
        OverridePackStoreSnapshot second = await store.LoadAsync(package, true, CancellationToken.None);

        await store.WriteAsync(
            new(package, Pack(package, "DefaultLocale.PublisherUrl"), first.ContentSha256, first.FormatVersion),
            CancellationToken.None);

        await Assert.ThrowsAsync<OverridePackStoreConflictException>(() => store.WriteAsync(
            new(package, Pack(package, "DefaultLocale.PackageUrl"), second.ContentSha256, second.FormatVersion),
            CancellationToken.None));
    }

    [Fact]
    public async Task Package_casing_shares_one_pack_and_lock_identity()
    {
        using var temporary = new StoreDirectory();
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = temporary.Path,
        });
        PackageIdentifier mixed = new("Example.App");
        PackageIdentifier lower = new("example.app");
        OverridePackStoreSnapshot empty = await store.LoadAsync(
            mixed,
            allowRecoveryWrites: true,
            CancellationToken.None);
        _ = await store.WriteAsync(
            new(
                mixed,
                Pack(mixed, "DefaultLocale.PublisherUrl"),
                empty.ContentSha256,
                empty.FormatVersion),
            CancellationToken.None);

        OverridePackStoreSnapshot loaded = await store.LoadAsync(
            lower,
            allowRecoveryWrites: false,
            CancellationToken.None);

        Assert.NotNull(loaded.Pack);
        Assert.Single(Directory.EnumerateFiles(temporary.Path, "*.yaml"));
    }

    [Fact]
    public async Task Corrupt_primary_recovers_from_last_verified_backup()
    {
        using var temporary = new StoreDirectory();
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = temporary.Path,
        });
        PackageIdentifier package = new("Example.App");
        OverridePackStoreSnapshot empty = await store.LoadAsync(package, true, CancellationToken.None);
        OverridePackWriteResult first = await store.WriteAsync(
            new(package, Pack(package, "DefaultLocale.PublisherUrl"), empty.ContentSha256, empty.FormatVersion),
            CancellationToken.None);
        OverridePackStoreSnapshot current = await store.LoadAsync(package, true, CancellationToken.None);
        _ = await store.WriteAsync(
            new(package, Pack(package, "DefaultLocale.PackageUrl"), current.ContentSha256, current.FormatVersion),
            CancellationToken.None);
        await File.WriteAllTextAsync(first.Path, "not: [valid");

        OverridePackStoreSnapshot recovered = await store.LoadAsync(package, true, CancellationToken.None);

        Assert.True(recovered.RecoveredFromBackup);
        Assert.True(recovered.QuarantinedCorruptPrimary);
        OverridePack recoveredPack = Assert.IsType<OverridePack>(recovered.Pack);
        Assert.Equal(["DefaultLocale.PublisherUrl"], recoveredPack.PreservedFields.ToArray());
        Assert.Equal(
            OverridePackYaml.Write(recoveredPack),
            OverridePackYaml.Write(OverridePackYaml.ReadFile(first.Path)));
        Assert.True(File.Exists($"{first.Path}.corrupt"));
    }

    [Fact]
    public async Task Corrupt_pack_without_verified_backup_uses_structured_recovery_error()
    {
        using var temporary = new StoreDirectory();
        Directory.CreateDirectory(temporary.Path);
        string primary = Path.Combine(temporary.Path, "EXAMPLE.APP.yaml");
        await File.WriteAllTextAsync(primary, "not: [valid");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = temporary.Path,
        });

        OverridePackStoreRecoveryException exception =
            await Assert.ThrowsAsync<OverridePackStoreRecoveryException>(
                () => store.LoadAsync(
                    new PackageIdentifier("Example.App"),
                    allowRecoveryWrites: false,
                    CancellationToken.None));

        Assert.Contains("no verified backup", exception.Message, StringComparison.Ordinal);
        Assert.False(exception.JournalRetained);
    }

    [Fact]
    public async Task Identity_corrupt_primary_recovers_only_from_matching_backup()
    {
        using var temporary = new StoreDirectory();
        Directory.CreateDirectory(temporary.Path);
        string primary = Path.Combine(temporary.Path, "EXAMPLE.APP.yaml");
        PackageIdentifier expected = new("Example.App");
        OverridePackYaml.WriteFile(
            primary,
            Pack(new PackageIdentifier("Other.App"), "DefaultLocale.PackageUrl"));
        OverridePackYaml.WriteFile(
            $"{primary}.bak",
            Pack(expected, "DefaultLocale.PublisherUrl"));
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = temporary.Path,
        });

        OverridePackStoreSnapshot recovered = await store.LoadAsync(
            expected,
            allowRecoveryWrites: true,
            CancellationToken.None);

        Assert.Equal(expected, Assert.IsType<OverridePack>(recovered.Pack).PackageIdentifier);
        Assert.True(recovered.QuarantinedCorruptPrimary);
    }

    [Fact]
    public void Package_identifier_rejects_path_traversal_before_store_resolution()
    {
        Assert.Throws<ArgumentException>(() => new PackageIdentifier("../escape"));
    }

    [Fact]
    public async Task Manifest_changes_require_durable_journal_output_root()
    {
        using var temporary = new StoreDirectory();
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = temporary.Path,
        });
        PackageIdentifier package = new("Example.App");

        await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(
            new(
                package,
                Pack(package, "DefaultLocale.PublisherUrl"),
                ExpectedContentSha256: null,
                ExpectedFormatVersion: null,
                OutputDirectory: null,
                ManifestChanges:
                [
                    Update("manifest.yaml", "before", "after"),
                ]),
            CancellationToken.None));
    }

    [Fact]
    public async Task Staged_pack_is_inactive_until_commit_and_stale_stage_is_recovered()
    {
        using var temporary = new StoreDirectory();
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = temporary.Path,
        });
        PackageIdentifier package = new("Example.App");
        OverridePackStoreSnapshot empty = await store.LoadAsync(package, false, CancellationToken.None);
        await using IOverridePackWriteStage stage = await store.StageAsync(
            new(
                package,
                Pack(package, "DefaultLocale.PublisherUrl"),
                empty.ContentSha256,
                empty.FormatVersion),
            CancellationToken.None);

        OverridePackStoreSnapshot whileStaged = await store.LoadAsync(
            package,
            false,
            CancellationToken.None);
        Assert.Null(whileStaged.Pack);
        await stage.AbortAsync();

        string pending = Path.Combine(temporary.Path, "EXAMPLE.APP.yaml.pending");
        OverridePackYaml.WriteFile(pending, Pack(package, "DefaultLocale.PackageUrl"));
        OverridePackStoreSnapshot recovered = await store.LoadAsync(
            package,
            true,
            CancellationToken.None);

        Assert.Null(recovered.Pack);
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public async Task Recovery_activates_approved_pack_when_manifests_are_committed()
    {
        using var temporary = new StoreDirectory();
        string output = Path.Combine(temporary.Path, "output");
        string storeRoot = Path.Combine(temporary.Path, "store");
        Directory.CreateDirectory(output);
        string manifest = Path.Combine(output, "manifest.yaml");
        await File.WriteAllTextAsync(manifest, "before");
        PackageIdentifier package = new("Example.App");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = storeRoot,
        });
        WorkflowFileChange change = Update("manifest.yaml", "before", "after");
        await using IOverridePackWriteStage stage = await store.StageAsync(
            new(
                package,
                Pack(package, "DefaultLocale.PublisherUrl"),
                ExpectedContentSha256: null,
                ExpectedFormatVersion: null,
                OutputDirectory: output,
                ManifestChanges: [change]),
            CancellationToken.None);

        await File.WriteAllTextAsync(manifest, "after");
        await stage.RetainForRecoveryAsync();
        await using (IOverridePackRecoveryLease lease =
                     await store.AcquireRecoveryLeaseAsync(package, CancellationToken.None))
        {
            Assert.Equal(Path.GetFullPath(output), lease.PendingOutputDirectory);
        }
        OverridePackStoreSnapshot pending = await store.LoadAsync(
            package,
            allowRecoveryWrites: false,
            CancellationToken.None);
        OverridePackStoreSnapshot recovered = await store.LoadAfterManifestRecoveryAsync(
            package,
            CancellationToken.None);

        Assert.Null(pending.Pack);
        Assert.True(pending.PendingActivation);
        Assert.True(recovered.ActivatedFromRecovery);
        Assert.Equal(
            ["DefaultLocale.PublisherUrl"],
            Assert.IsType<OverridePack>(recovered.Pack).PreservedFields.ToArray());
    }

    [Fact]
    public async Task Recovery_discards_approved_pack_when_manifests_rolled_back()
    {
        using var temporary = new StoreDirectory();
        string output = Path.Combine(temporary.Path, "output");
        string storeRoot = Path.Combine(temporary.Path, "store");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.yaml"), "before");
        PackageIdentifier package = new("Example.App");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = storeRoot,
        });
        await using IOverridePackWriteStage stage = await store.StageAsync(
            new(
                package,
                Pack(package, "DefaultLocale.PublisherUrl"),
                ExpectedContentSha256: null,
                ExpectedFormatVersion: null,
                OutputDirectory: output,
                ManifestChanges: [Update("manifest.yaml", "before", "after")]),
            CancellationToken.None);

        await stage.RetainForRecoveryAsync();
        OverridePackStoreSnapshot recovered = await store.LoadAsync(
            package,
            allowRecoveryWrites: true,
            CancellationToken.None);

        Assert.Null(recovered.Pack);
        Assert.False(File.Exists(Path.Combine(storeRoot, "EXAMPLE.APP.yaml.pending")));
        Assert.False(File.Exists(Path.Combine(storeRoot, "EXAMPLE.APP.yaml.transaction")));
    }

    [Fact]
    public async Task Mixed_manifest_state_retains_journal_for_deterministic_recovery()
    {
        using var temporary = new StoreDirectory();
        string output = Path.Combine(temporary.Path, "output");
        string storeRoot = Path.Combine(temporary.Path, "store");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "a.yaml"), "before-a");
        await File.WriteAllTextAsync(Path.Combine(output, "b.yaml"), "before-b");
        PackageIdentifier package = new("Example.App");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = storeRoot,
        });
        await using IOverridePackWriteStage stage = await store.StageAsync(
            new(
                package,
                Pack(package, "DefaultLocale.PublisherUrl"),
                ExpectedContentSha256: null,
                ExpectedFormatVersion: null,
                OutputDirectory: output,
                ManifestChanges:
                [
                    Update("a.yaml", "before-a", "after-a"),
                    Update("b.yaml", "before-b", "after-b"),
                ]),
            CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(output, "a.yaml"), "after-a");
        await stage.RetainForRecoveryAsync();

        OverridePackStoreRecoveryException exception =
            await Assert.ThrowsAsync<OverridePackStoreRecoveryException>(
            () => store.LoadAsync(package, allowRecoveryWrites: true, CancellationToken.None));
        Assert.True(exception.JournalRetained);
        Assert.True(File.Exists(Path.Combine(storeRoot, "EXAMPLE.APP.yaml.pending")));
        Assert.True(File.Exists(Path.Combine(storeRoot, "EXAMPLE.APP.yaml.transaction")));
    }

    [Fact]
    public async Task Cancellation_after_manifest_boundary_keeps_pack_recoverable()
    {
        using var temporary = new StoreDirectory();
        string output = Path.Combine(temporary.Path, "output");
        string storeRoot = Path.Combine(temporary.Path, "store");
        Directory.CreateDirectory(output);
        string manifest = Path.Combine(output, "manifest.yaml");
        await File.WriteAllTextAsync(manifest, "before");
        PackageIdentifier package = new("Example.App");
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = storeRoot,
        });
        await using IOverridePackWriteStage stage = await store.StageAsync(
            new(
                package,
                Pack(package, "DefaultLocale.PublisherUrl"),
                ExpectedContentSha256: null,
                ExpectedFormatVersion: null,
                OutputDirectory: output,
                ManifestChanges: [Update("manifest.yaml", "before", "after")]),
            CancellationToken.None);
        await File.WriteAllTextAsync(manifest, "after");
        await stage.MarkManifestCommittedAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stage.CommitAsync(cancellation.Token));
        await stage.RetainForRecoveryAsync();
        OverridePackStoreSnapshot recovered = await store.LoadAsync(
            package,
            allowRecoveryWrites: true,
            CancellationToken.None);

        Assert.True(recovered.ActivatedFromRecovery);
        Assert.NotNull(recovered.Pack);
    }

    [Fact]
    public async Task Abort_cleanup_failure_still_releases_pack_lock()
    {
        using var temporary = new StoreDirectory();
        var store = new FileOverridePackStore(new OverridePackStoreOptions
        {
            RootDirectory = temporary.Path,
        });
        PackageIdentifier package = new("Example.App");
        IOverridePackWriteStage stage = await store.StageAsync(
            new(
                package,
                Pack(package, "DefaultLocale.PublisherUrl"),
                ExpectedContentSha256: null,
                ExpectedFormatVersion: null),
            CancellationToken.None);
        string pending = Path.Combine(temporary.Path, "EXAMPLE.APP.yaml.pending");
        File.Delete(pending);
        Directory.CreateDirectory(pending);
        await File.WriteAllTextAsync(Path.Combine(pending, "blocker"), "block");

        await Assert.ThrowsAnyAsync<Exception>(() => stage.AbortAsync());
        Assert.True(stage.RecoveryRetained);
        await stage.DisposeAsync();
        Directory.Delete(pending, recursive: true);

        OverridePackStoreSnapshot loaded = await store.LoadAsync(
            package,
            allowRecoveryWrites: true,
            CancellationToken.None);
        Assert.Null(loaded.Pack);
    }

    private static OverridePack Pack(PackageIdentifier package, string preserved)
        => new()
        {
            PackageIdentifier = package,
            PreservedFields = [preserved],
        };

    private static WorkflowFileChange Update(string path, string before, string after)
        => new(
            PlannedChangeKind.Update,
            path,
            System.Text.Encoding.UTF8.GetBytes(after),
            ExpectedFileState.Present,
            WorkflowFileChange.Hash(System.Text.Encoding.UTF8.GetBytes(before)));

    private sealed class StoreDirectory : IDisposable
    {
        public StoreDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"winmatsch-override-store-{Guid.NewGuid():N}");
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
