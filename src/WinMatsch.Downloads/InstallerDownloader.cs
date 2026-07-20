using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using WinMatsch.Core;

namespace WinMatsch.Downloads;

/// <summary>
/// Downloads installer payloads over HTTP(S) for the manifest update/new flows: each payload is
/// streamed to disk while its SHA-256 is computed incrementally (the file is never read back),
/// response metadata needed later is captured (final URL after redirects for vanity-URL detection,
/// Last-Modified, Content-Type), and transient failures are retried with exponential backoff.
/// </summary>
public sealed class InstallerDownloader : IDisposable
{
    private const int CopyBufferSize = 81920;
    private const string FallbackFileName = "download";
    private const string TempFileSuffix = ".part";

    // Windows' invalid file name characters (a superset of Unix's), applied on every OS so that a
    // given URL yields the same file name regardless of the platform the tool runs on.
    private const string InvalidFileNameChars = "\"<>:/\\|?*";

    private readonly HttpClient _httpClient;
    private readonly DownloaderOptions _options;
    private bool _disposed;

    /// <summary>
    /// Creates a downloader with its own connection handler (automatic redirects and decompression enabled).
    /// </summary>
    public InstallerDownloader(DownloaderOptions? options = null)
        : this(new SocketsHttpHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All }, options)
    {
    }

    /// <summary>
    /// Creates a downloader that sends its requests through the given handler. This overload exists
    /// for testing and customization; the handler is disposed together with the downloader.
    /// </summary>
    public InstallerDownloader(HttpMessageHandler handler, DownloaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _options = options ?? new DownloaderOptions();

        // Attempt timeouts are enforced per request via a linked CancellationTokenSource below,
        // because HttpClient.Timeout would abort large streaming downloads mid-transfer.
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="destinationDirectory"/> (created when
    /// missing), overwriting an existing file of the same name. The payload is written to a
    /// temporary "<c>.part</c>" file first and renamed on success.
    /// </summary>
    /// <exception cref="ArgumentException">The URL is not an absolute http(s) URL.</exception>
    /// <exception cref="InvalidOperationException">The URL uses plain http and <see cref="DownloaderOptions.AllowInsecureDownloads"/> is off.</exception>
    /// <exception cref="HttpRequestException">The server answered with a non-success status, or the connection failed after all retries.</exception>
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
        Directory.CreateDirectory(destinationDirectory);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await DownloadAttemptAsync(url, initialUri, destinationDirectory, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < _options.MaxRetryAttempts && IsTransient(exception, cancellationToken))
            {
                TimeSpan delay = _options.RetryBaseDelay * Math.Pow(2, attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Downloads several URLs concurrently (at most <paramref name="maxConcurrency"/> in flight),
    /// returning the results in the same order as the input. The first failure cancels the
    /// remaining downloads and is rethrown. Progress reports are per file, not aggregated.
    /// </summary>
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

        var tasks = new Task[urlList.Count];
        for (int i = 0; i < urlList.Count; i++)
        {
            tasks[i] = DownloadOneAsync(i);
        }

        // WhenAll prefers a faulted task over cancelled siblings, so the genuine first failure
        // surfaces here rather than the cooperative cancellations it triggered.
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
                // Fail fast: stop the remaining downloads as soon as one of them fails.
                await linkedCts.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    private async Task<DownloadResult> DownloadAttemptAsync(
        string initialUrl,
        Uri initialUri,
        string destinationDirectory,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(_options.Timeout);

        using HttpRequestMessage request = new(HttpMethod.Get, initialUri);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
            .ConfigureAwait(false);

        // Throws HttpRequestException carrying the status code; the retry filter decides whether
        // the status (5xx/408/429) is worth another attempt.
        response.EnsureSuccessStatusCode();

        Uri finalUri = response.RequestMessage?.RequestUri ?? initialUri;
        long? totalBytes = response.Content.Headers.ContentLength;
        string fileName = ResolveFileName(response, initialUri, finalUri);
        string finalPath = Path.Combine(destinationDirectory, fileName);
        string tempPath = finalPath + TempFileSuffix;

        long bytesReceived = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            Stream contentStream = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
            await using (contentStream.ConfigureAwait(false))
            {
                FileStream fileStream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
                await using (fileStream.ConfigureAwait(false))
                {
                    byte[] buffer = new byte[CopyBufferSize];
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer.AsMemory(), attemptCts.Token).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer.AsSpan(0, read));
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), attemptCts.Token).ConfigureAwait(false);
                        bytesReceived += read;
                        progress?.Report(new DownloadProgress(bytesReceived, totalBytes));
                    }
                }
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        return new DownloadResult
        {
            FilePath = finalPath,
            FileName = fileName,
            Sha256 = Sha256Hash.FromHashBytes(hash.GetHashAndReset()),
            SizeInBytes = bytesReceived,
            LastModified = response.Content.Headers.LastModified,
            InitialUrl = initialUrl,
            FinalUrl = finalUri.AbsoluteUri,
            ContentType = response.Content.Headers.ContentType?.ToString(),
        };
    }

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
                $"Refusing to download '{url}' over insecure plain http. Use an https URL, or set {nameof(DownloaderOptions)}.{nameof(DownloaderOptions.AllowInsecureDownloads)} to opt in.");
        }

        throw new ArgumentException($"The URL '{url}' uses the unsupported scheme '{uri.Scheme}'; only https (and http when insecure downloads are allowed) is supported.", nameof(url));
    }

    /// <summary>
    /// Decides whether a failed attempt may be retried: connection-level errors, dropped streams,
    /// per-attempt timeouts (but never user-requested cancellation), and 5xx/408/429 statuses.
    /// </summary>
    private static bool IsTransient(Exception exception, CancellationToken userToken)
    {
        return exception switch
        {
            HttpRequestException { StatusCode: null } => true,
            HttpRequestException { StatusCode: { } status } => IsTransientStatus(status),
            IOException => true,
            OperationCanceledException => !userToken.IsCancellationRequested,
            _ => false,
        };
    }

    private static bool IsTransientStatus(HttpStatusCode status)
        => (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>
    /// Resolves the file name from, in order: the Content-Disposition header (filename*, then
    /// filename), the last segment of the final URL path (percent-decoded), and the last segment of
    /// the initial URL. The result is sanitized; when nothing usable remains, "download" is used.
    /// </summary>
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

        // FileNameStar is RFC 5987-decoded by the getter; FileName may keep its surrounding quotes.
        string? name = disposition.FileNameStar;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = disposition.FileName?.Trim('"');
        }

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? FileNameFromUrl(Uri uri)
    {
        string path = uri.AbsolutePath;
        string segment = path[(path.LastIndexOf('/') + 1)..];
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

        // "." and "." + "." pass character sanitization but would escape the destination directory.
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
            // Best effort: the partial file could not be removed (e.g. still locked); leave it behind.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, see above.
        }
    }
}
