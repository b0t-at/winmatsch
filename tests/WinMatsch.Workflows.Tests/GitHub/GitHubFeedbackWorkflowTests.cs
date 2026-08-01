using WinMatsch.GitHub;
using WinMatsch.Workflows.GitHub;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class GitHubFeedbackWorkflowTests
{
    [Theory]
    [InlineData("Duplicate entry found", FeedbackClassification.DuplicateEntry)]
    [InlineData("Installer hash mismatch", FeedbackClassification.HashMismatch)]
    [InlineData("Dependency infrastructure unavailable", FeedbackClassification.DependencyInfrastructureOutage)]
    [InlineData("Transient internal error; please rerun", FeedbackClassification.TransientInternalError)]
    [InlineData("Unrecognized reviewer note", FeedbackClassification.Unknown)]
    public void Feedback_signatures_are_classified(string text, FeedbackClassification expected)
    {
        PullRequestObservation observation = Observation(text);

        FeedbackClassification classification = GitHubFeedbackWorkflow.Classify(observation);

        Assert.Equal(expected, classification);
    }

    [Fact]
    public void Untrusted_comment_cannot_trigger_automated_repair()
    {
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            Comments =
            [
                new("untrusted-contributor", "Installer hash mismatch", DateTimeOffset.UtcNow),
            ],
        };

        FeedbackClassification classification = GitHubFeedbackWorkflow.Classify(observation);

        Assert.Equal(FeedbackClassification.None, classification);
    }

    [Fact]
    public async Task Infrastructure_failure_queues_retry_and_never_mutates_manifests()
    {
        var client = new FakeGitHubClient();
        var repairs = new FakeRepairPlanner();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            repairs,
            new FakeClock());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Dependency service unavailable")],
            new FeedbackPolicy { ApplyKnownSafeResponses = true });

        Assert.Equal(PullRequestLifecycleAction.RerunChecks, result.Statuses[0].RecommendedAction);
        Assert.Single(result.RetryMetadata);
        Assert.Equal(0, repairs.Calls);
        Assert.Equal(["comment"], client.Mutations);
    }

    [Fact]
    public async Task Infrastructure_response_failure_escalates_instead_of_throwing()
    {
        var client = new FakeGitHubClient { FailMutation = "comment" };
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Dependency service unavailable")],
            new FeedbackPolicy { ApplyKnownSafeResponses = true });

        Assert.Equal(
            PullRequestLifecycleAction.EscalateToHuman,
            result.Statuses[0].RecommendedAction);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH3207");
    }

    [Fact]
    public async Task Approved_repair_is_forced_through_submission_planning_and_preflight_contract()
    {
        var client = new FakeGitHubClient();
        var repairs = new FakeRepairPlanner
        {
            Repair = GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Apply) with
            {
                SupersedesPullRequestNumber = 20,
            },
        };
        var preflight = new FakePreflight();
        PullRequestObservation observation = Observation("Installer hash mismatch") with
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(
                20,
                branch: "winmatsch/update/example-app/old"),
        };
        client.AddPullRequest(observation.PullRequest);
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client, preflight),
            repairs,
            new FakeClock());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [observation]);

        Assert.Equal(PullRequestLifecycleAction.RepairManifest, result.Statuses[0].RecommendedAction);
        Assert.Equal(1, repairs.Calls);
        Assert.Equal(1, preflight.BoundaryCalls);
        Assert.Equal(["branch", "commit", "pull-request", "comment", "close"], client.Mutations);
        Assert.Single(result.RemoteStates);
        Assert.True(result.RemoteStates[0].State.CommitCreated);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Unknown_feedback_escalates_before_stale_window_without_unsafe_action()
    {
        var client = new FakeGitHubClient();
        var workflow = new GitHubFeedbackWorkflow(
            client,
            GitHubLifecycleTestSupport.Workflow(client),
            new FakeRepairPlanner(),
            new FakeClock());

        FeedbackResult result = await workflow.ProcessAsync(
            GitHubLifecycleTestSupport.Upstream,
            [Observation("Reviewer asks an unknown question")],
            new FeedbackPolicy { StaleEscalationWindow = TimeSpan.FromDays(30) });

        Assert.Equal(PullRequestLifecycleAction.EscalateToHuman, result.Statuses[0].RecommendedAction);
        Assert.Contains("before", result.Statuses[0].Reason, StringComparison.Ordinal);
        Assert.Empty(client.Mutations);
    }

    private static PullRequestObservation Observation(string text)
        => new()
        {
            PullRequest = GitHubLifecycleTestSupport.PullRequest(20),
            Author = "contributor",
            ToolOwned = true,
            Comments =
            [
                new("wingetbot", text, new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)),
            ],
        };
}

internal sealed class FakeRepairPlanner : IApprovedRepairPlanner
{
    public int Calls { get; private set; }

    public GitHubSubmissionRequest? Repair { get; init; }

    public Task<GitHubSubmissionRequest?> PlanApprovedRepairAsync(
        PullRequestObservation pullRequest,
        FeedbackClassification classification,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(Repair);
    }
}
