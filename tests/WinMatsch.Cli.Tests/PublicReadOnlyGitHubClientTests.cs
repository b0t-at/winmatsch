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
        using var client = new PublicReadOnlyGitHubClient(
            new GitHubClientOptions(),
            token: null,
            new HttpClient(handler));

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
        using var client = new PublicReadOnlyGitHubClient(
            new GitHubClientOptions(),
            token: null,
            new HttpClient(handler));

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
        using var client = new PublicReadOnlyGitHubClient(
            new GitHubClientOptions(),
            "stale-token",
            new HttpClient(handler));

        IReadOnlyList<RepositoryTreeEntry> entries = await client.GetTreeAsync(
            new RepositoryCoordinates("owner", "repo"),
            "tree",
            recursive: false);

        Assert.Empty(entries);
        Assert.Equal(2, handler.Calls);
        Assert.True(handler.FirstWasAuthenticated);
        Assert.True(handler.SecondWasAnonymous);
    }

    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

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
    }

    private sealed class StaleTokenHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public bool FirstWasAuthenticated { get; private set; }

        public bool SecondWasAnonymous { get; private set; }

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

            SecondWasAnonymous = request.Headers.Authorization is null;
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
