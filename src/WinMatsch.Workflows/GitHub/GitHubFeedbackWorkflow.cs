using System.Collections.Immutable;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public sealed class GitHubFeedbackWorkflow
{
    private readonly IGitHubRepositoryClient _gitHub;
    private readonly GitHubLifecycleWorkflow _submissions;
    private readonly IApprovedRepairPlanner _repairs;
    private readonly IWorkflowClock _clock;

    public GitHubFeedbackWorkflow(
        IGitHubRepositoryClient gitHub,
        GitHubLifecycleWorkflow submissions,
        IApprovedRepairPlanner repairs,
        IWorkflowClock? clock = null)
    {
        _gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        _submissions = submissions ?? throw new ArgumentNullException(nameof(submissions));
        _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
        _clock = clock ?? new SystemWorkflowClock();
    }

    public async Task<FeedbackResult> ProcessAsync(
        RepositoryCoordinates upstream,
        IEnumerable<PullRequestObservation> observations,
        FeedbackPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= new FeedbackPolicy();
        var statuses = ImmutableArray.CreateBuilder<PullRequestLifecycleStatus>();
        var retries = ImmutableArray.CreateBuilder<FeedbackRetryMetadata>();
        var diagnostics = ImmutableArray.CreateBuilder<GitHubLifecycleDiagnostic>();
        foreach (PullRequestObservation observation in observations)
        {
            if (!observation.ToolOwned || observation.PullRequest.State != PullRequestState.Open)
            {
                continue;
            }

            FeedbackClassification classification = Classify(observation, policy);
            switch (classification)
            {
                case FeedbackClassification.DuplicateEntry:
                case FeedbackClassification.HashMismatch:
                    {
                        GitHubSubmissionRequest? repair = await _repairs.PlanApprovedRepairAsync(
                            observation,
                            classification,
                            cancellationToken).ConfigureAwait(false);
                        if (repair is null)
                        {
                            statuses.Add(Status(
                                observation,
                                PullRequestLifecycleAction.EscalateToHuman,
                                "No approved repair plan is available."));
                            break;
                        }

                        if (repair.ExecutionMode != WorkflowExecutionMode.Apply)
                        {
                            statuses.Add(Status(
                                observation,
                                PullRequestLifecycleAction.EscalateToHuman,
                                "Approved repair must run in Apply mode so full final preflight is enforced."));
                            diagnostics.Add(new(
                                "GH3202",
                                $"Repair planner returned a non-applying plan for PR #{observation.PullRequest.Number}."));
                            break;
                        }

                        if (repair.SupersedesPullRequestNumber != observation.PullRequest.Number)
                        {
                            statuses.Add(Status(
                                observation,
                                PullRequestLifecycleAction.EscalateToHuman,
                                "Approved replacement repair must identify the exact PR it supersedes."));
                            diagnostics.Add(new(
                                "GH3203",
                                $"Repair plan did not bind superseded PR #{observation.PullRequest.Number}."));
                            break;
                        }

                        GitHubLifecycleResult result = await _submissions.ExecuteAsync(
                            repair,
                            cancellationToken).ConfigureAwait(false);
                        if (result.Code is not (GitHubLifecycleResultCode.Succeeded or GitHubLifecycleResultCode.Planned))
                        {
                            diagnostics.AddRange(result.Diagnostics);
                            statuses.Add(Status(
                                observation,
                                PullRequestLifecycleAction.EscalateToHuman,
                                "Approved repair did not complete safely."));
                            break;
                        }

                        if (result.RemoteState.PullRequestNumber is { } replacementNumber)
                        {
                            GitHubLifecycleDiagnostic? supersedeFailure = await CloseSupersededAsync(
                                upstream,
                                observation,
                                replacementNumber,
                                cancellationToken).ConfigureAwait(false);
                            if (supersedeFailure is not null)
                            {
                                diagnostics.Add(supersedeFailure);
                                statuses.Add(Status(
                                    observation,
                                    PullRequestLifecycleAction.EscalateToHuman,
                                    "Replacement exists, but superseded PR hygiene did not complete safely."));
                                break;
                            }
                        }

                        statuses.Add(Status(
                            observation,
                            PullRequestLifecycleAction.RepairManifest,
                            "Approved repair was routed through full submission preflight."));
                        retries.Add(new(
                            observation.PullRequest.Number,
                            classification,
                            _clock.UtcNow,
                            classification == FeedbackClassification.HashMismatch
                                ? "hash-mismatch"
                                : "duplicate-entry"));
                        break;
                    }
                case FeedbackClassification.DependencyInfrastructureOutage:
                case FeedbackClassification.TransientInternalError:
                    statuses.Add(Status(
                        observation,
                        PullRequestLifecycleAction.RerunChecks,
                        "Infrastructure failure does not justify manifest mutation."));
                    retries.Add(new(
                        observation.PullRequest.Number,
                        classification,
                        _clock.UtcNow.AddHours(1),
                        null));
                    if (policy.ApplyKnownSafeResponses)
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _ = await _gitHub.CommentOnPullRequestAsync(
                                upstream,
                                observation.PullRequest.Number,
                                "WinMatsch detected a transient infrastructure failure. Keeping this PR open; please rerun the failed checks.",
                                new MutationRequest($"feedback:{observation.PullRequest.Number}:{classification}"),
                                cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (
                            exception is GitHubApiException or OperationCanceledException)
                        {
                            diagnostics.Add(new(
                                "GH3207",
                                "Known-safe feedback response has an uncertain remote outcome: " +
                                GitHubSubmissionFormatter.Redact(exception.Message)));
                            statuses[^1] = Status(
                                observation,
                                PullRequestLifecycleAction.EscalateToHuman,
                                "Infrastructure feedback response did not complete safely.");
                        }
                    }

                    break;
                case FeedbackClassification.Unknown:
                    bool stale = _clock.UtcNow - observation.PullRequest.UpdatedAt >= policy.StaleEscalationWindow;
                    statuses.Add(Status(
                        observation,
                        PullRequestLifecycleAction.EscalateToHuman,
                        stale
                            ? "Unknown feedback reached the stale escalation window."
                            : "Unknown feedback requires human review before the stale window."));
                    diagnostics.Add(new(
                        "GH3201",
                        $"Unknown feedback on PR #{observation.PullRequest.Number} requires human escalation."));
                    break;
                default:
                    statuses.Add(Status(
                        observation,
                        PullRequestLifecycleAction.Wait,
                        "No actionable known feedback signature."));
                    break;
            }
        }

        return new(statuses.ToImmutable(), retries.ToImmutable(), diagnostics.ToImmutable());
    }

    public async Task<FeedbackResult> PollAsync(
        RepositoryCoordinates upstream,
        IPullRequestFeedbackSource source,
        FeedbackPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ImmutableArray<PullRequestObservation> observations =
            await source.GetOpenToolPullRequestsAsync(upstream, cancellationToken).ConfigureAwait(false);
        return await ProcessAsync(upstream, observations, policy, cancellationToken).ConfigureAwait(false);
    }

    public static FeedbackClassification Classify(
        PullRequestObservation observation,
        FeedbackPolicy? policy = null)
    {
        policy ??= new FeedbackPolicy();
        IEnumerable<string> evidence = observation.Labels
            .Where(policy.TrustedLabels.Contains)
            .Concat(observation.Comments
                .Where(comment => policy.TrustedCommentAuthors.Contains(comment.Author))
                .Select(static comment => comment.Body));
        string combined = string.Join('\n', evidence).ToLowerInvariant().Replace('-', ' ');
        if (combined.Contains("duplicate entry", StringComparison.Ordinal)
            || combined.Contains("duplicate manifest", StringComparison.Ordinal))
        {
            return FeedbackClassification.DuplicateEntry;
        }

        if (combined.Contains("hash mismatch", StringComparison.Ordinal)
            || combined.Contains("installer hash", StringComparison.Ordinal))
        {
            return FeedbackClassification.HashMismatch;
        }

        if (combined.Contains("dependency infrastructure", StringComparison.Ordinal)
            || combined.Contains("dependency service unavailable", StringComparison.Ordinal))
        {
            return FeedbackClassification.DependencyInfrastructureOutage;
        }

        if (combined.Contains("internal error", StringComparison.Ordinal)
            || combined.Contains("please rerun", StringComparison.Ordinal)
            || combined.Contains("transient", StringComparison.Ordinal))
        {
            return FeedbackClassification.TransientInternalError;
        }

        return string.IsNullOrWhiteSpace(combined)
            ? FeedbackClassification.None
            : FeedbackClassification.Unknown;
    }

    private static PullRequestLifecycleStatus Status(
        PullRequestObservation observation,
        PullRequestLifecycleAction action,
        string reason)
        => new(observation.PullRequest.Number, "open", action, reason);

    private async Task<GitHubLifecycleDiagnostic?> CloseSupersededAsync(
        RepositoryCoordinates upstream,
        PullRequestObservation observation,
        long replacementNumber,
        CancellationToken cancellationToken)
    {
        if (!observation.ToolOwned
            || !observation.PullRequest.HeadBranch.StartsWith("winmatsch/", StringComparison.Ordinal)
            || observation.PullRequest.Body?.Contains(
                "<!-- winmatsch:package=",
                StringComparison.Ordinal) != true)
        {
            return new(
                "GH3204",
                $"Refused to close PR #{observation.PullRequest.Number} because tool ownership was not proven.");
        }

        PullRequestInfo replacement = await _gitHub.GetPullRequestAsync(
            upstream,
            replacementNumber,
            cancellationToken).ConfigureAwait(false);
        PullRequestInfo old = await _gitHub.GetPullRequestAsync(
            upstream,
            observation.PullRequest.Number,
            cancellationToken).ConfigureAwait(false);
        string? oldAssociation = AssociationMarker(old.Body);
        if (replacement.State != PullRequestState.Open
            || old.State != PullRequestState.Open
            || oldAssociation is null
            || !string.Equals(AssociationMarker(replacement.Body), oldAssociation, StringComparison.Ordinal)
            || replacement.Body?.Contains(
                $"Supersedes: #{old.Number}",
                StringComparison.Ordinal) != true
            || !replacement.HeadBranch.StartsWith("winmatsch/", StringComparison.Ordinal)
            || !string.Equals(replacement.BaseBranch, old.BaseBranch, StringComparison.Ordinal)
            || !string.Equals(old.HeadOwner, observation.PullRequest.HeadOwner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(old.HeadBranch, observation.PullRequest.HeadBranch, StringComparison.Ordinal)
            || old.Body?.Contains("<!-- winmatsch:package=", StringComparison.Ordinal) != true)
        {
            return new(
                "GH3205",
                $"Fresh state no longer proves PR #{old.Number} is the tool-owned superseded PR.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await _gitHub.CommentOnPullRequestAsync(
                upstream,
                old.Number,
                $"Superseded by #{replacement.Number}. Closing this tool-owned PR for the stable reason `superseded`.",
                new MutationRequest($"feedback:{old.Number}:superseded-comment:{replacement.Number}"),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _ = await _gitHub.ClosePullRequestAsync(
                upstream,
                old.Number,
                new MutationRequest($"feedback:{old.Number}:superseded-close:{replacement.Number}"),
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is GitHubApiException or OperationCanceledException)
        {
            return new(
                "GH3206",
                "Superseded PR response was partially applied or has an uncertain remote outcome: " +
                GitHubSubmissionFormatter.Redact(exception.Message));
        }
    }

    private static string? AssociationMarker(string? body)
        => body?.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                "<!-- winmatsch:package=",
                StringComparison.Ordinal));
}
