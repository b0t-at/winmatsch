using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WinMatsch.GitHub.Internal;

internal sealed class GitHubHttpTransport
{
    private static readonly MediaTypeWithQualityHeaderValue _gitHubJson =
        new("application/vnd.github+json");

    private readonly HttpClient _httpClient;
    private readonly string _accessToken;
    private readonly GitHubClientOptions _options;
    private string[] _lastOAuthScopes = [];

    public GitHubHttpTransport(
        HttpClient httpClient,
        string accessToken,
        GitHubClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _httpClient = httpClient;
        _accessToken = accessToken;
        _options = options;
    }

    public event EventHandler<RateLimitInfo>? RateLimitObserved;

    public IReadOnlyList<string> LastOAuthScopes => Volatile.Read(ref _lastOAuthScopes);

    public async Task<TResponse> GetAsync<TResponse>(
        string relativePath,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        TransportResponse<TResponse> response = await SendAsync(
            HttpMethod.Get,
            new Uri(_options.NormalizedApiBaseUri, relativePath),
            null,
            responseType,
            allowRetry: true,
            cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    public Task<TransportResponse<TResponse>> GetPageAsync<TResponse>(
        Uri uri,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        ValidateApiUri(uri);
        return SendAsync(
            HttpMethod.Get,
            uri,
            null,
            responseType,
            allowRetry: true,
            cancellationToken);
    }

    public Task<TResponse> PostAsync<TRequest, TResponse>(
        string relativePath,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
        => SendValueAsync(
            HttpMethod.Post,
            new Uri(_options.NormalizedApiBaseUri, relativePath),
            Serialize(request, requestType),
            responseType,
            allowRetry: false,
            cancellationToken);

    public Task<TResponse> PatchAsync<TRequest, TResponse>(
        string relativePath,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
        => SendValueAsync(
            HttpMethod.Patch,
            new Uri(_options.NormalizedApiBaseUri, relativePath),
            Serialize(request, requestType),
            responseType,
            allowRetry: false,
            cancellationToken);

    public async Task DeleteAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            new Uri(_options.NormalizedApiBaseUri, relativePath),
            content: null);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ObserveResponseHeaders(response);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(
                response,
                GitHubApiErrorKind.Unknown,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<TResponse> GraphQlQueryAsync<TRequest, TResponse>(
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
        => SendValueAsync(
            HttpMethod.Post,
            _options.ResolvedGraphQlUri,
            Serialize(request, requestType),
            responseType,
            allowRetry: true,
            cancellationToken);

    public Task<TResponse> GraphQlMutationAsync<TRequest, TResponse>(
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
        => SendValueAsync(
            HttpMethod.Post,
            _options.ResolvedGraphQlUri,
            Serialize(request, requestType),
            responseType,
            allowRetry: false,
            cancellationToken);

    private async Task<TResponse> SendValueAsync<TResponse>(
        HttpMethod method,
        Uri uri,
        byte[]? content,
        JsonTypeInfo<TResponse> responseType,
        bool allowRetry,
        CancellationToken cancellationToken)
    {
        TransportResponse<TResponse> response = await SendAsync(
            method,
            uri,
            content,
            responseType,
            allowRetry,
            cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    private async Task<TransportResponse<TResponse>> SendAsync<TResponse>(
        HttpMethod method,
        Uri uri,
        byte[]? content,
        JsonTypeInfo<TResponse> responseType,
        bool allowRetry,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = CreateRequest(method, uri, content);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (allowRetry && attempt < _options.MaxTransientRetries)
            {
                await DelayAsync(
                    attempt,
                    null,
                    secondaryRateLimited: false,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException) when (
                allowRetry &&
                !cancellationToken.IsCancellationRequested &&
                attempt < _options.MaxTransientRetries)
            {
                await DelayAsync(
                    attempt,
                    null,
                    secondaryRateLimited: false,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                ObserveResponseHeaders(response);
                if (!response.IsSuccessStatusCode)
                {
                    bool headerRateLimited = IsRateLimited(response, "");
                    if (allowRetry
                        && attempt < _options.MaxTransientRetries
                        && IsTransient(response, ""))
                    {
                        RetryConditionHeaderValue? retryAfter = GetServerRetryAfter(response);
                        response.Dispose();
                        await DelayAsync(
                                attempt,
                                retryAfter,
                                headerRateLimited,
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    string errorBody;
                    try
                    {
                        errorBody = await response.Content
                            .ReadAsStringAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is HttpRequestException or IOException)
                    {
                        if (allowRetry
                            && attempt < _options.MaxTransientRetries
                            && response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            response.Dispose();
                            await DelayAsync(
                                attempt,
                                GetServerRetryAfter(response),
                                secondaryRateLimited: true,
                                cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        throw CreateUnreadableErrorResponseException(response, exception);
                    }
                    catch (OperationCanceledException exception) when (
                        !cancellationToken.IsCancellationRequested)
                    {
                        if (allowRetry
                            && attempt < _options.MaxTransientRetries
                            && response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            response.Dispose();
                            await DelayAsync(
                                attempt,
                                GetServerRetryAfter(response),
                                secondaryRateLimited: true,
                                cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        throw CreateUnreadableErrorResponseException(response, exception);
                    }

                    bool secondaryRateLimited =
                        IsSecondaryRateLimitResponse(response, errorBody);
                    if (allowRetry &&
                        attempt < _options.MaxTransientRetries &&
                        IsTransient(response, errorBody))
                    {
                        RetryConditionHeaderValue? retryAfter = GetServerRetryAfter(response);
                        response.Dispose();
                        await DelayAsync(
                                attempt,
                                retryAfter,
                                secondaryRateLimited,
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    GitHubApiErrorKind errorKind = IsRateLimited(response, errorBody)
                        ? GitHubApiErrorKind.RateLimited
                        : IsGraphQlEndpoint(uri) &&
                            response.StatusCode is HttpStatusCode.NotFound
                                or HttpStatusCode.MethodNotAllowed
                                or HttpStatusCode.NotImplemented
                                ? GitHubApiErrorKind.GraphQlUnavailable
                                : GitHubApiErrorKind.Unknown;
                    throw await CreateExceptionAsync(
                        response,
                        errorKind,
                        cancellationToken,
                        errorBody).ConfigureAwait(false);
                }

                await using Stream stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                TResponse? value = await JsonSerializer.DeserializeAsync(
                    stream,
                    responseType,
                    cancellationToken).ConfigureAwait(false);
                if (value is null)
                {
                    throw new GitHubApiException(
                        "GitHub returned an empty JSON response.",
                        response.StatusCode,
                        GetRequestId(response));
                }

                return new TransportResponse<TResponse>(
                    value,
                    TryGetNextUri(response.Headers));
            }
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, byte[]? content)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(_gitHubJson);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (content is not null)
        {
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private static byte[] Serialize<TRequest>(
        TRequest request,
        JsonTypeInfo<TRequest> requestType)
        => JsonSerializer.SerializeToUtf8Bytes(request, requestType);

    private async Task DelayAsync(
        int attempt,
        RetryConditionHeaderValue? retryAfter,
        bool secondaryRateLimited,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = retryAfter?.Delta ??
            (retryAfter?.Date is DateTimeOffset date
                ? date - DateTimeOffset.UtcNow
                : (secondaryRateLimited
                    ? _options.SecondaryRateLimitBaseDelay
                    : _options.RetryBaseDelay) * Math.Pow(2, attempt));
        TimeSpan maximumDelay = secondaryRateLimited
            ? _options.MaxSecondaryRateLimitDelay
            : _options.MaxRetryDelay;
        delay = TimeSpan.FromMilliseconds(Math.Clamp(
            delay.TotalMilliseconds,
            0,
            maximumDelay.TotalMilliseconds));
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ObserveResponseHeaders(HttpResponseMessage response)
    {
        ObserveRateLimit(response);
        if (response.Headers.TryGetValues(
                "X-OAuth-Scopes",
                out IEnumerable<string>? scopeHeaders))
        {
            string[] scopes = scopeHeaders
                .SelectMany(static value => value.Split(','))
                .Select(static value => value.Trim())
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Volatile.Write(ref _lastOAuthScopes, scopes);
        }
    }

    private void ObserveRateLimit(HttpResponseMessage response)
    {
        if (!TryGetRateLimit(response, out RateLimitInfo rateLimit))
        {
            return;
        }

        RateLimitObserved?.Invoke(this, rateLimit);
    }

    private static bool TryGetRateLimit(
        HttpResponseMessage response,
        out RateLimitInfo rateLimit)
    {
        if (!TryGetIntHeader(response.Headers, "X-RateLimit-Limit", out int limit)
            || !TryGetIntHeader(response.Headers, "X-RateLimit-Remaining", out int remaining)
            || !TryGetLongHeader(response.Headers, "X-RateLimit-Reset", out long reset))
        {
            rateLimit = null!;
            return false;
        }

        TryGetIntHeader(response.Headers, "X-RateLimit-Used", out int used);
        string resource = TryGetHeader(response.Headers, "X-RateLimit-Resource") ?? "core";
        rateLimit = new(
            resource,
            limit,
            remaining,
            used,
            DateTimeOffset.FromUnixTimeSeconds(reset));
        return true;
    }

    private static bool IsTransient(HttpResponseMessage response, string responseBody)
        => response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500 ||
            (response.StatusCode == HttpStatusCode.Forbidden &&
             (response.Headers.RetryAfter is not null ||
              string.Equals(
                  TryGetHeader(response.Headers, "X-RateLimit-Remaining"),
                  "0",
                  StringComparison.Ordinal) ||
              IsSecondaryRateLimitBody(responseBody)));

    private static bool IsRateLimited(HttpResponseMessage response, string responseBody)
        => response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden
               && (response.Headers.RetryAfter is not null
                  || string.Equals(
                      TryGetHeader(response.Headers, "X-RateLimit-Remaining"),
                      "0",
                      StringComparison.Ordinal)
                  || IsSecondaryRateLimitBody(responseBody)));

    private static bool IsSecondaryRateLimitBody(string responseBody)
    {
        RestErrorDto? error;
        try
        {
            error = JsonSerializer.Deserialize(
               responseBody,
               GitHubJsonContext.Default.RestErrorDto);
        }
        catch (JsonException)
        {
            return false;
        }

        return error?.Message?.Contains(
            "secondary rate limit",
            StringComparison.OrdinalIgnoreCase) == true
            || error?.Message?.Contains(
               "abuse detection",
               StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsSecondaryRateLimitResponse(
        HttpResponseMessage response,
        string responseBody)
        => (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            && IsSecondaryRateLimitBody(responseBody);

    private static RetryConditionHeaderValue? GetServerRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not null)
        {
            return response.Headers.RetryAfter;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
            string.Equals(
                TryGetHeader(response.Headers, "X-RateLimit-Remaining"),
                "0",
                StringComparison.Ordinal) &&
            TryGetLongHeader(response.Headers, "X-RateLimit-Reset", out long reset))
        {
            try
            {
                return new RetryConditionHeaderValue(DateTimeOffset.FromUnixTimeSeconds(reset));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static async Task<GitHubApiException> CreateExceptionAsync(
        HttpResponseMessage response,
        GitHubApiErrorKind errorKind,
        CancellationToken cancellationToken,
        string? responseBody = null)
    {
        string body = responseBody
            ?? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        RestErrorDto? error = null;
        try
        {
            error = JsonSerializer.Deserialize(body, GitHubJsonContext.Default.RestErrorDto);
        }
        catch (JsonException)
        {
            // Preserve the status and request ID when a proxy returns non-JSON error content.
        }

        List<string> details = error?.Errors?
            .Select(static item => item.Message ?? item.Code ?? "Unknown GitHub error")
            .ToList() ?? [];
        string message = error?.Message ??
            (string.IsNullOrWhiteSpace(body)
                ? $"GitHub returned HTTP {(int)response.StatusCode}."
                : body);
        RateLimitInfo? rateLimit = TryGetRateLimit(response, out RateLimitInfo parsedRateLimit)
            ? parsedRateLimit
            : null;
        return new GitHubApiException(
            message,
            response.StatusCode,
            GetRequestId(response),
            details,
            errorKind: errorKind,
            rateLimit: rateLimit,
            retryAfter: GetResponseRetryAfter(response));
    }

    private static GitHubApiException CreateUnreadableErrorResponseException(
        HttpResponseMessage response,
        Exception inner)
    {
        bool conservativelyRateLimited =
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;
        RateLimitInfo? rateLimit = TryGetRateLimit(response, out RateLimitInfo parsedRateLimit)
            ? parsedRateLimit
            : null;
        return new GitHubApiException(
            $"GitHub returned HTTP {(int)response.StatusCode}, but its error response could not be read.",
            response.StatusCode,
            GetRequestId(response),
            inner: inner,
            errorKind: conservativelyRateLimited
                ? GitHubApiErrorKind.RateLimited
                : GitHubApiErrorKind.Unknown,
            rateLimit: rateLimit,
            retryAfter: GetResponseRetryAfter(response));
    }

    private static TimeSpan? GetResponseRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delay)
        {
            return delay;
        }

        return retryAfter?.Date is { } retryAt
            ? retryAt - DateTimeOffset.UtcNow is { } remaining && remaining > TimeSpan.Zero
                ? remaining
                : TimeSpan.Zero
            : null;
    }

    private static Uri? TryGetNextUri(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string value in values)
        {
            foreach (string link in value.Split(','))
            {
                string[] parts = link.Split(';', StringSplitOptions.TrimEntries);
                if (parts.Length < 2 ||
                    !parts.Skip(1).Any(static part =>
                        string.Equals(part, "rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string uriText = parts[0].Trim();
                if (uriText.Length > 2 &&
                    uriText[0] == '<' &&
                    uriText[^1] == '>' &&
                    Uri.TryCreate(uriText[1..^1], UriKind.Absolute, out Uri? next) &&
                    next.Scheme is "http" or "https")
                {
                    return next;
                }

                throw new GitHubApiException(
                    "GitHub returned an invalid absolute pagination link.");
            }
        }

        return null;
    }

    private void ValidateApiUri(Uri uri)
    {
        Uri apiBaseUri = _options.NormalizedApiBaseUri;
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, apiBaseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, apiBaseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != apiBaseUri.Port)
        {
            throw new GitHubApiException(
                "GitHub returned a pagination link outside the configured API origin.");
        }
    }

    private bool IsGraphQlEndpoint(Uri uri)
        => uri == _options.ResolvedGraphQlUri;

    private static string? GetRequestId(HttpResponseMessage response)
        => TryGetHeader(response.Headers, "X-GitHub-Request-Id");

    private static string? TryGetHeader(HttpResponseHeaders headers, string name)
        => headers.TryGetValues(name, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    private static bool TryGetIntHeader(
        HttpResponseHeaders headers,
        string name,
        out int result)
        => int.TryParse(
            TryGetHeader(headers, name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out result);

    private static bool TryGetLongHeader(
        HttpResponseHeaders headers,
        string name,
        out long result)
        => long.TryParse(
            TryGetHeader(headers, name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out result);
}

internal readonly record struct TransportResponse<T>(
    T Value,
    Uri? NextUri);
