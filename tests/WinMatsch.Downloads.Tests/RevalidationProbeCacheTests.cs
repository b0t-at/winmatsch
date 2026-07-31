using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Xunit;

namespace WinMatsch.Downloads.Tests;

public sealed class RevalidationProbeCacheTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "winmatsch-downloads-revalidation-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task DownloadAsync_CapturesEtagDateFreshnessAndStableIdentity()
    {
        byte[] payload = Payload(128);
        DateTimeOffset date = DateTimeOffset.UtcNow;
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, payload);
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            response.Headers.Date = date;
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(15) };
            response.Headers.Age = TimeSpan.FromMinutes(5);
            return response;
        });
        using InstallerDownloader downloader = new(handler);

        DownloadResult result = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        Assert.Equal("\"v1\"", result.ETag);
        Assert.Equal(date, result.ResponseDate);
        Assert.Equal(result.RetrievedAt.AddMinutes(10), result.FreshUntil);
        Assert.True(result.IsFreshAt(result.RetrievedAt.AddMinutes(9)));
        Assert.False(result.IsFreshAt(result.RetrievedAt.AddMinutes(10)));
        Assert.Equal(result.Sha256, result.ContentIdentity.Sha256);
        Assert.Equal(result.SizeInBytes, result.ContentIdentity.SizeInBytes);
    }

    [Fact]
    public async Task RevalidateAsync_UsesConditionalGetAndAccepts304()
    {
        byte[] payload = Payload(256);
        var lastModified = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        using StubHttpMessageHandler handler = new((request, requestNumber) =>
        {
            if (requestNumber == 1)
            {
                HttpResponseMessage initial = Ok(request, payload);
                initial.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                initial.Content.Headers.LastModified = lastModified;
                return initial;
            }

            Assert.Contains(request.Headers.IfNoneMatch, value => value.Tag == "\"v1\"");
            Assert.Null(request.Headers.IfModifiedSince);
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified) { RequestMessage = request };
            notModified.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            notModified.Headers.Date = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
            notModified.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return notModified;
        });
        using InstallerDownloader downloader = new(handler);
        DownloadResult initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        DownloadRevalidationResult revalidated = await downloader.RevalidateAsync(initial);

        Assert.Equal(DownloadRevalidationStatus.Unchanged, revalidated.Status);
        Assert.True(revalidated.WasNotModifiedResponse);
        Assert.Equal(initial.ContentIdentity, revalidated.Result.ContentIdentity);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RevalidateAsync_WeakEtagForcesUnconditionalGetAndDetectsChangedBytes()
    {
        byte[] original = Payload(128);
        byte[] changed = Payload(129);
        using StubHttpMessageHandler handler = new((request, requestNumber) =>
        {
            if (requestNumber == 1)
            {
                HttpResponseMessage initial = Ok(request, original);
                initial.Headers.ETag = new EntityTagHeaderValue("\"v1\"", isWeak: true);
                return initial;
            }

            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            return Ok(request, changed);
        });
        using InstallerDownloader downloader = new(handler);
        DownloadResult initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        DownloadRevalidationResult revalidated = await downloader.RevalidateAsync(initial);

        Assert.Equal(DownloadRevalidationStatus.ContentChanged, revalidated.Status);
        Assert.False(revalidated.WasNotModifiedResponse);
        Assert.Equal(changed, await File.ReadAllBytesAsync(revalidated.Result.FilePath));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RevalidateAsync_LastModifiedOnlyForcesUnconditionalGet()
    {
        byte[] payload = Payload(128);
        DateTimeOffset lastModified = DateTimeOffset.UtcNow.AddDays(-1);
        using StubHttpMessageHandler handler = new((request, requestNumber) =>
        {
            if (requestNumber == 1)
            {
                HttpResponseMessage initial = Ok(request, payload);
                initial.Content.Headers.LastModified = lastModified;
                return initial;
            }

            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            return Ok(request, payload);
        });
        using InstallerDownloader downloader = new(handler);
        DownloadResult initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        DownloadRevalidationResult revalidated = await downloader.RevalidateAsync(initial);

        Assert.Equal(DownloadRevalidationStatus.Unchanged, revalidated.Status);
        Assert.False(revalidated.WasNotModifiedResponse);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RevalidateAsync_MismatchedStrongEtagOn304FallsBackToUnconditionalGet()
    {
        byte[] original = Payload(128);
        byte[] changed = Payload(129);
        using StubHttpMessageHandler handler = new((request, requestNumber) =>
        {
            if (requestNumber == 1)
            {
                HttpResponseMessage initial = Ok(request, original);
                initial.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return initial;
            }

            if (requestNumber == 2)
            {
                Assert.Contains(request.Headers.IfNoneMatch, value => value.Tag == "\"v1\"");
                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified) { RequestMessage = request };
                notModified.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
                return notModified;
            }

            Assert.Empty(request.Headers.IfNoneMatch);
            return Ok(request, changed);
        });
        using InstallerDownloader downloader = new(handler);
        DownloadResult initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        DownloadRevalidationResult revalidated = await downloader.RevalidateAsync(initial);

        Assert.Equal(DownloadRevalidationStatus.ContentChanged, revalidated.Status);
        Assert.False(revalidated.WasNotModifiedResponse);
        Assert.Equal(changed, await File.ReadAllBytesAsync(revalidated.Result.FilePath));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task RevalidateAsync_ReportsChangedEtagAndContent()
    {
        byte[] original = Payload(128);
        byte[] changed = Payload(129);
        using StubHttpMessageHandler handler = new((request, requestNumber) =>
        {
            HttpResponseMessage response = Ok(request, requestNumber == 1 ? original : changed);
            response.Headers.ETag = new EntityTagHeaderValue(requestNumber == 1 ? "\"v1\"" : "\"v2\"");
            return response;
        });
        using InstallerDownloader downloader = new(handler);
        DownloadResult initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        DownloadRevalidationResult revalidated = await downloader.RevalidateAsync(initial);

        Assert.Equal(DownloadRevalidationStatus.ContentChanged, revalidated.Status);
        Assert.Equal("\"v2\"", revalidated.Result.ETag);
        Assert.NotEqual(initial.ContentIdentity, revalidated.Result.ContentIdentity);
        Assert.Equal(changed, await File.ReadAllBytesAsync(revalidated.Result.FilePath));
    }

    [Fact]
    public async Task RevalidateAsync_TreatsChangedEtagWithIdenticalBytesAsUnchangedContent()
    {
        byte[] payload = Payload(128);
        using StubHttpMessageHandler handler = new((request, requestNumber) =>
        {
            HttpResponseMessage response = Ok(request, payload);
            response.Headers.ETag = new EntityTagHeaderValue(requestNumber == 1 ? "\"v1\"" : "\"v2\"");
            return response;
        });
        using InstallerDownloader downloader = new(handler);
        DownloadResult initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        DownloadRevalidationResult revalidated = await downloader.RevalidateAsync(initial);

        Assert.Equal(DownloadRevalidationStatus.Unchanged, revalidated.Status);
        Assert.Equal("\"v2\"", revalidated.Result.ETag);
        Assert.Equal(initial.ContentIdentity, revalidated.Result.ContentIdentity);
    }

    [Fact]
    public async Task RevalidateAsync_RedownloadsWhenValidatorsAreAbsent()
    {
        byte[] payload = Payload(64);
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            return Ok(request, payload);
        });
        using InstallerDownloader downloader = new(handler);
        DownloadResult initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        DownloadRevalidationResult revalidated = await downloader.RevalidateAsync(initial);

        Assert.Equal(DownloadRevalidationStatus.Unchanged, revalidated.Status);
        Assert.False(revalidated.WasNotModifiedResponse);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsLocallyChangedContentBeforeNetworkRequest()
    {
        byte[] payload = Payload(64);
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, payload));
        using InstallerDownloader downloader = new(handler);
        DownloadResult initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);
        await File.WriteAllBytesAsync(initial.FilePath, Payload(32));

        DownloadContentChangedException exception = await Assert.ThrowsAsync<DownloadContentChangedException>(
            () => downloader.RevalidateAsync(initial));

        Assert.Equal(DownloadFailureKind.ContentChanged, exception.FailureKind);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RevalidateAsync_RechecksLocalContentAfter304()
    {
        byte[] payload = Payload(64);
        DownloadResult? initial = null;
        using StubHttpMessageHandler handler = new((request, requestNumber) =>
        {
            if (requestNumber == 1)
            {
                HttpResponseMessage response = Ok(request, payload);
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return response;
            }

            File.WriteAllBytes(initial!.FilePath, Payload(32));
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified) { RequestMessage = request };
            notModified.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return notModified;
        });
        using InstallerDownloader downloader = new(handler);
        initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        await Assert.ThrowsAsync<DownloadContentChangedException>(() => downloader.RevalidateAsync(initial));
    }

    [Fact]
    public async Task RevalidateAsync_SerializesCompetingOperationsForSameDestination()
    {
        byte[] payload = Payload(256);
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, payload))
        {
            PerRequestDelay = TimeSpan.FromMilliseconds(75),
        };
        using InstallerDownloader downloader = new(handler);
        DownloadResult initial = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        DownloadRevalidationResult[] results = await Task.WhenAll(
            downloader.RevalidateAsync(initial),
            downloader.RevalidateAsync(initial));

        Assert.All(results, result => Assert.Equal(DownloadRevalidationStatus.Unchanged, result.Status));
        Assert.Equal(1, handler.MaxObservedConcurrency);
        Assert.Equal(3, handler.RequestCount);
        Assert.All(results, result => Assert.Equal(result.Result.ContentIdentity, initial.ContentIdentity));
        Assert.Equal(payload, await File.ReadAllBytesAsync(initial.FilePath));
    }

    [Fact]
    public async Task ProbeAsync_UsesHeadWhenSupported()
    {
        var methods = new List<HttpMethod>();
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            methods.Add(request.Method);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([]),
            };
            response.Content.Headers.ContentLength = 42;
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag = new EntityTagHeaderValue("\"probe\"");
            return response;
        });
        using InstallerDownloader downloader = new(handler);

        DownloadProbeResult result = await downloader.ProbeAsync("https://example.com/setup.exe");

        Assert.Equal(DownloadProbeMethod.Head, result.Method);
        Assert.Equal(42, result.SizeInBytes);
        Assert.Equal("\"probe\"", result.ETag);
        Assert.True(result.SupportsRanges);
        Assert.Equal([HttpMethod.Head], methods);
    }

    [Fact]
    public async Task ProbeAsync_FallsBackToOneByteRangeGetWhenHeadRejected()
    {
        var methods = new List<HttpMethod>();
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            methods.Add(request.Method);
            if (request.Method == HttpMethod.Head)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { RequestMessage = request };
            }

            Assert.Equal("bytes=0-0", request.Headers.Range?.ToString());
            var response = Ok(request, [7], HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 500);
            return response;
        });
        using InstallerDownloader downloader = new(handler);

        DownloadProbeResult result = await downloader.ProbeAsync("https://example.com/setup.exe");

        Assert.Equal(DownloadProbeMethod.RangeGet, result.Method);
        Assert.Equal(500, result.SizeInBytes);
        Assert.True(result.SupportsRanges);
        Assert.Equal([HttpMethod.Head, HttpMethod.Get], methods);
    }

    [Fact]
    public async Task ProbeAsync_DisposesRejectedHeadBeforeRangeFallbackOnConstrainedConnection()
    {
        using ConstrainedFallbackHandler handler = new();
        using InstallerDownloader downloader = new(handler);

        DownloadProbeResult result = await downloader.ProbeAsync("https://example.com/setup.exe");

        Assert.Equal(DownloadProbeMethod.RangeGet, result.Method);
        Assert.True(handler.HeadExchangeDisposedBeforeGet);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ProbeAsync_DoesNotClaimRangeSupportWhenServerIgnoresRange()
    {
        using StubHttpMessageHandler handler = new((request, _) => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { RequestMessage = request }
            : Ok(request, Payload(500)));
        using InstallerDownloader downloader = new(handler);

        DownloadProbeResult result = await downloader.ProbeAsync("https://example.com/setup.exe");

        Assert.Equal(DownloadProbeMethod.RangeGet, result.Method);
        Assert.False(result.SupportsRanges);
        Assert.Equal(500, result.SizeInBytes);
    }

    [Fact]
    public async Task DownloadAsync_NeverFollowsHttpsToHttpDowngrade()
    {
        bool insecureRequestSent = false;
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.Scheme == Uri.UriSchemeHttps)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
                redirect.Headers.Location = new Uri("http://example.com/setup.exe");
                return redirect;
            }

            insecureRequestSent = true;
            return Ok(request, Payload(32));
        });
        using InstallerDownloader downloader = new(handler, new DownloaderOptions { AllowInsecureDownloads = true });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.DownloadAsync("https://example.com/setup.exe", _tempDir));

        Assert.False(insecureRequestSent);
        Assert.False(File.Exists(Path.Combine(_tempDir, "setup.exe")));
    }

    [Fact]
    public async Task DownloadAsync_RedirectLimitIsPermanentAndNotRetried()
    {
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            redirect.Headers.Location = request.RequestUri;
            return redirect;
        });
        using InstallerDownloader downloader = new(
            handler,
            new DownloaderOptions { MaxRetryAttempts = 3, RetryBaseDelay = TimeSpan.Zero });

        DownloadRedirectException exception = await Assert.ThrowsAsync<DownloadRedirectException>(
            () => downloader.DownloadAsync("https://example.com/loop.exe", _tempDir));

        Assert.Equal(DownloadFailureKind.PermanentHttp, exception.FailureKind);
        Assert.Equal(10, exception.RedirectLimit);
        Assert.Equal(11, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_DoesNotPersistNoStoreResponse()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, Payload(100));
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        });
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);

        DownloadResult first = await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        DownloadResult second = await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "second"));

        Assert.False(first.MayBeStored);
        Assert.False(second.IsFromCache);
        Assert.Equal(2, handler.RequestCount);
        Assert.Empty(await downloader.Cache!.InspectAsync());
    }

    [Fact]
    public async Task DownloadAsync_DoesNotReuseNoCacheResponseWithoutRevalidation()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, Payload(100));
            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            return response;
        });
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);

        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        DownloadResult second = await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "second"));

        Assert.False(second.IsFromCache);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Cache_RestoresFreshEntryWithoutNetwork()
    {
        byte[] payload = Payload(100);
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, payload));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);

        DownloadResult first = await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        DownloadResult second = await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "second"));

        Assert.False(first.IsFromCache);
        Assert.True(second.IsFromCache);
        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
        Assert.Equal(payload, await File.ReadAllBytesAsync(second.FilePath));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Cache_StaleEntryIsRemovedAndRedownloaded()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, requestNumber) => Ok(request, Payload(100 + requestNumber)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory, ttl: TimeSpan.FromMilliseconds(20));
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        await Task.Delay(60);

        IReadOnlyList<DownloadCacheEntryInfo> stale = await downloader.Cache!.InspectAsync();
        DownloadResult second = await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "second"));

        Assert.Single(stale);
        Assert.Equal(DownloadCacheEntryState.Stale, stale[0].State);
        Assert.False(second.IsFromCache);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Cache_CorruptPayloadRaisesDistinctFailure()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        string payloadPath = Assert.Single(Directory.EnumerateFiles(cacheDirectory, "*.payload"));
        await File.WriteAllBytesAsync(payloadPath, Payload(12));

        IReadOnlyList<DownloadCacheEntryInfo> inspection = await downloader.Cache!.InspectAsync();
        DownloadCacheCorruptionException exception = await Assert.ThrowsAsync<DownloadCacheCorruptionException>(
            () => downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "second")));

        Assert.Equal(DownloadCacheEntryState.Corrupt, Assert.Single(inspection).State);
        Assert.Equal(DownloadFailureKind.CacheCorruption, exception.FailureKind);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Cache_RestoreMoveFailureUsesLocalFileTaxonomyAndCleansTemporaryFile()
    {
        byte[] payload = Payload(100);
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        string destination = Path.Combine(_tempDir, "blocked-destination");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, payload));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        Directory.CreateDirectory(Path.Combine(destination, "setup.exe"));

        DownloadFileException exception = await Assert.ThrowsAsync<DownloadFileException>(
            () => downloader.DownloadAsync("https://example.com/setup.exe", destination));

        Assert.Equal(DownloadFailureKind.LocalFile, exception.FailureKind);
        Assert.Equal(Path.Combine(destination, "setup.exe"), exception.FilePath);
        Assert.Empty(Directory.EnumerateFiles(destination, "*.tmp.*"));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Cache_CorruptMetadataIsInspectableAndClearable()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        string metadataPath = Assert.Single(Directory.EnumerateFiles(cacheDirectory, "*.json"));
        await File.WriteAllTextAsync(metadataPath, "{broken");

        IReadOnlyList<DownloadCacheEntryInfo> inspection = await downloader.Cache!.InspectAsync();
        await downloader.Cache.ClearAsync();

        Assert.Equal(DownloadCacheEntryState.Corrupt, Assert.Single(inspection).State);
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory));
    }

    [Fact]
    public async Task Cache_WrongMetadataValueKindIsReportedAsCorrupt()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        string metadataPath = Assert.Single(Directory.EnumerateFiles(cacheDirectory, "*.json"));
        string metadata = await File.ReadAllTextAsync(metadataPath);
        await File.WriteAllTextAsync(metadataPath, metadata.Replace("\"sizeInBytes\":100", "\"sizeInBytes\":{}", StringComparison.Ordinal));

        IReadOnlyList<DownloadCacheEntryInfo> inspection = await downloader.Cache!.InspectAsync();

        Assert.Equal(DownloadCacheEntryState.Corrupt, Assert.Single(inspection).State);
    }

    [Fact]
    public async Task Cache_RejectsTraversalFileNameFromTamperedMetadata()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        string metadataPath = Assert.Single(Directory.EnumerateFiles(cacheDirectory, "*.json"));
        string metadata = await File.ReadAllTextAsync(metadataPath);
        await File.WriteAllTextAsync(
            metadataPath,
            metadata.Replace("\"fileName\":\"setup.exe\"", "\"fileName\":\"..\\\\outside.exe\"", StringComparison.Ordinal));

        DownloadCacheCorruptionException exception = await Assert.ThrowsAsync<DownloadCacheCorruptionException>(
            () => downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "second")));

        Assert.Equal(DownloadFailureKind.CacheCorruption, exception.FailureKind);
        Assert.False(File.Exists(Path.Combine(_tempDir, "outside.exe")));
    }

    [Fact]
    public async Task Cache_CoalescesConcurrentRequestsAndUsesAtomicFiles()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)))
        {
            PerRequestDelay = TimeSpan.FromMilliseconds(50),
        };
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        string url = "https://example.com/setup.exe";

        DownloadResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(index =>
                downloader.DownloadAsync(url, Path.Combine(_tempDir, "destination-" + index))));

        Assert.Equal(1, handler.RequestCount);
        Assert.Single(results, result => !result.IsFromCache);
        Assert.Equal(7, results.Count(result => result.IsFromCache));
        Assert.DoesNotContain(Directory.EnumerateFiles(cacheDirectory), path => path.Contains(".tmp.", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories), path => path.Contains(".part.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cache_EvictsLeastRecentlyUsedEntriesToConfiguredBound()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory, maxEntries: 1);
        await downloader.DownloadAsync("https://example.com/first.exe", Path.Combine(_tempDir, "first"));
        await downloader.DownloadAsync("https://example.com/second.exe", Path.Combine(_tempDir, "second"));

        IReadOnlyList<DownloadCacheEntryInfo> entries = await downloader.Cache!.InspectAsync();

        Assert.Single(entries);
        Assert.Equal("https://example.com/second.exe", entries[0].Url);
        Assert.Equal(2, Directory.EnumerateFiles(cacheDirectory).Count());
    }

    [Fact]
    public async Task Cache_EvictsEntriesToConfiguredByteBound()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory, maxBytes: 150);
        await downloader.DownloadAsync("https://example.com/first.exe", Path.Combine(_tempDir, "first"));
        await downloader.DownloadAsync("https://example.com/second.exe", Path.Combine(_tempDir, "second"));

        IReadOnlyList<DownloadCacheEntryInfo> entries = await downloader.Cache!.InspectAsync();

        Assert.Single(entries);
        Assert.Equal("https://example.com/second.exe", entries[0].Url);
    }

    [Fact]
    public async Task Cache_RemovesUnreferencedPayloadGenerationsDuringMaintenance()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/first.exe", Path.Combine(_tempDir, "first"));
        string orphan = Path.Combine(cacheDirectory, new string('a', 64) + ".orphan.payload");
        await File.WriteAllBytesAsync(orphan, Payload(10));

        await downloader.DownloadAsync("https://example.com/second.exe", Path.Combine(_tempDir, "second"));

        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task DownloadAsync_RetriesTransientFailuresThenCategorizesExhaustion()
    {
        using StubHttpMessageHandler handler = new((request, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request });
        using InstallerDownloader downloader = new(
            handler,
            new DownloaderOptions { MaxRetryAttempts = 2, RetryBaseDelay = TimeSpan.Zero });

        DownloadNetworkException exception = await Assert.ThrowsAsync<DownloadNetworkException>(
            () => downloader.DownloadAsync("https://example.com/setup.exe", _tempDir));

        Assert.Equal(DownloadFailureKind.TransientNetwork, exception.FailureKind);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_CancellationIsNeverRetriedOrWrappedAndCleansTemporaryFile()
    {
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)))
        {
            PerRequestDelay = TimeSpan.FromSeconds(5),
        };
        using InstallerDownloader downloader = new(
            handler,
            new DownloaderOptions { MaxRetryAttempts = 3, RetryBaseDelay = TimeSpan.Zero });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync("https://example.com/setup.exe", _tempDir, cancellationToken: cancellation.Token));

        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(Directory.Exists(_tempDir) ? Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories) : []);
    }

    private static InstallerDownloader CreateCachedDownloader(
        HttpMessageHandler handler,
        string cacheDirectory,
        TimeSpan? ttl = null,
        int maxEntries = 64,
        long maxBytes = 10_000_000)
        => new(
            handler,
            new DownloaderOptions
            {
                CacheDirectory = cacheDirectory,
                CacheTtl = ttl ?? TimeSpan.FromMinutes(10),
                CacheMaxEntries = maxEntries,
                CacheMaxBytes = maxBytes,
                RetryBaseDelay = TimeSpan.Zero,
            });

    private static HttpResponseMessage Ok(
        HttpRequestMessage request,
        byte[] payload,
        HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new ByteArrayContent(payload),
            RequestMessage = request,
        };

    private static byte[] Payload(int length)
    {
        var payload = new byte[length];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 17) + 11);
        }

        return payload;
    }
}
