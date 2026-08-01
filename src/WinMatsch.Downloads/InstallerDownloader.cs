using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using WinMatsch.Core;

namespace WinMatsch.Downloads;

/// <summary>
/// Streams installer payloads to disk, computes stable identities, captures HTTP validators,
/// probes origins, and revalidates bytes immediately before callers submit derived manifests.
/// </summary>
public sealed class InstallerDownloader : IDisposable
{
    private const int CopyBufferSize = 81920;
    private const string FallbackFileName = "download";
    private const string TempFileSuffix = ".part";
    private const string InvalidFileNameChars = "\"<>:/\\|?*";

    private readonly HttpClient _httpClient;
    private readonly DownloaderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly DownloadCache? _cache;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadGates = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a downloader with validated redirects and automatic decompression enabled.</summary>
    public InstallerDownloader(DownloaderOptions? options = null)
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }, options)
    {
    }

    /// <summary>Creates a downloader using a custom handler that is disposed with this instance.</summary>
    public InstallerDownloader(HttpMessageHandler handler, DownloaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _options = options ?? new DownloaderOptions();
        ValidateOptions(_options);
        _timeProvider = _options.TimeProvider;
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (!string.IsNullOrWhiteSpace(_options.CacheDirectory))
        {
            _cache = new DownloadCache(
                _options.CacheDirectory,
                new DownloadCacheOptions
                {
                    TimeToLive = _options.CacheTtl,
                    MaxEntries = _options.CacheMaxEntries,
                    MaxBytes = _options.CacheMaxBytes,
                    TimeProvider = _timeProvider,
                });
        }
    }

    /// <summary>The configured persistent cache, or null when caching is disabled.</summary>
    public DownloadCache? Cache => _cache;

    /// <summary>
    /// Downloads an installer atomically. When caching is enabled, a fresh integrity-checked entry
    /// is restored without a network request and concurrent requests for the same URL are coalesced.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        string url,
        string destinationDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Uri initialUri = ValidateUrl(url);
        CreateDestinationDirectory(destinationDirectory);
        if (_cache is null)
        {
            DownloadAttemptResult attempt = await DownloadFromOriginAsync(
                url,
                initialUri,
                destinationDirectory,
                progress,
                null,
                cancellationToken).ConfigureAwait(false);
            return attempt.Result;
        }

        SemaphoreSlim gate = _downloadGates.GetOrAdd(initialUri.AbsoluteUri, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DownloadResult? cached = await _cache.TryRestoreAsync(url, destinationDirectory, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            DownloadAttemptResult attempt = await DownloadFromOriginAsync(
                url,
                initialUri,
                destinationDirectory,
                progress,
                null,
                cancellationToken).ConfigureAwait(false);
            DownloadResult downloaded = attempt.Result;
            await _cache.StoreAsync(downloaded, cancellationToken).ConfigureAwait(false);
            return downloaded;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Downloads directly from the origin without reading or writing the persistent cache.
    /// Submission boundaries use this to compare current bytes with a previously planned identity.
    /// </summary>
    public async Task<DownloadResult> DownloadFreshAsync(
        string url,
        string destinationDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Uri initialUri = ValidateUrl(url);
        CreateDestinationDirectory(destinationDirectory);
        DownloadAttemptResult attempt = await DownloadFromOriginAsync(
            url,
            initialUri,
            destinationDirectory,
            progress,
            null,
            cancellationToken).ConfigureAwait(false);
        return attempt.Result;
    }

    /// <summary>
    /// Verifies the local bytes and then conditionally revalidates the origin. Servers without a
    /// usable validator are re-downloaded and compared by stable SHA-256/size identity.
    /// </summary>
    public async Task<DownloadRevalidationResult> RevalidateAsync(
        DownloadResult previous,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Uri initialUri = ValidateUrl(previous.InitialUrl);
        string destinationDirectory = Path.GetDirectoryName(previous.FilePath)
            ?? throw new InvalidOperationException("The downloaded file has no parent directory.");
        DownloadContentIdentity localIdentity = await ComputeFileIdentityAsync(previous.FilePath, cancellationToken).ConfigureAwait(false);
        if (localIdentity != previous.ContentIdentity)
        {
            throw new DownloadContentChangedException(previous.ContentIdentity, localIdentity, previous.FilePath);
        }

        DownloadAttemptResult attempt = await DownloadFromOriginAsync(
            previous.InitialUrl,
            initialUri,
            destinationDirectory,
            progress,
            previous,
            cancellationToken).ConfigureAwait(false);
        if (attempt.NotModified)
        {
            DownloadContentIdentity confirmedIdentity = await ComputeFileIdentityAsync(previous.FilePath, cancellationToken).ConfigureAwait(false);
            if (confirmedIdentity != previous.ContentIdentity)
            {
                throw new DownloadContentChangedException(previous.ContentIdentity, confirmedIdentity, previous.FilePath);
            }
        }

        if (_cache is not null)
        {
            await _cache.StoreAsync(attempt.Result, cancellationToken).ConfigureAwait(false);
        }

        if (attempt.NotModified)
        {
            return new DownloadRevalidationResult
            {
                Status = DownloadRevalidationStatus.Unchanged,
                Result = attempt.Result,
                WasNotModifiedResponse = true,
            };
        }

        return new DownloadRevalidationResult
        {
            Status = attempt.Result.ContentIdentity == previous.ContentIdentity
                ? DownloadRevalidationStatus.Unchanged
                : DownloadRevalidationStatus.ContentChanged,
            Result = attempt.Result,
        };
    }

    /// <summary>
    /// Probes an installer with HEAD and automatically falls back to a one-byte range GET when the
    /// origin rejects HEAD with HTTP 405 or 501.
    /// </summary>
    public async Task<DownloadProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri initialUri = ValidateUrl(url);

        return await ExecuteWithRetriesAsync(
            async attemptToken =>
            {
                using HttpRequestMessage head = CreateRequest(HttpMethod.Head, initialUri);
                {
                    using RedirectedResponse headExchange = await SendFollowingRedirectsAsync(head, attemptToken).ConfigureAwait(false);
                    HttpResponseMessage headResponse = headExchange.Response;
                    if (headResponse.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
                    {
                        EnsureSuccessOrThrowTransient(headResponse, url);
                        return CreateProbeResult(url, headExchange, DownloadProbeMethod.Head);
                    }
                }

                using HttpRequestMessage range = CreateRequest(HttpMethod.Get, initialUri);
                range.Headers.Range = new RangeHeaderValue(0, 0);
                using RedirectedResponse rangeExchange = await SendFollowingRedirectsAsync(range, attemptToken).ConfigureAwait(false);
                HttpResponseMessage rangeResponse = rangeExchange.Response;
                EnsureSuccessOrThrowTransient(rangeResponse, url);
                return CreateProbeResult(url, rangeExchange, DownloadProbeMethod.RangeGet);
            },
            url,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads several URLs concurrently while preserving input order.</summary>
    public async Task<IReadOnlyList<DownloadResult>> DownloadManyAsync(
        IEnumerable<string> urls,
        string destinationDirectory,
        int maxConcurrency,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<string> urlList = [.. urls];
        var results = new DownloadResult[urlList.Count];
        using SemaphoreSlim gate = new(maxConcurrency);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] tasks = [.. Enumerable.Range(0, urlList.Count).Select(DownloadOneAsync)];
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;

        async Task DownloadOneAsync(int index)
        {
            await gate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            try
            {
                results[index] = await DownloadAsync(urlList[index], destinationDirectory, progress, linkedCts.Token).ConfigureAwait(false);
            }
            catch
            {
                await linkedCts.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        foreach (SemaphoreSlim gate in _downloadGates.Values)
        {
            gate.Dispose();
        }
    }

    private async Task<DownloadAttemptResult> DownloadFromOriginAsync(
        string initialUrl,
        Uri initialUri,
        string destinationDirectory,
        IProgress<DownloadProgress>? progress,
        DownloadResult? previous,
        CancellationToken cancellationToken)
        => await ExecuteWithRetriesAsync(
            attemptToken => DownloadAttemptAsync(
                initialUrl,
                initialUri,
                destinationDirectory,
                progress,
                previous,
                attemptToken),
            initialUrl,
            cancellationToken).ConfigureAwait(false);

    private async Task<DownloadAttemptResult> DownloadAttemptAsync(
        string initialUrl,
        Uri initialUri,
        string destinationDirectory,
        IProgress<DownloadProgress>? progress,
        DownloadResult? previous,
        CancellationToken cancellationToken,
        bool allowConditionalRequest = true)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, initialUri);
        EntityTagHeaderValue? expectedStrongETag = allowConditionalRequest
            ? GetStrongETag(previous?.ETag)
            : null;
        if (expectedStrongETag is not null)
        {
            request.Headers.IfNoneMatch.Add(expectedStrongETag);
        }

        using RedirectedResponse exchange = await SendFollowingRedirectsAsync(request, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response = exchange.Response;

        if (response.StatusCode == HttpStatusCode.NotModified && previous is not null)
        {
            EntityTagHeaderValue? responseETag = response.Headers.ETag;
            if (expectedStrongETag is not null
                && responseETag is { IsWeak: false }
                && string.Equals(expectedStrongETag.Tag, responseETag.Tag, StringComparison.Ordinal))
            {
                return new DownloadAttemptResult(
                    CloneAfterNotModified(previous, exchange),
                    NotModified: true);
            }

            if (allowConditionalRequest && expectedStrongETag is not null)
            {
                exchange.Dispose();
                return await DownloadAttemptAsync(
                    initialUrl,
                    initialUri,
                    destinationDirectory,
                    progress,
                    previous,
                    cancellationToken,
                    allowConditionalRequest: false).ConfigureAwait(false);
            }

            throw new DownloadHttpException(response.StatusCode, initialUrl);
        }

        EnsureSuccessOrThrowTransient(response, initialUrl);
        Uri responseUri = exchange.FinalUri;
        long? totalBytes = response.Content.Headers.ContentLength;
        string fileName = previous?.FileName ?? ResolveFileName(response, initialUri, responseUri);
        string preferredPath = previous?.FilePath ?? Path.Combine(destinationDirectory, fileName);
        string tempPath = preferredPath + TempFileSuffix + "." + Guid.NewGuid().ToString("N");

        long bytesReceived = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            Stream contentStream;
            try
            {
                contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new HttpRequestException("The response stream could not be opened.", exception);
            }

            await using (contentStream.ConfigureAwait(false))
            {
                FileStream fileStream = CreateDestinationFile(tempPath);
                await using (fileStream.ConfigureAwait(false))
                {
                    var buffer = new byte[CopyBufferSize];
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        }
                        catch (IOException exception)
                        {
                            throw new HttpRequestException("The response stream ended unexpectedly.", exception);
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        hash.AppendData(buffer.AsSpan(0, read));
                        try
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            throw new DownloadFileException(tempPath, $"Failed to write the installer to '{tempPath}'.", exception);
                        }
                        bytesReceived += read;
                        progress?.Report(new DownloadProgress(bytesReceived, totalBytes));
                    }

                    try
                    {
                        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new DownloadFileException(tempPath, $"Failed to flush the installer at '{tempPath}'.", exception);
                    }
                }
            }

        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        var identity = new DownloadContentIdentity(
            Sha256Hash.FromHashBytes(hash.GetHashAndReset()),
            bytesReceived);
        string finalPath;
        try
        {
            finalPath = await DownloadDestination.PublishAsync(
                tempPath,
                preferredPath,
                identity,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        DateTimeOffset retrievedAt = _timeProvider.GetUtcNow();
        var result = new DownloadResult
        {
            FilePath = finalPath,
            FileName = Path.GetFileName(finalPath),
            Sha256 = identity.Sha256,
            SizeInBytes = bytesReceived,
            LastModified = response.Content.Headers.LastModified,
            ETag = response.Headers.ETag?.ToString(),
            ResponseDate = response.Headers.Date,
            FreshUntil = GetFreshUntil(
                response,
                exchange.RequestTime,
                exchange.ResponseTime,
                retrievedAt),
            RetrievedAt = retrievedAt,
            InitialUrl = initialUrl,
            FinalUrl = responseUri.AbsoluteUri,
            ContentType = response.Content.Headers.ContentType?.ToString(),
            MayBeStored = response.Headers.CacheControl?.NoStore != true,
        };
        return new DownloadAttemptResult(result, NotModified: false);
    }

    private async Task<T> ExecuteWithRetriesAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string requestUrl,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(_options.Timeout);
                return await operation(attemptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                if (attempt >= _options.MaxRetryAttempts)
                {
                    throw new DownloadNetworkException(
                        $"A transient failure downloading '{requestUrl}' persisted after {attempt + 1} attempt(s).",
                        exception);
                }

                TimeSpan delay = _options.RetryBaseDelay * Math.Pow(2, attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        return request;
    }

    private async Task<RedirectedResponse> SendFollowingRedirectsAsync(
        HttpRequestMessage initialRequest,
        CancellationToken cancellationToken)
    {
        const int maxRedirects = 10;
        var ownedRequests = new List<HttpRequestMessage>();
        HttpRequestMessage currentRequest = initialRequest;
        Uri currentUri = initialRequest.RequestUri!;
        try
        {
            for (int redirectCount = 0; ; redirectCount++)
            {
                DateTimeOffset requestTime = _timeProvider.GetUtcNow();
                HttpResponseMessage response = await _httpClient
                    .SendAsync(currentRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                DateTimeOffset responseTime = _timeProvider.GetUtcNow();
                if ((int)response.StatusCode is not (>= 300 and < 400) || response.Headers.Location is not { } location)
                {
                    return new RedirectedResponse(
                        response,
                        currentUri,
                        requestTime,
                        responseTime,
                        ownedRequests);
                }

                if (redirectCount >= maxRedirects)
                {
                    response.Dispose();
                    throw new DownloadRedirectException(initialRequest.RequestUri!.AbsoluteUri, maxRedirects);
                }

                Uri target = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                EnsureNoHttpsDowngrade(currentUri, target);
                response.Dispose();

                var redirected = new HttpRequestMessage(currentRequest.Method, target);
                foreach (KeyValuePair<string, IEnumerable<string>> header in currentRequest.Headers)
                {
                    redirected.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                ownedRequests.Add(redirected);
                currentRequest = redirected;
                currentUri = target;
            }
        }
        catch
        {
            foreach (HttpRequestMessage request in ownedRequests)
            {
                request.Dispose();
            }

            throw;
        }
    }

    private static DownloadProbeResult CreateProbeResult(
        string initialUrl,
        RedirectedResponse exchange,
        DownloadProbeMethod method)
    {
        HttpResponseMessage response = exchange.Response;
        return new DownloadProbeResult
        {
            InitialUrl = initialUrl,
            FinalUrl = exchange.FinalUri.AbsoluteUri,
            Method = method,
            SizeInBytes = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength,
            ETag = response.Headers.ETag?.ToString(),
            LastModified = response.Content.Headers.LastModified,
            ResponseDate = response.Headers.Date,
            FreshUntil = GetFreshUntil(
                response,
                exchange.RequestTime,
                exchange.ResponseTime,
                exchange.ResponseTime),
            ContentType = response.Content.Headers.ContentType?.ToString(),
            SupportsRanges = (response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange is not null)
                || response.Headers.AcceptRanges.Any(static value => string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static DownloadResult CloneAfterNotModified(
        DownloadResult previous,
        RedirectedResponse exchange)
        => new()
        {
            FilePath = previous.FilePath,
            FileName = previous.FileName,
            Sha256 = previous.Sha256,
            SizeInBytes = previous.SizeInBytes,
            LastModified = exchange.Response.Content.Headers.LastModified ?? previous.LastModified,
            ETag = exchange.Response.Headers.ETag?.ToString() ?? previous.ETag,
            ResponseDate = exchange.Response.Headers.Date ?? previous.ResponseDate,
            FreshUntil = GetFreshUntil(
                exchange.Response,
                exchange.RequestTime,
                exchange.ResponseTime,
                exchange.ResponseTime) ?? previous.FreshUntil,
            RetrievedAt = exchange.ResponseTime,
            InitialUrl = previous.InitialUrl,
            FinalUrl = exchange.FinalUri.AbsoluteUri,
            ContentType = exchange.Response.Content.Headers.ContentType?.ToString() ?? previous.ContentType,
            MayBeStored = exchange.Response.Headers.CacheControl?.NoStore != true && previous.MayBeStored,
        };

    private static DateTimeOffset? GetFreshUntil(
        HttpResponseMessage response,
        DateTimeOffset requestTime,
        DateTimeOffset responseTime,
        DateTimeOffset currentTime)
    {
        if (response.Headers.CacheControl is { NoCache: true } or { NoStore: true })
        {
            return currentTime;
        }

        DateTimeOffset dateValue = response.Headers.Date ?? responseTime;
        TimeSpan? freshnessLifetime = response.Headers.CacheControl?.MaxAge;
        if (freshnessLifetime is null && response.Content.Headers.Expires is { } expires)
        {
            freshnessLifetime = ClampNonNegative(expires - dateValue);
        }

        if (freshnessLifetime is null)
        {
            return null;
        }

        TimeSpan apparentAge = ClampNonNegative(responseTime - dateValue);
        TimeSpan responseDelay = ClampNonNegative(responseTime - requestTime);
        TimeSpan ageValue = ClampNonNegative(response.Headers.Age ?? TimeSpan.Zero);
        TimeSpan correctedAgeValue = AddClamped(ageValue, responseDelay);
        TimeSpan correctedInitialAge = apparentAge > correctedAgeValue
            ? apparentAge
            : correctedAgeValue;
        TimeSpan residentTime = ClampNonNegative(currentTime - responseTime);
        TimeSpan currentAge = AddClamped(correctedInitialAge, residentTime);
        TimeSpan remainingFreshness = freshnessLifetime.Value > currentAge
            ? freshnessLifetime.Value - currentAge
            : TimeSpan.Zero;

        try
        {
            return currentTime + remainingFreshness;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private static TimeSpan ClampNonNegative(TimeSpan value)
        => value > TimeSpan.Zero ? value : TimeSpan.Zero;

    private static TimeSpan AddClamped(TimeSpan left, TimeSpan right)
        => left.Ticks > TimeSpan.MaxValue.Ticks - right.Ticks
            ? TimeSpan.MaxValue
            : left + right;

    private Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException($"The URL '{url}' is not a valid absolute URL.", nameof(url));
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (_options.AllowInsecureDownloads)
            {
                return uri;
            }

            throw new InvalidOperationException(
                $"Refusing to download '{url}' over insecure plain http. Use an https URL, or explicitly enable insecure downloads.");
        }

        throw new ArgumentException(
            $"The URL '{url}' uses the unsupported scheme '{uri.Scheme}'; only http(s) is supported.",
            nameof(url));
    }

    private static void EnsureNoHttpsDowngrade(Uri initialUri, Uri finalUri)
    {
        if (initialUri.Scheme == Uri.UriSchemeHttps && finalUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"Refusing redirect downgrade from secure '{initialUri}' to insecure '{finalUri}'.");
        }
    }

    private static void EnsureSuccessOrThrowTransient(HttpResponseMessage response, string requestUrl)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (IsTransientStatus(response.StatusCode))
        {
            throw new HttpRequestException(
                $"The server returned transient HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                null,
                response.StatusCode);
        }

        throw new DownloadHttpException(response.StatusCode, requestUrl);
    }

    private static bool IsTransient(Exception exception)
        => exception switch
        {
            HttpRequestException { StatusCode: null } => true,
            HttpRequestException { StatusCode: { } status } => IsTransientStatus(status),
            OperationCanceledException => true,
            _ => false,
        };

    private static bool IsTransientStatus(HttpStatusCode status)
        => (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static EntityTagHeaderValue? GetStrongETag(string? value)
        => EntityTagHeaderValue.TryParse(value, out EntityTagHeaderValue? etag) && !etag.IsWeak
            ? etag
            : null;

    private static void CreateDestinationDirectory(string destinationDirectory)
    {
        try
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DownloadFileException(
                destinationDirectory,
                $"Failed to create the installer destination directory '{destinationDirectory}'.",
                exception);
        }
    }

    private static async Task<DownloadContentIdentity> ComputeFileIdentityAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Sha256Hash sha256 = await Sha256Hash.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
        return new DownloadContentIdentity(sha256, stream.Length);
    }

    private static FileStream CreateDestinationFile(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DownloadFileException(path, $"Failed to create the installer file '{path}'.", exception);
        }
    }

    private static string ResolveFileName(HttpResponseMessage response, Uri initialUri, Uri finalUri)
    {
        string? candidate = FileNameFromContentDisposition(response.Content.Headers.ContentDisposition)
            ?? FileNameFromUrl(finalUri)
            ?? FileNameFromUrl(initialUri);
        return SanitizeFileName(candidate);
    }

    private static string? FileNameFromContentDisposition(ContentDispositionHeaderValue? disposition)
    {
        if (disposition is null)
        {
            return null;
        }

        string? name = disposition.FileNameStar;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = disposition.FileName?.Trim('"');
        }

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? FileNameFromUrl(Uri uri)
    {
        string segment = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
        if (segment.Length == 0)
        {
            return null;
        }

        string decoded = Uri.UnescapeDataString(segment);
        return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return FallbackFileName;
        }

        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]) || InvalidFileNameChars.Contains(chars[i], StringComparison.Ordinal))
            {
                chars[i] = '_';
            }
        }

        string sanitized = new(chars);
        return sanitized is "." or ".." ? FallbackFileName : sanitized;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateOptions(DownloaderOptions options)
    {
        if (options.MaxRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retry attempts cannot be negative.");
        }

        if (options.RetryBaseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delay cannot be negative.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive.");
        }

        ArgumentNullException.ThrowIfNull(options.TimeProvider);
    }

    private sealed record DownloadAttemptResult(DownloadResult Result, bool NotModified);

    private sealed class RedirectedResponse : IDisposable
    {
        private readonly IReadOnlyList<HttpRequestMessage> _ownedRequests;

        public RedirectedResponse(
            HttpResponseMessage response,
            Uri finalUri,
            DateTimeOffset requestTime,
            DateTimeOffset responseTime,
            IReadOnlyList<HttpRequestMessage> ownedRequests)
        {
            Response = response;
            FinalUri = finalUri;
            RequestTime = requestTime;
            ResponseTime = responseTime;
            _ownedRequests = ownedRequests;
        }

        public HttpResponseMessage Response { get; }

        public Uri FinalUri { get; }

        public DateTimeOffset RequestTime { get; }

        public DateTimeOffset ResponseTime { get; }

        public void Dispose()
        {
            Response.Dispose();
            foreach (HttpRequestMessage request in _ownedRequests)
            {
                request.Dispose();
            }
        }
    }
}
