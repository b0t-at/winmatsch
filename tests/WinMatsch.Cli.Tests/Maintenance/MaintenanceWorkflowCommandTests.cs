using System.Collections.Immutable;
using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Cli.Commands.Mutations;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Configuration;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
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

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
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
        Assert.StartsWith(
            "{\"schemaVersion\":\"1.0\",\"operation\":\"sync\"",
            result.StandardOutput,
            StringComparison.Ordinal);
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
        var source = new ScriptedFeedbackSource(
        [
            .. MaintenancePullRequests.Observe(MaintenancePullRequests.ToolOwned(41))
                .Select(observation => observation with
                {
                    Comments =
                    [
                        new PullRequestCommentObservation(
                            "wingetbot",
                            "Please rerun after the transient infrastructure error.",
                            DateTimeOffset.UnixEpoch),
                    ],
                }),
        ]);
        CliHarness harness = CreateHarness(client, source);

        CliRunResult result = await harness.RunAsync(["complete", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.StartsWith(
            "{\"schemaVersion\":\"1.0\",\"operation\":\"complete\"",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("\"recommendedAction\":\"rerunChecks\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_apply_safe_requires_confirmation()
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
                            "Please rerun after the transient infrastructure error.",
                            DateTimeOffset.UnixEpoch),
                    ],
                }),
        ]);
        CliHarness harness = CreateHarness(client, source);
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
        var source = new ScriptedFeedbackSource(
        [
            .. MaintenancePullRequests.Observe(MaintenancePullRequests.ToolOwned(41))
                .Select(observation => observation with
                {
                    Comments =
                    [
                        new PullRequestCommentObservation(
                            "wingetbot",
                            "Please rerun after the transient infrastructure error.",
                            DateTimeOffset.UnixEpoch),
                    ],
                }),
        ]);
        CliHarness harness = CreateHarness(client, source);

        CliRunResult result = await harness.RunAsync(["complete", "--apply-safe", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Empty(client.Mutations);
        Assert.Empty(harness.Interaction.Questions);
        Assert.Contains("rerunChecks", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_apply_safe_reports_false_when_nothing_was_actionable()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        CliHarness harness = CreateHarness(client);

        CliRunResult result = await harness.RunAsync(
            ["complete", "--apply-safe", "--yes", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(
            "\"appliedKnownSafeResponses\":false",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Complete_reports_partial_application_when_one_response_fails()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(42));
        client.CommentFailures[42] = new GitHubApiException("failed");
        CliHarness harness = CreateHarness(
            client,
            TransientFeedbackSource(41, 42));

        CliRunResult result = await harness.RunAsync(
            ["complete", "--apply-safe", "--yes", "--format", "json"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains(
            "\"appliedKnownSafeResponses\":true",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("\"number\":41", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"number\":42", result.StandardOutput, StringComparison.Ordinal);
        Assert.Single(client.Mutations);
    }

    [Fact]
    public async Task Queued_allowlisted_repair_remains_pending_and_unapplied()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        PullRequestInfo pullRequest = MaintenancePullRequests.ToolOwned(41);
        client.PullRequests.Add(pullRequest);
        var source = new ScriptedFeedbackSource(
        [
            Assert.Single(MaintenancePullRequests.Observe(pullRequest)) with
            {
                Labels = ["hash-mismatch"],
            },
        ]);
        var store = new InMemoryFeedbackStateStore();
        CliHarness harness = CreateHarness(client, source, store);

        CliRunResult result = await harness.RunAsync(
            ["complete", "--apply-safe", "--yes", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(
            "\"appliedKnownSafeResponses\":false",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"state\":\"awaitingApprovedRepair\"",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(
            FeedbackWorkState.AwaitingApprovedRepair,
            Assert.Single(store.Pending).State);
        Assert.Empty(client.Mutations);
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

    [Fact]
    public async Task Complete_can_schedule_durable_retry_state_without_remote_writes()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        var store = new InMemoryFeedbackStateStore();
        CliHarness harness = CreateHarness(
            client,
            TransientFeedbackSource(41),
            store);

        CliRunResult result = await harness.RunAsync(
            ["complete", "--schedule-pending", "--yes"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(41, Assert.Single(store.Pending).PullRequestNumber);
        Assert.Contains("Pending retries:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("#41", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Schedule_persistence_failure_returns_operation_failure()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        var store = new InMemoryFeedbackStateStore
        {
            PersistFailure = new IOException("state unavailable"),
        };
        CliHarness harness = CreateHarness(
            client,
            TransientFeedbackSource(41),
            store);

        CliRunResult result = await harness.RunAsync(
            ["complete", "--schedule-pending", "--yes", "--format", "json"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("GH3208", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_replays_only_due_durable_entries_in_json_without_prompts()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(42));
        var store = new InMemoryFeedbackStateStore
        {
            Pending =
            [
                PendingItem(41, DateTimeOffset.UnixEpoch, "transient-internal-error"),
                PendingItem(42, DateTimeOffset.MaxValue, "transient-internal-error"),
            ],
        };
        CliHarness harness = CreateHarness(
            client,
            TransientFeedbackSource(41, 42),
            store);

        CliRunResult result = await harness.RunAsync(
        [
            "complete",
            "--replay-pending",
            "--apply-safe",
            "--yes",
            "--format",
            "json",
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.StartsWith(
            "comment:41:",
            Assert.Single(client.Mutations),
            StringComparison.Ordinal);
        Assert.Contains("\"pendingRetries\":[", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"pullRequestNumber\":41", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "\"learnedOverrideSignal\":\"transient-internal-error\"",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Replay_requires_explicit_confirmation_without_a_tty()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        var store = new InMemoryFeedbackStateStore
        {
            Pending =
            [
                PendingItem(41, DateTimeOffset.UnixEpoch, null),
            ],
        };
        CliHarness harness = CreateHarness(
            client,
            TransientFeedbackSource(41),
            store);
        harness.IsInputRedirected = true;

        CliRunResult result = await harness.RunAsync(
            ["complete", "--replay-pending", "--apply-safe"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Empty(client.Mutations);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Allowlisted_repair_factory_runs_local_preflight_and_binds_superseded_pr()
    {
        var workflow = new FakeMutationWorkflow();
        GitHubSubmissionRequest? planned = null;
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(async context =>
        {
            var planner = new AllowlistedApprovedRepairPlanner(
                context,
                new Dictionary<long, string> { [41] = "approved-directory" },
                new FixedMutationWorkflowFactory(workflow),
                new FakeManifestLoader());
            PullRequestInfo pullRequest = MaintenancePullRequests.ToolOwned(41) with
            {
                Body = "<!-- winmatsch:package=Example.App;version=1.0 -->",
            };
            planned = await planner.PlanApprovedRepairAsync(
                Assert.Single(MaintenancePullRequests.Observe(pullRequest)),
                FeedbackClassification.HashMismatch,
                context.CancellationToken);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotNull(planned);
        Assert.Equal(WorkflowExecutionMode.Apply, planned!.ExecutionMode);
        Assert.Equal(41, planned.SupersedesPullRequestNumber);
        Assert.Equal(
            WorkflowExecutionMode.Plan,
            Assert.Single(workflow.Requests).ExecutionMode);
    }

    [Fact]
    public async Task Allowlisted_repair_planner_owns_workflow_until_disposed()
    {
        var workflow = new DisposableMutationWorkflow();
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(async context =>
        {
            using var planner = new AllowlistedApprovedRepairPlanner(
                context,
                new Dictionary<long, string> { [41] = "approved-directory" },
                new FixedMutationWorkflowFactory(workflow),
                new FakeManifestLoader());
            PullRequestInfo pullRequest = MaintenancePullRequests.ToolOwned(41) with
            {
                Body = "<!-- winmatsch:package=Example.App;version=1.0 -->",
            };
            _ = await planner.PlanApprovedRepairAsync(
                Assert.Single(MaintenancePullRequests.Observe(pullRequest)),
                FeedbackClassification.HashMismatch,
                context.CancellationToken);
            Assert.False(workflow.Disposed);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(workflow.Disposed);
    }

    [Fact]
    public async Task Allowlisted_repair_never_runs_for_arbitrary_classifications()
    {
        var workflow = new FakeMutationWorkflow();
        GitHubSubmissionRequest? planned = null;
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(async context =>
        {
            var planner = new AllowlistedApprovedRepairPlanner(
                context,
                new Dictionary<long, string> { [41] = "approved-directory" },
                new FixedMutationWorkflowFactory(workflow),
                new FakeManifestLoader());
            planned = await planner.PlanApprovedRepairAsync(
                Assert.Single(MaintenancePullRequests.Observe(
                    MaintenancePullRequests.ToolOwned(41))),
                FeedbackClassification.TransientInternalError,
                context.CancellationToken);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Null(planned);
        Assert.Empty(workflow.Requests);
    }

    [Fact]
    public async Task Allowlisted_repair_preserves_replace_operation()
    {
        string output = Directory.CreateTempSubdirectory(
            "winmatsch-approved-replace-").FullName;
        string deletionPath =
            "manifests/e/Example/App/0.9/Example.App.yaml";
        string deletionFile = Path.Combine(
            output,
            deletionPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(deletionFile)!);
        await File.WriteAllTextAsync(deletionFile, "old: true\n");
        var workflow = new FakeMutationWorkflow();
        GitHubSubmissionRequest? planned = null;
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(async context =>
        {
            var planner = new AllowlistedApprovedRepairPlanner(
                context,
                new Dictionary<long, string> { [41] = "approved-directory" },
                new FixedMutationWorkflowFactory(workflow),
                new FakeManifestLoader());
            PullRequestInfo pullRequest = MaintenancePullRequests.ToolOwned(41) with
            {
                Body = "<!-- winmatsch:package=Example.App;version=1.0 -->\n"
                    + "Operation: Replace\n"
                    + "## Changes\n"
                    + "- Delete: `manifests/a/Attacker/App/9.9/Attacker.App.yaml`",
            };
            PullRequestObservation observation =
                Assert.Single(MaintenancePullRequests.Observe(pullRequest)) with
                {
                    ChangedFiles =
                    [
                        new(deletionPath, Status: PullRequestFileStatus.Removed),
                    ],
                    EvidenceHeadSha = pullRequest.HeadSha,
                    EvidenceBaseSha = pullRequest.BaseSha,
                };
            planned = await planner.PlanApprovedRepairAsync(
                observation,
                FeedbackClassification.HashMismatch,
                context.CancellationToken);
            return ExitCodes.Success;
        }));

        try
        {
            CliRunResult result = await harness.RunAsync(
                ["probe", "--output", output]);

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.Equal(GitHubManifestOperation.Replace, planned!.Operation);
            Assert.True(planned.Policy.ReplacePreviousVersion);
            Assert.Equal("0.9", planned.Policy.PreviousVersion!.Value);
            Assert.Contains(
                planned.LocalPlan.FileChanges,
                change => change.Kind == PlannedChangeKind.Delete
                    && change.RepositoryPath == deletionPath);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Replacement_repair_rejects_body_deletions_without_head_bound_evidence()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(async context =>
        {
            using var planner = new AllowlistedApprovedRepairPlanner(
                context,
                new Dictionary<long, string> { [41] = "approved-directory" },
                new FixedMutationWorkflowFactory(new FakeMutationWorkflow()),
                new FakeManifestLoader());
            PullRequestInfo pullRequest = MaintenancePullRequests.ToolOwned(41) with
            {
                Body = "<!-- winmatsch:package=Example.App;version=1.0 -->\n"
                    + "Operation: Replace\n"
                    + "- Delete: `manifests/e/Example/App/0.9/Example.App.yaml`",
            };

            CliOperationException exception = await Assert.ThrowsAsync<CliOperationException>(
                () => planner.PlanApprovedRepairAsync(
                    Assert.Single(MaintenancePullRequests.Observe(pullRequest)),
                    FeedbackClassification.HashMismatch,
                    context.CancellationToken));
            Assert.Contains(
                "authoritative changed-file evidence",
                exception.Message,
                StringComparison.Ordinal);
            return ExitCodes.Success;
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
    }

    [Fact]
    public async Task Replay_cancellation_maps_to_130_and_preserves_pending_state()
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        client.PullRequests.Add(MaintenancePullRequests.ToolOwned(41));
        var store = new InMemoryFeedbackStateStore
        {
            Pending =
            [
                PendingItem(41, DateTimeOffset.UnixEpoch, null),
            ],
        };
        CliHarness harness = CreateHarness(
            client,
            TransientFeedbackSource(41),
            store);
        using var cancellation = new CancellationTokenSource();
        client.OnComment = cancellation.Cancel;
        client.CommentFailure = new OperationCanceledException();

        CliRunResult result = await harness.RunAsync(
            ["complete", "--replay-pending", "--apply-safe", "--yes"],
            cancellation.Token);

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Equal(41, Assert.Single(store.Pending).PullRequestNumber);
        Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Feedback_retry_state_round_trips_durably()
    {
        string directory = Directory.CreateTempSubdirectory("winmatsch-feedback-state-").FullName;
        string stateRoot = Path.Combine(directory, "feedback");
        try
        {
            var store = new FileFeedbackStateStore(stateRoot);
            FeedbackWorkItem expected = PendingItem(
                41,
                new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
                "hash-mismatch",
                FeedbackClassification.HashMismatch);

            await store.PersistAsync(expected, CancellationToken.None);
            ImmutableArray<FeedbackWorkItem> actual = await store.GetPendingAsync(
                Upstream,
                DateTimeOffset.MaxValue,
                CancellationToken.None);
            Assert.Collection(
                actual,
                item => Assert.Equal(expected, item));
            Assert.NotEmpty(Directory.EnumerateFiles(stateRoot, "*.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Feedback_downloader_uses_the_default_cache_when_unconfigured()
    {
        string? cacheDirectory = await CaptureFeedbackCacheDirectoryAsync();

        Assert.Equal(DefaultCacheDirectory(), cacheDirectory);
    }

    [Fact]
    public async Task Feedback_downloader_uses_the_configured_cache_directory()
    {
        string? cacheDirectory = await CaptureFeedbackCacheDirectoryAsync(
            configure: harness =>
            {
                harness.Files[DefaultConfigPath(harness)] =
                    "cache:\n  directory: file-cache\n";
            });

        Assert.Equal("file-cache", cacheDirectory);
    }

    [Fact]
    public async Task Feedback_downloader_environment_cache_directory_overrides_config()
    {
        string? cacheDirectory = await CaptureFeedbackCacheDirectoryAsync(
            configure: harness =>
            {
                harness.Files[DefaultConfigPath(harness)] =
                    "cache:\n  directory: file-cache\n";
                harness.EnvironmentVariables["WINMATSCH_CACHE_DIRECTORY"] = "env-cache";
            });

        Assert.Equal("env-cache", cacheDirectory);
    }

    [Fact]
    public async Task Feedback_downloader_cli_cache_directory_overrides_environment()
    {
        string? cacheDirectory = await CaptureFeedbackCacheDirectoryAsync(
            configure: harness =>
            {
                harness.EnvironmentVariables["WINMATSCH_CACHE_DIRECTORY"] = "env-cache";
            },
            arguments: ["complete", "--cache-directory", "cli-cache"]);

        Assert.Equal("cli-cache", cacheDirectory);
    }

    [Fact]
    public async Task Feedback_downloader_no_cache_option_disables_cache()
    {
        string? cacheDirectory = await CaptureFeedbackCacheDirectoryAsync(
            configure: harness =>
            {
                harness.Files[DefaultConfigPath(harness)] =
                    "cache:\n  directory: file-cache\n";
                harness.EnvironmentVariables["WINMATSCH_CACHE_DIRECTORY"] = "env-cache";
            },
            arguments: ["complete", "--cache-directory", "cli-cache", "--no-cache"]);

        Assert.Null(cacheDirectory);
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
        IFeedbackStateStore? feedbackStateStore = null,
        IApprovedRepairPlannerFactory? repairPlannerFactory = null,
        ISubmissionJournalStore? submissionJournals = null,
        Func<DownloaderOptions, InstallerDownloader>? feedbackDownloaderFactory = null)
    {
        var harness = new CliHarness();
        harness.EnvironmentVariables["GITHUB_TOKEN"] = Token;
        harness.Modules.Add(new MaintenanceCommandModule(
            clientFactory: _ => client,
            sourceFactory: source is null
                ? (gitHub, forkOwner) => new ToolPullRequestObservationSource(gitHub, forkOwner)
                : (_, _) => source,
            repairPlannerFactory: repairPlannerFactory,
            feedbackStateStore: feedbackStateStore,
            submissionJournals: submissionJournals,
            feedbackDownloaderFactory: feedbackDownloaderFactory));
        return harness;
    }

    private static async Task<string?> CaptureFeedbackCacheDirectoryAsync(
        Action<CliHarness>? configure = null,
        string[]? arguments = null)
    {
        FakeMaintenanceGitHubClient client = CreateClient(forkSha: "sha-upstream");
        string? cacheDirectory = null;
        CliHarness harness = CreateHarness(
            client,
            feedbackDownloaderFactory: options =>
            {
                cacheDirectory = options.CacheDirectory;
                return new InstallerDownloader(options);
            });
        configure?.Invoke(harness);

        CliRunResult result = await harness.RunAsync(arguments ?? ["complete"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        return cacheDirectory;
    }

    private static string DefaultConfigPath(CliHarness harness)
        => Path.Combine(harness.HomeDirectory!, ".config", "winmatsch", "config.yaml");

    private static string DefaultCacheDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "winmatsch",
            "downloads");

    private static ScriptedFeedbackSource TransientFeedbackSource(params long[] numbers)
        => new(
        [
            .. numbers.Select(number =>
                MaintenancePullRequests.Observe(MaintenancePullRequests.ToolOwned(number))
                    .Single() with
                {
                    Comments =
                    [
                        new PullRequestCommentObservation(
                            "wingetbot",
                            "Please rerun after the transient infrastructure error.",
                            DateTimeOffset.UnixEpoch),
                    ],
                }),
        ]);

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

    private static FeedbackWorkItem PendingItem(
        long pullRequestNumber,
        DateTimeOffset retryAfter,
        string? learnedOverrideSignal,
        FeedbackClassification classification =
            FeedbackClassification.TransientInternalError)
        => new(
            Upstream,
            pullRequestNumber,
            classification,
            FeedbackWorkState.RetryScheduled,
            DateTimeOffset.UnixEpoch,
            retryAfter,
            learnedOverrideSignal,
            "test pending feedback");

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

    private sealed class InMemoryFeedbackStateStore : IFeedbackStateStore
    {
        public ImmutableArray<FeedbackWorkItem> Pending { get; set; } = [];

        public Exception? PersistFailure { get; init; }

        public Task PersistAsync(
            FeedbackWorkItem item,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PersistFailure is not null)
            {
                return Task.FromException(PersistFailure);
            }

            Pending =
            [
                .. Pending.Where(existing =>
                    !string.Equals(
                        existing.Repository,
                        item.Repository,
                        StringComparison.OrdinalIgnoreCase)
                    || existing.PullRequestNumber != item.PullRequestNumber),
                item,
            ];
            return Task.CompletedTask;
        }

        public Task<ImmutableArray<FeedbackWorkItem>> GetPendingAsync(
            string repository,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ImmutableArray<FeedbackWorkItem>>(
            [
                .. Pending.Where(item =>
                    string.Equals(
                        item.Repository,
                        repository,
                        StringComparison.OrdinalIgnoreCase)
                    && item.State is FeedbackWorkState.AwaitingApprovedRepair
                        or FeedbackWorkState.RetryScheduled
                    && item.RetryAfter.GetValueOrDefault(DateTimeOffset.MinValue) <= now),
            ]);
        }
    }

    private sealed class DisposableMutationWorkflow : IMutationWorkflow, IDisposable
    {
        public bool Disposed { get; private set; }

        public Task<WorkflowOperationResult> ExecuteAsync(
            WorkflowOperationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(FakeMutationWorkflow.Result(request));

        public void Dispose()
        {
            Disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
