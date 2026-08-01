using System.Diagnostics;
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
        var date = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(date);
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, payload);
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            response.Headers.Date = date;
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(15) };
            response.Headers.Age = TimeSpan.FromMinutes(5);
            return response;
        });
        using InstallerDownloader downloader = new(
            handler,
            new DownloaderOptions { TimeProvider = timeProvider });

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
    public async Task DownloadAsync_Rfc9111AgeIncludesFinalResponseDelayAndResidentTime()
    {
        byte[] payload = Payload(128);
        var start = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(start);
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.Host == "example.com")
            {
                timeProvider.Advance(TimeSpan.FromSeconds(20));
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
                redirect.Headers.Location = new Uri("https://cdn.example.com/setup.exe");
                return redirect;
            }

            DateTimeOffset finalRequestTime = timeProvider.GetUtcNow();
            timeProvider.Advance(TimeSpan.FromSeconds(10));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new ClockAdvancingStream(payload, timeProvider, TimeSpan.FromSeconds(20))),
                RequestMessage = request,
            };
            response.Headers.Date = finalRequestTime;
            response.Headers.Age = TimeSpan.FromSeconds(40);
            response.Headers.CacheControl = new CacheControlHeaderValue
            {
                MaxAge = TimeSpan.FromSeconds(120),
            };
            return response;
        });
        using InstallerDownloader downloader = new(
            handler,
            new DownloaderOptions { TimeProvider = timeProvider });

        DownloadResult result = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        Assert.Equal(start.AddSeconds(50), result.RetrievedAt);
        Assert.Equal(start.AddSeconds(100), result.FreshUntil);
        Assert.True(result.IsFreshAt(start.AddSeconds(99)));
        Assert.False(result.IsFreshAt(start.AddSeconds(100)));
    }

    [Fact]
    public async Task DownloadAsync_Rfc9111ExpiresUsesDateLifetimeAndCorrectedAge()
    {
        byte[] payload = Payload(128);
        var start = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(start);
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            HttpResponseMessage response = Ok(request, payload);
            response.Headers.Date = start.AddSeconds(-10);
            response.Headers.Age = TimeSpan.FromSeconds(30);
            response.Content.Headers.Expires = start.AddSeconds(110);
            return response;
        });
        using InstallerDownloader downloader = new(
            handler,
            new DownloaderOptions { TimeProvider = timeProvider });

        DownloadResult result = await downloader.DownloadAsync("https://example.com/setup.exe", _tempDir);

        Assert.Equal(start.AddSeconds(5), result.RetrievedAt);
        Assert.Equal(start.AddSeconds(90), result.FreshUntil);
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
        Assert.NotEqual(initial.FilePath, revalidated.Result.FilePath);
        Assert.Equal(original, await File.ReadAllBytesAsync(initial.FilePath));
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
    public async Task RevalidateAsync_FinalizesCompetingOperationsWithoutSerializingNetwork()
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
        Assert.Equal(2, handler.MaxObservedConcurrency);
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
    public async Task Cache_UsesInjectedClockForHttpFreshnessExpiration()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        var start = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(start);
        using StubHttpMessageHandler handler = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, Payload(100));
            response.Headers.Date = timeProvider.GetUtcNow();
            response.Headers.CacheControl = new CacheControlHeaderValue
            {
                MaxAge = TimeSpan.FromHours(1),
            };
            return response;
        });
        using InstallerDownloader downloader = CreateCachedDownloader(
            handler,
            cacheDirectory,
            ttl: TimeSpan.FromHours(2),
            timeProvider: timeProvider);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        timeProvider.Advance(TimeSpan.FromMinutes(59));

        DownloadResult fresh = await downloader.DownloadAsync(
            "https://example.com/setup.exe",
            Path.Combine(_tempDir, "second"));
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        IReadOnlyList<DownloadCacheEntryInfo> inspection = await downloader.Cache!.InspectAsync();
        DownloadResult refreshed = await downloader.DownloadAsync(
            "https://example.com/setup.exe",
            Path.Combine(_tempDir, "third"));

        Assert.True(fresh.IsFromCache);
        Assert.Equal(DownloadCacheEntryState.Stale, Assert.Single(inspection).State);
        Assert.False(refreshed.IsFromCache);
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
        Assert.Equal(
            [Path.Combine(cacheDirectory, ".winmatsch-cache.lock")],
            Directory.EnumerateFiles(cacheDirectory).Order(StringComparer.Ordinal));
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
    public async Task Cache_ProcessLockPersistsBetweenOperationsAndClearExcludesIt()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        string lockPath = Path.Combine(cacheDirectory, ".winmatsch-cache.lock");

        _ = await downloader.Cache!.InspectAsync();
        Assert.True(File.Exists(lockPath));

        await downloader.Cache.ClearAsync();

        Assert.True(File.Exists(lockPath));
        Assert.Equal(
            [lockPath],
            Directory.EnumerateFiles(cacheDirectory).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cache_MaintenanceSweepsOnlyOldOwnedInactiveTemporaryFiles()
    {
        DateTimeOffset now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(now);
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        Directory.CreateDirectory(cacheDirectory);
        string key = new('a', 64);
        string oldMetadata = Path.Combine(
            cacheDirectory,
            key + ".json.tmp." + Guid.NewGuid().ToString("N"));
        string oldPayload = Path.Combine(
            cacheDirectory,
            key + "." + Guid.NewGuid().ToString("N") + ".payload.tmp." + Guid.NewGuid().ToString("N"));
        string activeTemporary = Path.Combine(
            cacheDirectory,
            key + ".json.tmp." + Guid.NewGuid().ToString("N"));
        string youngTemporary = Path.Combine(
            cacheDirectory,
            key + ".json.tmp." + Guid.NewGuid().ToString("N"));
        string arbitraryUserFile = Path.Combine(
            cacheDirectory,
            "notes.tmp." + Guid.NewGuid().ToString("N"));
        foreach (string path in new[] { oldMetadata, oldPayload, activeTemporary, youngTemporary, arbitraryUserFile })
        {
            await File.WriteAllTextAsync(path, "partial");
        }

        DateTime oldTimestamp = now.UtcDateTime - TimeSpan.FromHours(2);
        File.SetLastWriteTimeUtc(oldMetadata, oldTimestamp);
        File.SetLastWriteTimeUtc(oldPayload, oldTimestamp);
        File.SetLastWriteTimeUtc(activeTemporary, oldTimestamp);
        File.SetLastWriteTimeUtc(arbitraryUserFile, oldTimestamp);
        await using var activeWriter = new FileStream(
            activeTemporary,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous);
        var cache = new DownloadCache(cacheDirectory, new DownloadCacheOptions
        {
            TimeProvider = timeProvider,
            AbandonedTemporaryFileAge = TimeSpan.FromHours(1),
        });

        _ = await cache.TryRestoreAsync(
            "https://example.com/missing.exe",
            Path.Combine(_tempDir, "restore"));

        Assert.False(File.Exists(oldMetadata));
        Assert.False(File.Exists(oldPayload));
        Assert.True(File.Exists(activeTemporary));
        Assert.True(File.Exists(youngTemporary));
        Assert.True(File.Exists(arbitraryUserFile));

        await activeWriter.DisposeAsync();
        await cache.ClearAsync("https://example.com/missing.exe");

        Assert.False(File.Exists(activeTemporary));
        Assert.True(File.Exists(youngTemporary));
        Assert.True(File.Exists(arbitraryUserFile));
    }

    [Fact]
    public async Task Cache_ProcessLockHasCrossProcessDeadlineCancellationAndRecovery()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        string lockPath = Path.Combine(cacheDirectory, ".winmatsch-cache.lock");
        using Process lockHolder = StartLockHolder(lockPath);
        string? ready = await lockHolder.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("LOCKED", ready);
        var cache = new DownloadCache(cacheDirectory, new DownloadCacheOptions
        {
            ProcessLockTimeout = TimeSpan.FromMilliseconds(150),
        });

        DownloadCacheLockTimeoutException timeout =
            await Assert.ThrowsAsync<DownloadCacheLockTimeoutException>(() => cache.ClearAsync());

        Assert.Equal(lockPath, timeout.LockFilePath);
        Assert.Equal(TimeSpan.FromMilliseconds(150), timeout.Timeout);
        Assert.IsType<IOException>(timeout.InnerException);

        var cancellationCache = new DownloadCache(cacheDirectory, new DownloadCacheOptions
        {
            ProcessLockTimeout = TimeSpan.FromSeconds(5),
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        OperationCanceledException canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancellationCache.ClearAsync(cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);

        await lockHolder.StandardInput.WriteLineAsync();
        await lockHolder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, lockHolder.ExitCode);
        await cache.ClearAsync();
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public async Task Cache_LinuxSymlinkAliasCannotBypassSameProcessLock()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string cacheDirectory = Path.Combine(_tempDir, "cache");
        string aliasDirectory = Path.Combine(_tempDir, "cache-alias");
        Directory.CreateDirectory(cacheDirectory);
        Directory.CreateSymbolicLink(aliasDirectory, cacheDirectory);
        var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalCache = new DownloadCache(cacheDirectory, new DownloadCacheOptions
        {
            AfterProcessLockAcquiredAsync = async cancellationToken =>
            {
                lockAcquired.TrySetResult();
                await releaseLock.Task.WaitAsync(cancellationToken);
            },
        });
        var aliasCache = new DownloadCache(aliasDirectory, new DownloadCacheOptions
        {
            ProcessLockTimeout = TimeSpan.FromMilliseconds(150),
        });
        Task originalOperation = originalCache.ClearAsync();
        await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            _ = await Assert.ThrowsAsync<DownloadCacheLockTimeoutException>(
                () => aliasCache.ClearAsync());
        }
        finally
        {
            releaseLock.TrySetResult();
            await originalOperation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Cache_InspectOfEmptyExistingDirectoryDoesNotCreateLockState()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var cache = new DownloadCache(cacheDirectory);

        IReadOnlyList<DownloadCacheEntryInfo> entries = await cache.InspectAsync();

        Assert.Empty(entries);
        Assert.Empty(Directory.EnumerateFileSystemEntries(cacheDirectory));
    }

    [Fact]
    public async Task Cache_InspectOfPopulatedLegacyCacheDoesNotCreateLockState()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        string lockPath = Path.Combine(cacheDirectory, ".winmatsch-cache.lock");
        File.Delete(lockPath);
        string[] before = [.. Directory.EnumerateFiles(cacheDirectory).Order(StringComparer.Ordinal)];

        IReadOnlyList<DownloadCacheEntryInfo> entries = await downloader.Cache!.InspectAsync();

        Assert.Single(entries);
        Assert.Equal(before, Directory.EnumerateFiles(cacheDirectory).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task Cache_LegacyInspectionRetriesWhenPersistentLockAppears()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        string lockPath = Path.Combine(cacheDirectory, ".winmatsch-cache.lock");
        File.Delete(lockPath);
        var reachedRecheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new DownloadCache(cacheDirectory, new DownloadCacheOptions
        {
            BeforeUnlockedInspectionRecheckAsync = async cancellationToken =>
            {
                reachedRecheck.TrySetResult();
                await releaseRecheck.Task.WaitAsync(cancellationToken);
            },
        });

        Task<IReadOnlyList<DownloadCacheEntryInfo>> inspection = cache.InspectAsync();
        await reachedRecheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var blocker = new FileStream(
            lockPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous);
        releaseRecheck.TrySetResult();
        await Task.Yield();
        Assert.False(inspection.IsCompleted);

        await blocker.DisposeAsync();
        IReadOnlyList<DownloadCacheEntryInfo> entries =
            await inspection.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(entries);
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public async Task Cache_LegacyInspectionDoesNotBlockFirstCrossProcessMutation()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        string lockPath = Path.Combine(cacheDirectory, ".winmatsch-cache.lock");
        File.Delete(lockPath);
        var payloadOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePayload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new DownloadCache(cacheDirectory, new DownloadCacheOptions
        {
            AfterUnlockedInspectionPayloadOpenAsync = async (_, cancellationToken) =>
            {
                payloadOpened.TrySetResult();
                await releasePayload.Task.WaitAsync(cancellationToken);
            },
        });

        Task<IReadOnlyList<DownloadCacheEntryInfo>> inspection = cache.InspectAsync();
        IReadOnlyList<DownloadCacheEntryInfo>? entries = null;
        try
        {
            await payloadOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var crossProcessLock = new FileStream(
                lockPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous);
            foreach (string path in Directory.EnumerateFiles(cacheDirectory)
                         .Where(path => !string.Equals(path, lockPath, StringComparison.Ordinal)))
            {
                File.Delete(path);
            }
        }
        finally
        {
            releasePayload.TrySetResult();
            entries = await inspection.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Empty(entries);
        Assert.Equal(
            [lockPath],
            Directory.EnumerateFiles(cacheDirectory).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cache_InspectOfMissingDirectoryStillHonorsCancellation()
    {
        var cache = new DownloadCache(Path.Combine(_tempDir, "missing-cache"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.InspectAsync(cancellation.Token));
    }

    [Fact]
    public async Task Cache_ClearWaitsForPersistedCrossProcessLockWithoutFileFinalizationRace()
    {
        string cacheDirectory = Path.Combine(_tempDir, "cache");
        using StubHttpMessageHandler handler = new((request, _) => Ok(request, Payload(100)));
        using InstallerDownloader downloader = CreateCachedDownloader(handler, cacheDirectory);
        await downloader.DownloadAsync("https://example.com/setup.exe", Path.Combine(_tempDir, "first"));
        string lockPath = Path.Combine(cacheDirectory, ".winmatsch-cache.lock");
        await using var blocker = new FileStream(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous);

        Task clear = downloader.Cache!.ClearAsync();
        await Task.Yield();
        Assert.False(clear.IsCompleted);

        await blocker.DisposeAsync();
        await clear.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(File.Exists(lockPath));
        Assert.Equal(
            [lockPath],
            Directory.EnumerateFiles(cacheDirectory).Order(StringComparer.Ordinal));
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
        Assert.Equal(
            2,
            Directory.EnumerateFiles(cacheDirectory).Count(path =>
                Path.GetExtension(path) is ".json" or ".payload"));
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
        long maxBytes = 10_000_000,
        TimeProvider? timeProvider = null)
        => new(
            handler,
            new DownloaderOptions
            {
                CacheDirectory = cacheDirectory,
                CacheTtl = ttl ?? TimeSpan.FromMinutes(10),
                CacheMaxEntries = maxEntries,
                CacheMaxBytes = maxBytes,
                RetryBaseDelay = TimeSpan.Zero,
                TimeProvider = timeProvider ?? TimeProvider.System,
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

    private static Process StartLockHolder(string lockPath)
    {
        string testAssemblyName = typeof(RevalidationProbeCacheTests).Assembly.GetName().Name!;
        string runtimeConfig = Path.Combine(AppContext.BaseDirectory, testAssemblyName + ".runtimeconfig.json");
        string lockHost = Path.Combine(AppContext.BaseDirectory, "WinMatsch.Downloads.LockHost.dll");
        Assert.True(File.Exists(runtimeConfig), $"Missing test runtime config '{runtimeConfig}'.");
        Assert.True(File.Exists(lockHost), $"Missing lock host '{lockHost}'.");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfig);
        startInfo.ArgumentList.Add(lockHost);
        startInfo.ArgumentList.Add(lockPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the cache lock test process.");
    }
}
