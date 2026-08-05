using WinMatsch.GitHub;
using WinMatsch.Workflows.Discovery;
using Xunit;

namespace WinMatsch.Workflows.Tests.Discovery;

public sealed class ReleaseAssetDiscoveryTests
{
    [Theory]
    [InlineData(
        "https://github.com/acme/app/releases/download/v1/app.exe",
        "app",
        "v1")]
    [InlineData(
        "https://github.com/acme/releases/releases/download/v1/app.exe",
        "releases",
        "v1")]
    [InlineData(
        "https://github.com/acme/app/releases/latest/download/app.exe",
        "app",
        "latest")]
    public void GitHub_release_asset_identity_handles_immutable_alias_and_repository_names(
        string url,
        string repository,
        string tag)
    {
        Assert.True(
            GitHubReleaseAssetIdentity.TryParse(
                new Uri(url),
                out GitHubReleaseAssetIdentity identity));
        Assert.Equal("acme", identity.Repository.Owner);
        Assert.Equal(repository, identity.Repository.Name);
        Assert.Equal(tag, identity.ReleaseTag);
        Assert.Equal("app.exe", identity.AssetName);
    }

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

    [Fact]
    public void Product_name_containing_source_is_not_dropped_when_windows_is_explicit()
    {
        GitHubRelease release = CreateRelease(
            1,
            "v1.0.0",
            DateTimeOffset.UnixEpoch,
            new ReleaseAsset(
                1,
                "Resource-Editor-source-windows-x64.zip",
                new("https://example.test/Resource-Editor-source-windows-x64.zip"),
                "application/zip",
                1,
                0,
                DateTimeOffset.UnixEpoch));

        DiscoveredAsset asset = Assert.Single(ReleaseAssetDiscovery.Discover([release]));

        Assert.Equal("Resource-Editor-source-windows-x64.zip", asset.AssetName);
    }

    [Fact]
    public void Linux_architecture_asset_is_not_classified_as_windows()
    {
        GitHubRelease release = CreateRelease(
            1,
            "v1.0.0",
            DateTimeOffset.UnixEpoch,
            new ReleaseAsset(
                1,
                "tool-linux-x64.tar.gz",
                new("https://example.test/tool-linux-x64.tar.gz"),
                "application/gzip",
                1,
                0,
                DateTimeOffset.UnixEpoch));

        Assert.Empty(ReleaseAssetDiscovery.Discover([release]));
    }

    [Fact]
    public void Ambiguous_architecture_archive_is_retained_for_mapping_review()
    {
        GitHubRelease release = CreateRelease(
            1,
            "v1.0.0",
            DateTimeOffset.UnixEpoch,
            new ReleaseAsset(
                1,
                "tool-windows-x64-arm64.zip",
                new("https://example.test/tool-windows-x64-arm64.zip"),
                "application/zip",
                1,
                0,
                DateTimeOffset.UnixEpoch));

        Assert.Single(ReleaseAssetDiscovery.Discover([release]));
    }

    [Fact]
    public void Architecture_only_archive_is_not_assumed_to_be_windows()
    {
        GitHubRelease release = CreateRelease(
            1,
            "v1.0.0",
            DateTimeOffset.UnixEpoch,
            new ReleaseAsset(
                1,
                "tool-x64.zip",
                new("https://example.test/tool-x64.zip"),
                "application/zip",
                1,
                0,
                DateTimeOffset.UnixEpoch));

        Assert.Empty(ReleaseAssetDiscovery.Discover([release]));
    }

    [Fact]
    public void Windows_only_extension_is_retained_even_when_product_name_contains_source()
    {
        GitHubRelease release = CreateRelease(
            1,
            "v1.0.0",
            DateTimeOffset.UnixEpoch,
            new ReleaseAsset(
                1,
                "Open-Source-Setup.exe",
                new("https://example.test/Open-Source-Setup.exe"),
                "application/octet-stream",
                1,
                0,
                DateTimeOffset.UnixEpoch));

        Assert.Single(ReleaseAssetDiscovery.Discover([release]));
    }

    [Fact]
    public void Conflicting_operating_system_tokens_are_retained_and_marked()
    {
        GitHubRelease release = CreateRelease(
            1,
            "v1.0.0",
            DateTimeOffset.UnixEpoch,
            new ReleaseAsset(
                1,
                "tool-windows-linux-x64.zip",
                new("https://example.test/tool-windows-linux-x64.zip"),
                "application/zip",
                1,
                0,
                DateTimeOffset.UnixEpoch));

        DiscoveredAsset asset = Assert.Single(ReleaseAssetDiscovery.Discover([release]));

        Assert.True(asset.HasOperatingSystemConflict);
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
