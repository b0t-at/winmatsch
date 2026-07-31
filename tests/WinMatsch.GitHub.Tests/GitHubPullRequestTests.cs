using System.Net;
using Xunit;

namespace WinMatsch.GitHub.Tests;

public sealed class GitHubPullRequestTests
{
    private static readonly RepositoryCoordinates _repository = new("upstream", "repo");

    [Fact]
    public async Task Pull_request_search_paginates_and_matches_exact_title_token()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Contains("head=contributor%3Aupdate", request.Uri.Query, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                $"[{GitHubClientTestSupport.PullRequestJson(1, "Update Contoso.AppExtra")}]",
                headers:
                [
                    ("Link", "<https://github.invalid/api/pr-page-2>; rel=\"next\""),
                ]);
        });
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"[{GitHubClientTestSupport.PullRequestJson(2, "Update Contoso.App")}]"));

        IReadOnlyList<PullRequestInfo> pullRequests = await GitHubClientTestSupport
            .CreateClient(handler)
            .SearchPullRequestsAsync(
                _repository,
                new PullRequestSearch(
                    PullRequestState.Open,
                    "contributor",
                    "update",
                    "main",
                    "Contoso.App"),
                TestContext.Current.CancellationToken);

        PullRequestInfo pullRequest = Assert.Single(pullRequests);
        Assert.Equal(2, pullRequest.Number);
    }

    [Fact]
    public async Task Pull_request_from_deleted_fork_uses_head_user()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            [{
              "number": 3,
              "node_id": "PR_3",
              "title": "Update deleted fork",
              "body": null,
              "state": "open",
              "draft": false,
              "head": {
                "label": "contributor:update",
                "ref": "update",
                "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "repo": null,
                "user": { "login": "contributor" }
              },
              "base": {
                "label": "upstream:main",
                "ref": "main",
                "sha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "repo": null,
                "user": { "login": "upstream" }
              },
              "html_url": "https://github.invalid/upstream/repo/pull/3",
              "created_at": "2026-01-01T00:00:00Z",
              "updated_at": "2026-01-02T00:00:00Z"
            }]
            """));

        IReadOnlyList<PullRequestInfo> pullRequests = await GitHubClientTestSupport
            .CreateClient(handler)
            .SearchPullRequestsAsync(
                _repository,
                new PullRequestSearch(),
                TestContext.Current.CancellationToken);

        PullRequestInfo pullRequest = Assert.Single(pullRequests);
        Assert.Equal("contributor", pullRequest.HeadOwner);
        Assert.Equal("update", pullRequest.HeadBranch);
    }

    [Fact]
    public async Task Existing_pull_request_prevents_duplicate_creation()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"[{GitHubClientTestSupport.PullRequestJson(7, "Update Contoso.App")}]"));
        var request = new CreatePullRequestRequest(
            "Update Contoso.App",
            "Synthetic body",
            "contributor",
            "update",
            "main");

        PullRequestInfo result = await GitHubClientTestSupport.CreateClient(handler)
            .CreatePullRequestAsync(
                _repository,
                request,
                new MutationRequest("pr-create-1"),
                TestContext.Current.CancellationToken);

        Assert.Equal(7, result.Number);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_create_posts_when_no_duplicate_exists()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json("[]"));
        handler.Add(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("\"head\":\"contributor:update\"", request.Body, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                GitHubClientTestSupport.PullRequestJson(8, "Update Contoso.App"),
                HttpStatusCode.Created);
        });

        PullRequestInfo result = await GitHubClientTestSupport.CreateClient(handler)
            .CreatePullRequestAsync(
                _repository,
                new CreatePullRequestRequest(
                    "Update Contoso.App",
                    null,
                    "contributor",
                    "update",
                    "main"),
                new MutationRequest("pr-create-2"),
                TestContext.Current.CancellationToken);

        Assert.Equal(8, result.Number);
    }

    [Fact]
    public async Task Pull_request_can_be_read_commented_and_closed()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            GitHubClientTestSupport.PullRequestJson(9, "Update Contoso.App")));
        handler.Add(request =>
        {
            Assert.Contains("/issues/9/comments", request.Uri.AbsoluteUri, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                """
                {
                  "id": 100,
                  "body": "Synthetic comment",
                  "html_url": "https://github.invalid/upstream/repo/issues/9#issuecomment-100",
                  "created_at": "2026-01-03T00:00:00Z"
                }
                """,
                HttpStatusCode.Created);
        });
        handler.Add(_ => GitHubClientTestSupport.Json(
            GitHubClientTestSupport.PullRequestJson(9, "Update Contoso.App")));
        handler.Add(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Contains("\"state\":\"closed\"", request.Body, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                GitHubClientTestSupport.PullRequestJson(
                    9,
                    "Update Contoso.App",
                    state: "closed"));
        });
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);

        PullRequestInfo read = await client.GetPullRequestAsync(
            _repository,
            9,
            TestContext.Current.CancellationToken);
        PullRequestComment comment = await client.CommentOnPullRequestAsync(
            _repository,
            9,
            "Synthetic comment",
            new MutationRequest("comment-9"),
            TestContext.Current.CancellationToken);
        PullRequestInfo closed = await client.ClosePullRequestAsync(
            _repository,
            9,
            new MutationRequest("close-9"),
            TestContext.Current.CancellationToken);

        Assert.Equal(9, read.Number);
        Assert.Equal(100, comment.Id);
        Assert.Equal(PullRequestState.Closed, closed.State);
    }

    [Fact]
    public async Task Failed_comment_is_cached_to_avoid_duplicate_retry_hazard()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"temporary"}""",
            HttpStatusCode.ServiceUnavailable));
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);
        var mutation = new MutationRequest("comment-uncertain");

        await Assert.ThrowsAsync<GitHubApiException>(
            () => client.CommentOnPullRequestAsync(
                _repository,
                10,
                "Synthetic comment",
                mutation,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<GitHubApiException>(
            () => client.CommentOnPullRequestAsync(
                _repository,
                10,
                "Synthetic comment",
                mutation,
                TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }
}
