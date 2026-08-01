using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Validation;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using WinMatsch.Workflows.Tests.GitHub;
using Xunit;

namespace WinMatsch.E2E.Tests;

public sealed class GitHubLifecycleE2ETests
{
    private const string LiveMutationRepository = "b0t-at/winmatsch-e2e";

    [Fact]
    public async Task Sanitized_lifecycle_fixture_covers_success_duplicate_race_and_partial_state()
    {
        var successful = new FakeGitHubClient(includeFork: false);
        GitHubSubmissionRequest createFork = GitHubLifecycleTestSupport.Request(
            policy: new GitHubSubmissionPolicy
            {
                ForkConsent = ForkConsentPolicy.AllowCreate,
                MinimumReleaseFreshness = TimeSpan.Zero,
            });
        GitHubLifecycleResult success = await GitHubLifecycleTestSupport.Workflow(successful)
            .ExecuteAsync(createFork);
        Assert.Equal(GitHubLifecycleResultCode.Succeeded, success.Code);
        Assert.Equal(["fork", "branch", "commit", "pull-request"], successful.Mutations);
        Assert.True(success.RemoteState.ForkCreated);
        Assert.True(success.RemoteState.PullRequestCreated);

        var duplicate = new FakeGitHubClient();
        duplicate.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(17, author: "other-author"));
        GitHubLifecycleResult duplicateResult = await GitHubLifecycleTestSupport.Workflow(duplicate)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());
        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, duplicateResult.Code);
        Assert.Empty(duplicate.Mutations);

        var racer = new FakeGitHubClient
        {
            OnSearch = static (client, call) =>
            {
                if (call == 2)
                {
                    client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(18, author: "racer"));
                }
            },
        };
        GitHubLifecycleResult race = await GitHubLifecycleTestSupport.Workflow(racer)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());
        Assert.Equal(GitHubLifecycleResultCode.DuplicatePullRequest, race.Code);
        Assert.Equal(["branch", "commit"], racer.Mutations);
        Assert.True(race.RemoteState.CommitCreated);
        Assert.False(race.RemoteState.PullRequestCreated);

        var moved = new FakeGitHubClient
        {
            MoveUpstreamBeforeCommitTo = "cccccccccccccccccccccccccccccccccccccccc",
        };
        GitHubLifecycleResult conflict = await GitHubLifecycleTestSupport.Workflow(moved)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());
        Assert.Equal(GitHubLifecycleResultCode.Conflict, conflict.Code);
        Assert.Equal(["branch"], moved.Mutations);
        Assert.True(conflict.RemoteState.BranchCreated);
        Assert.False(conflict.RemoteState.CommitCreated);
    }

    [Fact]
    public async Task Plan_and_failed_final_revalidation_are_zero_remote_mutation()
    {
        var plannedClient = new FakeGitHubClient();
        GitHubLifecycleResult plan = await GitHubLifecycleTestSupport.Workflow(plannedClient)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));
        Assert.Equal(GitHubLifecycleResultCode.Planned, plan.Code);
        Assert.Empty(plannedClient.Mutations);

        var invalidClient = new FakeGitHubClient();
        var artifacts = new FakeArtifactRevalidator
        {
            Result = new(
                false,
                [new GitHubLifecycleDiagnostic("E2E_ETAG_CHANGED", "Artifact ETag changed.")]),
        };
        GitHubLifecycleResult invalid = await GitHubLifecycleTestSupport.Workflow(
                invalidClient,
                artifacts: artifacts)
            .ExecuteAsync(GitHubLifecycleTestSupport.Request());
        Assert.Equal(GitHubLifecycleResultCode.ValidationFailed, invalid.Code);
        Assert.Empty(invalidClient.Mutations);
    }

    [Fact]
    public async Task Concurrent_duplicate_attempts_are_serialized_without_duplicate_pull_requests()
    {
        var client = new FakeGitHubClient();
        var locks = new BlockingLockProvider();
        GitHubLifecycleWorkflow workflow = GitHubLifecycleTestSupport.Workflow(client, locks: locks);
        Task<GitHubLifecycleResult> first = workflow.ExecuteAsync(GitHubLifecycleTestSupport.Request());
        await locks.FirstAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        GitHubLifecycleResult second = await workflow.ExecuteAsync(
            GitHubLifecycleTestSupport.Request() with { IdempotencyKey = "operation-2" });
        locks.ReleaseFirst.TrySetResult();
        GitHubLifecycleResult firstResult = await first;

        Assert.Equal(GitHubLifecycleResultCode.Conflict, second.Code);
        Assert.Equal(GitHubLifecycleResultCode.Succeeded, firstResult.Code);
        Assert.Single(client.PullRequests);
    }

    [Fact]
    public void Off_target_diff_is_rejected_before_any_remote_boundary()
    {
        LocalOperationPlan local = GitHubLifecycleTestSupport.Plan(
        [
            new WorkflowFileChange(
                PlannedChangeKind.Add,
                "manifests/x/Other/Package/1.0.0/Other.Package.yaml",
                "x"u8),
        ]);

        GitHubSubmissionPlan plan = GitHubLifecycleWorkflow.Plan(
            GitHubLifecycleTestSupport.Request() with { LocalPlan = local });

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "GH1004");
    }

    [EnvironmentFact("WINMATSCH_E2E_TEST_REPOSITORY")]
    public async Task Configurable_test_fork_runs_production_read_discovery_and_lifecycle_plan_without_mutation()
    {
        string repositoryValue = Environment.GetEnvironmentVariable("WINMATSCH_E2E_TEST_REPOSITORY")!;
        string token = Environment.GetEnvironmentVariable("WINMATSCH_E2E_GITHUB_TOKEN")
            ?? throw new InvalidOperationException("WINMATSCH_E2E_GITHUB_TOKEN is required.");
        RepositoryCoordinates repository = RepositoryCoordinates.Parse(repositoryValue);
        var recorder = new RecordingNetworkHandler(new HttpClientHandler());
        using var client = new GitHubRepositoryClient(new HttpClient(recorder), token);

        GitHubUser user = await client.GetAuthenticatedUserAsync();
        RepositoryInfo info = await client.GetRepositoryAsync(repository);
        BranchState branch = await client.GetDefaultBranchAsync(repository);
        IReadOnlyList<GitHubRelease> releases = await client.GetReleasesAsync(repository);
        IReadOnlyList<RepositoryTreeEntry> tree = await client.GetTreeAsync(
            repository,
            branch.HeadSha,
            recursive: false);
        IReadOnlyList<PullRequestInfo> pullRequests = await client.SearchPullRequestsAsync(
            repository,
            new PullRequestSearch(
                PullRequestState.Open,
                ExactTitleToken: $"winmatsch-read-only-{Guid.NewGuid():N}"));
        ImmutableArray<DiscoveredAsset> discovered = await new GitHubWorkflowReleaseSource(
                client,
                repository)
            .DiscoverAsync(
                new PackageIdentifier("WinMatsch.ReadOnlyFixture"),
                new ReleaseRequest(null, [], []),
                CancellationToken.None);
        GitHubLifecycleResult plan = await new GitHubLifecycleWorkflow(
                client,
                new NoOpPreflight(),
                new NoOpRevalidator(),
                new NoOpLockProvider())
            .ExecuteAsync(GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan) with
            {
                UpstreamRepository = info.Coordinates,
                TargetRepository = info.Coordinates,
            });

        Assert.Equal(GitHubLifecycleResultCode.Planned, plan.Code);
        Assert.False(string.IsNullOrWhiteSpace(user.Login));
        Assert.Equal(repository, info.Coordinates);
        Assert.False(string.IsNullOrWhiteSpace(branch.HeadSha));
        Assert.NotNull(releases);
        Assert.NotNull(tree);
        Assert.Empty(pullRequests);
        Assert.Equal(
            ReleaseAssetDiscovery.Discover(releases).Select(static asset => asset.DownloadUri),
            discovered.Select(static asset => asset.DownloadUri));
        Assert.True(recorder.Requests.Count >= 7);
        Assert.Contains(recorder.Requests, static request => request.Method == HttpMethod.Get);
        Assert.Contains(
            recorder.Requests,
            static request => request.Method == HttpMethod.Post
                && request.Uri.AbsoluteUri == "https://api.github.com/graphql");
        Assert.All(
            recorder.Requests,
            static request =>
            {
                Assert.False(
                    request.Method == HttpMethod.Delete
                    || request.Method == HttpMethod.Patch
                    || request.Method == HttpMethod.Put);
                if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal("https://api.github.com/graphql", request.Uri.AbsoluteUri);
                    Assert.Contains("\"query\"", request.Body, StringComparison.Ordinal);
                    Assert.DoesNotContain("mutation ", request.Body, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("mutation(", request.Body, StringComparison.OrdinalIgnoreCase);
                }
            });
    }

    [EnvironmentFact("WINMATSCH_E2E_LIVE_MUTATION", "1")]
    public async Task Live_mutation_contract_uses_unique_disposable_state_and_cleans_up()
    {
        string repository = Environment.GetEnvironmentVariable("WINMATSCH_E2E_LIVE_REPOSITORY")
            ?? throw new InvalidOperationException("WINMATSCH_E2E_LIVE_REPOSITORY is required.");
        string token = Environment.GetEnvironmentVariable("WINMATSCH_E2E_GITHUB_TOKEN")
            ?? throw new InvalidOperationException("WINMATSCH_E2E_GITHUB_TOKEN is required.");

        Assert.Equal(LiveMutationRepository, repository);
        Assert.NotEmpty(token);
        RepositoryCoordinates coordinates = RepositoryCoordinates.Parse(repository);
        var package = new PackageIdentifier($"WinMatsch.E2E.{Guid.NewGuid():N}");
        var version = new PackageVersion("0.0.0-e2e");
        string directory = ManifestPaths.GetVersionDirectory(package, version);
        string path = $"{directory}/{package.Value}.yaml";
        byte[] content = System.Text.Encoding.UTF8.GetBytes(
            $"PackageIdentifier: {package.Value}\n"
            + $"PackageVersion: {version.Value}\n"
            + "DefaultLocale: en-US\n"
            + "ManifestType: version\n"
            + "ManifestVersion: 1.12.0\n");
        var change = new WorkflowFileChange(
            PlannedChangeKind.Add,
            path,
            content,
            ExpectedFileState.Absent);
        var document = new RawManifestDocument(path, content);
        var preflight = new WorkflowPreflightRequest
        {
            BeforeDocuments = [],
            AfterDocuments = [document],
            Changes = [change],
            Options = new PreflightOptions { NetworkMode = NetworkValidationMode.Skip },
        };
        var local = new LocalOperationPlan
        {
            Operation = "New",
            PackageIdentifier = package,
            PackageVersion = version,
            OutputDirectory = "live-e2e-unused",
            FileChanges = [change],
            BeforeDocuments = [],
            AfterDocuments = [document],
            Validation = new ValidationReport(),
            Preflight = preflight,
            Rules = RuleRunSummary.Empty,
        };
        var request = new GitHubSubmissionRequest
        {
            LocalPlan = local,
            UpstreamRepository = coordinates,
            TargetRepository = coordinates,
            ExecutionMode = WorkflowExecutionMode.Apply,
            Operation = GitHubManifestOperation.New,
            Policy = new() { MinimumReleaseFreshness = TimeSpan.Zero },
            IdempotencyKey = $"live-e2e-{Guid.NewGuid():N}",
            CreatedWith = "winmatsch live E2E",
        };
        string? branchName = null;
        using var client = new GitHubRepositoryClient(new HttpClient(), token);
        var workflow = new GitHubLifecycleWorkflow(
            client,
            new NoOpPreflight(),
            new NoOpRevalidator(),
            new NoOpLockProvider());
        GitHubLifecycleResult? result = null;
        Exception? operationFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            result = await workflow.ExecuteAsync(request);
            branchName = result.RemoteState.BranchName;
            Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
            Assert.True(result.RemoteState.PullRequestCreated);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            try
            {
                IReadOnlyList<PullRequestInfo> pullRequests = await client.SearchPullRequestsAsync(
                    coordinates,
                    new PullRequestSearch(
                        PullRequestState.Open,
                        HeadOwner: coordinates.Owner,
                        ExactTitleToken: package.Value));
                foreach (PullRequestInfo pullRequest in pullRequests.Where(candidate =>
                             string.Equals(candidate.HeadBranch, branchName, StringComparison.Ordinal)))
                {
                    await client.ClosePullRequestAsync(
                        coordinates,
                        pullRequest.Number,
                        new MutationRequest($"{request.IdempotencyKey}:cleanup-pr"));
                }
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            try
            {
                if (branchName is not null)
                {
                    await client.DeleteReferenceAsync(
                        coordinates,
                        branchName,
                        new MutationRequest($"{request.IdempotencyKey}:cleanup-branch"));
                }
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        Assert.True(
            cleanupFailures.Count == 0,
            "Live cleanup failed. Close the reported pull request and delete branch "
            + $"'{branchName}' in '{coordinates}' before retrying. "
            + string.Join(" | ", cleanupFailures.Select(static failure => failure.Message)));
        if (operationFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
    }

    private sealed class BlockingLockProvider : IRemoteOperationLockProvider
    {
        private int _calls;

        public TaskCompletionSource FirstAcquired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IAsyncDisposable> AcquireAsync(
            string repository,
            PackageIdentifier packageIdentifier,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) != 1)
            {
                throw new RemoteOperationLockException("Synthetic concurrent operation.");
            }

            FirstAcquired.TrySetResult();
            await ReleaseFirst.Task.WaitAsync(cancellationToken);
            return new Lease();
        }

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingNetworkHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, body));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class NoOpPreflight : IWorkflowPreflight
    {
        public Task<ValidationReport> ValidateAsync(
            WorkflowPreflightRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new ValidationReport());

        public async Task<ValidationReport> ExecuteAsync(
            WorkflowPreflightRequest request,
            Func<CancellationToken, Task> boundary,
            CancellationToken cancellationToken)
        {
            await boundary(cancellationToken);
            return new ValidationReport();
        }
    }

    private sealed class NoOpRevalidator : IFinalArtifactRevalidator
    {
        public Task<FinalArtifactRevalidationResult> RevalidateAsync(
            GitHubSubmissionRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(FinalArtifactRevalidationResult.Valid);
    }

    private sealed class NoOpLockProvider : IRemoteOperationLockProvider
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(
            string repository,
            PackageIdentifier packageIdentifier,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IAsyncDisposable>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
