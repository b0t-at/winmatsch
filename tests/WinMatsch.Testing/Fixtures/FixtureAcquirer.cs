using System.Net;
using System.Security.Cryptography;
using WinMatsch.Testing.Infrastructure;

namespace WinMatsch.Testing.Fixtures;

public enum FixtureAcquisitionStatus
{
    Available,
    Unavailable,
}

public enum FixtureAcquisitionSource
{
    None,
    Cache,
    Network,
}

public sealed record FixtureAcquisitionResult(
    FixtureAcquisitionStatus Status,
    FixtureAcquisitionSource Source,
    string? Path,
    string Message)
{
    public bool IsAvailable => Status == FixtureAcquisitionStatus.Available;
}

public sealed record FixtureAcquisitionOptions
{
    public required string CacheDirectory { get; init; }

    public bool AllowNetwork { get; init; }
}

public sealed class FixtureAcquirer(HttpClient httpClient, ITestFileSystem fileSystem)
{
    public async Task<FixtureAcquisitionResult> AcquireAsync(
        FixtureAsset asset,
        FixtureAcquisitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheDirectory);
        ValidateSha256(asset.UpstreamSha256);

        string fileName = Path.GetFileName(asset.FileName);
        if (!string.Equals(fileName, asset.FileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Fixture asset file names must not contain directory segments.",
                nameof(asset));
        }

        fileSystem.CreateDirectory(options.CacheDirectory);
        string cachePath = Path.Combine(
            options.CacheDirectory,
            $"{asset.UpstreamSha256.ToLowerInvariant()}-{fileName}");

        if (fileSystem.FileExists(cachePath) && HasExpectedChecksum(cachePath, asset.UpstreamSha256))
        {
            return new FixtureAcquisitionResult(
                FixtureAcquisitionStatus.Available,
                FixtureAcquisitionSource.Cache,
                cachePath,
                "Checksum-pinned fixture is available in the local cache.");
        }

        if (!options.AllowNetwork)
        {
            string reason = fileSystem.FileExists(cachePath)
                ? "The cached fixture failed checksum verification and network acquisition is disabled."
                : "The fixture is not cached and network acquisition is disabled.";
            return new FixtureAcquisitionResult(
                FixtureAcquisitionStatus.Unavailable,
                FixtureAcquisitionSource.None,
                null,
                reason);
        }

        string partialPath = $"{cachePath}.{Guid.NewGuid():N}.partial";
        fileSystem.DeleteFile(partialPath);
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                asset.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is not HttpStatusCode.OK)
            {
                return new FixtureAcquisitionResult(
                    FixtureAcquisitionStatus.Unavailable,
                    FixtureAcquisitionSource.None,
                    null,
                    $"Fixture download returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (Stream destination = fileSystem.CreateFile(partialPath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (!HasExpectedChecksum(partialPath, asset.UpstreamSha256))
            {
                return new FixtureAcquisitionResult(
                    FixtureAcquisitionStatus.Unavailable,
                    FixtureAcquisitionSource.None,
                    null,
                    "Downloaded fixture failed checksum verification.");
            }

            fileSystem.MoveFile(partialPath, cachePath, overwrite: true);
            return new FixtureAcquisitionResult(
                FixtureAcquisitionStatus.Available,
                FixtureAcquisitionSource.Network,
                cachePath,
                "Fixture was downloaded and verified against its pinned checksum.");
        }
        catch (HttpRequestException exception)
        {
            return new FixtureAcquisitionResult(
                FixtureAcquisitionStatus.Unavailable,
                FixtureAcquisitionSource.None,
                null,
                $"Fixture network acquisition is unavailable: {exception.Message}");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new FixtureAcquisitionResult(
                FixtureAcquisitionStatus.Unavailable,
                FixtureAcquisitionSource.None,
                null,
                $"Fixture network acquisition timed out: {exception.Message}");
        }
        finally
        {
            fileSystem.DeleteFile(partialPath);
        }
    }

    private bool HasExpectedChecksum(string path, string expectedSha256)
    {
        using Stream stream = fileSystem.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != SHA256.HashSizeInBytes * 2 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "Fixture asset SHA-256 values must contain exactly 64 hexadecimal characters.",
                nameof(value));
        }
    }
}
