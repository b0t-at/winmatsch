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
    public async Task Sync_failure_returns_uncertain_remote_state()
    {
        var client = new FakeGitHubClient { FailMutation = "sync" };
        client.SetForkHead("cccccccccccccccccccccccccccccccccccccccc");

        GitHubMaintenanceResult result = await new GitHubMaintenanceWorkflow(client, new FakeClock())
            .SyncAsync(new(
                GitHubLifecycleTestSupport.Upstream,
                GitHubLifecycleTestSupport.Fork,
                WorkflowExecutionMode.Apply,
                "sync-failure"));

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.Equal(RemoteOperationKind.SyncFork, result.RemoteState.LastAttemptedOperation);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
    }

    [Fact]
    public async Task Cleanup_identifies_owned_branch_but_refuses_racey_unconditional_delete()
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

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        Assert.Empty(client.Mutations);
        Assert.Single(result.Plan.Operations);
        Assert.Contains("owned", result.Plan.Operations[0].Target, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3009");
    }

    [Fact]
    public async Task Cleanup_identifies_only_fresh_prless_tool_reservations_for_manual_deletion()
    {
        var client = new FakeGitHubClient();
        string reservation = new DefaultGitHubBranchNameGenerator().Create(new(
            new PackageIdentifier("Example.App"),
            new PackageVersion("2.0.0"),
            GitHubManifestOperation.Update,
            null,
            "main",
            GitHubLifecycleTestSupport.UpstreamSha,
            "operation-1"));
        client.AddBranch(
            GitHubLifecycleTestSupport.Fork,
            reservation,
            GitHubLifecycleTestSupport.UpstreamSha);
        client.AddBranch(
            GitHubLifecycleTestSupport.Fork,
            "winmatsch/submissions/update/example-app/unknown-human-branch",
            GitHubLifecycleTestSupport.UpstreamSha);

        GitHubMaintenanceResult result = await new GitHubMaintenanceWorkflow(
                client,
                new FakeClock())
            .CleanupAsync(new(
                GitHubLifecycleTestSupport.Upstream,
                GitHubLifecycleTestSupport.Fork,
                WorkflowExecutionMode.Apply,
                "cleanup-prless"));

        Assert.Equal(GitHubLifecycleResultCode.HumanEscalationRequired, result.Code);
        PlannedRemoteOperation operation = Assert.Single(result.Plan.Operations);
        Assert.Contains(reservation, operation.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-human-branch", operation.Target, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Cleanup_never_treats_markerless_pr_branch_as_prless()
    {
        var client = new FakeGitHubClient();
        string reservation = new DefaultGitHubBranchNameGenerator().Create(new(
            new PackageIdentifier("Example.App"),
            new PackageVersion("2.0.0"),
            GitHubManifestOperation.Update,
            null,
            "main",
            GitHubLifecycleTestSupport.UpstreamSha,
            "operation-1"));
        client.AddBranch(
            GitHubLifecycleTestSupport.Fork,
            reservation,
            GitHubLifecycleTestSupport.UpstreamSha);
        client.AddPullRequest(GitHubLifecycleTestSupport.PullRequest(
            50,
            author: GitHubLifecycleTestSupport.Fork.Owner,
            branch: reservation) with
        {
            Body = "Human-authored pull request without tool ownership marker.",
        });

        GitHubMaintenanceResult result = await new GitHubMaintenanceWorkflow(
                client,
                new FakeClock())
            .CleanupAsync(new(
                GitHubLifecycleTestSupport.Upstream,
                GitHubLifecycleTestSupport.Fork,
                WorkflowExecutionMode.Plan,
                "cleanup-human-pr"));

        Assert.Equal(GitHubLifecycleResultCode.NoAction, result.Code);
        Assert.Empty(result.Plan.Operations);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Cleanup_rejects_branch_with_marked_closed_pr_and_additional_markerless_pr()
    {
        var client = new FakeGitHubClient();
        string branch = "winmatsch/update/example-app/shared";
        PullRequestInfo owned = GitHubLifecycleTestSupport.PullRequest(
            51,
            PullRequestState.Closed,
            GitHubLifecycleTestSupport.Fork.Owner,
            branch);
        PullRequestInfo markerless = GitHubLifecycleTestSupport.PullRequest(
            52,
            PullRequestState.Open,
            GitHubLifecycleTestSupport.Fork.Owner,
            branch) with
        {
            Body = "Human-authored pull request.",
        };
        client.AddBranch(GitHubLifecycleTestSupport.Fork, branch, owned.HeadSha);
        client.AddPullRequest(owned);
        client.AddPullRequest(markerless);

        GitHubMaintenanceResult result = await new GitHubMaintenanceWorkflow(
                client,
                new FakeClock())
            .CleanupAsync(new(
                GitHubLifecycleTestSupport.Upstream,
                GitHubLifecycleTestSupport.Fork,
                WorkflowExecutionMode.Plan,
                "cleanup-shared"));

        Assert.Equal(GitHubLifecycleResultCode.NoAction, result.Code);
        Assert.Empty(result.Plan.Operations);
        Assert.Empty(client.Mutations);
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
        PullRequestInfo replacement = GitHubLifecycleTestSupport.PullRequest(15) with
        {
            Body = GitHubLifecycleTestSupport.PullRequest(15).Body + "\nSupersedes: #14",
            HeadBranch = "winmatsch/update/example-app/replacement",
        };
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
        Assert.Contains(closed.Audit, audit => audit.Message.Contains("superseded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Superseded_close_failure_returns_comment_audit_and_uncertain_close_state()
    {
        var client = new FakeGitHubClient { FailMutation = "close" };
        PullRequestInfo oldPullRequest = GitHubLifecycleTestSupport.PullRequest(
            16,
            branch: "winmatsch/update/example-app/old");
        PullRequestInfo replacement = GitHubLifecycleTestSupport.PullRequest(17) with
        {
            Body = GitHubLifecycleTestSupport.PullRequest(17).Body + "\nSupersedes: #16",
            HeadBranch = "winmatsch/update/example-app/replacement",
        };
        client.AddPullRequest(oldPullRequest);
        client.AddPullRequest(replacement);

        GitHubMaintenanceResult result = await new GitHubMaintenanceWorkflow(client, new FakeClock())
            .CloseSupersededAsync(
                GitHubLifecycleTestSupport.Upstream,
                Observation(oldPullRequest, toolOwned: true),
                replacement,
                "supersede-partial");

        Assert.Equal(GitHubLifecycleResultCode.RemoteFailure, result.Code);
        Assert.Single(result.Audit);
        Assert.Equal(RemoteOperationKind.ClosePullRequest, result.RemoteState.LastAttemptedOperation);
        Assert.True(result.RemoteState.RemoteOutcomeUncertain);
        Assert.Equal(["comment"], client.Mutations);
    }

    [Fact]
    public async Task Forged_replacement_from_another_owner_cannot_close_old_pr()
    {
        var client = new FakeGitHubClient();
        PullRequestInfo oldPullRequest = GitHubLifecycleTestSupport.PullRequest(
            18,
            branch: "winmatsch/update/example-app/old");
        PullRequestInfo replacement = GitHubLifecycleTestSupport.PullRequest(
            19,
            author: "attacker",
            branch: "winmatsch/update/example-app/replacement") with
        {
            Body = GitHubLifecycleTestSupport.PullRequest(19).Body + "\nSupersedes: #18",
        };
        client.AddPullRequest(oldPullRequest);
        client.AddPullRequest(replacement);

        GitHubMaintenanceResult result = await new GitHubMaintenanceWorkflow(client, new FakeClock())
            .CloseSupersededAsync(
                GitHubLifecycleTestSupport.Upstream,
                Observation(oldPullRequest, toolOwned: true),
                replacement,
                "supersede-forged");

        Assert.Equal(GitHubLifecycleResultCode.Conflict, result.Code);
        Assert.Empty(client.Mutations);
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

    [Fact]
    public async Task Lock_identity_resolver_collapses_process_local_aliases()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-lock-alias-test-{Guid.NewGuid():N}");
        var resolver = new FixedLockIdentityResolver();
        var first = new FileRemoteOperationLockProvider(
            new RemoteOperationLockOptions { RootDirectory = root },
            new FakeClock(),
            resolver);
        var second = new FileRemoteOperationLockProvider(
            new RemoteOperationLockOptions { RootDirectory = root },
            new FakeClock(),
            resolver);
        await using IAsyncDisposable lease = await first.AcquireAsync(
            "real-path",
            new PackageIdentifier("Example.App"),
            CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<RemoteOperationLockException>(async () =>
                await second.AcquireAsync(
                    "symlink-path",
                    new PackageIdentifier("Example.App"),
                    CancellationToken.None));
        }
        finally
        {
            await lease.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Lock_cleanup_removes_expired_unlocked_files_but_cannot_break_held_exclusion()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-lock-cleanup-test-{Guid.NewGuid():N}");
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow.AddYears(2) };
        var options = new RemoteOperationLockOptions
        {
            RootDirectory = root,
            UnusedFileRetention = TimeSpan.FromDays(1),
        };
        var provider = new FileRemoteOperationLockProvider(options, clock);
        IAsyncDisposable held = await provider.AcquireAsync(
            "upstream/held",
            new PackageIdentifier("Example.App"),
            CancellationToken.None);
        try
        {
            await using IAsyncDisposable other = await provider.AcquireAsync(
                "upstream/other",
                new PackageIdentifier("Example.App"),
                CancellationToken.None);
            Assert.Equal(2, Directory.EnumerateFiles(root, "*.lock").Count());
            await Assert.ThrowsAsync<RemoteOperationLockException>(async () =>
                await provider.AcquireAsync(
                    "upstream/held",
                    new PackageIdentifier("Example.App"),
                    CancellationToken.None));
        }
        finally
        {
            await held.DisposeAsync();
        }

        await using (IAsyncDisposable sweep = await provider.AcquireAsync(
                         "upstream/sweep",
                         new PackageIdentifier("Example.App"),
                         CancellationToken.None))
        {
            Assert.Single(Directory.EnumerateFiles(root, "*.lock"));
        }

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

internal sealed class FixedLockIdentityResolver : IRemoteLockIdentityResolver
{
    public string Resolve(string repository)
        => repository is "real-path" or "symlink-path"
            ? "canonical-path"
            : repository;
}
