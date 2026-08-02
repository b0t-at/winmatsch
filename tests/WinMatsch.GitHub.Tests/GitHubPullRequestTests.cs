using System.Net;
using System.Text.Json;
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
    public async Task Pull_request_head_branch_without_owner_is_rejected_before_transport()
    {
        var handler = new ScriptedHttpMessageHandler();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => GitHubClientTestSupport.CreateClient(handler).SearchPullRequestsAsync(
                _repository,
                new PullRequestSearch(HeadBranch: "update"),
                TestContext.Current.CancellationToken));

        Assert.Contains("head owner", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_owner_only_filter_is_enforced_client_side()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.DoesNotContain("head=", request.Uri.Query, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                $"[{GitHubClientTestSupport.PullRequestJson(1, "One", headOwner: "other")}," +
                $"{GitHubClientTestSupport.PullRequestJson(2, "Two", headOwner: "contributor")}]");
        });

        IReadOnlyList<PullRequestInfo> pullRequests = await GitHubClientTestSupport
            .CreateClient(handler)
            .SearchPullRequestsAsync(
                _repository,
                new PullRequestSearch(HeadOwner: "contributor"),
                TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(pullRequests).Number);
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
        Assert.Null(pullRequest.HeadRepository);
    }

    [Fact]
    public async Task Pull_request_maps_complete_head_repository_coordinates()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"[{GitHubClientTestSupport.PullRequestJson(3, "Update fork", headOwner: "renamed-owner")}]"));

        PullRequestInfo pullRequest = Assert.Single(await GitHubClientTestSupport
            .CreateClient(handler)
            .SearchPullRequestsAsync(_repository, new PullRequestSearch(), TestContext.Current.CancellationToken));

        Assert.Equal(new RepositoryCoordinates("renamed-owner", "repo"), pullRequest.HeadRepository);
    }

    [Fact]
    public async Task Pull_request_changed_file_maps_renamed_source_path()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            [{
              "filename": "manifests/e/Example/App/2.0.0/Renamed.yaml",
              "status": "renamed",
              "previous_filename": "manifests/e/Example/App/2.0.0/Example.App.yaml"
            }]
            """));

        PullRequestChangedFile file = Assert.Single(await GitHubClientTestSupport
            .CreateClient(handler)
            .GetPullRequestChangedFilesAsync(
                _repository,
                7,
                TestContext.Current.CancellationToken));

        Assert.Equal("manifests/e/Example/App/2.0.0/Renamed.yaml", file.Path);
        Assert.Equal(
            "manifests/e/Example/App/2.0.0/Example.App.yaml",
            file.PreviousPath);
        Assert.Equal(PullRequestFileStatus.Renamed, file.Status);
    }

    [Fact]
    public async Task Pull_request_changed_file_pagination_fails_closed_at_the_limit()
    {
        var handler = new ScriptedHttpMessageHandler();
        for (int page = 1; page <= 10; page++)
        {
            int nextPage = page + 1;
            handler.Add(_ => GitHubClientTestSupport.Json(
                """[{"filename":"manifests/e/Example/App/2.0.0/Example.App.yaml"}]""",
                headers:
                [
                    ("Link", $"<https://github.invalid/api/pulls/7/files?page={nextPage}>; rel=\"next\""),
                ]));
        }

        await Assert.ThrowsAsync<GitHubApiException>(() => GitHubClientTestSupport
            .CreateClient(handler)
            .GetPullRequestChangedFilesAsync(_repository, 7, TestContext.Current.CancellationToken));

        Assert.Equal(10, handler.Requests.Count);
    }

    [Fact]
    public async Task Pull_request_changed_files_batch_uses_graphql_batches()
    {
        PullRequestInfo[] pullRequests = CreatePullRequests(400);
        var byNode = pullRequests.ToDictionary(
            static pullRequest => pullRequest.NodeId,
            StringComparer.Ordinal);
        var handler = new ScriptedHttpMessageHandler();
        for (int batch = 0; batch < 8; batch++)
        {
            handler.Add(request =>
            {
                Assert.Equal(new Uri("https://github.invalid/graphql"), request.Uri);
                string[] ids = ReadGraphQlIds(request);
                Assert.Equal(50, ids.Length);
                return GitHubClientTestSupport.Json(
                    GraphQlFilesJson(ids.Select(id => byNode[id])),
                    headers:
                    [
                        ("X-RateLimit-Limit", "5000"),
                        ("X-RateLimit-Remaining", "4990"),
                        ("X-RateLimit-Used", "10"),
                        ("X-RateLimit-Reset", "1767229200"),
                    ]);
            });
        }

        var options = new GitHubClientOptions
        {
            ApiBaseUri = new Uri("https://github.invalid/api/"),
            GraphQlUri = new Uri("https://github.invalid/graphql"),
            UserAgent = "winmatsch-tests",
            RetryBaseDelay = TimeSpan.Zero,
            MaxTransientRetries = 0,
        };
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler, options);
        IReadOnlyDictionary<long, IReadOnlyList<PullRequestChangedFile>> files =
            await client.GetPullRequestChangedFilesBatchAsync(
                _repository,
                pullRequests,
                TestContext.Current.CancellationToken);

        Assert.Equal(400, files.Count);
        Assert.Equal(8, handler.Requests.Count);
        Assert.Equal("graphql", Assert.IsType<RateLimitInfo>(client.LastRateLimit).Resource);
    }

    [Fact]
    public async Task Pull_request_changed_files_batch_completes_truncated_graphql_files_via_rest()
    {
        PullRequestInfo pullRequest = Assert.Single(CreatePullRequests(1));
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            GraphQlFilesJson([pullRequest], hasNextPage: true)));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """[{"filename":"first.yaml","status":"modified"}]""",
            headers:
            [
                ("Link", "<https://github.invalid/api/files-page-2>; rel=\"next\""),
            ]));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """[{"filename":"second.yaml","status":"added"}]"""));

        IReadOnlyDictionary<long, IReadOnlyList<PullRequestChangedFile>> files =
            await GitHubClientTestSupport.CreateClient(handler)
                .GetPullRequestChangedFilesBatchAsync(
                    _repository,
                    [pullRequest],
                    TestContext.Current.CancellationToken);

        Assert.Equal(["first.yaml", "second.yaml"], files[pullRequest.Number].Select(static file => file.Path));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Pull_request_changed_files_batch_fails_closed_before_unbounded_truncation_fallback()
    {
        PullRequestInfo[] pullRequests = CreatePullRequests(17);
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            GraphQlFilesJson(pullRequests, hasNextPage: true)));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler)
                .GetPullRequestChangedFilesBatchAsync(
                    _repository,
                    pullRequests,
                    TestContext.Current.CancellationToken));

        Assert.Contains("safe REST fallback bound", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_changed_files_batch_fails_closed_when_graphql_fallback_is_unbounded()
    {
        PullRequestInfo[] pullRequests = CreatePullRequests(65);
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"GraphQL unavailable"}""",
            HttpStatusCode.NotFound));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler)
                .GetPullRequestChangedFilesBatchAsync(
                    _repository,
                    pullRequests,
                    TestContext.Current.CancellationToken));

        Assert.Contains("safe REST request bound", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_changed_files_batch_preserves_rate_limit_failure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"API rate limit exceeded"}""",
            HttpStatusCode.Forbidden,
            ("X-RateLimit-Limit", "5000"),
            ("X-RateLimit-Remaining", "0"),
            ("X-RateLimit-Used", "5000"),
            ("X-RateLimit-Reset", "1767229200"),
            ("X-RateLimit-Resource", "graphql")));
        var options = new GitHubClientOptions
        {
            ApiBaseUri = new Uri("https://github.invalid/api/"),
            GraphQlUri = new Uri("https://github.invalid/graphql"),
            UserAgent = "winmatsch-tests",
            RetryBaseDelay = TimeSpan.Zero,
            MaxTransientRetries = 0,
        };
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler, options);

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => client.GetPullRequestChangedFilesBatchAsync(
                _repository,
                CreatePullRequests(1),
                TestContext.Current.CancellationToken));

        Assert.Equal(GitHubApiErrorKind.RateLimited, exception.ErrorKind);
        Assert.Equal(0, Assert.IsType<RateLimitInfo>(exception.RateLimit).Remaining);
        Assert.Equal(0, Assert.IsType<RateLimitInfo>(client.LastRateLimit).Remaining);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_changed_files_batch_honors_pre_cancelled_token()
    {
        var handler = new ScriptedHttpMessageHandler();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GitHubClientTestSupport.CreateClient(handler)
                .GetPullRequestChangedFilesBatchAsync(
                    _repository,
                    CreatePullRequests(1),
                    cancellation.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_search_stops_at_the_requested_result_bound()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"[{GitHubClientTestSupport.PullRequestJson(1, "Update one")}," +
            $"{GitHubClientTestSupport.PullRequestJson(2, "Update two")}]"));

        await Assert.ThrowsAsync<GitHubApiException>(() => GitHubClientTestSupport
            .CreateClient(handler)
            .SearchPullRequestsAsync(
                _repository,
                new PullRequestSearch { MaximumResults = 1 },
                TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_search_uses_the_requested_base_branch()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Contains("base=release", request.Uri.Query, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json("[]");
        });

        _ = await GitHubClientTestSupport.CreateClient(handler).SearchPullRequestsAsync(
            _repository,
            new PullRequestSearch(PullRequestState.Open, BaseBranch: "release"),
            TestContext.Current.CancellationToken);
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
    public async Task Pull_request_fingerprint_is_not_ambiguous_when_fields_contain_pipes()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"[{GitHubClientTestSupport.PullRequestJson(8, "Update Contoso.App", headOwner: "a|b", headBranch: "c")}]"));
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);
        var mutation = new MutationRequest("pr-pipe-fingerprint");
        await client.CreatePullRequestAsync(
            _repository,
            new CreatePullRequestRequest(
                "Update Contoso.App",
                null,
                "a|b",
                "c",
                "main"),
            mutation,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreatePullRequestAsync(
                _repository,
                new CreatePullRequestRequest(
                    "Update Contoso.App",
                    null,
                    "a",
                    "b|c",
                    "main"),
                mutation,
                TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_fingerprint_distinguishes_null_and_empty_body()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"[{GitHubClientTestSupport.PullRequestJson(8, "Update Contoso.App")}]"));
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);
        var mutation = new MutationRequest("pr-null-body-fingerprint");
        await client.CreatePullRequestAsync(
            _repository,
            new CreatePullRequestRequest(
                "Update Contoso.App",
                null,
                "contributor",
                "update",
                "main"),
            mutation,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreatePullRequestAsync(
                _repository,
                new CreatePullRequestRequest(
                    "Update Contoso.App",
                    "",
                    "contributor",
                    "update",
                    "main"),
                mutation,
                TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_fingerprint_is_not_ambiguous_when_fields_contain_nulls()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"[{GitHubClientTestSupport.PullRequestJson(8, "Update Contoso.App", headOwner: "a\\u0000b", headBranch: "c")}]"));
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);
        var mutation = new MutationRequest("pr-null-character-fingerprint");
        await client.CreatePullRequestAsync(
            _repository,
            new CreatePullRequestRequest(
                "Update Contoso.App",
                null,
                "a\u0000b",
                "c",
                "main"),
            mutation,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreatePullRequestAsync(
                _repository,
                new CreatePullRequestRequest(
                    "Update Contoso.App",
                    null,
                    "a",
                    "b\u0000c",
                    "main"),
                mutation,
                TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
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

    private static PullRequestInfo[] CreatePullRequests(int count)
        =>
        [
            .. Enumerable.Range(1, count).Select(number =>
                new PullRequestInfo(
                    number,
                    $"PR_{number}",
                    $"Maintenance {number}",
                    null,
                    PullRequestState.Open,
                    false,
                    $"author-{number}",
                    $"branch-{number}",
                    number.ToString("D40", System.Globalization.CultureInfo.InvariantCulture),
                    "main",
                    new Uri($"https://github.invalid/upstream/repo/pull/{number}"),
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero))
                {
                    HeadRepository = new RepositoryCoordinates($"author-{number}", "repo"),
                    BaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                }),
        ];

    private static string[] ReadGraphQlIds(RecordedRequest request)
    {
        using JsonDocument document = JsonDocument.Parse(request.Body!);
        Assert.Contains("nodes(ids: $ids)", document.RootElement.GetProperty("query").GetString());
        return
        [
            .. document.RootElement
                .GetProperty("variables")
                .GetProperty("ids")
                .EnumerateArray()
                .Select(static id => id.GetString()!),
        ];
    }

    private static string GraphQlFilesJson(
        IEnumerable<PullRequestInfo> pullRequests,
        bool hasNextPage = false)
    {
        string nodes = string.Join(
            ',',
            pullRequests.Select(pullRequest =>
                $$"""
                {
                  "id":"{{pullRequest.NodeId}}",
                  "number":{{pullRequest.Number}},
                  "headRefOid":"{{pullRequest.HeadSha}}",
                  "files":{
                    "nodes":[{"path":"manifests/example.yaml","changeType":"MODIFIED"}],
                    "pageInfo":{"hasNextPage":{{hasNextPage.ToString().ToLowerInvariant()}}}
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
}
