using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public sealed class GitHubFeedbackWorkflow
{
    private readonly IGitHubRepositoryClient _gitHub;
    private readonly GitHubLifecycleWorkflow _submissions;
    private readonly IApprovedRepairPlanner _repairs;
    private readonly IWorkflowClock _clock;
    private readonly IFeedbackStateStore _stateStore;

    public GitHubFeedbackWorkflow(
        IGitHubRepositoryClient gitHub,
        GitHubLifecycleWorkflow submissions,
        IApprovedRepairPlanner repairs,
        IWorkflowClock? clock = null)
        : this(gitHub, submissions, repairs, clock, new FileFeedbackStateStore())
    {
    }

    public GitHubFeedbackWorkflow(
        IGitHubRepositoryClient gitHub,
        GitHubLifecycleWorkflow submissions,
        IApprovedRepairPlanner repairs,
        IWorkflowClock? clock,
        IFeedbackStateStore stateStore)
    {
        _gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        _submissions = submissions ?? throw new ArgumentNullException(nameof(submissions));
        _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
        _clock = clock ?? new SystemWorkflowClock();
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
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
        var remoteStates = ImmutableArray.CreateBuilder<FeedbackRemoteState>();
        var diagnostics = ImmutableArray.CreateBuilder<GitHubLifecycleDiagnostic>();
        foreach (PullRequestObservation observation in observations)
        {
            if (!observation.ToolOwned || observation.PullRequest.State != PullRequestState.Open)
            {
                continue;
            }

            FeedbackClassification classification = Classify(observation, policy);
            FeedbackWorkState? workState = null;
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
                                PullRequestLifecycleAction.RepairManifest,
                                "Queued for an allowlisted approved repair; no arbitrary mutation was attempted."));
                            retries.Add(new(
                                observation.PullRequest.Number,
                                classification,
                                _clock.UtcNow,
                                classification == FeedbackClassification.HashMismatch
                                    ? "hash-mismatch"
                                    : "duplicate-entry"));
                            workState = FeedbackWorkState.AwaitingApprovedRepair;
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
                            workState = FeedbackWorkState.Escalated;
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
                            workState = FeedbackWorkState.Escalated;
                            break;
                        }

                        if (!IsAllowlistedRepair(upstream, observation, repair))
                        {
                            statuses.Add(Status(
                                observation,
                                PullRequestLifecycleAction.EscalateToHuman,
                                "Approved repair did not match the exact package/version association or allowlisted operation."));
                            diagnostics.Add(new(
                                "GH3209",
                                $"Repair plan for PR #{observation.PullRequest.Number} failed the allowlisted association contract."));
                            workState = FeedbackWorkState.Escalated;
                            break;
                        }

                        GitHubLifecycleResult result = await _submissions.ExecuteAsync(
                            repair,
                            cancellationToken).ConfigureAwait(false);
                        remoteStates.Add(new(observation.PullRequest.Number, result.RemoteState));
                        long? replacementNumber = result.RemoteState.PullRequestNumber;
                        string? replacementOwner = result.RemoteState.Fork?.Owner;
                        string? replacementHeadSha = result.RemoteState.CommitSha;
                        if (result.Code == GitHubLifecycleResultCode.DuplicatePullRequest)
                        {
                            ExistingReplacementResolution existing =
                                await FindExistingReplacementAsync(
                                    upstream,
                                    observation,
                                    repair,
                                    cancellationToken).ConfigureAwait(false);
                            if (existing.PullRequest is null)
                            {
                                diagnostics.AddRange(result.Diagnostics);
                                if (existing.Diagnostic is not null)
                                {
                                    diagnostics.Add(existing.Diagnostic);
                                }

                                statuses.Add(Status(
                                    observation,
                                    PullRequestLifecycleAction.EscalateToHuman,
                                    "An existing duplicate could not be proven as the exact prior replacement."));
                                workState = FeedbackWorkState.Escalated;
                                break;
                            }

                            replacementNumber = existing.PullRequest.Number;
                            replacementOwner = existing.PullRequest.HeadOwner;
                            replacementHeadSha = existing.PullRequest.HeadSha;
                        }
                        else if (result.Code is not (
                                     GitHubLifecycleResultCode.Succeeded
                                     or GitHubLifecycleResultCode.Planned))
                        {
                            diagnostics.AddRange(result.Diagnostics);
                            statuses.Add(Status(
                                observation,
                                PullRequestLifecycleAction.EscalateToHuman,
                                "Approved repair did not complete safely."));
                            workState = FeedbackWorkState.Escalated;
                            break;
                        }

                        if (replacementNumber is { } replacement)
                        {
                            SupersessionResult supersession = await CloseSupersededAsync(
                                upstream,
                                observation,
                                replacement,
                                replacementOwner,
                                replacementHeadSha,
                                repair.Operation,
                                cancellationToken).ConfigureAwait(false);
                            remoteStates.Add(new(observation.PullRequest.Number, supersession.State));
                            if (supersession.Diagnostic is not null)
                            {
                                diagnostics.Add(supersession.Diagnostic);
                                statuses.Add(Status(
                                    observation,
                                    PullRequestLifecycleAction.EscalateToHuman,
                                    "Replacement exists, but superseded PR hygiene did not complete safely."));
                                workState = FeedbackWorkState.Escalated;
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
                        workState = FeedbackWorkState.Completed;
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
                    workState = FeedbackWorkState.RetryScheduled;
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
                            workState = FeedbackWorkState.Escalated;
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
                    workState = FeedbackWorkState.Escalated;
                    break;
                default:
                    statuses.Add(Status(
                        observation,
                        PullRequestLifecycleAction.Wait,
                        "No actionable known feedback signature."));
                    break;
            }

            if (workState is { } state)
            {
                PullRequestLifecycleStatus status = statuses[^1];
                FeedbackRetryMetadata? retry = retries
                    .LastOrDefault(candidate =>
                        candidate.PullRequestNumber == observation.PullRequest.Number);
                try
                {
                    await _stateStore.PersistAsync(
                        new(
                            upstream.ToString(),
                            observation.PullRequest.Number,
                            classification,
                            state,
                            _clock.UtcNow,
                            retry?.RetryAfter,
                            retry?.LearnedOverrideSignal,
                            status.Reason),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new(
                        "GH3208",
                        "Feedback retry/repair state could not be persisted: "
                        + GitHubSubmissionFormatter.Redact(exception.Message)));
                    statuses[^1] = status with
                    {
                        RecommendedAction = PullRequestLifecycleAction.EscalateToHuman,
                        Reason = "Durable feedback state failed; human recovery is required.",
                    };
                }
            }
        }

        return new(
            statuses.ToImmutable(),
            retries.ToImmutable(),
            remoteStates.ToImmutable(),
            diagnostics.ToImmutable());
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

    public async Task<FeedbackResult> ReplayPendingAsync(
        RepositoryCoordinates upstream,
        IPullRequestFeedbackSource source,
        FeedbackPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ImmutableArray<FeedbackWorkItem> pending = await _stateStore.GetPendingAsync(
            upstream.ToString(),
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (pending.IsEmpty)
        {
            return new([], [], [], []);
        }

        ImmutableArray<PullRequestObservation> observations =
            await source.GetOpenToolPullRequestsAsync(upstream, cancellationToken).ConfigureAwait(false);
        HashSet<long> pendingNumbers =
        [
            .. pending.Select(static item => item.PullRequestNumber),
        ];
        PullRequestObservation[] replay =
        [
            .. observations.Where(observation =>
                pendingNumbers.Contains(observation.PullRequest.Number)),
        ];
        FeedbackResult result = await ProcessAsync(
            upstream,
            replay,
            policy,
            cancellationToken).ConfigureAwait(false);
        long[] missing =
        [
            .. pendingNumbers.Except(replay.Select(static observation =>
                observation.PullRequest.Number)),
        ];
        var reconciliationDiagnostics = result.Diagnostics.ToBuilder();
        foreach (FeedbackWorkItem item in pending)
        {
            PullRequestLifecycleStatus? status = result.Statuses.FirstOrDefault(candidate =>
                candidate.PullRequestNumber == item.PullRequestNumber);
            FeedbackWorkState? terminalState = missing.Contains(item.PullRequestNumber)
                ? FeedbackWorkState.Escalated
                : status?.RecommendedAction is PullRequestLifecycleAction.None
                    or PullRequestLifecycleAction.Wait
                    ? FeedbackWorkState.Completed
                    : status is null
                        ? FeedbackWorkState.Escalated
                        : null;
            if (terminalState is null)
            {
                continue;
            }

            string reason = missing.Contains(item.PullRequestNumber)
                ? "The queued pull request is no longer present in the open tool-owned feed."
                : status?.Reason ?? "The queued pull request is no longer actionable.";
            try
            {
                await _stateStore.PersistAsync(
                    item with
                    {
                        State = terminalState.Value,
                        RecordedAt = _clock.UtcNow,
                        Reason = reason,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                reconciliationDiagnostics.Add(new(
                    "GH3208",
                    "Feedback reconciliation state could not be persisted: "
                    + GitHubSubmissionFormatter.Redact(exception.Message)));
            }
        }

        if (missing.Length > 0)
        {
            reconciliationDiagnostics.AddRange(missing.Select(number =>
                new GitHubLifecycleDiagnostic(
                    "GH3210",
                    $"Queued feedback work for PR #{number} is no longer present in the open tool-owned feed.")));
        }

        return result with
        {
            Diagnostics = reconciliationDiagnostics.ToImmutable(),
        };
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

    private async Task<ExistingReplacementResolution> FindExistingReplacementAsync(
        RepositoryCoordinates upstream,
        PullRequestObservation observation,
        GitHubSubmissionRequest repair,
        CancellationToken cancellationToken)
    {
        string? expectedOwner = repair.TargetRepository?.Owner ?? repair.ForkOwner;
        string? association = AssociationMarker(observation.PullRequest.Body);
        if (string.IsNullOrWhiteSpace(expectedOwner))
        {
            try
            {
                expectedOwner = (await _gitHub.GetAuthenticatedUserAsync(cancellationToken)
                    .ConfigureAwait(false)).Login;
            }
            catch (Exception exception) when (
                exception is GitHubApiException or OperationCanceledException)
            {
                return new(
                    null,
                    new(
                        "GH3212",
                        "Unable to resolve the authenticated fork owner for replacement recovery: "
                        + GitHubSubmissionFormatter.Redact(exception.Message)));
            }
        }

        if (association is null)
        {
            return new(
                null,
                new(
                    "GH3212",
                    "The repair request does not identify the exact owner and association required to recover an existing replacement."));
        }

        IReadOnlyList<PullRequestInfo> pullRequests;
        try
        {
            pullRequests = await _gitHub.SearchPullRequestsAsync(
                upstream,
                new(
                    PullRequestState.Open,
                    HeadOwner: expectedOwner,
                    ExactTitleToken: repair.LocalPlan.PackageIdentifier.Value),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is GitHubApiException or OperationCanceledException)
        {
            return new(
                null,
                new(
                    "GH3212",
                    "Unable to search for the exact existing replacement: "
                    + GitHubSubmissionFormatter.Redact(exception.Message)));
        }

        PullRequestInfo[] matches =
        [
            .. pullRequests.Where(candidate =>
                candidate.Number != observation.PullRequest.Number
                && candidate.State == PullRequestState.Open
                && string.Equals(candidate.HeadOwner, expectedOwner, StringComparison.OrdinalIgnoreCase)
                && candidate.HeadBranch.StartsWith("winmatsch/", StringComparison.Ordinal)
                && string.Equals(candidate.BaseBranch, observation.PullRequest.BaseBranch, StringComparison.Ordinal)
                && string.Equals(AssociationMarker(candidate.Body), association, StringComparison.Ordinal)
                && candidate.Body?.Contains(
                    $"Supersedes: #{observation.PullRequest.Number}",
                    StringComparison.Ordinal) == true
                && TryGetOperation(candidate.Body, out GitHubManifestOperation operation)
                && operation == repair.Operation
                && GitHubSubmissionFormatter.IsCanonicalTitleFor(
                    repair.Operation,
                    candidate.Title,
                    repair.LocalPlan.PackageIdentifier,
                    repair.LocalPlan.PackageVersion)),
        ];
        return matches is [PullRequestInfo match]
            ? new(match, null)
            : new(
                null,
                new(
                    "GH3212",
                    $"Expected exactly one proven replacement for PR #{observation.PullRequest.Number}, found {matches.Length}."));
    }

    private static bool IsAllowlistedRepair(
        RepositoryCoordinates upstream,
        PullRequestObservation observation,
        GitHubSubmissionRequest repair)
    {
        if (repair.UpstreamRepository != upstream
            || repair.Operation is not (
                GitHubManifestOperation.Update or GitHubManifestOperation.Replace)
            || !TryGetAssociation(
                observation.PullRequest.Body,
                out string? packageIdentifier,
                out string? packageVersion,
                out GitHubManifestOperation originalOperation)
            || repair.Operation != originalOperation)
        {
            return false;
        }

        if (repair.Operation == GitHubManifestOperation.Update
            && (repair.Policy.ReplacePreviousVersion
                || repair.Policy.PreviousVersion is not null
                || repair.LocalPlan.FileChanges.Any(change =>
                    change.Kind == PlannedChangeKind.Delete
                    && !change.RepositoryPath.StartsWith(
                        ManifestPaths.GetVersionDirectory(
                            repair.LocalPlan.PackageIdentifier,
                            repair.LocalPlan.PackageVersion) + "/",
                        StringComparison.Ordinal))))
        {
            return false;
        }

        if (repair.Operation == GitHubManifestOperation.Replace
            && (!repair.Policy.ReplacePreviousVersion
                || repair.Policy.PreviousVersion is null))
        {
            return false;
        }

        ImmutableHashSet<string> repairDeletions =
        [
            .. repair.LocalPlan.FileChanges
                .Where(static change => change.Kind == PlannedChangeKind.Delete)
                .Select(static change => change.RepositoryPath),
        ];
        if ((!repairDeletions.IsEmpty || repair.Operation == GitHubManifestOperation.Replace)
            && !observation.HasAuthoritativeChangeEvidence)
        {
            return false;
        }

        ImmutableHashSet<string> originalDeletions =
        [
            .. observation.ChangedFiles
                .Where(static file => file.Status == PullRequestFileStatus.Removed)
                .Select(static file => file.Path),
        ];
        if (!originalDeletions.SetEquals(repairDeletions))
        {
            return false;
        }

        if (repair.Operation == GitHubManifestOperation.Replace)
        {
            string previousDirectory = ManifestPaths.GetVersionDirectory(
                repair.LocalPlan.PackageIdentifier,
                repair.Policy.PreviousVersion!);
            if (originalDeletions.IsEmpty
                || originalDeletions.Any(path => !path.StartsWith(
                    previousDirectory + "/",
                    StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return string.Equals(
                repair.LocalPlan.PackageIdentifier.Value,
                packageIdentifier,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                repair.LocalPlan.PackageVersion.Value,
                packageVersion,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetAssociation(
        string? body,
        out string? packageIdentifier,
        out string? packageVersion,
        out GitHubManifestOperation operation)
    {
        packageIdentifier = null;
        packageVersion = null;
        operation = default;
        const string prefix = "<!-- winmatsch:package=";
        string? marker = body?.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(prefix, StringComparison.Ordinal));
        if (marker is null || !marker.EndsWith("-->", StringComparison.Ordinal))
        {
            return false;
        }

        string association = marker[prefix.Length..^3].Trim();
        const string versionSeparator = ";version=";
        int separator = association.IndexOf(versionSeparator, StringComparison.Ordinal);
        if (separator <= 0 || separator + versionSeparator.Length >= association.Length)
        {
            return false;
        }

        packageIdentifier = association[..separator];
        packageVersion = association[(separator + versionSeparator.Length)..];
        const string operationPrefix = "Operation:";
        string? operationLine = body?.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                operationPrefix,
                StringComparison.Ordinal));
        return operationLine is not null
            && Enum.TryParse(
                operationLine[operationPrefix.Length..].Trim(),
                ignoreCase: true,
                out operation);
    }

    private async Task<SupersessionResult> CloseSupersededAsync(
        RepositoryCoordinates upstream,
        PullRequestObservation observation,
        long replacementNumber,
        string? expectedReplacementOwner,
        string? expectedReplacementHeadSha,
        GitHubManifestOperation expectedOperation,
        CancellationToken cancellationToken)
    {
        RemoteMutationState state = new() { PullRequestNumber = observation.PullRequest.Number };
        if (!observation.ToolOwned
            || !observation.PullRequest.HeadBranch.StartsWith("winmatsch/", StringComparison.Ordinal)
            || observation.PullRequest.Body?.Contains(
                "<!-- winmatsch:package=",
                StringComparison.Ordinal) != true)
        {
            return new(
                new(
                    "GH3204",
                    $"Refused to close PR #{observation.PullRequest.Number} because tool ownership was not proven."),
                state);
        }

        PullRequestInfo replacement;
        PullRequestInfo old;
        try
        {
            replacement = await _gitHub.GetPullRequestAsync(
                upstream,
                replacementNumber,
                cancellationToken).ConfigureAwait(false);
            old = await _gitHub.GetPullRequestAsync(
                upstream,
                observation.PullRequest.Number,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is GitHubApiException or OperationCanceledException)
        {
            return new(
                new(
                    "GH3211",
                    "Unable to verify fresh supersession state; the old pull request remains open: "
                    + GitHubSubmissionFormatter.Redact(exception.Message)),
                state);
        }

        string? oldAssociation = AssociationMarker(old.Body);
        bool replacementOperationMatches =
            TryGetOperation(replacement.Body, out GitHubManifestOperation replacementOperation)
            && replacementOperation == expectedOperation;
        bool oldOperationMatches =
            TryGetOperation(old.Body, out GitHubManifestOperation oldOperation)
            && oldOperation == expectedOperation;
        if (replacement.State != PullRequestState.Open
            || old.State != PullRequestState.Open
            || string.IsNullOrWhiteSpace(expectedReplacementOwner)
            || string.IsNullOrWhiteSpace(expectedReplacementHeadSha)
            || !string.Equals(
                replacement.HeadOwner,
                expectedReplacementOwner,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                replacement.HeadSha,
                expectedReplacementHeadSha,
                StringComparison.Ordinal)
            || !replacementOperationMatches
            || !oldOperationMatches
            || oldAssociation is null
            || !string.Equals(AssociationMarker(replacement.Body), oldAssociation, StringComparison.Ordinal)
            || replacement.Body?.Contains(
                $"Supersedes: #{old.Number}",
                StringComparison.Ordinal) != true
            || !replacement.HeadBranch.StartsWith("winmatsch/", StringComparison.Ordinal)
            || !string.Equals(replacement.BaseBranch, old.BaseBranch, StringComparison.Ordinal)
            || !string.Equals(old.HeadOwner, observation.PullRequest.HeadOwner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(old.HeadBranch, observation.PullRequest.HeadBranch, StringComparison.Ordinal)
            || !string.Equals(
                old.HeadSha,
                observation.PullRequest.HeadSha,
                StringComparison.Ordinal)
            || old.Body?.Contains("<!-- winmatsch:package=", StringComparison.Ordinal) != true)
        {
            return new(
                new(
                    "GH3205",
                    $"Fresh state no longer proves PR #{old.Number} is the tool-owned superseded PR."),
                state);
        }

        RemoteOperationKind attempted = RemoteOperationKind.Comment;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await _gitHub.CommentOnPullRequestAsync(
                upstream,
                old.Number,
                $"Superseded by #{replacement.Number}. Closing this tool-owned PR for the stable reason `superseded`.",
                new MutationRequest($"feedback:{old.Number}:superseded-comment:{replacement.Number}"),
                cancellationToken).ConfigureAwait(false);
            state = state with { CommentCreated = true };
            cancellationToken.ThrowIfCancellationRequested();
            attempted = RemoteOperationKind.ClosePullRequest;
            _ = await _gitHub.ClosePullRequestAsync(
                upstream,
                old.Number,
                new MutationRequest($"feedback:{old.Number}:superseded-close:{replacement.Number}"),
                cancellationToken).ConfigureAwait(false);
            return new(null, state with { PullRequestClosed = true });
        }
        catch (Exception exception) when (exception is GitHubApiException or OperationCanceledException)
        {
            return new(
                new(
                    "GH3206",
                    "Superseded PR response was partially applied or has an uncertain remote outcome: " +
                    GitHubSubmissionFormatter.Redact(exception.Message)),
                state with
                {
                    LastAttemptedOperation = attempted,
                    RemoteOutcomeUncertain = true,
                });
        }
    }

    private static string? AssociationMarker(string? body)
        => body?.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                "<!-- winmatsch:package=",
                StringComparison.Ordinal));

    private static bool TryGetOperation(
        string? body,
        out GitHubManifestOperation operation)
    {
        operation = default;
        const string operationPrefix = "Operation:";
        string? operationLine = body?.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                operationPrefix,
                StringComparison.Ordinal));
        return operationLine is not null
            && Enum.TryParse(
                operationLine[operationPrefix.Length..].Trim(),
                ignoreCase: true,
                out operation);
    }

    private sealed record ExistingReplacementResolution(
        PullRequestInfo? PullRequest,
        GitHubLifecycleDiagnostic? Diagnostic);
}
