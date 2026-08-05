using System.Net;
using System.Text;

namespace WinMatsch.GitHub.Tests;

internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<RecordedRequest, CancellationToken, HttpResponseMessage>> _steps = [];

    public List<RecordedRequest> Requests { get; } = [];

    public int RemainingSteps => _steps.Count;

    public bool IsDisposed { get; private set; }

    public void Add(Func<RecordedRequest, HttpResponseMessage> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Enqueue((request, _) => step(request));
    }

    public void Add(
        Func<RecordedRequest, CancellationToken, HttpResponseMessage> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Enqueue(step);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri ?? throw new InvalidOperationException("A request URI is required."),
            body,
            request.Headers.Authorization?.ToString(),
            request.Headers.UserAgent.ToString());
        Requests.Add(recorded);
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }

        return _steps.Dequeue()(recorded, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    string? Authorization,
    string UserAgent);

internal sealed class TestContext
{
    private readonly CancellationToken _cancellationToken;

    private TestContext()
    {
        _cancellationToken = System.Threading.CancellationToken.None;
    }

    public static TestContext Current { get; } = new();

    public CancellationToken CancellationToken => _cancellationToken;
}

internal static class GitHubClientTestSupport
{
    public static GitHubRepositoryClient CreateClient(ScriptedHttpMessageHandler handler)
        => CreateClient(handler, CreateOptions());

    public static GitHubRepositoryClient CreateClient(
        ScriptedHttpMessageHandler handler,
        GitHubClientOptions options,
        bool disposeHttpClient = false)
        => new(
            new HttpClient(handler),
            "synthetic-token",
            options,
            disposeHttpClient);

    public static GitHubClientOptions CreateOptions()
        => new()
        {
            ApiBaseUri = new Uri("https://github.invalid/api/"),
            GraphQlUri = new Uri("https://github.invalid/graphql"),
            UserAgent = "winmatsch-tests",
            RetryBaseDelay = TimeSpan.Zero,
            SecondaryRateLimitBaseDelay = TimeSpan.Zero,
            MaxSecondaryRateLimitDelay = TimeSpan.Zero,
            MaxTransientRetries = 2,
            ForkAvailabilityBaseDelay = TimeSpan.Zero,
            ForkAvailabilityMaxAttempts = 3,
        };

    public static HttpResponseMessage Json(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
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

    public static string PullRequestJson(
        long number,
        string title,
        string state = "open",
        string headOwner = "contributor",
        string headBranch = "update",
        string baseBranch = "main",
        string headSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string baseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        => $$"""
        {
          "number": {{number}},
          "node_id": "PR_{{number}}",
          "title": "{{title}}",
          "body": "Synthetic body",
          "state": "{{state}}",
          "draft": false,
          "head": {
            "ref": "{{headBranch}}",
            "sha": "{{headSha}}",
            "repo": {
              "node_id": "R_head",
              "full_name": "{{headOwner}}/repo",
              "html_url": "https://github.invalid/{{headOwner}}/repo",
              "fork": true,
              "private": false,
              "default_branch": "main",
              "owner": { "login": "{{headOwner}}" }
            }
          },
          "base": {
            "ref": "{{baseBranch}}",
            "sha": "{{baseSha}}",
            "repo": {
              "node_id": "R_base",
              "full_name": "upstream/repo",
              "html_url": "https://github.invalid/upstream/repo",
              "fork": false,
              "private": false,
              "default_branch": "main",
              "owner": { "login": "upstream" }
            }
          },
          "html_url": "https://github.invalid/upstream/repo/pull/{{number}}",
          "created_at": "2026-01-01T00:00:00Z",
          "updated_at": "2026-01-02T00:00:00Z"
        }
        """;

    public static string RepositoryGraphQlJson(
        string owner = "upstream",
        string name = "repo")
        => $$"""
        {
          "data": {
            "repository": {
              "id": "R_repo",
              "nameWithOwner": "{{owner}}/{{name}}",
              "url": "https://github.invalid/{{owner}}/{{name}}",
              "isPrivate": false,
              "isFork": false,
              "parent": null,
              "defaultBranchRef": {
                "name": "main",
                "target": { "oid": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
              }
            },
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
