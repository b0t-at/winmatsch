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
                GraphQlUri = new Uri("https://github.invalid/graphql"),
                UserAgent = "winmatsch-tests",
                MaxTransientRetries = 1,
                RetryBaseDelay = retryBaseDelay,
            });

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
}
