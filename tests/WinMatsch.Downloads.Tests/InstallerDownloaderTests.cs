using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace WinMatsch.Downloads.Tests;

public sealed class InstallerDownloaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "winmatsch-downloads-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Nothing was downloaded, or a file is still locked; temp cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, see above.
        }
    }

    [Fact]
    public async Task DownloadAsync_ComputesStreamedHashSizeAndMetadata()
    {
        byte[] payload = CreatePayload(300_000);
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, payload);
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
            return response;
        });
        using InstallerDownloader downloader = new(stub);
        string destination = Path.Combine(_tempDir, "nested", "dir");

        DownloadResult result = await downloader.DownloadAsync("https://example.com/files/app.exe", destination);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), result.Sha256.Normalized);
        Assert.Equal(payload.LongLength, result.SizeInBytes);
        Assert.Equal("app.exe", result.FileName);
        Assert.Equal(Path.Combine(destination, "app.exe"), result.FilePath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal("https://example.com/files/app.exe", result.InitialUrl);
        Assert.Equal("https://example.com/files/app.exe", result.FinalUrl);
        Assert.Equal("application/octet-stream", result.ContentType);
        Assert.Null(result.LastModified);
    }

    [Fact]
    public async Task DownloadAsync_UsesFileNameFromContentDisposition()
    {
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, CreatePayload(16));
            response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=\"my installer.exe\"");
            return response;
        });
        using InstallerDownloader downloader = new(stub);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/download", _tempDir);

        Assert.Equal("my installer.exe", result.FileName);
    }

    [Fact]
    public async Task DownloadAsync_PrefersRfc5987FileNameStarOverFileName()
    {
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, CreatePayload(16));
            response.Content.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                "attachment; filename=\"fallback.exe\"; filename*=UTF-8''na%C3%AFve%20setup.exe");
            return response;
        });
        using InstallerDownloader downloader = new(stub);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/download", _tempDir);

        Assert.Equal("naïve setup.exe", result.FileName);
    }

    [Fact]
    public async Task DownloadAsync_UsesPercentDecodedUrlSegment_WhenNoContentDisposition()
    {
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, CreatePayload(16)));
        using InstallerDownloader downloader = new(stub);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/dl/my%20app.exe?token=abc", _tempDir);

        Assert.Equal("my app.exe", result.FileName);
    }

    [Fact]
    public async Task DownloadAsync_SanitizesInvalidFileNameCharacters()
    {
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, CreatePayload(16));
            response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=\"we*ird:name?.exe\"");
            return response;
        });
        using InstallerDownloader downloader = new(stub);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/download", _tempDir);

        Assert.Equal("we_ird_name_.exe", result.FileName);
        Assert.True(File.Exists(result.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_FallsBackToDefaultFileName_WhenNothingUsable()
    {
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, CreatePayload(16)));
        using InstallerDownloader downloader = new(stub);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/", _tempDir);

        Assert.Equal("download", result.FileName);
    }

    [Fact]
    public async Task DownloadAsync_CapturesFinalUrlAfterRedirect()
    {
        byte[] payload = CreatePayload(64);
        var initial = new Uri("https://example.com/latest");
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            if (request.RequestUri == initial)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://cdn.example.com/app-2.0.exe");
                return redirect;
            }

            return Ok(request, payload);
        });
        using InstallerDownloader downloader = new(stub);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/latest", _tempDir);

        Assert.Equal("https://example.com/latest", result.InitialUrl);
        Assert.Equal("https://cdn.example.com/app-2.0.exe", result.FinalUrl);
        Assert.Equal("app-2.0.exe", result.FileName);
    }

    [Fact]
    public async Task DownloadAsync_CapturesLastModifiedHeader()
    {
        var lastModified = new DateTimeOffset(2026, 5, 4, 12, 30, 0, TimeSpan.Zero);
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, CreatePayload(16));
            response.Content.Headers.LastModified = lastModified;
            return response;
        });
        using InstallerDownloader downloader = new(stub);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/a.exe", _tempDir);

        Assert.Equal(lastModified, result.LastModified);
    }

    [Fact]
    public async Task DownloadAsync_RejectsPlainHttpByDefault()
    {
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, CreatePayload(16)));
        using InstallerDownloader downloader = new(stub);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.DownloadAsync("http://example.com/a.exe", _tempDir));
        Assert.Equal(0, stub.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_AllowsPlainHttp_WhenInsecureDownloadsEnabled()
    {
        byte[] payload = CreatePayload(32);
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, payload));
        using InstallerDownloader downloader = new(stub, new DownloaderOptions { AllowInsecureDownloads = true });

        DownloadResult result = await downloader.DownloadAsync("http://example.com/tool.exe", _tempDir);

        Assert.Equal("tool.exe", result.FileName);
        Assert.Equal(payload.LongLength, result.SizeInBytes);
    }

    [Theory]
    [InlineData("ftp://example.com/a.exe")]
    [InlineData("file:///c:/a.exe")]
    [InlineData("not a url")]
    public async Task DownloadAsync_RejectsUnsupportedUrls(string url)
    {
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, CreatePayload(16)));
        using InstallerDownloader downloader = new(stub);

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(url, _tempDir));
        Assert.Equal(0, stub.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RetriesTransient503_ThenSucceeds()
    {
        byte[] payload = CreatePayload(64);
        using StubHttpMessageHandler stub = new((request, requestNumber) => requestNumber == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request }
            : Ok(request, payload));
        using InstallerDownloader downloader = new(stub, new DownloaderOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) });

        DownloadResult result = await downloader.DownloadAsync("https://example.com/a.exe", _tempDir);

        Assert.Equal(2, stub.RequestCount);
        Assert.Equal(payload.LongLength, result.SizeInBytes);
    }

    [Fact]
    public async Task DownloadAsync_DoesNotRetryNonTransient404()
    {
        using StubHttpMessageHandler stub = new((request, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        using InstallerDownloader downloader = new(stub, new DownloaderOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) });

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadAsync("https://example.com/a.exe", _tempDir));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(1, stub.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ReportsMonotonicallyIncreasingProgress()
    {
        byte[] payload = CreatePayload(300_000);
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, payload));
        using InstallerDownloader downloader = new(stub);
        ProgressCollector progress = new();

        await downloader.DownloadAsync("https://example.com/a.exe", _tempDir, progress);

        IReadOnlyList<DownloadProgress> reports = progress.Reports;
        Assert.NotEmpty(reports);
        for (int i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].BytesReceived > reports[i - 1].BytesReceived, "BytesReceived must increase monotonically.");
        }

        Assert.All(reports, report => Assert.Equal(payload.LongLength, report.TotalBytes));
        Assert.Equal(payload.LongLength, reports[^1].BytesReceived);
    }

    [Fact]
    public async Task DownloadAsync_OverwritesExistingFile()
    {
        byte[] payload = CreatePayload(128);
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, payload));
        using InstallerDownloader downloader = new(stub);
        Directory.CreateDirectory(_tempDir);
        string targetPath = Path.Combine(_tempDir, "a.exe");
        await File.WriteAllBytesAsync(targetPath, [1, 2, 3]);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/a.exe", _tempDir);

        Assert.Equal(targetPath, result.FilePath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(targetPath));
    }

    [Fact]
    public async Task DownloadAsync_CleansUpPartFile_WhenStreamFailsMidDownload()
    {
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            var content = new StreamContent(new FaultyStream(CreatePayload(10_000)));
            content.Headers.ContentLength = 20_000;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
        });
        using InstallerDownloader downloader = new(stub, new DownloaderOptions { MaxRetryAttempts = 0 });

        await Assert.ThrowsAnyAsync<IOException>(() => downloader.DownloadAsync("https://example.com/big.exe", _tempDir));

        Assert.False(File.Exists(Path.Combine(_tempDir, "big.exe.part")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "big.exe")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_tempDir));
    }

    [Fact]
    public async Task DownloadManyAsync_PreservesInputOrder_AndHonorsMaxConcurrency()
    {
        const int UrlCount = 6;
        const int MaxConcurrency = 2;
        string[] urls = [.. Enumerable.Range(0, UrlCount).Select(i => $"https://example.com/files/file{i}.bin")];
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            // Payload size encodes the file index so order can be verified from the results.
            int index = int.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath)["file".Length..], CultureInfo.InvariantCulture);
            return Ok(request, CreatePayload(1_000 + index));
        })
        {
            PerRequestDelay = TimeSpan.FromMilliseconds(40),
        };
        using InstallerDownloader downloader = new(stub);

        IReadOnlyList<DownloadResult> results = await downloader.DownloadManyAsync(urls, _tempDir, MaxConcurrency);

        Assert.Equal(UrlCount, results.Count);
        for (int i = 0; i < UrlCount; i++)
        {
            Assert.Equal(urls[i], results[i].InitialUrl);
            Assert.Equal($"file{i}.bin", results[i].FileName);
            Assert.Equal(1_000 + i, results[i].SizeInBytes);
        }

        Assert.Equal(UrlCount, stub.RequestCount);
        Assert.InRange(stub.MaxObservedConcurrency, 1, MaxConcurrency);
    }

    [Fact]
    public async Task DownloadManyAsync_FailsFast_WhenOneDownloadFails()
    {
        string[] urls =
        [
            "https://example.com/files/ok0.bin",
            "https://example.com/files/missing.bin",
            "https://example.com/files/ok1.bin",
            "https://example.com/files/ok2.bin",
        ];
        using StubHttpMessageHandler stub = new((request, _) => request.RequestUri!.AbsolutePath.Contains("missing", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
            : Ok(request, CreatePayload(64)))
        {
            PerRequestDelay = TimeSpan.FromMilliseconds(10),
        };
        using InstallerDownloader downloader = new(stub);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadManyAsync(urls, _tempDir, maxConcurrency: 2));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 31) + 7);
        }

        return payload;
    }

    private static HttpResponseMessage Ok(HttpRequestMessage request, byte[] payload)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
            RequestMessage = request,
        };
}
