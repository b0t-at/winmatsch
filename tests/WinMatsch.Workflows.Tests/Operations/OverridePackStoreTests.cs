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
        OverridePack recoveredPack = Assert.IsType<OverridePack>(recovered.Pack);
        Assert.Equal(["DefaultLocale.PublisherUrl"], recoveredPack.PreservedFields.ToArray());
        Assert.Equal(
            OverridePackYaml.Write(recoveredPack),
            OverridePackYaml.Write(OverridePackYaml.ReadFile(first.Path)));
    }

    [Fact]
    public void Package_identifier_rejects_path_traversal_before_store_resolution()
    {
        Assert.Throws<ArgumentException>(() => new PackageIdentifier("../escape"));
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

        string pending = Path.Combine(temporary.Path, "Example.App.yaml.pending");
        OverridePackYaml.WriteFile(pending, Pack(package, "DefaultLocale.PackageUrl"));
        OverridePackStoreSnapshot recovered = await store.LoadAsync(
            package,
            true,
            CancellationToken.None);

        Assert.Null(recovered.Pack);
        Assert.False(File.Exists(pending));
    }

    private static OverridePack Pack(PackageIdentifier package, string preserved)
        => new()
        {
            PackageIdentifier = package,
            PreservedFields = [preserved],
        };

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
