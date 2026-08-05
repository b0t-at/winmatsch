using System.Net;
using System.Text;
using WinMatsch.Cli.Commands.Diagnostics;
using WinMatsch.GitHub;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class PublicReadOnlyGitHubClientTests
{
    [Fact]
    public async Task Nonrecursive_tree_request_omits_recursive_query_and_authorization()
    {
        var handler = new RecordingHandler(
            """{"sha":"tree","truncated":false,"tree":[]}""");
        using var httpClient = new HttpClient(handler);
        using var client = new PublicReadOnlyGitHubClient(
            new GitHubClientOptions(),
            token: null,
            httpClient);

        IReadOnlyList<RepositoryTreeEntry> entries = await client.GetTreeAsync(
            new RepositoryCoordinates("owner", "repo"),
            "tree",
            recursive: false);

        Assert.Empty(entries);
        Assert.Equal(string.Empty, handler.RequestUri!.Query);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task Truncated_tree_response_is_rejected()
    {
        var handler = new RecordingHandler(
            """{"sha":"tree","truncated":true,"tree":[]}""");
        using var httpClient = new HttpClient(handler);
        using var client = new PublicReadOnlyGitHubClient(
            new GitHubClientOptions(),
            token: null,
            httpClient);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetTreeAsync(
                new RepositoryCoordinates("owner", "repo"),
                "tree",
                recursive: true));

        Assert.Contains("truncated", exception.Message, StringComparison.Ordinal);
        Assert.Equal("?recursive=1", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task Stale_optional_token_retries_public_read_anonymously()
    {
        var handler = new StaleTokenHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new PublicReadOnlyGitHubClient(
            new GitHubClientOptions(),
            "stale-token",
            httpClient);

        IReadOnlyList<RepositoryTreeEntry> entries = await client.GetTreeAsync(
            new RepositoryCoordinates("owner", "repo"),
            "tree",
            recursive: false);
        _ = await client.GetTreeAsync(
            new RepositoryCoordinates("owner", "repo"),
            "tree",
            recursive: false);

        Assert.Empty(entries);
        Assert.Equal(3, handler.Calls);
        Assert.True(handler.FirstWasAuthenticated);
        Assert.True(handler.SecondWasAnonymous);
        Assert.True(handler.ThirdWasAnonymous);
    }

    [Fact]
    public async Task Public_release_assets_are_available_without_a_token()
    {
        var handler = new RecordingHandler(
            """
            [{
              "id": 174,
              "tag_name": "1.7.4",
              "name": "1.7.4",
              "body": null,
              "html_url": "https://github.com/vcmi/vcmi/releases/tag/1.7.4",
              "draft": false,
              "prerelease": false,
              "published_at": "2026-08-01T00:00:00Z",
              "updated_at": "2026-08-01T00:01:00Z",
              "assets": [{
                "id": 1,
                "name": "VCMI-Windows-x64.exe",
                "browser_download_url": "https://github.com/vcmi/vcmi/releases/download/1.7.4/VCMI-Windows-x64.exe",
                "content_type": "application/octet-stream",
                "size": 42,
                "download_count": 7,
                "created_at": "2026-08-01T00:00:00Z",
                "updated_at": "2026-08-01T00:01:00Z"
              }]
            }]
            """);
        using var httpClient = new HttpClient(handler);
        using var client = new PublicReadOnlyGitHubClient(
            new GitHubClientOptions(),
            token: null,
            httpClient);

        IReadOnlyList<GitHubRelease> releases = await client.GetReleasesAsync(
            new RepositoryCoordinates("vcmi", "vcmi"));

        ReleaseAsset asset = Assert.Single(Assert.Single(releases).Assets);
        Assert.Equal("VCMI-Windows-x64.exe", asset.Name);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public void Dispose_preserves_a_caller_owned_http_client()
    {
        var handler = new RecordingHandler(
            """{"sha":"tree","truncated":false,"tree":[]}""");
        using var httpClient = new HttpClient(handler);
        var client = new PublicReadOnlyGitHubClient(
            new GitHubClientOptions(),
            token: null,
            httpClient);

        client.Dispose();

        Assert.False(handler.IsDisposed);
    }

    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StaleTokenHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public bool FirstWasAuthenticated { get; private set; }

        public bool SecondWasAnonymous { get; private set; }

        public bool ThirdWasAnonymous { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                FirstWasAuthenticated = request.Headers.Authorization is not null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (Calls == 2)
            {
                SecondWasAnonymous = request.Headers.Authorization is null;
            }
            else
            {
                ThirdWasAnonymous = request.Headers.Authorization is null;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"sha":"tree","truncated":false,"tree":[]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
