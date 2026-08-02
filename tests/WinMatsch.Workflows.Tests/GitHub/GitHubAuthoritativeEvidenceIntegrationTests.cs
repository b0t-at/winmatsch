using System.Net;
using System.Text;
using System.Text.Json;
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
                return Json(GraphQlFilesJson("unrelated/readme.txt"));
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
            _ => Json(GraphQlFilesJson(change.RepositoryPath, hasNextPage: true)),
            _ => Json(
                $"[{{\"filename\":\"{change.RepositoryPath}\",\"status\":\"modified\"}}]",
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
        Assert.Equal(4, requests.Count);
        Assert.DoesNotContain(
            requests,
            request => request.Uri.AbsolutePath.Contains("/contents/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_scale_discovery_batches_and_reuses_pinned_head_evidence()
    {
        PullRequestInfo[] firstSnapshot =
        [
            .. Enumerable.Range(1, 400).Select(number => PullRequest(number)),
        ];
        var byNode = firstSnapshot.ToDictionary(
            static pullRequest => pullRequest.NodeId,
            StringComparer.Ordinal);
        var heads = firstSnapshot.ToDictionary(
            static pullRequest => pullRequest.NodeId,
            static pullRequest => pullRequest.HeadSha,
            StringComparer.Ordinal);
        var batchSizes = new List<int>();
        int pullRequestPageRequests = 0;
        int graphQlRequests = 0;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                pullRequestPageRequests++;
                Assert.Contains("base=main", request.RequestUri!.Query, StringComparison.Ordinal);
                int page = ReadPage(request.RequestUri);
                PullRequestInfo[] pageItems =
                [
                    .. firstSnapshot
                        .Skip((page - 1) * 100)
                        .Take(100)
                        .Select(pullRequest => pullRequest with
                        {
                            HeadSha = heads[pullRequest.NodeId],
                        }),
                ];
                return page < 4
                    ? Json(
                        RestPullRequestsJson(pageItems),
                        ("Link",
                            $"<https://ghe.invalid/api/v3/repos/upstream/repo/pulls" +
                            $"?per_page=100&state=open&base=main&page={page + 1}>; rel=\"next\""))
                    : Json(RestPullRequestsJson(pageItems));
            }

            graphQlRequests++;
            Assert.Equal("https://ghe.invalid/api/graphql", request.RequestUri!.AbsoluteUri);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            string[] ids =
            [
                .. document.RootElement
                    .GetProperty("variables")
                    .GetProperty("ids")
                    .EnumerateArray()
                    .Select(static item => item.GetString()!),
            ];
            batchSizes.Add(ids.Length);
            return Json(GraphQlBatchFilesJson(ids.Select(id =>
                byNode[id] with { HeadSha = heads[id] })));
        });
        var options = new GitHubClientOptions
        {
            ApiBaseUri = new Uri("https://ghe.invalid/api/v3"),
            GraphQlUri = new Uri("https://ghe.invalid/api/graphql"),
            UserAgent = "winmatsch-evidence-budget-tests",
            RetryBaseDelay = TimeSpan.Zero,
            MaxTransientRetries = 0,
        };
        using var client = new GitHubRepositoryClient(
            new HttpClient(handler),
            "synthetic-token",
            options);
        var provider = new GitHubPullRequestManifestEvidenceProvider(client);
        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        IReadOnlyList<PullRequestInfo> discovered = await client.SearchPullRequestsAsync(
            _upstream,
            new PullRequestSearch(BaseBranch: "main"),
            CancellationToken.None);
        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            discovered,
            CancellationToken.None));
        discovered = await client.SearchPullRequestsAsync(
            _upstream,
            new PullRequestSearch(BaseBranch: "main"),
            CancellationToken.None);
        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            discovered,
            CancellationToken.None));
        const string movedHead = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        heads["PR_200"] = movedHead;
        discovered = await client.SearchPullRequestsAsync(
            _upstream,
            new PullRequestSearch(BaseBranch: "main"),
            CancellationToken.None);
        Assert.Empty(await provider.GetCandidatesAsync(
            plan,
            discovered,
            CancellationToken.None));

        Assert.Equal(21, handler.Requests.Count);
        Assert.Equal(12, pullRequestPageRequests);
        Assert.Equal(9, graphQlRequests);
        Assert.Equal([50, 50, 50, 50, 50, 50, 50, 50, 1], batchSizes);
        Assert.Equal("graphql", Assert.IsType<RateLimitInfo>(client.LastRateLimit).Resource);
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

    private static PullRequestInfo PullRequest(int number)
        => new(
            number,
            $"PR_{number}",
            $"Unrelated maintenance {number}",
            null,
            PullRequestState.Open,
            false,
            $"contributor-{number}",
            $"branch-{number}",
            number.ToString("D40", System.Globalization.CultureInfo.InvariantCulture),
            "main",
            new Uri($"https://ghe.invalid/upstream/repo/pull/{number}"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero))
        {
            HeadRepository = new RepositoryCoordinates($"contributor-{number}", "repo"),
            BaseSha = BaseSha,
        };

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

    private static string GraphQlFilesJson(string path, bool hasNextPage = false)
        => $$"""
        {
          "data": {
            "nodes": [{
              "id": "PR_7",
              "number": 7,
              "headRefOid": "{{HeadSha}}",
              "files": {
                "nodes": [{
                  "path": "{{path}}",
                  "changeType": "MODIFIED"
                }],
                "pageInfo": {
                  "hasNextPage": {{hasNextPage.ToString().ToLowerInvariant()}}
                }
              }
            }],
            "rateLimit": {
              "limit": 5000,
              "remaining": 4998,
              "used": 2,
              "resetAt": "2026-01-01T01:00:00Z"
            }
          }
        }
        """;

    private static string GraphQlBatchFilesJson(IEnumerable<PullRequestInfo> pullRequests)
    {
        string nodes = string.Join(
            ',',
            pullRequests.Select(pullRequest =>
                $$"""
                {
                  "id": "{{pullRequest.NodeId}}",
                  "number": {{pullRequest.Number}},
                  "headRefOid": "{{pullRequest.HeadSha}}",
                  "files": {
                    "nodes": [{
                      "path": "unrelated/{{pullRequest.Number}}.txt",
                      "changeType": "MODIFIED"
                    }],
                    "pageInfo": { "hasNextPage": false }
                  }
                }
                """));
        return $$"""
        {
          "data": {
            "nodes": [{{nodes}}],
            "rateLimit": {
              "limit": 5000,
              "remaining": 4999,
              "used": 1,
              "resetAt": "2026-01-01T01:00:00Z"
            }
          }
        }
        """;
    }

    private static string RestPullRequestsJson(IEnumerable<PullRequestInfo> pullRequests)
    {
        string items = string.Join(
            ',',
            pullRequests.Select(pullRequest =>
                $$"""
                {
                  "number": {{pullRequest.Number}},
                  "node_id": "{{pullRequest.NodeId}}",
                  "title": "{{pullRequest.Title}}",
                  "body": null,
                  "state": "open",
                  "draft": false,
                  "head": {
                    "ref": "{{pullRequest.HeadBranch}}",
                    "sha": "{{pullRequest.HeadSha}}",
                    "repo": {
                      "node_id": "R_head_{{pullRequest.Number}}",
                      "full_name": "{{pullRequest.HeadRepository}}",
                      "html_url": "https://ghe.invalid/{{pullRequest.HeadRepository}}",
                      "fork": true,
                      "private": false,
                      "default_branch": "main",
                      "owner": { "login": "{{pullRequest.HeadOwner}}" }
                    },
                    "user": { "login": "{{pullRequest.HeadOwner}}" }
                  },
                  "base": {
                    "ref": "{{pullRequest.BaseBranch}}",
                    "sha": "{{pullRequest.BaseSha}}",
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
                  "html_url": "{{pullRequest.WebUri}}",
                  "created_at": "2026-01-01T00:00:00Z",
                  "updated_at": "2026-01-02T00:00:00Z"
                }
                """));
        return $"[{items}]";
    }

    private static int ReadPage(Uri uri)
    {
        const string marker = "&page=";
        int markerIndex = uri.Query.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return 1;
        }

        int valueStart = markerIndex + marker.Length;
        int valueEnd = uri.Query.IndexOf('&', valueStart);
        string value = valueEnd < 0
            ? uri.Query[valueStart..]
            : uri.Query[valueStart..valueEnd];
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

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
