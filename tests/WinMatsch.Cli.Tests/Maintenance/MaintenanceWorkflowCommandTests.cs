using System.Collections.Immutable;
using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.GitHub;
using WinMatsch.Workflows.GitHub;
using Xunit;

namespace WinMatsch.Cli.Tests.Maintenance;

public sealed class MaintenanceWorkflowCommandTests
{
    private const string Token = "test-token-value";
    private const string Upstream = "microsoft/winget-pkgs";
    private const string Fork = "octocat/winget-pkgs";

    [Fact]
    public async Task Sync_reports_no_action_when_the_fork_is_current()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["sync"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Result: noAction", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"Upstream: {Upstream} (master @ sha-upstream)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"Fork: {Fork} (master @ sha-upstream)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Submissions_reports_pending_journal_visibility_without_a_token()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        using var state = new TemporaryDirectory();
        CliHarness harness = CreateHarness(
            client,
            submissionJournals: new FileSubmissionJournalStore(
                new SubmissionJournalOptions { RootDirectory = state.Path }));
        harness.EnvironmentVariables.Remove("GITHUB_TOKEN");

        CliRunResult result = await harness.RunAsync(["submissions", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("\"pendingSubmissions\":[]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_dry_run_plans_without_mutation()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-behind");
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["sync", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Result: planned", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("syncFork", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Sync_applies_after_confirmation()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-behind");
        client.SyncedHeadSha = "sha-upstream";
        CliHarness harness = CreateHarness(client);
        harness.Interaction.EnqueueConfirm(true);

        CliRunResult result = await harness.RunAsync(["sync"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Result: succeeded", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal([$"syncFork:{Fork}:master"], client.Mutations);
        Assert.Single(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Sync_decline_leaves_the_remote_untouched()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-behind");
        CliHarness harness = CreateHarness(client);
        harness.Interaction.EnqueueConfirm(false);

        CliRunResult result = await harness.RunAsync(["sync"]);

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Contains("confirmation declined", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Sync_without_a_tty_and_without_yes_is_missing_input()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-behind");
        CliHarness harness = CreateHarness(client);
        harness.IsInputRedirected = true;

        CliRunResult result = await harness.RunAsync(["sync"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Contains("--yes", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Sync_in_ci_applies_only_with_yes()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-behind");
        client.SyncedHeadSha = "sha-upstream";
        CliHarness harness = CreateHarness(client);
        harness.EnvironmentVariables["CI"] = "true";

        CliRunResult withoutYes = await harness.RunAsync(["sync"]);
        CliRunResult withYes = await harness.RunAsync(["sync", "--yes"]);

        Assert.Equal(ExitCodes.MissingInput, withoutYes.ExitCode);
        Assert.Equal(ExitCodes.Success, withYes.ExitCode);
        Assert.Single(client.Mutations);
    }

    [Fact]
    public async Task Sync_escalates_diverged_forks_without_mutation()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-diverged");
        client.Comparison = new CompareResult("diverged", 2, 1, 3, []);
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["sync", "--yes"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("Result: conflict", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GH3001", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Sync_remote_failure_reports_uncertain_outcome()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-behind");
        client.SyncForkFailure = new GitHubApiException(
            "Merge failed.",
            System.Net.HttpStatusCode.Conflict,
            requestId: null);
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["sync", "--yes"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("Result: remoteFailure", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("remote outcome is uncertain", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_json_is_stable_and_never_prompts()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-behind");
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["sync", "--dry-run", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.StartsWith("{\"operation\":\"sync\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"result\":\"planned\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(harness.Interaction.Questions);
        Assert.DoesNotContain(Token, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_without_a_token_is_missing_input()
    {
        CliHarness harness = CreateHarness(CreateClient(forkSha: "sha-behind"));
        harness.EnvironmentVariables.Remove("GITHUB_TOKEN");

        CliRunResult result = await harness.RunAsync(["sync"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Contains("GITHUB_TOKEN", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_cancellation_maps_to_the_cancelled_exit_code()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-behind");
        CliHarness harness = CreateHarness(client);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        CliRunResult result = await harness.RunAsync(["sync", "--dry-run"], cancellation.Token);

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Cleanup_reports_candidates_in_plan_mode_without_mutation()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        AddStaleToolBranch(client);
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["cleanup", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Result: planned", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("deleteBranch", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("winmatsch/submissions/pkg/1.0.0", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Cleanup_never_deletes_and_escalates_in_apply_mode()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        AddStaleToolBranch(client);
        CliHarness harness = CreateHarness(client);
        harness.Interaction.EnqueueConfirm(true);

        CliRunResult result = await harness.RunAsync(["cleanup"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("Result: humanEscalationRequired", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GH3009", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Cleanup_ignores_user_branches_and_reports_no_action()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.Branches[Fork] =
        [
            new BranchState("feature/user-work", "sha-user", IsProtected: false),
        ];
        client.PullRequests.Add(MaintenancePullRequests.UserOwned(11));
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["cleanup"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Result: noAction", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Complete_inspects_open_tool_pull_requests_read_only()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        client.PullRequests.Add(MaintenancePullRequests.UserOwned(42));
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["complete"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("#41 [open] wait", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("#42 [unowned] none", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Complete_json_reports_statuses()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["complete", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.StartsWith("{\"operation\":\"complete\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"recommendedAction\":\"wait\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_apply_safe_requires_confirmation()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        CliHarness harness = CreateHarness(client);
        harness.IsInputRedirected = true;

        CliRunResult result = await harness.RunAsync(["complete", "--apply-safe"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Complete_apply_safe_posts_only_the_fixed_keep_alive_comment()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        var source = new ScriptedFeedbackSource(
        [
            .. MaintenancePullRequests.Observe(MaintenancePullRequests.ToolOwned(41))
                .Select(observation => observation with
                {
                    Comments =
                    [
                        new PullRequestCommentObservation(
                            "wingetbot",
                            "Please rerun the failed checks after the transient internal error.",
                            DateTimeOffset.UnixEpoch),
                    ],
                }),
        ]);
        CliHarness harness = CreateHarness(client, source);

        CliRunResult result = await harness.RunAsync(["complete", "--apply-safe", "--yes"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        string mutation = Assert.Single(client.Mutations);
        Assert.StartsWith("comment:41:", mutation, StringComparison.Ordinal);
        Assert.Contains("transient infrastructure failure", mutation, StringComparison.Ordinal);
        Assert.Contains("rerunChecks", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_apply_safe_dry_run_stays_read_only()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(["complete", "--apply-safe", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Empty(client.Mutations);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Complete_apply_safe_cancellation_maps_to_the_cancelled_exit_code()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        var source = new ScriptedFeedbackSource(
        [
            .. MaintenancePullRequests.Observe(MaintenancePullRequests.ToolOwned(41))
                .Select(observation => observation with
                {
                    Comments =
                    [
                        new PullRequestCommentObservation(
                            "wingetbot",
                            "Please rerun the failed checks after the transient internal error.",
                            DateTimeOffset.UnixEpoch),
                    ],
                }),
        ]);
        CliHarness harness = CreateHarness(client, source);
        using var cancellation = new CancellationTokenSource();
        client.OnComment = cancellation.Cancel;
        client.CommentFailure = new OperationCanceledException();

        CliRunResult result = await harness.RunAsync(
            ["complete", "--apply-safe", "--yes"],
            cancellation.Token);

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Contains("GH3207", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("cancelled during remote processing", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Complete_apply_safe_escalates_unknown_feedback()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        var source = new ScriptedFeedbackSource(
        [
            .. MaintenancePullRequests.Observe(MaintenancePullRequests.ToolOwned(41))
                .Select(observation => observation with
                {
                    Comments =
                    [
                        new PullRequestCommentObservation(
                            "wingetbot",
                            "Something entirely novel happened.",
                            DateTimeOffset.UnixEpoch),
                    ],
                }),
        ]);
        CliHarness harness = CreateHarness(client, source);

        CliRunResult result = await harness.RunAsync(["complete", "--apply-safe", "--yes"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("escalateToHuman", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GH3201", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    private static FakeMaintenanceGitHubClient CreateClient(string forkSha)
    {
        var client = new FakeMaintenanceGitHubClient();
        client.DefaultBranches[Upstream] = new BranchState("master", "sha-upstream", IsProtected: true);
        client.DefaultBranches[Fork] = new BranchState("master", forkSha, IsProtected: false);
        return client;
    }

    private static void AddStaleToolBranch(FakeMaintenanceGitHubClient client)
    {
        client.Branches[Fork] =
        [
            new BranchState("winmatsch/submissions/pkg/1.0.0", "sha-tool", IsProtected: false),
            new BranchState("feature/user-work", "sha-user", IsProtected: false),
        ];
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(31, state: PullRequestState.Closed));
    }

    private static CliHarness CreateHarness(
        FakeMaintenanceGitHubClient client,
        IPullRequestFeedbackSource? source = null,
        ISubmissionJournalStore? submissionJournals = null)
    {
        var harness = new CliHarness();
        harness.EnvironmentVariables["GITHUB_TOKEN"] = Token;
        harness.Modules.Add(new MaintenanceCommandModule(
            clientFactory: _ => client,
            sourceFactory: source is null ? null : (_, _) => source,
            submissionJournals: submissionJournals));
        return harness;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"winmatsch-cli-submissions-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ScriptedFeedbackSource : IPullRequestFeedbackSource
    {
        private readonly ImmutableArray<PullRequestObservation> _observations;

        public ScriptedFeedbackSource(ImmutableArray<PullRequestObservation> observations)
        {
            _observations = observations;
        }

        public Task<ImmutableArray<PullRequestObservation>> GetOpenToolPullRequestsAsync(
            RepositoryCoordinates upstream,
            CancellationToken cancellationToken)
            => Task.FromResult(_observations);
    }
}
