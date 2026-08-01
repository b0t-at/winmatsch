using System.Net;
using Xunit;

namespace WinMatsch.GitHub.Tests;

public sealed class GitHubReadOperationsTests
{
    private static readonly RepositoryCoordinates _repository = new("upstream", "repo");

    [Fact]
    public async Task Authenticated_user_uses_graphql_and_reports_rate_limit()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://github.invalid/graphql", request.Uri.AbsoluteUri);
            Assert.Equal("Bearer synthetic-token", request.Authorization);
            Assert.Equal("winmatsch-tests", request.UserAgent);
            return GitHubClientTestSupport.Json(
                """
                {
                  "data": {
                    "viewer": {
                      "login": "octocat",
                      "name": "Synthetic User",
                      "email": null,
                      "avatarUrl": "https://github.invalid/avatar.png"
                    },
                    "rateLimit": {
                      "limit": 5000,
                      "remaining": 4998,
                      "used": 2,
                      "resetAt": "2026-01-01T01:00:00Z"
                    }
                  }
                }
                """);
        });
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);
        RateLimitInfo? observed = null;
        client.RateLimitObserved += (_, rateLimit) => observed = rateLimit;

        GitHubUser user = await client.GetAuthenticatedUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal("octocat", user.Login);
        Assert.Equal("graphql", observed?.Resource);
        Assert.Equal(4998, client.LastRateLimit?.Remaining);
    }

    [Fact]
    public async Task OAuth_scopes_are_exposed_when_response_headers_report_them()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "data": {
                "viewer": {
                  "login": "octocat",
                  "avatarUrl": "https://github.invalid/avatar.png"
                }
              }
            }
            """,
            headers: [("X-OAuth-Scopes", "repo, read:org, repo")]));
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);

        await client.GetAuthenticatedUserAsync(TestContext.Current.CancellationToken);

        Assert.Collection(
            client.LastOAuthScopes,
            scope => Assert.Equal("repo", scope),
            scope => Assert.Equal("read:org", scope));
    }

    [Fact]
    public async Task Authenticated_user_falls_back_to_rest_when_graphql_is_unavailable()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"GitHub repository 'decoy' was not found."}""",
            HttpStatusCode.NotFound));
        handler.Add(request =>
        {
            Assert.Equal("https://github.invalid/api/user", request.Uri.AbsoluteUri);
            return GitHubClientTestSupport.Json(
                """
                {
                  "login": "fallback-user",
                  "name": null,
                  "email": "fallback@example.invalid",
                  "avatar_url": "https://github.invalid/fallback.png"
                }
                """);
        });

        GitHubUser user = await GitHubClientTestSupport.CreateClient(handler)
            .GetAuthenticatedUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal("fallback-user", user.Login);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Repository_discovery_returns_default_branch_state()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.DoesNotContain("oid", request.Body, StringComparison.OrdinalIgnoreCase);
            return GitHubClientTestSupport.Json(
                GitHubClientTestSupport.RepositoryGraphQlJson());
        });
        handler.Add(request =>
        {
            Assert.EndsWith("/repos/upstream/repo/branches/main", request.Uri.AbsolutePath);
            return GitHubClientTestSupport.Json(
                """
                {
                  "name": "main",
                  "commit": { "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                  "protected": true
                }
                """);
        });

        RepositoryInfo repository = await GitHubClientTestSupport.CreateClient(handler)
            .GetRepositoryAsync(_repository, TestContext.Current.CancellationToken);

        Assert.Equal(_repository, repository.Coordinates);
        Assert.Equal("main", repository.DefaultBranch.Name);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", repository.DefaultBranch.HeadSha);
        Assert.True(repository.DefaultBranch.IsProtected);
    }

    [Fact]
    public async Task Repository_discovery_has_rest_fallback()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"GraphQL disabled"}""",
            HttpStatusCode.MethodNotAllowed));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "id": 100,
              "node_id": "R_rest",
              "full_name": "upstream/repo",
              "html_url": "https://github.invalid/upstream/repo",
              "fork": false,
              "private": false,
              "default_branch": "trunk",
              "owner": { "login": "upstream" }
            }
            """));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "name": "trunk",
              "commit": { "sha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
              "protected": true
            }
            """));

        RepositoryInfo repository = await GitHubClientTestSupport.CreateClient(handler)
            .GetRepositoryAsync(_repository, TestContext.Current.CancellationToken);

        Assert.Equal("trunk", repository.DefaultBranch.Name);
        Assert.True(repository.DefaultBranch.IsProtected);
    }

    [Fact]
    public async Task Repository_metadata_maps_license_topics_and_public_urls()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Equal("https://github.invalid/api/repos/upstream/repo", request.Uri.AbsoluteUri);
            return GitHubClientTestSupport.Json(
                """
                {
                  "id": 100,
                  "node_id": "R_rest",
                  "full_name": "upstream/repo",
                  "html_url": "https://github.com/upstream/repo",
                  "fork": false,
                  "private": false,
                  "default_branch": "main",
                  "owner": {
                    "login": "upstream",
                    "html_url": "https://github.com/upstream"
                  },
                  "license": { "spdx_id": "Apache-2.0" },
                  "topics": ["windows", "weather", "windows"]
                }
                """,
                headers:
                [
                    ("X-RateLimit-Limit", "5000"),
                    ("X-RateLimit-Remaining", "4990"),
                    ("X-RateLimit-Used", "10"),
                    ("X-RateLimit-Reset", "1767229200"),
                    ("X-RateLimit-Resource", "core"),
                ]);
        });
        handler.Add(request =>
        {
            Assert.Equal("https://github.invalid/api/repos/upstream/repo/license", request.Uri.AbsoluteUri);
            return GitHubClientTestSupport.Json(
                """{"html_url":"https://github.com/upstream/repo/blob/main/LICENSE"}""");
        });
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);

        RepositoryMetadataInfo metadata = await client.GetRepositoryMetadataAsync(
            _repository,
            TestContext.Current.CancellationToken);

        Assert.Equal("Apache-2.0", metadata.LicenseSpdxId);
        Assert.Equal("https://github.com/upstream/repo/blob/main/LICENSE", metadata.LicenseUri?.AbsoluteUri);
        Assert.Equal(["weather", "windows"], metadata.Topics);
        Assert.Equal("https://github.com/upstream", metadata.OwnerUri.AbsoluteUri);
        Assert.Equal(4990, client.LastRateLimit?.Remaining);
    }

    [Fact]
    public async Task Manifest_read_uses_tree_and_content_endpoints()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(request =>
        {
            Assert.Contains("git/trees/main?recursive=1", request.Uri.AbsoluteUri, StringComparison.Ordinal);
            return GitHubClientTestSupport.Json(
                """
                {
                  "truncated": false,
                  "tree": [
                    { "path": "manifests/a/App/1.0/App.yaml", "sha": "aaa", "type": "blob", "size": 4 },
                    { "path": "manifests/a/App/1.0/readme.txt", "sha": "bbb", "type": "blob", "size": 4 },
                    { "path": "manifests/a/App/2.0/App.yaml", "sha": "ccc", "type": "blob", "size": 4 }
                  ]
                }
                """);
        });
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "name": "App.yaml",
              "path": "manifests/a/App/1.0/App.yaml",
              "sha": "aaa",
              "size": 4,
              "encoding": "base64",
              "content": "VGVzdA=="
            }
            """));

        IReadOnlyList<ManifestFile> manifests = await GitHubClientTestSupport.CreateClient(handler)
            .GetManifestFilesAsync(
                _repository,
                "manifests/a/App/1.0",
                "main",
                TestContext.Current.CancellationToken);

        ManifestFile manifest = Assert.Single(manifests);
        Assert.Equal("Test", manifest.GetText());
    }

    [Fact]
    public async Task Truncated_tree_is_rejected_instead_of_returning_partial_data()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"truncated":true,"tree":[]}"""));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).GetTreeAsync(
                _repository,
                "main",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Releases_follow_link_pagination_and_map_assets()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            [{
              "id": 1,
              "tag_name": "v2",
              "name": "Version 2",
              "body": "notes",
              "html_url": "https://github.invalid/upstream/repo/releases/v2",
              "draft": false,
              "prerelease": false,
              "published_at": "2026-01-02T00:00:00Z",
              "assets": [{
                "id": 10,
                "name": "app.exe",
                "browser_download_url": "https://github.invalid/assets/app.exe",
                "content_type": "application/octet-stream",
                "size": 123,
                "download_count": 4,
                "created_at": "2026-01-02T00:00:00Z"
              }]
            }]
            """,
            headers:
            [
                ("Link", "<https://github.invalid/api/releases-page-2>; rel=\"next\""),
                ("X-RateLimit-Limit", "5000"),
                ("X-RateLimit-Remaining", "4990"),
                ("X-RateLimit-Used", "10"),
                ("X-RateLimit-Reset", "1767229200"),
                ("X-RateLimit-Resource", "core"),
            ]));
        handler.Add(request =>
        {
            Assert.Equal("https://github.invalid/api/releases-page-2", request.Uri.AbsoluteUri);
            return GitHubClientTestSupport.Json(
                """
                [{
                  "id": 2,
                  "tag_name": "v1",
                  "name": null,
                  "body": null,
                  "html_url": "https://github.invalid/upstream/repo/releases/v1",
                  "draft": false,
                  "prerelease": false,
                  "published_at": null,
                  "assets": []
                }]
                """);
        });
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);

        IReadOnlyList<GitHubRelease> releases = await client.GetReleasesAsync(
            _repository,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, releases.Count);
        Assert.Single(releases[0].Assets);
        Assert.Equal("core", client.LastRateLimit?.Resource);
    }

    [Fact]
    public async Task Rest_errors_include_status_request_id_and_details()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "message": "Validation Failed",
              "errors": [{ "resource": "Reference", "code": "already_exists", "message": "Reference exists" }]
            }
            """,
            HttpStatusCode.UnprocessableEntity,
            ("X-GitHub-Request-Id", "SANITIZED")));

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Equal("SANITIZED", exception.RequestId);
        Assert.True(exception.IsConflict);
        Assert.Contains("Reference exists", exception.Errors);
    }

    [Fact]
    public async Task Safe_reads_retry_transient_responses()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"temporary"}""",
            HttpStatusCode.ServiceUnavailable));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "name": "manifest.yaml",
              "path": "manifest.yaml",
              "sha": "aaa",
              "size": 4,
              "encoding": "base64",
              "content": "VGVzdA=="
            }
            """));

        RepositoryContent result = await GitHubClientTestSupport.CreateClient(handler)
            .GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                TestContext.Current.CancellationToken);

        Assert.Equal("Test", result.GetText());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Safe_reads_honor_rate_limit_forbidden_as_transient()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """{"message":"secondary rate limit"}""",
            HttpStatusCode.Forbidden,
            ("Retry-After", "0"),
            ("X-RateLimit-Limit", "5000"),
            ("X-RateLimit-Remaining", "0"),
            ("X-RateLimit-Used", "5000"),
            ("X-RateLimit-Reset", "1767229200")));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            {
              "name": "manifest.yaml",
              "path": "manifest.yaml",
              "sha": "aaa",
              "size": 4,
              "encoding": "base64",
              "content": "VGVzdA=="
            }
            """));
        GitHubRepositoryClient client = GitHubClientTestSupport.CreateClient(handler);

        RepositoryContent result = await client.GetContentAsync(
            _repository,
            "manifest.yaml",
            "main",
            TestContext.Current.CancellationToken);

        Assert.Equal("Test", result.GetText());
        Assert.Equal(0, client.LastRateLimit?.Remaining);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Exhausted_secondary_rate_limit_has_response_specific_error_kind()
    {
        var handler = new ScriptedHttpMessageHandler();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            handler.Add(_ => GitHubClientTestSupport.Json(
                """{"message":"secondary rate limit"}""",
                HttpStatusCode.Forbidden,
                ("Retry-After", "0")));
        }

        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler).GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                TestContext.Current.CancellationToken));

        Assert.Equal(GitHubApiErrorKind.RateLimited, exception.ErrorKind);
        Assert.Equal(TimeSpan.Zero, exception.RetryAfter);
        Assert.Null(exception.RateLimit);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Http_date_retry_after_is_preserved_as_response_cooldown()
    {
        var handler = new ScriptedHttpMessageHandler();
        string retryAt = DateTimeOffset.UtcNow.AddMinutes(5).ToString("R");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            handler.Add(_ => GitHubClientTestSupport.Json(
                """{"message":"secondary rate limit"}""",
                HttpStatusCode.Forbidden,
                ("Retry-After", retryAt)));
        }

        var options = new GitHubClientOptions
        {
            ApiBaseUri = new Uri("https://github.invalid/api/"),
            GraphQlUri = new Uri("https://github.invalid/graphql"),
            UserAgent = "winmatsch-tests",
            RetryBaseDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.Zero,
            MaxTransientRetries = 2,
        };
        GitHubApiException exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => GitHubClientTestSupport.CreateClient(handler, options).GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                TestContext.Current.CancellationToken));

        Assert.Equal(GitHubApiErrorKind.RateLimited, exception.ErrorKind);
        Assert.True(exception.RetryAfter > TimeSpan.FromMinutes(4));
    }

    [Fact]
    public async Task Branch_enumeration_follows_pagination()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            [{
              "name": "main",
              "commit": { "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
              "protected": true
            }]
            """,
            headers:
            [
                ("Link", "<https://github.invalid/api/branches-page-2>; rel=\"next\""),
            ]));
        handler.Add(_ => GitHubClientTestSupport.Json(
            """
            [{
              "name": "update",
              "commit": { "sha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
              "protected": false
            }]
            """));

        IReadOnlyList<BranchState> branches = await GitHubClientTestSupport.CreateClient(handler)
            .GetBranchesAsync(_repository, TestContext.Current.CancellationToken);

        Assert.Equal(2, branches.Count);
        Assert.True(branches[0].IsProtected);
        Assert.Equal("update", branches[1].Name);
    }

    [Fact]
    public async Task Cancellation_is_forwarded_to_transport()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Add((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GitHubClientTestSupport.Json("{}");
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GitHubClientTestSupport.CreateClient(handler).GetContentAsync(
                _repository,
                "manifest.yaml",
                "main",
                cancellation.Token));
    }
}
