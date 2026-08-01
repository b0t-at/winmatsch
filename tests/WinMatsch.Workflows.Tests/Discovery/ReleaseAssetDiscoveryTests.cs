using WinMatsch.GitHub;
using WinMatsch.Workflows.Discovery;
using Xunit;

namespace WinMatsch.Workflows.Tests.Discovery;

public sealed class ReleaseAssetDiscoveryTests
{
    [Fact]
    public void Enumerates_windows_assets_with_release_provenance_in_stable_order()
    {
        GitHubRelease older = CreateRelease(
            1,
            "v1.0.0",
            DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            new ReleaseAsset(
                12,
                "tool-windows-x64.zip",
                new("https://example.test/tool-windows-x64.zip"),
                "application/zip",
                42,
                0,
                DateTimeOffset.Parse("2025-01-01T00:00:00Z")),
            new ReleaseAsset(
                13,
                "source.zip",
                new("https://example.test/source.zip"),
                "application/zip",
                42,
                0,
                DateTimeOffset.Parse("2025-01-01T00:00:00Z")));
        GitHubRelease newer = CreateRelease(
            2,
            "v2.0.0",
            DateTimeOffset.Parse("2025-02-01T00:00:00Z"),
            new ReleaseAsset(
                22,
                "setup.exe",
                new("https://example.test/setup.exe"),
                "application/octet-stream",
                84,
                0,
                DateTimeOffset.Parse("2025-02-01T00:00:00Z")));

        var result = ReleaseAssetDiscovery.Discover([older, newer]);

        Assert.Equal([22L, 12L], result.Select(static asset => asset.AssetId));
        Assert.Equal("v2.0.0", result[0].ReleaseTag);
        Assert.Equal(84, result[0].DeclaredSize);
        Assert.Equal(new Uri("https://example.test/setup.exe"), result[0].DownloadUri);
    }

    [Fact]
    public async Task Async_discovery_propagates_cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ReleaseAssetDiscovery.DiscoverAsync(
                _ => Task.FromResult<IReadOnlyList<GitHubRelease>>([]),
                cancellationToken: source.Token));
    }

    private static GitHubRelease CreateRelease(
        long id,
        string tag,
        DateTimeOffset publishedAt,
        params ReleaseAsset[] assets)
        => new(
            id,
            tag,
            tag,
            null,
            new Uri($"https://example.test/releases/{id}"),
            IsDraft: false,
            IsPrerelease: false,
            publishedAt,
            assets);
}
