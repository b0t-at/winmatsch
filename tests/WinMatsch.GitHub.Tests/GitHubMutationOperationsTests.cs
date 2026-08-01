using System.Net;
using Xunit;

namespace WinMatsch.GitHub.Tests;

public sealed class GitHubMutationOperationsTests
{
    private static readonly RepositoryCoordinates _repository = new("upstream", "repo");
    private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task Existing_matching_reference_makes_creation_idempotent()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"{{\"ref\":\"refs/heads/update\",\"object\":{{\"sha\":\"{HeadSha}\"}}}}"));
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);
        var mutation = new MutationRequest("create-ref-1");

        GitReference first = await client.CreateReferenceAsync(
            _repository,
            "update",
            HeadSha,
            mutation,
            TestContext.Current.CancellationToken);
        GitReference second = await client.CreateReferenceAsync(
            _repository,
            "update",
            HeadSha,
            mutation,
            TestContext.Current.CancellationToken);

        Assert.Equal(HeadSha, first.Sha);
        Assert.Same(first, second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Existing_reference_at_different_sha_is_a_conflict()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"{{\"ref\":\"refs/heads/update\",\"object\":{{\"sha\":\"{OtherSha}\"}}}}"));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).CreateReferenceAsync(
                _repository,
                "update",
                HeadSha,
                new MutationRequest("create-ref-conflict"),
                TestContext.Current.CancellationToken));

        Assert.True(exception.IsConflict);
    }

    [Fact]
    public async Task Unique_reference_creation_uses_atomic_post_without_prefetch()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("\"ref\":\"refs/heads/reservation\"", request.Body, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                $"{{\"ref\":\"refs/heads/reservation\",\"object\":{{\"sha\":\"{HeadSha}\"}}}}",
                HttpStatusCode.Created);
        });

        GitReference reference = await GitHubClientTestSupport.CreateClient(handler)
            .CreateUniqueReferenceAsync(
                _repository,
                "reservation",
                HeadSha,
                new MutationRequest("unique-ref-1"),
                TestContext.Current.CancellationToken);

        Assert.Equal(HeadSha, reference.Sha);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unique_reference_creation_surfaces_existing_branch_conflict()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"Reference already exists"}""",
            HttpStatusCode.UnprocessableEntity));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).CreateUniqueReferenceAsync(
                _repository,
                "reservation",
                HeadSha,
                new MutationRequest("unique-ref-conflict"),
                TestContext.Current.CancellationToken));

        Assert.True(exception.IsConflict);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reference_deletion_is_explicitly_unconditional()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        bool deleted = await GitHubClientTestSupport.CreateClient(handler)
            .DeleteReferenceAsync(
                _repository,
                "update",
                new MutationRequest("delete-ref-1"),
                TestContext.Current.CancellationToken);

        Assert.True(deleted);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reference_deletion_reports_an_already_absent_branch()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"Reference does not exist"}""",
            HttpStatusCode.NotFound));

        bool deleted = await GitHubClientTestSupport.CreateClient(handler).DeleteReferenceAsync(
                _repository,
                "update",
                new MutationRequest("delete-ref-conflict"),
                TestContext.Current.CancellationToken);

        Assert.False(deleted);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Mutation_is_not_retried_after_transient_server_error()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"Not Found"}""",
            HttpStatusCode.NotFound));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"temporary"}""",
            HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).CreateReferenceAsync(
                _repository,
                "update",
                HeadSha,
                new MutationRequest("no-retry"),
                TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(0, handler.RemainingSteps);
    }

    [Fact]
    public async Task Reusing_idempotency_key_for_different_inputs_is_rejected()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"{{\"ref\":\"refs/heads/one\",\"object\":{{\"sha\":\"{HeadSha}\"}}}}"));
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);
        var mutation = new MutationRequest("shared-key");
        await client.CreateReferenceAsync(
            _repository,
            "one",
            HeadSha,
            mutation,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateReferenceAsync(
                _repository,
                "two",
                HeadSha,
                mutation,
                TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Server_commit_uses_graphql_mutation_and_client_mutation_id()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Contains("\"clientMutationId\":\"commit-1\"", request.Body, StringComparison.Ordinal);
            Assert.Contains("\"repositoryNameWithOwner\":\"upstream/repo\"", request.Body, StringComparison.Ordinal);
            Assert.Contains("\"contents\":\"VGVzdA==\"", request.Body, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                $$"""
                {
                  "data": {
                    "createCommitOnBranch": {
                      "commit": {
                        "oid": "{{OtherSha}}",
                        "url": "https://github.invalid/upstream/repo/commit/{{OtherSha}}"
                      },
                      "clientMutationId": "commit-1"
                    },
                    "rateLimit": {
                      "limit": 5000,
                      "remaining": 4990,
                      "used": 10,
                      "resetAt": "2026-01-01T01:00:00Z"
                    }
                  }
                }
                """);
        });
        var request = new ServerCommitRequest(
            "update",
            HeadSha,
            "Update manifest",
            null,
            [new CommitFileAddition("manifest.yaml", "Test"u8.ToArray())],
            []);

        ServerCommitResult result = await GitHubClientTestSupport.CreateClient(handler)
            .CreateCommitAsync(
                _repository,
                request,
                new MutationRequest("commit-1"),
                TestContext.Current.CancellationToken);

        Assert.Equal(OtherSha, result.Sha);
    }

    [Fact]
    public async Task Expected_head_error_is_reported_as_conflict()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "errors": [{
                "type": "UNPROCESSABLE",
                "message": "expectedHeadOid does not match branch head"
              }]
            }
            """));
        var request = new ServerCommitRequest(
            "update",
            HeadSha,
            "Update manifest",
            null,
            [],
            ["obsolete.yaml"]);

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).CreateCommitAsync(
                _repository,
                request,
                new MutationRequest("commit-conflict"),
                TestContext.Current.CancellationToken));

        Assert.True(exception.IsConflict);
    }

    [Fact]
    public async Task Server_commit_falls_back_to_rest_when_graphql_is_unavailable()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"GraphQL disabled"}""",
            HttpStatusCode.NotFound));
        handler.Add(request =>
        {
            Assert.Contains($"/git/commits/{HeadSha}", request.Uri.AbsoluteUri, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                $"{{\"sha\":\"{HeadSha}\",\"tree\":{{\"sha\":\"tree-parent\"}}}}");
        });
        handler.Add(request =>
        {
            Assert.Contains("/git/blobs", request.Uri.AbsoluteUri, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json("""{"sha":"blob-new"}""");
        });
        handler.Add(request =>
        {
            Assert.Equal(
                """{"base_tree":"tree-parent","tree":[{"path":"manifest.yaml","mode":"100644","type":"blob","sha":"blob-new"},{"path":"obsolete.yaml","mode":"100644","type":"blob","sha":null}]}""",
                request.Body);
            return GitHubClientTestSupport.Json("""{"sha":"tree-new"}""");
        });
        handler.Add(request =>
        {
            Assert.Contains("\"parents\":[\"" + HeadSha + "\"]", request.Body, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                $$"""
                {
                  "sha": "{{OtherSha}}",
                  "html_url": "https://github.invalid/upstream/repo/commit/{{OtherSha}}"
                }
                """);
        });
        handler.Add(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Contains("\"force\":false", request.Body, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                $"{{\"ref\":\"refs/heads/update\",\"object\":{{\"sha\":\"{OtherSha}\"}}}}");
        });
        var request = new ServerCommitRequest(
            "update",
            HeadSha,
            "Update manifest",
            "Synthetic body",
            [new CommitFileAddition("manifest.yaml", "Test"u8.ToArray())],
            ["obsolete.yaml"]);

        ServerCommitResult result = await GitHubClientTestSupport.CreateClient(handler)
            .CreateCommitAsync(
                _repository,
                request,
                new MutationRequest("commit-rest"),
                TestContext.Current.CancellationToken);

        Assert.Equal(OtherSha, result.Sha);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task Remove_commit_rest_fallback_serializes_explicit_null_sha()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"GraphQL unavailable"}""",
            HttpStatusCode.NotImplemented));
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"{{\"sha\":\"{HeadSha}\",\"tree\":{{\"sha\":\"tree-parent\"}}}}"));
        handler.Add(request =>
        {
            Assert.Equal(
                """{"base_tree":"tree-parent","tree":[{"path":"obsolete.yaml","mode":"100644","type":"blob","sha":null}]}""",
                request.Body);
            return GitHubClientTestSupport.Json("""{"sha":"tree-new"}""");
        });
        handler.Add(_ => GitHubClientTestSupport.Json(
            $$"""
            {
              "sha": "{{OtherSha}}",
              "html_url": "https://github.invalid/upstream/repo/commit/{{OtherSha}}"
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"{{\"ref\":\"refs/heads/update\",\"object\":{{\"sha\":\"{OtherSha}\"}}}}"));

        ServerCommitResult result = await GitHubClientTestSupport.CreateClient(handler)
            .CreateCommitAsync(
                _repository,
                new ServerCommitRequest(
                    "update",
                    HeadSha,
                    "Remove manifest",
                    null,
                    [],
                    ["obsolete.yaml"]),
                new MutationRequest("commit-rest-remove"),
                TestContext.Current.CancellationToken);

        Assert.Equal(OtherSha, result.Sha);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task Compare_paginates_commit_pages()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Contains("/compare/main...update", request.Uri.AbsoluteUri, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                $$"""
                {
                  "status": "ahead",
                  "ahead_by": 1,
                  "behind_by": 0,
                  "total_commits": 1,
                  "commits": [{
                    "sha": "{{OtherSha}}",
                    "html_url": "https://github.invalid/upstream/repo/commit/{{OtherSha}}",
                    "commit": { "message": "Synthetic commit", "tree": { "sha": "tree" } }
                  }]
                }
                """,
                headers:
                [
                    ("Link", "<https://github.invalid/api/compare-page-2>; rel=\"next\""),
                ]);
        });
        handler.Add(request =>
        {
            Assert.Equal("https://github.invalid/api/compare-page-2", request.Uri.AbsoluteUri);
            return GitHubClientTestSupport.Json(
                $$"""
                {
                 "status": "ahead",
                 "ahead_by": 2,
                 "behind_by": 0,
                 "total_commits": 2,
                 "commits": [{
                   "sha": "{{HeadSha}}",
                   "html_url": "https://github.invalid/upstream/repo/commit/{{HeadSha}}",
                   "commit": { "message": "Second commit", "tree": { "sha": "tree-2" } }
                 }]
                }
                """);
        });

        CompareResult comparison = await GitHubClientTestSupport.CreateClient(handler)
            .CompareAsync(
                _repository,
                "main",
                "update",
                TestContext.Current.CancellationToken);

        Assert.Equal(1, comparison.AheadBy);
        Assert.Equal(2, comparison.Commits.Count);
    }

    [Fact]
    public async Task Ensure_fork_creates_missing_fork()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "data": { "repository": null, "rateLimit": null },
              "errors": [{
                "type": "NOT_FOUND",
                "path": ["repository"],
                "locations": [{ "line": 2, "column": 3 }],
                "message": "Could not resolve to a Repository with the name 'contributor/repo'."
              }]
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "data": {
                "viewer": {
                  "login": "contributor",
                  "name": null,
                  "email": null,
                  "avatarUrl": "https://github.invalid/avatar.png"
                },
                "rateLimit": null
              }
            }
            """));
        handler.Add(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("/repos/upstream/repo/forks", request.Uri.AbsoluteUri, StringComparison.Ordinal);
            Assert.DoesNotContain("organization", request.Body, StringComparison.OrdinalIgnoreCase);
            return GitHubClientTestSupport.Json(
                """
                {
                  "id": 200,
                  "node_id": "R_fork",
                  "full_name": "contributor/repo",
                  "html_url": "https://github.invalid/contributor/repo",
                  "fork": true,
                  "private": false,
                  "default_branch": "main",
                  "owner": { "login": "contributor" },
                  "parent": { "full_name": "upstream/repo" }
                }
                """,
                HttpStatusCode.Accepted);
        });
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "data": { "repository": null, "rateLimit": null },
              "errors": [{
                "type": "NOT_FOUND",
                "path": ["repository"],
                "message": "Could not resolve to a Repository with the name 'contributor/repo'."
              }]
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            $$"""
            {
              "data": {
                "repository": {
                  "id": "R_fork",
                  "nameWithOwner": "contributor/repo",
                  "url": "https://github.invalid/contributor/repo",
                  "isPrivate": false,
                  "isFork": true,
                  "parent": { "nameWithOwner": "upstream/repo" },
                  "defaultBranchRef": {
                    "name": "main",
                    "target": { "oid": "{{HeadSha}}" }
                  }
                },
                "rateLimit": null
              }
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            $$"""
            {
              "name": "main",
              "commit": { "sha": "{{HeadSha}}" },
              "protected": true
            }
            """));

        ForkResult fork = await GitHubClientTestSupport.CreateClient(handler)
            .EnsureForkAsync(
                _repository,
                "contributor",
                new MutationRequest("fork-1"),
                TestContext.Current.CancellationToken);

        Assert.False(fork.AlreadyExisted);
        Assert.Equal(new RepositoryCoordinates("contributor", "repo"), fork.Repository.Coordinates);
        Assert.True(fork.Repository.DefaultBranch.IsProtected);
    }

    [Fact]
    public async Task Fork_poll_uses_structured_not_ready_classification()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "data": { "repository": null },
              "errors": [{ "type": "NOT_FOUND", "message": "missing" }]
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "data": {
                "viewer": {
                  "login": "contributor",
                  "avatarUrl": "https://github.invalid/avatar.png"
                }
              }
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "node_id": "R_fork",
              "full_name": "contributor/repo",
              "html_url": "https://github.invalid/contributor/repo",
              "fork": true,
              "private": false,
              "default_branch": "main",
              "owner": { "login": "contributor" },
              "parent": { "full_name": "upstream/repo" }
            }
            """,
            HttpStatusCode.Accepted));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "data": {
                "repository": {
                  "id": "R_fork",
                  "nameWithOwner": "contributor/repo",
                  "url": "https://github.invalid/contributor/repo",
                  "isPrivate": false,
                  "isFork": true,
                  "parent": { "nameWithOwner": "upstream/repo" },
                  "defaultBranchRef": null
                }
              }
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            $$"""
            {
              "data": {
                "repository": {
                  "id": "R_fork",
                  "nameWithOwner": "contributor/repo",
                  "url": "https://github.invalid/contributor/repo",
                  "isPrivate": false,
                  "isFork": true,
                  "parent": { "nameWithOwner": "upstream/repo" },
                  "defaultBranchRef": { "name": "main" }
                }
              }
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            $$"""
            {
              "name": "main",
              "commit": { "sha": "{{HeadSha}}" },
              "protected": false
            }
            """));

        ForkResult result = await GitHubClientTestSupport.CreateClient(handler)
            .EnsureForkAsync(
                _repository,
                "contributor",
                new MutationRequest("fork-structured-not-ready"),
                TestContext.Current.CancellationToken);

        Assert.Equal("contributor", result.Repository.Coordinates.Owner);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task Fork_poll_failure_is_not_cached_after_creation_is_accepted()
    {
        var handler = new ScriptedHttpMessageHandler();
        string missing = """
            {
              "data": { "repository": null },
              "errors": [{
                "type": "NOT_FOUND",
                "message": "Could not resolve to a Repository with the name 'contributor/repo'."
              }]
            }
            """;
        handler.Add(_ => GitHubClientTestSupport.Json(missing));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "data": {
                "viewer": {
                  "login": "contributor",
                  "avatarUrl": "https://github.invalid/avatar.png"
                }
              }
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "node_id": "R_fork",
              "full_name": "contributor/repo",
              "html_url": "https://github.invalid/contributor/repo",
              "fork": true,
              "private": false,
              "default_branch": "main",
              "owner": { "login": "contributor" },
              "parent": { "full_name": "upstream/repo" }
            }
            """,
            HttpStatusCode.Accepted));
        handler.Add(_ => GitHubClientTestSupport.Json(missing));
        handler.Add(_ => GitHubClientTestSupport.Json(
            $$"""
            {
              "data": {
                "repository": {
                  "id": "R_fork",
                  "nameWithOwner": "contributor/repo",
                  "url": "https://github.invalid/contributor/repo",
                  "isPrivate": false,
                  "isFork": true,
                  "parent": { "nameWithOwner": "upstream/repo" },
                  "defaultBranchRef": {
                    "name": "main",
                    "target": { "oid": "{{HeadSha}}" }
                  }
                },
                "rateLimit": null
              }
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            $$"""
            {
              "name": "main",
              "commit": { "sha": "{{HeadSha}}" },
              "protected": false
            }
            """));
        var options = new GitHubClientOptions
        {
            ApiBaseUri = new Uri("https://github.invalid/api/"),
            GraphQlUri = new Uri("https://github.invalid/graphql"),
            UserAgent = "winmatsch-tests",
            RetryBaseDelay = TimeSpan.Zero,
            ForkAvailabilityBaseDelay = TimeSpan.Zero,
            ForkAvailabilityMaxAttempts = 1,
        };
        var client = new GitHubRepositoryClient(
            new HttpClient(handler),
            "synthetic-token",
            options);
        var mutation = new MutationRequest("fork-repoll");

        await Assert.ThrowsAsync<GitHubApiException>(
            () => client.EnsureForkAsync(
                _repository,
                "contributor",
                mutation,
                TestContext.Current.CancellationToken));
        ForkResult fork = await client.EnsureForkAsync(
            _repository,
            "contributor",
            mutation,
            TestContext.Current.CancellationToken);

        Assert.False(fork.AlreadyExisted);
        Assert.Equal("contributor", fork.Repository.Coordinates.Owner);
        Assert.Single(handler.Requests, static request =>
            request.Method == HttpMethod.Post &&
            request.Uri.AbsolutePath.EndsWith("/forks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sync_fork_uses_merge_upstream_and_reads_resulting_head()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"Successfully synced","merge_type":"fast-forward","base_branch":"main"}"""));
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"{{\"ref\":\"refs/heads/main\",\"object\":{{\"sha\":\"{OtherSha}\"}}}}"));

        UpstreamSyncResult result = await GitHubClientTestSupport.CreateClient(handler)
            .SyncForkAsync(
                new RepositoryCoordinates("contributor", "repo"),
                "main",
                new MutationRequest("sync-1"),
                TestContext.Current.CancellationToken);

        Assert.Equal("fast-forward", result.MergeType);
        Assert.Equal(OtherSha, result.HeadSha);
    }
}
