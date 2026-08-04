using System.Net;
using Xunit;

namespace WinMatsch.GitHub.Tests;

public sealed class GitHubTransportSafetyTests
{
    private static readonly RepositoryCoordinates _repository = new("upstream", "repo");

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Rate_limit_reset_is_honored_and_response_is_disposed_before_retry(
        HttpStatusCode statusCode)
    {
        var handler = new ScriptedHttpMessageHandler();
        var content = new DisposeTrackingContent("""{"message":"rate limited"}"""u8.ToArray());
        handler.Add(_ =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = content,
            };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "0");
            return response;
        });
        handler.Add(_ =>
        {
            Assert.True(content.IsDisposed);
            return ContentResponse();
        });
        GitHubRepositoryClient client = CreateClient(
            handler,
            new Uri("https://github.invalid/api/"),
            retryBaseDelay: TimeSpan.FromSeconds(30));

        RepositoryContent result = await client.GetContentAsync(
            _repository,
            "manifest.yaml",
            "main",
            TestContext.Current.CancellationToken);

        Assert.Equal("Test", result.GetText());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Invalid_rate_limit_reset_uses_safe_configured_fallback()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"rate limited"}""",
            HttpStatusCode.TooManyRequests,
            ("X-RateLimit-Reset", "not-a-timestamp")));
        handler.Add(_ => ContentResponse());

        RepositoryContent result = await CreateClient(
                handler,
                new Uri("https://github.invalid/api/"),
                retryBaseDelay: TimeSpan.Zero)
            .GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                TestContext.Current.CancellationToken);

        Assert.Equal("Test", result.GetText());
    }

    [Fact]
    public async Task Rate_limit_status_retries_before_reading_an_unreadable_error_body()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new ThrowingReadContent(),
        });
        handler.Add(_ => ContentResponse());
        GitHubClientOptions options = CreateOptions(
            maxRetryDelay: TimeSpan.Zero,
            maxTransientRetries: 1);

        RepositoryContent result = await GitHubClientTestSupport.CreateClient(handler, options)
            .GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                TestContext.Current.CancellationToken);

        Assert.Equal("Test", result.GetText());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Unreadable_final_rate_limit_response_preserves_classification()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new ThrowingReadContent(),
        });

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(
                    handler,
                    CreateOptions(maxTransientRetries: 0))
                .GetContentAsync(
                    _repository,
                    "manifest.yaml",
                    "main",
                    TestContext.Current.CancellationToken));

        Assert.Equal(GitHubApiErrorKind.RateLimited, exception.ErrorKind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Server_retry_delays_are_bounded_by_configured_ceiling(bool useRetryAfter)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => useRetryAfter
            ? GitHubClientTestSupport.Json(
                """{"message":"temporarily unavailable"}""",
                HttpStatusCode.ServiceUnavailable,
                ("Retry-After", "86400"))
            : GitHubClientTestSupport.Json(
                """{"message":"rate limited"}""",
                HttpStatusCode.TooManyRequests,
                ("X-RateLimit-Remaining", "0"),
                ("X-RateLimit-Reset", "4102444800")));
        handler.Add(_ => ContentResponse());
        GitHubClientOptions options = CreateOptions(
            maxRetryDelay: TimeSpan.Zero,
            maxTransientRetries: 1);

        RepositoryContent result = await GitHubClientTestSupport.CreateClient(handler, options)
            .GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                TestContext.Current.CancellationToken);

        Assert.Equal("Test", result.GetText());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_interrupts_server_directed_retry_delay()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"temporarily unavailable"}""",
            HttpStatusCode.ServiceUnavailable,
            ("Retry-After", "86400")));
        GitHubClientOptions options = CreateOptions(
            maxRetryDelay: TimeSpan.FromMinutes(1),
            maxTransientRetries: 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GitHubClientTestSupport.CreateClient(handler, options).GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                cancellation.Token));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://github.invalid/api/page-2")]
    [InlineData("https://other.invalid/api/page-2")]
    [InlineData("https://github.invalid:444/api/page-2")]
    public async Task Pagination_rejects_links_outside_configured_api_origin(string nextPage)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            "[]",
            headers: [("Link", $"<{nextPage}>; rel=\"next\"")]));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).GetReleasesAsync(
                _repository,
                TestContext.Current.CancellationToken));

        Assert.Contains("pagination link", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pagination_rejects_a_relative_next_link()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            "[]",
            headers: [("Link", "</api/page-2>; rel=\"next\"")]));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).GetReleasesAsync(
                _repository,
                TestContext.Current.CancellationToken));

        Assert.Contains("absolute pagination link", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pagination_rejects_same_origin_loops_before_repeating_request()
    {
        const string firstPage =
            "https://github.invalid/api/repos/upstream/repo/releases?per_page=100";
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            "[]",
            headers: [("Link", $"<{firstPage}>; rel=\"next\"")]));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).GetReleasesAsync(
                _repository,
                TestContext.Current.CancellationToken));

        Assert.Contains("loops", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pagination_page_limit_stops_before_requesting_excess_page()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            "[]",
            headers: [("Link", "<https://github.invalid/api/page-2>; rel=\"next\"")]));
        GitHubClientOptions options = CreateOptions(maxPaginationPages: 1);

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler, options).GetReleasesAsync(
                _repository,
                TestContext.Current.CancellationToken));

        Assert.Contains("1 pages", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pagination_item_limit_rejects_oversized_result()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            [
              { "name": "one", "commit": { "sha": "aaa" }, "protected": false },
              { "name": "two", "commit": { "sha": "bbb" }, "protected": false }
            ]
            """));
        GitHubClientOptions options = CreateOptions(maxPaginationItems: 1);

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler, options).GetBranchesAsync(
                _repository,
                TestContext.Current.CancellationToken));

        Assert.Contains("1 items", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Enterprise_api_base_path_is_normalized_before_routes_are_resolved()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Equal(
                "https://ghe.invalid/api/v3/repos/upstream/repo/contents/manifest.yaml?ref=main",
                request.Uri.AbsoluteUri);
            return ContentResponse();
        });

        RepositoryContent result = await CreateClient(
                handler,
                new Uri("https://ghe.invalid/api/v3"),
                retryBaseDelay: TimeSpan.Zero)
            .GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                TestContext.Current.CancellationToken);

        Assert.Equal("Test", result.GetText());
    }

    [Fact]
    public async Task Enterprise_api_base_path_is_preserved_for_release_enumeration()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Equal(
                "https://ghe.invalid/api/v3/repos/upstream/repo/releases?per_page=100",
                request.Uri.AbsoluteUri);
            return GitHubClientTestSupport.Json("[]");
        });

        IReadOnlyList<GitHubRelease> releases = await CreateClient(
                handler,
                new Uri("https://ghe.invalid/api/v3"),
                retryBaseDelay: TimeSpan.Zero)
            .GetReleasesAsync(
                _repository,
                TestContext.Current.CancellationToken);

        Assert.Empty(releases);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://ghe.invalid/api/v3", "https://ghe.invalid/api/graphql")]
    [InlineData("https://ghe.invalid/tenant/api/v3/", "https://ghe.invalid/tenant/api/graphql")]
    [InlineData("https://ghe.invalid/custom", "https://ghe.invalid/custom/graphql")]
    public async Task Graphql_endpoint_is_derived_from_api_base_without_leaving_origin(
        string apiBase,
        string expectedGraphQl)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Equal(expectedGraphQl, request.Uri.AbsoluteUri);
            return GitHubClientTestSupport.Json(
                """
                {
                  "data": {
                    "viewer": {
                      "login": "octocat",
                      "avatarUrl": "https://ghe.invalid/avatar.png"
                    }
                  }
                }
                """);
        });
        var options = new GitHubClientOptions
        {
            ApiBaseUri = new Uri(apiBase),
            UserAgent = "winmatsch-tests",
        };

        GitHubUser user = await GitHubClientTestSupport.CreateClient(handler, options)
            .GetAuthenticatedUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal("octocat", user.Login);
        Assert.DoesNotContain(
            handler.Requests,
            static request => request.Uri.Host.Equals(
                "api.github.com",
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://api.github.com/graphql")]
    [InlineData("https://ghe.invalid/api/v3")]
    [InlineData("https://ghe.invalid/api/graphql?tenant=one")]
    public void Explicit_graphql_endpoint_must_match_api_origin_and_path(string graphQlUri)
    {
        var handler = new ScriptedHttpMessageHandler();
        var options = new GitHubClientOptions
        {
            ApiBaseUri = new Uri("https://ghe.invalid/api/v3"),
            GraphQlUri = new Uri(graphQlUri),
        };

        Assert.Throws<ArgumentException>(
            () => GitHubClientTestSupport.CreateClient(handler, options));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Injected_http_client_remains_caller_owned_by_default()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var httpClient = new HttpClient(handler);
        var client = new GitHubRepositoryClient(
            httpClient,
            "synthetic-token",
            GitHubClientTestSupport.CreateOptions());

        client.Dispose();
        using HttpResponseMessage response = await httpClient.GetAsync(
            "https://github.invalid/after-dispose",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(handler.IsDisposed);
    }

    [Fact]
    public void Injected_http_client_can_be_explicitly_owned()
    {
        var handler = new ScriptedHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var client = new GitHubRepositoryClient(
            httpClient,
            "synthetic-token",
            GitHubClientTestSupport.CreateOptions(),
            disposeHttpClient: true);

        client.Dispose();
        client.Dispose();

        Assert.True(handler.IsDisposed);
    }

    private static GitHubRepositoryClient CreateClient(
        ScriptedHttpMessageHandler handler,
        Uri apiBaseUri,
        TimeSpan retryBaseDelay)
        => new(
            new HttpClient(handler),
            "synthetic-token",
            new GitHubClientOptions
            {
                ApiBaseUri = apiBaseUri,
                UserAgent = "winmatsch-tests",
                MaxTransientRetries = 1,
                RetryBaseDelay = retryBaseDelay,
                SecondaryRateLimitBaseDelay = TimeSpan.Zero,
                MaxSecondaryRateLimitDelay = TimeSpan.Zero,
            });

    private static GitHubClientOptions CreateOptions(
        TimeSpan? maxRetryDelay = null,
        int maxTransientRetries = 2,
        int maxPaginationPages = 100,
        int maxPaginationItems = 10_000)
        => new()
        {
            ApiBaseUri = new Uri("https://github.invalid/api/"),
            GraphQlUri = new Uri("https://github.invalid/graphql"),
            UserAgent = "winmatsch-tests",
            RetryBaseDelay = TimeSpan.Zero,
            MaxTransientRetries = maxTransientRetries,
            MaxRetryDelay = maxRetryDelay ?? TimeSpan.FromSeconds(30),
            SecondaryRateLimitBaseDelay = TimeSpan.Zero,
            MaxSecondaryRateLimitDelay = TimeSpan.Zero,
            MaxPaginationPages = maxPaginationPages,
            MaxPaginationItems = maxPaginationItems,
            ForkAvailabilityBaseDelay = TimeSpan.Zero,
            ForkAvailabilityMaxAttempts = 3,
        };

    private static HttpResponseMessage ContentResponse()
        => GitHubClientTestSupport.Json(
            """
            {
              "name": "manifest.yaml",
              "path": "manifest.yaml",
              "sha": "aaa",
              "size": 4,
              "encoding": "base64",
              "content": "VGVzdA=="
            }
            """);

    private sealed class DisposeTrackingContent(byte[] content) : ByteArrayContent(content)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
            => Task.FromException(new HttpRequestException("Synthetic body read failure."));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
