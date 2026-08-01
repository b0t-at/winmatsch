using System.Net;
using System.Text;
using WinMatsch.GitHub;
using WinMatsch.Testing.Infrastructure;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class GitHubAuthoritativeEvidenceIntegrationTests
{
    private static readonly RepositoryCoordinates _upstream = new("upstream", "repo");
    private static readonly RepositoryCoordinates _headRepository = new("contributor", "renamed-repo");
    private const string HeadSha = "cccccccccccccccccccccccccccccccccccccccc";
    private const string BaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string MergeBaseSha = "dddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    public async Task Authoritative_evidence_uses_authenticated_same_origin_ghe_transport()
    {
        WorkflowFileChange change = GitHubLifecycleTestSupport.Plan().FileChanges[0];
        string pullRequestJson = PullRequestJson();
        var steps = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(
        [
            request =>
            {
                Assert.Contains("base=main", request.RequestUri!.Query, StringComparison.Ordinal);
                return Json($"[{pullRequestJson}]");
            },
            request =>
            {
                Assert.EndsWith("/repos/upstream/repo/pulls/7", request.RequestUri!.AbsolutePath);
                return Json(pullRequestJson);
            },
            request =>
            {
                Assert.Equal("https://ghe.invalid/api/graphql", request.RequestUri!.AbsoluteUri);
                return Json(RepositoryGraphQlJson(), ("X-OAuth-Scopes", "repo, read:org, repo"));
            },
            request =>
            {
                Assert.EndsWith(
                    "/repos/contributor/renamed-repo/branches/main",
                    request.RequestUri!.AbsolutePath);
                return Json(BranchJson());
            },
            request =>
            {
                Assert.Contains(
                    $"/compare/{BaseSha}...{HeadSha}",
                    request.RequestUri!.AbsolutePath,
                    StringComparison.Ordinal);
                return Json(ComparisonJson());
            },
            request =>
            {
                Assert.EndsWith(
                    "/repos/upstream/repo/pulls/7/files",
                    request.RequestUri!.AbsolutePath);
                return Json($"[{{\"filename\":\"{change.RepositoryPath}\"}}]");
            },
            request =>
            {
                Assert.Contains(
                    $"/repos/upstream/repo/contents/{change.RepositoryPath}",
                    request.RequestUri!.AbsolutePath,
                    StringComparison.Ordinal);
                Assert.Contains($"ref={MergeBaseSha}", request.RequestUri.Query, StringComparison.Ordinal);
                return Json(
                    """{"message":"Not Found"}""",
                    HttpStatusCode.NotFound);
            },
            request =>
            {
                Assert.Contains(
                    $"/repos/contributor/renamed-repo/contents/{change.RepositoryPath}",
                    request.RequestUri!.AbsolutePath,
                    StringComparison.Ordinal);
                Assert.Contains($"ref={HeadSha}", request.RequestUri.Query, StringComparison.Ordinal);
                return Json(ContentJson(change));
            },
            request =>
            {
                Assert.EndsWith("/repos/upstream/repo/pulls/7", request.RequestUri!.AbsolutePath);
                return Json(pullRequestJson);
            },
        ]);
        (GitHubRepositoryClient client, List<ObservedRequest> requests) = CreateClient(steps);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));
        PullRequestInfo candidate = Assert.Single(await client.SearchPullRequestsAsync(
            _upstream,
            new PullRequestSearch(BaseBranch: "main") { MaximumResults = 1 }));
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);

        PullRequestManifestEvidence evidence = await provider.GetEvidenceAsync(
            plan,
            candidate,
            CancellationToken.None);

        Assert.True(evidence.HasManifestPath);
        Assert.True(evidence.HasMatchingContent);
        Assert.Empty(steps);
        Assert.All(requests, static request =>
        {
            Assert.Equal("ghe.invalid", request.Uri.Host);
            Assert.Equal("Bearer", request.AuthorizationScheme);
            Assert.True(request.HasAuthorizationParameter);
        });
        Assert.Equal(["repo", "read:org"], client.LastOAuthScopes);
    }

    [Fact]
    public async Task Authoritative_evidence_rejects_changed_file_pagination_loop()
    {
        WorkflowFileChange change = GitHubLifecycleTestSupport.Plan().FileChanges[0];
        string pullRequestJson = PullRequestJson();
        const string filesUri =
            "https://ghe.invalid/api/v3/repos/upstream/repo/pulls/7/files?per_page=100";
        var steps = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(
        [
            _ => Json($"[{pullRequestJson}]"),
            _ => Json(pullRequestJson),
            _ => Json(RepositoryGraphQlJson()),
            _ => Json(BranchJson()),
            _ => Json(ComparisonJson()),
            _ => Json(
                $"[{{\"filename\":\"{change.RepositoryPath}\"}}]",
                ("Link", $"<{filesUri}>; rel=\"next\"")),
        ]);
        (GitHubRepositoryClient client, List<ObservedRequest> requests) = CreateClient(steps);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));
        PullRequestInfo candidate = Assert.Single(await client.SearchPullRequestsAsync(
            _upstream,
            new PullRequestSearch(BaseBranch: "main") { MaximumResults = 1 }));
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);

        PullRequestEvidenceLimitException exception =
            await Assert.ThrowsAsync<PullRequestEvidenceLimitException>(
                () => provider.GetEvidenceAsync(plan, candidate, CancellationToken.None));

        Assert.Contains("loops", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(steps);
        Assert.Equal(6, requests.Count);
        Assert.DoesNotContain(
            requests,
            request => request.Uri.AbsolutePath.Contains("/contents/", StringComparison.Ordinal));
    }

    private static (GitHubRepositoryClient Client, List<ObservedRequest> Requests) CreateClient(
        Queue<Func<HttpRequestMessage, HttpResponseMessage>> steps)
    {
        var requests = new List<ObservedRequest>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(new(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Missing request URI."),
                request.Headers.Authorization?.Scheme,
                !string.IsNullOrWhiteSpace(request.Headers.Authorization?.Parameter)));
            return steps.Dequeue()(request);
        });
        var options = new GitHubClientOptions
        {
            ApiBaseUri = new Uri("https://ghe.invalid/api/v3"),
            UserAgent = "winmatsch-evidence-tests",
            RetryBaseDelay = TimeSpan.Zero,
            MaxTransientRetries = 0,
        };
        return (
            new GitHubRepositoryClient(
                new HttpClient(handler),
                "synthetic-token",
                options),
            requests);
    }

    private static HttpResponseMessage Json(
        string json,
        params (string Name, string Value)[] headers)
        => Json(json, HttpStatusCode.OK, headers);

    private static HttpResponseMessage Json(
        string json,
        HttpStatusCode statusCode,
        params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        foreach ((string name, string value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private static string PullRequestJson()
        => $$"""
        {
          "number": 7,
          "node_id": "PR_7",
          "title": "Hand-authored manifest update",
          "body": null,
          "state": "open",
          "draft": false,
          "head": {
            "ref": "update",
            "sha": "{{HeadSha}}",
            "repo": {
              "node_id": "R_head",
              "full_name": "{{_headRepository}}",
              "html_url": "https://ghe.invalid/{{_headRepository}}",
              "fork": true,
              "private": false,
              "default_branch": "main",
              "owner": { "login": "{{_headRepository.Owner}}" }
            },
            "user": { "login": "{{_headRepository.Owner}}" }
          },
          "base": {
            "ref": "main",
            "sha": "{{BaseSha}}",
            "repo": {
              "node_id": "R_base",
              "full_name": "{{_upstream}}",
              "html_url": "https://ghe.invalid/{{_upstream}}",
              "fork": false,
              "private": false,
              "default_branch": "main",
              "owner": { "login": "{{_upstream.Owner}}" }
            },
            "user": { "login": "{{_upstream.Owner}}" }
          },
          "html_url": "https://ghe.invalid/upstream/repo/pull/7",
          "created_at": "2026-01-01T00:00:00Z",
          "updated_at": "2026-01-02T00:00:00Z"
        }
        """;

    private static string RepositoryGraphQlJson()
        => $$"""
        {
          "data": {
            "repository": {
              "id": "R_head",
              "nameWithOwner": "{{_headRepository}}",
              "url": "https://ghe.invalid/{{_headRepository}}",
              "isPrivate": false,
              "isFork": true,
              "parent": { "nameWithOwner": "{{_upstream}}" },
              "defaultBranchRef": { "name": "main" }
            }
          }
        }
        """;

    private static string BranchJson()
        => $$"""
        {
          "name": "main",
          "commit": { "sha": "{{HeadSha}}" },
          "protected": false
        }
        """;

    private static string ComparisonJson()
        => $$"""
        {
          "status": "ahead",
          "ahead_by": 1,
          "behind_by": 0,
          "total_commits": 1,
          "merge_base_commit": { "sha": "{{MergeBaseSha}}" },
          "commits": []
        }
        """;

    private static string ContentJson(WorkflowFileChange change)
        => $$"""
        {
          "name": "Example.App.yaml",
          "path": "{{change.RepositoryPath}}",
          "sha": "{{WorkflowFileChange.Hash(change.Content.AsSpan())}}",
          "size": {{change.Content.Length}},
          "encoding": "base64",
          "content": "{{Convert.ToBase64String(change.Content.AsSpan())}}"
        }
        """;

    private sealed record ObservedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        bool HasAuthorizationParameter);
}
