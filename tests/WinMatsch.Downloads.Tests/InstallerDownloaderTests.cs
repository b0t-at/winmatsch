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

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("prn.exe", "_prn.exe")]
    [InlineData("AuX.tar.gz", "_AuX.tar.gz")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("COM1.exe", "_COM1.exe")]
    [InlineData("com9", "_com9")]
    [InlineData("LPT1.bin", "_LPT1.bin")]
    [InlineData("lpt9.txt", "_lpt9.txt")]
    [InlineData("COM10.exe", "COM10.exe")]
    [InlineData("console.exe", "console.exe")]
    public async Task DownloadAsync_SanitizesWindowsReservedDeviceNamesOnEveryPlatform(
        string suppliedName,
        string expectedName)
    {
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, CreatePayload(16));
            response.Content.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                $"attachment; filename=\"{suppliedName}\"");
            return response;
        });
        using InstallerDownloader downloader = new(stub);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/download", _tempDir);

        Assert.Equal(expectedName, result.FileName);
        Assert.True(File.Exists(result.FilePath));
    }

    [Theory]
    [InlineData("COM\u00B9.exe", "_COM\u00B9.exe")]
    [InlineData("com\u00B2", "_com\u00B2")]
    [InlineData("LPT\u00B3.bin", "_LPT\u00B3.bin")]
    public async Task DownloadAsync_SanitizesSuperscriptWindowsReservedDeviceNames(
        string suppliedName,
        string expectedName)
    {
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, CreatePayload(16)));
        using InstallerDownloader downloader = new(stub);
        string encodedName = Uri.EscapeDataString(suppliedName);

        DownloadResult result = await downloader.DownloadAsync(
            "https://example.com/" + encodedName,
            _tempDir);

        Assert.Equal(expectedName, result.FileName);
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
    public async Task DownloadAsync_PreservesInitialExtension_WhenRedirectTargetHasNoFileExtension()
    {
        byte[] payload = CreatePayload(64);
        var initial = new Uri("https://github.com/example/app/releases/download/v1/app%401.0.zip");
        using StubHttpMessageHandler stub = new((request, _) =>
        {
            if (request.RequestUri == initial)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://release-assets.example.com/51b1c6bf-bf6b-4511-a554-dac5653b7425");
                return redirect;
            }

            return Ok(request, payload);
        });
        using InstallerDownloader downloader = new(stub);

        DownloadResult result = await downloader.DownloadAsync(initial.AbsoluteUri, _tempDir);

        Assert.Equal("app@1.0.zip", result.FileName);
        Assert.Equal("https://release-assets.example.com/51b1c6bf-bf6b-4511-a554-dac5653b7425", result.FinalUrl);
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

        DownloadHttpException exception = await Assert.ThrowsAsync<DownloadHttpException>(
            () => downloader.DownloadAsync("https://example.com/a.exe", _tempDir));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(DownloadFailureKind.PermanentHttp, exception.FailureKind);
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
    public async Task DownloadAsync_PreservesExistingDifferentFileAndUsesContentAddressedPath()
    {
        byte[] payload = CreatePayload(128);
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, payload));
        using InstallerDownloader downloader = new(stub);
        Directory.CreateDirectory(_tempDir);
        string targetPath = Path.Combine(_tempDir, "a.exe");
        await File.WriteAllBytesAsync(targetPath, [1, 2, 3]);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/a.exe", _tempDir);

        Assert.NotEqual(targetPath, result.FilePath);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(targetPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.StartsWith("sha256-", result.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_RetriesAStalledBody_ThenSucceeds()
    {
        byte[] payload = CreatePayload(64);
        using StubHttpMessageHandler stub = new((request, requestNumber) => requestNumber == 1
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream(CreatePayload(16))),
                RequestMessage = request,
            }
            : Ok(request, payload));
        using InstallerDownloader downloader = new(stub, new DownloaderOptions
        {
            StallTimeout = TimeSpan.FromMilliseconds(200),
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        });

        DownloadResult result = await downloader.DownloadAsync("https://example.com/a.exe", _tempDir);

        Assert.Equal(2, stub.RequestCount);
        Assert.Equal(payload.LongLength, result.SizeInBytes);
    }

    [Fact]
    public async Task DownloadAsync_FailsFast_WhenTheBodyKeepsStalling()
    {
        using StubHttpMessageHandler stub = new((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(CreatePayload(16))),
            RequestMessage = request,
        });
        using InstallerDownloader downloader = new(stub, new DownloaderOptions
        {
            StallTimeout = TimeSpan.FromMilliseconds(100),
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryAttempts = 1,
        });

        DownloadNetworkException exception = await Assert.ThrowsAsync<DownloadNetworkException>(
            () => downloader.DownloadAsync("https://example.com/a.exe", _tempDir));

        HttpRequestException transportException = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Contains("stalled", transportException.Message, StringComparison.Ordinal);
        Assert.Equal(2, stub.RequestCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_tempDir));
    }

    [Fact]
    public async Task DownloadAsync_FailsFast_WhenHeadersNeverArrive()
    {
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, CreatePayload(16)))
        {
            PerRequestDelay = TimeSpan.FromMinutes(10),
        };
        using InstallerDownloader downloader = new(stub, new DownloaderOptions
        {
            StallTimeout = TimeSpan.FromMilliseconds(100),
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryAttempts = 0,
        });

        DownloadNetworkException exception = await Assert.ThrowsAsync<DownloadNetworkException>(
            () => downloader.DownloadAsync("https://example.com/a.exe", _tempDir));

        HttpRequestException transportException = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Contains("response headers", transportException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void InvalidStallAndConnectTimeouts_AreRejected(int seconds)
    {
        using StubHttpMessageHandler stub = new((request, _) => Ok(request, CreatePayload(16)));

        Assert.Throws<ArgumentOutOfRangeException>(() => new InstallerDownloader(
            stub,
            new DownloaderOptions { StallTimeout = TimeSpan.FromSeconds(seconds) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstallerDownloader(
            stub,
            new DownloaderOptions { ConnectTimeout = TimeSpan.FromSeconds(seconds) }));
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

        DownloadNetworkException exception = await Assert.ThrowsAsync<DownloadNetworkException>(
            () => downloader.DownloadAsync("https://example.com/big.exe", _tempDir));

        HttpRequestException transportException = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.IsType<IOException>(transportException.InnerException);
        Assert.Equal(DownloadFailureKind.TransientNetwork, exception.FailureKind);
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
        using CoordinatedHttpMessageHandler handler = new(MaxConcurrency, request =>
        {
            // Payload size encodes the file index so order can be verified from the results.
            int index = int.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath)["file".Length..], CultureInfo.InvariantCulture);
            return Ok(request, CreatePayload(1_000 + index));
        });
        using InstallerDownloader downloader = new(handler);

        IReadOnlyList<DownloadResult> results = await downloader.DownloadManyAsync(urls, _tempDir, MaxConcurrency);

        Assert.Equal(UrlCount, results.Count);
        for (int i = 0; i < UrlCount; i++)
        {
            Assert.Equal(urls[i], results[i].InitialUrl);
            Assert.Equal($"file{i}.bin", results[i].FileName);
            Assert.Equal(1_000 + i, results[i].SizeInBytes);
        }

        Assert.Equal(UrlCount, handler.RequestCount);
        Assert.Equal(MaxConcurrency, handler.MaxObservedConcurrency);
    }

    [Fact]
    public async Task DownloadManyAsync_ContentAddressesDistinctPayloadsWithSameResolvedFileName()
    {
        const int MaxConcurrency = 2;
        string[] urls =
        [
            "https://example.com/first/setup.exe",
            "https://example.com/second/setup.exe",
        ];
        byte[][] payloads =
        [
            CreatePayload(1_001),
            CreatePayload(1_002),
        ];
        using CoordinatedHttpMessageHandler handler = new(MaxConcurrency, request =>
        {
            int index = request.RequestUri!.AbsolutePath.Contains("/first/", StringComparison.Ordinal) ? 0 : 1;
            return Ok(request, payloads[index]);
        });
        using InstallerDownloader downloader = new(handler);

        IReadOnlyList<DownloadResult> results = await downloader.DownloadManyAsync(
            urls,
            _tempDir,
            MaxConcurrency);

        Assert.Equal(MaxConcurrency, handler.MaxObservedConcurrency);
        Assert.NotEqual(results[0].FilePath, results[1].FilePath);
        for (int index = 0; index < results.Count; index++)
        {
            DownloadResult result = results[index];
            byte[] persisted = await File.ReadAllBytesAsync(result.FilePath);
            Assert.Equal(payloads[index], persisted);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(persisted)), result.Sha256.Normalized);
            Assert.Equal(persisted.LongLength, result.SizeInBytes);
            Assert.Equal(Path.GetFileName(result.FilePath), result.FileName);
        }

        Assert.Equal(2, Directory.EnumerateFiles(_tempDir).Count());
        Assert.DoesNotContain(Directory.EnumerateFiles(_tempDir), path => path.Contains(".part.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadManyAsync_RetriesSharingViolationDuringCoordinatedFinalization()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int MaxConcurrency = 2;
        string[] urls =
        [
            "https://example.com/first/setup.exe",
            "https://example.com/second/setup.exe",
        ];
        byte[][] payloads =
        [
            CreatePayload(2_001),
            CreatePayload(2_002),
        ];
        using CoordinatedHttpMessageHandler handler = new(MaxConcurrency, request =>
        {
            int index = request.RequestUri!.AbsolutePath.Contains("/first/", StringComparison.Ordinal) ? 0 : 1;
            return Ok(request, payloads[index]);
        });
        var preferredHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharingViolationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSharingViolation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object preferredHandleGate = new();
        FileStream? preferredHandle = null;
        bool releasePreferredHandle = false;
        int holdPreferred = 0;
        using InstallerDownloader downloader = new(
            handler,
            new DownloaderOptions
            {
                DestinationHooks = new DownloadDestinationHooks
                {
                    AfterPublishAsync = (path, _) =>
                    {
                        if (Path.GetFileName(path) == "setup.exe"
                            && Interlocked.CompareExchange(ref holdPreferred, 1, 0) == 0)
                        {
                            var newHandle = new FileStream(
                                path,
                                FileMode.Open,
                                FileAccess.ReadWrite,
                                FileShare.None);
                            lock (preferredHandleGate)
                            {
                                if (releasePreferredHandle)
                                {
                                    newHandle.Dispose();
                                }
                                else
                                {
                                    preferredHandle = newHandle;
                                }
                            }

                            preferredHeld.TrySetResult();
                        }

                        return Task.CompletedTask;
                    },
                    BeforeSharingViolationRetryAsync = async (_, _, cancellationToken) =>
                    {
                        sharingViolationObserved.TrySetResult();
                        await releaseSharingViolation.Task.WaitAsync(cancellationToken);
                    },
                },
            });

        Task<IReadOnlyList<DownloadResult>> download = downloader.DownloadManyAsync(
            urls,
            _tempDir,
            MaxConcurrency);
        IReadOnlyList<DownloadResult>? results = null;
        try
        {
            await preferredHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await sharingViolationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(download.IsCompleted);
        }
        finally
        {
            FileStream? handleToDispose;
            lock (preferredHandleGate)
            {
                releasePreferredHandle = true;
                handleToDispose = preferredHandle;
                preferredHandle = null;
            }

            if (handleToDispose is not null)
            {
                await handleToDispose.DisposeAsync();
            }

            releaseSharingViolation.TrySetResult();
            results = await download.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(MaxConcurrency, handler.MaxObservedConcurrency);
        Assert.NotEqual(results[0].FilePath, results[1].FilePath);
        for (int index = 0; index < results.Count; index++)
        {
            byte[] persisted = await File.ReadAllBytesAsync(results[index].FilePath);
            Assert.Equal(payloads[index], persisted);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(persisted)), results[index].Sha256.Normalized);
        }
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

        DownloadHttpException exception = await Assert.ThrowsAsync<DownloadHttpException>(
            () => downloader.DownloadManyAsync(urls, _tempDir, maxConcurrency: 2));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsDownloadManyBeforeDisposingSharedResources()
    {
        var handler = new CancellationBlockingHttpMessageHandler(expectedConcurrentRequests: 2);
        var downloader = new InstallerDownloader(
            handler,
            new DownloaderOptions { MaxRetryAttempts = 0 });
        string[] urls =
        [
            "https://example.com/one.exe",
            "https://example.com/two.exe",
            "https://example.com/three.exe",
            "https://example.com/four.exe",
        ];
        Task<IReadOnlyList<DownloadResult>> download =
            downloader.DownloadManyAsync(urls, _tempDir, maxConcurrency: 2);
        await handler.AllRequestsStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposal = downloader.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => downloader.DownloadAsync(urls[0], _tempDir));
        downloader.Dispose();
        await downloader.DisposeAsync();
        Assert.Equal(1, handler.DisposeCount);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories),
            path => path.Contains(".part.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_CancelsAndDrainsRevalidationWithoutDisposedSemaphoreFailures()
    {
        const string Url = "https://example.com/setup.exe";
        using StubHttpMessageHandler initialHandler = new((request, _) => Ok(request, CreatePayload(128)));
        using var initialDownloader = new InstallerDownloader(initialHandler);
        DownloadResult previous = await initialDownloader.DownloadAsync(Url, _tempDir);
        var blockingHandler = new CancellationBlockingHttpMessageHandler(expectedConcurrentRequests: 1);
        var downloader = new InstallerDownloader(
            blockingHandler,
            new DownloaderOptions { MaxRetryAttempts = 0 });
        Task<DownloadRevalidationResult> revalidation = downloader.RevalidateAsync(previous);
        await blockingHandler.AllRequestsStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposal = Task.Run(downloader.Dispose);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => revalidation);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, blockingHandler.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => downloader.RevalidateAsync(previous));
    }

    [Fact]
    public async Task Dispose_ReentrantFromProgressInitiatesShutdownWithoutDeadlock()
    {
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, CreatePayload(200_000)));
        var downloader = new InstallerDownloader(
            handler,
            new DownloaderOptions { MaxRetryAttempts = 0 });
        int disposeCalls = 0;
        var progress = new CallbackProgress<DownloadProgress>(_ =>
        {
            if (Interlocked.Increment(ref disposeCalls) == 1)
            {
                downloader.Dispose();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(
                "https://example.com/reentrant.exe",
                _tempDir,
                progress)).WaitAsync(TimeSpan.FromSeconds(5));
        await downloader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => downloader.DownloadAsync("https://example.com/rejected.exe", _tempDir));
    }

    [Fact]
    public async Task DisposeAsync_ReentrantFromHandlerInitiatesShutdownWithoutAwaitingItself()
    {
        InstallerDownloader? downloader = null;
        var handler = new ReentrantAsyncDisposalHttpMessageHandler(
            () => downloader!.DisposeAsync());
        downloader = new InstallerDownloader(
            handler,
            new DownloaderOptions { MaxRetryAttempts = 0 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(
                "https://example.com/reentrant-async.exe",
                _tempDir)).WaitAsync(TimeSpan.FromSeconds(5));
        await downloader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => downloader.DownloadAsync("https://example.com/rejected.exe", _tempDir));
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
