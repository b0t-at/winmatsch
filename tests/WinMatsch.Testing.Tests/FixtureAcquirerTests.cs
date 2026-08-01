using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Testing.Fixtures;
using WinMatsch.Testing.Infrastructure;
using Xunit;

namespace WinMatsch.Testing.Tests;

public sealed class FixtureAcquirerTests
{
    private static readonly string _cacheDirectory =
        Path.Combine(Path.GetTempPath(), "winmatsch-fixture-cache");

    [Fact]
    public async Task Hermetic_default_reports_uncached_fixture_as_unavailable_without_HTTP()
    {
        var handler = new StubHttpMessageHandler(
            _ => throw new InvalidOperationException("HTTP must not be used."));
        var fileSystem = new InMemoryFileSystem();
        var acquirer = new FixtureAcquirer(new HttpClient(handler), fileSystem);

        FixtureAcquisitionResult result = await acquirer.AcquireAsync(
            CreateAsset("payload"u8.ToArray()),
            new FixtureAcquisitionOptions { CacheDirectory = _cacheDirectory });

        Assert.False(result.IsAvailable);
        Assert.Equal(FixtureAcquisitionStatus.Unavailable, result.Status);
        Assert.Contains("network acquisition is disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Verified_cache_entry_is_reused_without_HTTP()
    {
        byte[] contents = "cached fixture"u8.ToArray();
        FixtureAsset asset = CreateAsset(contents);
        var handler = new StubHttpMessageHandler(
            _ => throw new InvalidOperationException("HTTP must not be used."));
        var fileSystem = new InMemoryFileSystem();
        string expectedPath = Path.Combine(
            _cacheDirectory,
            $"{asset.UpstreamSha256.ToLowerInvariant()}-{asset.FileName}");
        fileSystem.WriteAllBytes(expectedPath, contents);
        var acquirer = new FixtureAcquirer(new HttpClient(handler), fileSystem);

        FixtureAcquisitionResult result = await acquirer.AcquireAsync(
            asset,
            new FixtureAcquisitionOptions { CacheDirectory = _cacheDirectory });

        Assert.True(result.IsAvailable);
        Assert.Equal(FixtureAcquisitionSource.Cache, result.Source);
        Assert.Equal(expectedPath, result.Path);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Explicit_network_acquisition_verifies_and_populates_cache()
    {
        byte[] contents = "downloaded fixture"u8.ToArray();
        FixtureAsset asset = CreateAsset(contents);
        var handler = new StubHttpMessageHandler(
            request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(contents),
                RequestMessage = request,
            });
        var fileSystem = new InMemoryFileSystem();
        var acquirer = new FixtureAcquirer(new HttpClient(handler), fileSystem);
        var options = new FixtureAcquisitionOptions
        {
            CacheDirectory = _cacheDirectory,
            AllowNetwork = true,
        };

        FixtureAcquisitionResult downloaded = await acquirer.AcquireAsync(asset, options);
        FixtureAcquisitionResult cached = await acquirer.AcquireAsync(asset, options);

        Assert.True(downloaded.IsAvailable);
        Assert.Equal(FixtureAcquisitionSource.Network, downloaded.Source);
        Assert.Equal(contents, fileSystem.ReadAllBytes(downloaded.Path!));
        Assert.Equal(FixtureAcquisitionSource.Cache, cached.Source);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Checksum_mismatch_is_unavailable_and_not_cached()
    {
        FixtureAsset asset = CreateAsset("expected"u8.ToArray());
        var handler = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("different"u8.ToArray()),
            });
        var fileSystem = new InMemoryFileSystem();
        var acquirer = new FixtureAcquirer(new HttpClient(handler), fileSystem);

        FixtureAcquisitionResult result = await acquirer.AcquireAsync(
            asset,
            new FixtureAcquisitionOptions
            {
                CacheDirectory = _cacheDirectory,
                AllowNetwork = true,
            });

        Assert.False(result.IsAvailable);
        Assert.Contains("checksum", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fileSystem.Paths);
    }

    [Fact]
    public async Task Offline_network_is_a_clear_unavailable_result()
    {
        var handler = new StubHttpMessageHandler(
            _ => throw new HttpRequestException("offline"));
        var acquirer = new FixtureAcquirer(new HttpClient(handler), new InMemoryFileSystem());

        FixtureAcquisitionResult result = await acquirer.AcquireAsync(
            CreateAsset("payload"u8.ToArray()),
            new FixtureAcquisitionOptions
            {
                CacheDirectory = _cacheDirectory,
                AllowNetwork = true,
            });

        Assert.False(result.IsAvailable);
        Assert.Contains("offline", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_checksum_is_rejected_before_any_cache_or_network_access()
    {
        var handler = new StubHttpMessageHandler(
            _ => throw new InvalidOperationException("HTTP must not be used."));
        var fileSystem = new InMemoryFileSystem();
        var acquirer = new FixtureAcquirer(new HttpClient(handler), fileSystem);
        FixtureAsset asset = CreateAsset("payload"u8.ToArray()) with
        {
            UpstreamSha256 = "..\\outside-cache",
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => acquirer.AcquireAsync(
                asset,
                new FixtureAcquisitionOptions
                {
                    CacheDirectory = _cacheDirectory,
                    AllowNetwork = true,
                }));

        Assert.Empty(handler.Requests);
        Assert.Empty(fileSystem.Paths);
    }

    private static FixtureAsset CreateAsset(byte[] contents) => new()
    {
        FileName = "fixture.bin",
        Url = new Uri("https://fixtures.invalid/fixture.bin"),
        UpstreamSha256 = Convert.ToHexString(SHA256.HashData(contents)),
        SyntheticSha256 = Convert.ToHexString(SHA256.HashData(contents)),
        ExpectedArchitecture = "x64",
        ExpectedInstallerType = "portable",
    };
}
