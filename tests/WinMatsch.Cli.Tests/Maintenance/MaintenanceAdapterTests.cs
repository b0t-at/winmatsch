using System.Collections.Immutable;
using System.Net;
using System.Text;
using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Testing.Infrastructure;
using WinMatsch.Workflows.GitHub;
using Xunit;

namespace WinMatsch.Cli.Tests.Maintenance;

/// <summary>
/// Unit coverage for the maintenance adapters: dead-artifact classification, upstream
/// dead-version inspection, and installer URL extraction from remote manifests.
/// </summary>
public sealed class MaintenanceAdapterTests
{
    [Fact]
    public void Lifecycle_cancellation_maps_to_130_only_for_a_cancelled_invocation()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(
            ExitCodes.OperationFailed,
            MaintenanceCommandHelpers.MapResultCode(
                GitHubLifecycleResultCode.Cancelled,
                cancellation.Token));
        cancellation.Cancel();
        Assert.Equal(
            ExitCodes.Cancelled,
            MaintenanceCommandHelpers.MapResultCode(
                GitHubLifecycleResultCode.Cancelled,
                cancellation.Token));
    }

    [Fact]
    public async Task Pull_request_source_enforces_fork_owner_and_enriches_feedback()
    {
        var client = new FakeMaintenanceGitHubClient { IgnoreHeadOwnerFilter = true };
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41, headOwner: "octocat"));
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(42, headOwner: "attacker"));
        var metadata = new FakePullRequestMetadataSource();
        using var source = new ToolPullRequestObservationSource(client, "octocat", metadata);

        ImmutableArray<PullRequestObservation> observations =
                await source.GetOpenToolPullRequestsAsync(
                    new RepositoryCoordinates("microsoft", "winget-pkgs"),
                    CancellationToken.None);

        PullRequestObservation observation = Assert.Single(observations);
        Assert.Equal(41, observation.PullRequest.Number);
        Assert.Collection(
            observation.Labels,
            label => Assert.Equal("infra-failure", label));
        Assert.Single(observation.Comments);
        Assert.Equal([41L], metadata.RequestedPullRequests);
    }

    [Fact]
    public async Task Pull_request_metadata_follows_pages_and_skips_deleted_users()
    {
        var handler = new FeedbackMetadataHandler();
        using var source = new GitHubPullRequestMetadataSource(
            new GitHubClientOptions(),
            "token",
            new HttpClient(handler));

        PullRequestMetadata metadata = await source.GetAsync(
            new RepositoryCoordinates("owner", "repo"),
            41,
            CancellationToken.None);

        Assert.Equal(["first", "second"], metadata.Comments.Select(static comment => comment.Body));
        Assert.Equal(3, handler.RequestCount);
    }

    private static readonly RepositoryCoordinates _upstream = new("microsoft", "winget-pkgs");
    private static readonly PackageIdentifier _package = new("Contoso.App");
    private static readonly PackageVersion _version = new("1.0.0");

    [Theory]
    [InlineData(HttpStatusCode.NotFound, DeadArtifactState.PermanentlyMissing)]
    [InlineData(HttpStatusCode.Gone, DeadArtifactState.PermanentlyMissing)]
    [InlineData(HttpStatusCode.Unauthorized, DeadArtifactState.NetworkBlocked)]
    [InlineData(HttpStatusCode.Forbidden, DeadArtifactState.NetworkBlocked)]
    [InlineData(HttpStatusCode.BadRequest, DeadArtifactState.NetworkBlocked)]
    public void Only_confirmed_absence_statuses_count_as_dead(
        HttpStatusCode status,
        DeadArtifactState expected)
    {
        var exception = new DownloadHttpException(status, "https://example.invalid/app.exe");

        Assert.Equal(expected, HttpInstallerUrlProber.Classify(exception));
    }

    [Fact]
    public void Redirect_and_transient_failures_never_count_as_dead()
    {
        var redirect = new DownloadRedirectException("https://example.invalid/app.exe", 10);
        var transient = new DownloadNetworkException("timeout", new HttpRequestException("timeout"));

        Assert.Equal(DeadArtifactState.NetworkBlocked, HttpInstallerUrlProber.Classify(redirect));
        Assert.Equal(DeadArtifactState.TransientFailure, HttpInstallerUrlProber.Classify(transient));
    }

    [Fact]
    public async Task Head_absence_is_confirmed_with_a_ranged_get_before_counting_as_dead()
    {
        var origin = new StubHttpMessageHandler(request =>
            request.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent([0]),
                });
        var prober = new HttpInstallerUrlProber(
            new InstallerDownloader(origin),
            () => new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([0]),
            }));

        DeadArtifactState state = await prober.ProbeAsync(
            "https://example.invalid/app.exe",
            CancellationToken.None);

        Assert.Equal(DeadArtifactState.Exists, state);
    }

    [Fact]
    public async Task Absence_confirmed_by_both_head_and_get_counts_as_dead()
    {
        var origin = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var prober = new HttpInstallerUrlProber(
            new InstallerDownloader(origin),
            () => new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone)));

        DeadArtifactState state = await prober.ProbeAsync(
            "https://example.invalid/app.exe",
            CancellationToken.None);

        Assert.Equal(DeadArtifactState.PermanentlyMissing, state);
    }

    [Fact]
    public async Task Indeterminate_absence_confirmation_escalates()
    {
        var origin = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var prober = new HttpInstallerUrlProber(
            new InstallerDownloader(origin),
            () => new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        DeadArtifactState state = await prober.ProbeAsync(
            "https://example.invalid/app.exe",
            CancellationToken.None);

        Assert.Equal(DeadArtifactState.NetworkBlocked, state);
    }

    [Fact]
    public async Task Missing_version_directories_report_not_existing_upstream()
    {
        var client = new FakeMaintenanceGitHubClient();
        client.DefaultBranches[_upstream.ToString()] = new BranchState("master", "sha", IsProtected: true);
        var inspector = new GitHubDeadVersionInspector(client, new ThrowingProber());

        DeadVersionInspection inspection = await inspector.InspectAsync(
            _upstream, _package, _version, CancellationToken.None);

        Assert.False(inspection.ExistsUpstream);
        Assert.Empty(inspection.ArtifactStates);
    }

    [Fact]
    public async Task Malformed_installer_manifests_classify_as_indeterminate()
    {
        FakeMaintenanceGitHubClient client = WithInstallerManifest("InstallerUrl: [unclosed\n  - broken");
        var inspector = new GitHubDeadVersionInspector(client, new ThrowingProber());

        DeadVersionInspection inspection = await inspector.InspectAsync(
            _upstream, _package, _version, CancellationToken.None);

        Assert.True(inspection.ExistsUpstream);
        Assert.Equal([DeadArtifactState.TransientFailure], inspection.ArtifactStates.ToArray());
    }

    [Fact]
    public async Task Installer_urls_are_probed_in_manifest_order()
    {
        FakeMaintenanceGitHubClient client = WithInstallerManifest(
            "PackageIdentifier: Contoso.App\n"
            + "Installers:\n"
            + "  - Architecture: x64\n"
            + "    InstallerUrl: https://example.invalid/a.exe\n"
            + "  - Architecture: x86\n"
            + "    InstallerUrl: https://example.invalid/b.exe\n");
        var prober = new ScriptedProber
        {
            States =
            {
                ["https://example.invalid/a.exe"] = DeadArtifactState.PermanentlyMissing,
                ["https://example.invalid/b.exe"] = DeadArtifactState.Exists,
            },
        };
        var inspector = new GitHubDeadVersionInspector(client, prober);

        DeadVersionInspection inspection = await inspector.InspectAsync(
            _upstream, _package, _version, CancellationToken.None);

        Assert.True(inspection.ExistsUpstream);
        Assert.Equal(
            [DeadArtifactState.PermanentlyMissing, DeadArtifactState.Exists],
            inspection.ArtifactStates.ToArray());
    }

    [Fact]
    public void Extracts_every_installer_url()
    {
        byte[] manifest = Encoding.UTF8.GetBytes(
            "Installers:\n"
            + "  - InstallerUrl: https://example.invalid/a.exe\n"
            + "  - InstallerUrl: https://example.invalid/b.exe\n");

        IReadOnlyList<string> urls = GitHubDeadVersionInspector.ExtractInstallerUrls(manifest);

        Assert.Equal(["https://example.invalid/a.exe", "https://example.invalid/b.exe"], urls);
    }

    [Fact]
    public void Installer_url_extraction_rejects_yaml_alias_cycles()
    {
        byte[] manifest = Encoding.UTF8.GetBytes("root: &loop\n  - *loop\n");

        Assert.Throws<YamlDotNet.Core.YamlException>(
            () => GitHubDeadVersionInspector.ExtractInstallerUrls(manifest));
    }

    private static FakeMaintenanceGitHubClient WithInstallerManifest(string yaml)
    {
        var client = new FakeMaintenanceGitHubClient();
        client.DefaultBranches[_upstream.ToString()] = new BranchState("master", "sha", IsProtected: true);
        string directory = ManifestPaths.GetVersionDirectory(_package, _version);
        string treeish = "sha";
        int index = 0;
        foreach (string segment in directory.Split('/'))
        {
            string next = $"tree-{index++}";
            client.Trees[treeish] =
            [
                new RepositoryTreeEntry(segment, next, RepositoryTreeEntryType.Tree, null),
            ];
            treeish = next;
        }

        string fileName = ManifestPaths.GetInstallerFileName(_package);
        string path = $"{directory}/{fileName}";
        byte[] content = Encoding.UTF8.GetBytes(yaml);
        client.Trees[treeish] =
        [
            new RepositoryTreeEntry(
                fileName,
                "file-sha",
                RepositoryTreeEntryType.Blob,
                content.Length),
        ];
        client.Contents[path] = new RepositoryContent(
            fileName,
            path,
            "file-sha",
            content.Length,
            "base64",
            content);
        return client;
    }

    private sealed class FakePullRequestMetadataSource : IPullRequestMetadataSource
    {
        public List<long> RequestedPullRequests { get; } = [];

        public Task<PullRequestMetadata> GetAsync(
            RepositoryCoordinates repository,
            long pullRequestNumber,
            CancellationToken cancellationToken)
        {
            RequestedPullRequests.Add(pullRequestNumber);
            return Task.FromResult(new PullRequestMetadata(
                ["infra-failure"],
                [new PullRequestCommentObservation("github-actions", "timed out", DateTimeOffset.UnixEpoch)]));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FeedbackMetadataHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            string path = request.RequestUri!.PathAndQuery;
            if (!path.Contains("/comments", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"labels":[]}"""));
            }

            if (!path.Contains("page=2", StringComparison.Ordinal))
            {
                HttpResponseMessage first = Json(
                    """[{"user":{"login":"wingetbot"},"body":"first","created_at":"2026-01-01T00:00:00Z"}]""");
                first.Headers.TryAddWithoutValidation(
                    "Link",
                    "<https://api.github.com/repos/owner/repo/issues/41/comments?per_page=100&page=2>; rel=\"next\"");
                return Task.FromResult(first);
            }

            return Task.FromResult(Json(
                """[{"user":null,"body":"deleted","created_at":"2026-01-01T00:00:00Z"},{"user":{"login":"wingetbot"},"body":"second","created_at":"2026-01-02T00:00:00Z"}]"""));

            static HttpResponseMessage Json(string content)
                => new(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json"),
                };
        }
    }

    private sealed class ThrowingProber : IInstallerUrlProber
    {
        public Task<DeadArtifactState> ProbeAsync(string url, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The prober must not run in this scenario.");
    }

    private sealed class ScriptedProber : IInstallerUrlProber
    {
        public Dictionary<string, DeadArtifactState> States { get; } = new(StringComparer.Ordinal);

        public Task<DeadArtifactState> ProbeAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult(States[url]);
    }
}
