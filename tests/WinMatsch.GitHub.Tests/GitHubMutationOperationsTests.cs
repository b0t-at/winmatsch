using System.Net;
using Xunit;

namespace WinMatsch.GitHub.Tests;

public sealed class GitHubMutationOperationsTests
{
    private static readonly RepositoryCoordinates Repository = new("upstream", "repo");
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
            Repository,
            "update",
            HeadSha,
            mutation,
            TestContext.Current.CancellationToken);
        GitReference second = await client.CreateReferenceAsync(
            Repository,
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
                Repository,
                "update",
                HeadSha,
                new MutationRequest("create-ref-conflict"),
                TestContext.Current.CancellationToken));

        Assert.True(exception.IsConflict);
    }

    [Fact]
    public async Task Reference_deletion_requires_the_planned_head()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"{{\"ref\":\"refs/heads/update\",\"object\":{{\"sha\":\"{HeadSha}\"}}}}"));
        handler.Add(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        bool deleted = await GitHubClientTestSupport.CreateClient(handler)
            .DeleteReferenceAsync(
                Repository,
                "update",
                HeadSha,
                new MutationRequest("delete-ref-1"),
                TestContext.Current.CancellationToken);

        Assert.True(deleted);
    }

    [Fact]
    public async Task Reference_deletion_rejects_a_branch_that_advanced()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            $"{{\"ref\":\"refs/heads/update\",\"object\":{{\"sha\":\"{OtherSha}\"}}}}"));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).DeleteReferenceAsync(
                Repository,
                "update",
                HeadSha,
                new MutationRequest("delete-ref-conflict"),
                TestContext.Current.CancellationToken));

        Assert.True(exception.IsConflict);
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
                Repository,
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
            Repository,
            "one",
            HeadSha,
            mutation,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateReferenceAsync(
                Repository,
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
                Repository,
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
                Repository,
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
            Assert.Contains("\"base_tree\":\"tree-parent\"", request.Body, StringComparison.Ordinal);
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
                Repository,
                request,
                new MutationRequest("commit-rest"),
                TestContext.Current.CancellationToken);

        Assert.Equal(OtherSha, result.Sha);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task Compare_maps_commit_distance_and_commits()
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
                """);
        });

        CompareResult comparison = await GitHubClientTestSupport.CreateClient(handler)
            .CompareAsync(
                Repository,
                "main",
                "update",
                TestContext.Current.CancellationToken);

        Assert.Equal(1, comparison.AheadBy);
        Assert.Single(comparison.Commits);
    }

    [Fact]
    public async Task Ensure_fork_creates_missing_fork()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"data":{"repository":null,"rateLimit":null}}"""));
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

        ForkResult fork = await GitHubClientTestSupport.CreateClient(handler)
            .EnsureForkAsync(
                Repository,
                "contributor",
                new MutationRequest("fork-1"),
                TestContext.Current.CancellationToken);

        Assert.False(fork.AlreadyExisted);
        Assert.Equal(new RepositoryCoordinates("contributor", "repo"), fork.Repository.Coordinates);
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
