using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows.GitHub;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class GitHubMaintenanceWorkflowTests
{
    [Fact]
    public async Task Sync_refuses_to_force_unknown_user_commits()
    {
        var client = new FakeGitHubClient { ForkAhead = true };
        client.SetForkHead("cccccccccccccccccccccccccccccccccccccccc");
        var workflow = new GitHubMaintenanceWorkflow(client, new FakeClock());

        GitHubMaintenanceResult result = await workflow.SyncAsync(new(
            GitHubLifecycleTestSupport.Upstream,
            GitHubLifecycleTestSupport.Fork,
            WorkflowExecutionMode.Apply,
            "sync-1"));

        Assert.Equal(GitHubLifecycleResultCode.Conflict, result.Code);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Sync_fast_forwards_only_the_fork_default_branch()
    {
        var client = new FakeGitHubClient();
        client.SetForkHead("cccccccccccccccccccccccccccccccccccccccc");
        var workflow = new GitHubMaintenanceWorkflow(client, new FakeClock());

        GitHubMaintenanceResult result = await workflow.SyncAsync(new(
            GitHubLifecycleTestSupport.Upstream,
            GitHubLifecycleTestSupport.Fork,
            WorkflowExecutionMode.Apply,
            "sync-2"));

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.Equal(["sync"], client.Mutations);
    }

    [Fact]
    public async Task Cleanup_deletes_only_proven_tool_owned_closed_pr_branches()
    {
        var client = new FakeGitHubClient();
        PullRequestInfo owned = GitHubLifecycleTestSupport.PullRequest(
            10,
            PullRequestState.Closed,
            GitHubLifecycleTestSupport.Fork.Owner,
            "winmatsch/update/example-app/owned");
        PullRequestInfo unowned = GitHubLifecycleTestSupport.PullRequest(
            11,
            PullRequestState.Closed,
            GitHubLifecycleTestSupport.Fork.Owner,
            "user/feature") with
        { Body = "not tool owned" };
        client.AddPullRequest(owned);
        client.AddPullRequest(unowned);
        client.AddBranch(GitHubLifecycleTestSupport.Fork, owned.HeadBranch, owned.HeadSha);
        client.AddBranch(GitHubLifecycleTestSupport.Fork, unowned.HeadBranch, unowned.HeadSha);
        var workflow = new GitHubMaintenanceWorkflow(client, new FakeClock());

        GitHubMaintenanceResult result = await workflow.CleanupAsync(new(
            GitHubLifecycleTestSupport.Upstream,
            GitHubLifecycleTestSupport.Fork,
            WorkflowExecutionMode.Apply,
            "cleanup-1"));

        Assert.Equal(GitHubLifecycleResultCode.Succeeded, result.Code);
        Assert.Equal(["delete"], client.Mutations);
        Assert.Single(result.Plan.Operations);
        Assert.Contains("owned", result.Plan.Operations[0].Target, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_returns_structured_lifecycle_status_without_mutation()
    {
        PullRequestObservation open = Observation(
            GitHubLifecycleTestSupport.PullRequest(12),
            toolOwned: true);
        PullRequestObservation foreign = Observation(
            GitHubLifecycleTestSupport.PullRequest(13),
            toolOwned: false);

        GitHubCompleteResult result = GitHubMaintenanceWorkflow.Complete([open, foreign]);

        Assert.Equal(PullRequestLifecycleAction.Wait, result.PullRequests[0].RecommendedAction);
        Assert.Equal(PullRequestLifecycleAction.None, result.PullRequests[1].RecommendedAction);
        Assert.Equal("unowned", result.PullRequests[1].Status);
    }

    [Fact]
    public async Task Superseded_close_requires_ownership_and_replacement_before_comment_then_close()
    {
        var client = new FakeGitHubClient();
        PullRequestInfo oldPullRequest = GitHubLifecycleTestSupport.PullRequest(
            14,
            branch: "winmatsch/update/example-app/old");
        PullRequestInfo replacement = GitHubLifecycleTestSupport.PullRequest(15);
        client.AddPullRequest(oldPullRequest);
        client.AddPullRequest(replacement);
        var workflow = new GitHubMaintenanceWorkflow(client, new FakeClock());

        GitHubMaintenanceResult denied = await workflow.CloseSupersededAsync(
            GitHubLifecycleTestSupport.Upstream,
            Observation(oldPullRequest, toolOwned: false),
            replacement,
            "supersede-denied");
        GitHubMaintenanceResult closed = await workflow.CloseSupersededAsync(
            GitHubLifecycleTestSupport.Upstream,
            Observation(oldPullRequest, toolOwned: true),
            replacement,
            "supersede-ok");

        Assert.Equal(GitHubLifecycleResultCode.InvalidPlan, denied.Code);
        Assert.Equal(GitHubLifecycleResultCode.Succeeded, closed.Code);
        Assert.Equal(["comment", "close"], client.Mutations);
        Assert.Contains("superseded", closed.Audit[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_dead_versions_rejects_transient_failures_and_default_grouping()
    {
        var inspector = new FakeDeadVersionInspector
        {
            Inspection = new(
                new PackageIdentifier("Example.App"),
                new PackageVersion("1.0.0"),
                true,
                [DeadArtifactState.PermanentlyMissing, DeadArtifactState.TransientFailure]),
        };
        var workflow = new RemoveDeadVersionsWorkflow(inspector);
        RemoveDeadVersionsRequest one = new(
            GitHubLifecycleTestSupport.Upstream,
            [(new PackageIdentifier("Example.App"), new PackageVersion("1.0.0"))]);
        RemoveDeadVersionsRequest grouped = one with
        {
            Versions =
            [
                .. one.Versions,
                (new PackageIdentifier("Example.App"), new PackageVersion("2.0.0")),
            ],
        };

        ImmutableArray<RemoveDeadVersionPlan> transient = await workflow.PlanAsync(one);
        ImmutableArray<RemoveDeadVersionPlan> grouping = await workflow.PlanAsync(grouped);

        Assert.False(transient[0].CanRemove);
        Assert.Contains(transient[0].Diagnostics, diagnostic => diagnostic.Code == "GH3103");
        Assert.All(grouping, static plan => Assert.False(plan.CanRemove));
    }

    [Fact]
    public async Task File_lock_blocks_concurrent_package_operation_and_recovers_after_release()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-lock-test-{Guid.NewGuid():N}");
        var options = new RemoteOperationLockOptions { RootDirectory = root };
        var first = new FileRemoteOperationLockProvider(options, new FakeClock());
        var second = new FileRemoteOperationLockProvider(options, new FakeClock());
        IAsyncDisposable lease = await first.AcquireAsync(
            GitHubLifecycleTestSupport.Upstream.ToString(),
            new PackageIdentifier("Example.App"),
            CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<RemoteOperationLockException>(async () =>
                await second.AcquireAsync(
                    GitHubLifecycleTestSupport.Upstream.ToString(),
                    new PackageIdentifier("Example.App"),
                    CancellationToken.None));
        }
        finally
        {
            await lease.DisposeAsync();
        }

        await using IAsyncDisposable recovered = await second.AcquireAsync(
            GitHubLifecycleTestSupport.Upstream.ToString(),
            new PackageIdentifier("Example.App"),
            CancellationToken.None);
        await recovered.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    private static PullRequestObservation Observation(
        PullRequestInfo pullRequest,
        bool toolOwned)
        => new()
        {
            PullRequest = pullRequest,
            Author = pullRequest.HeadOwner,
            ToolOwned = toolOwned,
        };
}

internal sealed class FakeDeadVersionInspector : IDeadVersionInspector
{
    public required DeadVersionInspection Inspection { get; init; }

    public Task<DeadVersionInspection> InspectAsync(
        RepositoryCoordinates upstream,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Inspection with
        {
            PackageIdentifier = packageIdentifier,
            PackageVersion = packageVersion,
        });
    }
}
