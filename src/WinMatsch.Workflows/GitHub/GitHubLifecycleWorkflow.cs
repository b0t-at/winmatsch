using System.Collections.Immutable;
using System.Net;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Validation;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public sealed class GitHubLifecycleWorkflow
{
    private readonly IGitHubRepositoryClient _gitHub;
    private readonly IWorkflowPreflight _preflight;
    private readonly IFinalArtifactRevalidator _artifactRevalidator;
    private readonly IRemoteOperationLockProvider _locks;
    private readonly IGitHubBranchNameGenerator _branchNames;
    private readonly IWorkflowClock _clock;
    private readonly IRepositorySubmissionEvidenceProvider _repositoryEvidence;
    private readonly IPullRequestManifestEvidenceProvider _pullRequestEvidence;
    private const int MaximumBranchReservationAttempts = 8;

    public GitHubLifecycleWorkflow(
        IGitHubRepositoryClient gitHub,
        IWorkflowPreflight preflight,
        IFinalArtifactRevalidator artifactRevalidator,
        IRemoteOperationLockProvider locks,
        IGitHubBranchNameGenerator? branchNames = null,
        IWorkflowClock? clock = null)
        : this(
            gitHub,
            preflight,
            artifactRevalidator,
            locks,
            branchNames,
            clock,
            EmptyRepositorySubmissionEvidenceProvider.Instance,
            new GitHubPullRequestManifestEvidenceProvider(gitHub))
    {
    }

    public GitHubLifecycleWorkflow(
        IGitHubRepositoryClient gitHub,
        IWorkflowPreflight preflight,
        IFinalArtifactRevalidator artifactRevalidator,
        IRemoteOperationLockProvider locks,
        IGitHubBranchNameGenerator? branchNames,
        IWorkflowClock? clock,
        IRepositorySubmissionEvidenceProvider repositoryEvidence,
        IPullRequestManifestEvidenceProvider pullRequestEvidence)
    {
        _gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _artifactRevalidator = artifactRevalidator ?? throw new ArgumentNullException(nameof(artifactRevalidator));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _branchNames = branchNames ?? new DefaultGitHubBranchNameGenerator();
        _clock = clock ?? new SystemWorkflowClock();
        _repositoryEvidence = repositoryEvidence
            ?? throw new ArgumentNullException(nameof(repositoryEvidence));
        _pullRequestEvidence = pullRequestEvidence
            ?? throw new ArgumentNullException(nameof(pullRequestEvidence));
    }

    public static GitHubSubmissionPlan Plan(GitHubSubmissionRequest request)
        => Plan(request, TimeProvider.System);

    public static GitHubSubmissionPlan Plan(
        GitHubSubmissionRequest request,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return CreatePlan(request, timeProvider.GetUtcNow());
    }

    private static GitHubSubmissionPlan CreatePlan(
        GitHubSubmissionRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        ImmutableArray<GitHubLifecycleDiagnostic>.Builder diagnostics =
            GitHubManifestChangeGuard.Validate(request.LocalPlan, request.Policy).ToBuilder();
        ValidateDuplicateHashes(request, diagnostics);
        ValidateReleaseFreshness(request, now, diagnostics);
        string versionDirectory = ManifestPaths.GetVersionDirectory(
            request.LocalPlan.PackageIdentifier,
            request.LocalPlan.PackageVersion);
        string title = GitHubSubmissionFormatter.CreateTitle(
            request.Operation,
            request.LocalPlan.PackageIdentifier,
            request.LocalPlan.PackageVersion,
            request.CustomTitle);
        RepositoryCoordinates anticipatedTarget = request.TargetRepository
            ?? new RepositoryCoordinates(
                request.ForkOwner ?? "<authenticated-user>",
                request.UpstreamRepository.Name);

        var operations = ImmutableArray.CreateBuilder<PlannedRemoteOperation>();
        operations.Add(new(
            RemoteOperationKind.EnsureFork,
            anticipatedTarget.ToString(),
            request.Policy.ForkConsent == ForkConsentPolicy.AllowCreate
                ? "Discover or create the explicitly consented fork."
                : "Use an existing fork; fork creation is forbidden."));
        operations.Add(new(
            RemoteOperationKind.CreateBranch,
            anticipatedTarget.ToString(),
            "Create a unique branch from the fresh upstream default-branch head."));
        operations.Add(new(
            RemoteOperationKind.CreateCommit,
            anticipatedTarget.ToString(),
            "Revalidate preflight, URLs, hashes, and branch heads before a server-side commit."));
        operations.Add(new(
            RemoteOperationKind.CreatePullRequest,
            request.UpstreamRepository.ToString(),
            "Re-check duplicates immediately before creating the pull request."));

        return new()
        {
            Request = request,
            CommitTitle = title,
            PullRequestTitle = title,
            PullRequestBody = GitHubSubmissionFormatter.CreateBody(request, versionDirectory),
            PackageVersionDirectory = versionDirectory,
            Operations = operations.ToImmutable(),
            Diagnostics = diagnostics.ToImmutable(),
        };
    }

    public async Task<GitHubLifecycleResult> ExecuteAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        GitHubSubmissionPlan plan = CreatePlan(request, _clock.UtcNow);
        if (!plan.CanApply)
        {
            return Result(GitHubLifecycleResultCode.InvalidPlan, plan, diagnostics: plan.Diagnostics);
        }

        if (request.ExecutionMode == WorkflowExecutionMode.Plan)
        {
            return Result(GitHubLifecycleResultCode.Planned, plan);
        }

        var audit = ImmutableArray.CreateBuilder<GitHubLifecycleAuditEntry>();
        var recoveryDiagnostics = ImmutableArray.CreateBuilder<GitHubLifecycleDiagnostic>();
        RemoteMutationState state = new();
        RemoteOperationKind? attemptedMutation = null;
        RepositoryCoordinates? mutationRepository = null;
        string? expectedReservationSha = null;
        try
        {
            await using IAsyncDisposable packageLock = await _locks.AcquireAsync(
                request.UpstreamRepository.ToString(),
                request.LocalPlan.PackageIdentifier,
                cancellationToken).ConfigureAwait(false);
            Audit(audit, "GH2001", "Acquired the external per-package remote-operation lock.");

            GitHubUser user = await _gitHub.GetAuthenticatedUserAsync(cancellationToken).ConfigureAwait(false);
            RepositoryCoordinates target = request.TargetRepository
                ?? new RepositoryCoordinates(request.ForkOwner ?? user.Login, request.UpstreamRepository.Name);

            BranchState upstreamDefault = await _gitHub.GetDefaultBranchAsync(
                request.UpstreamRepository,
                cancellationToken).ConfigureAwait(false);
            RepositorySubmissionEvidence repositoryEvidence =
                await _repositoryEvidence.GetEvidenceAsync(
                    request,
                    upstreamDefault.HeadSha,
                    cancellationToken).ConfigureAwait(false);
            request = RepositorySubmissionEvidenceMerger.Merge(request, repositoryEvidence);
            plan = CreatePlan(request, _clock.UtcNow);
            if (!plan.CanApply)
            {
                return Result(
                    GitHubLifecycleResultCode.InvalidPlan,
                    plan,
                    state,
                    audit,
                    plan.Diagnostics);
            }

            if (!request.Policy.SkipPullRequestCheck)
            {
                PullRequestInfo? duplicate = await FindDuplicateAsync(
                    plan,
                    upstreamDefault.Name,
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                {
                    return Result(
                        GitHubLifecycleResultCode.DuplicatePullRequest,
                        plan,
                        state,
                        audit,
                        [new("GH2002", $"Open pull request #{duplicate.Number} already covers this package version.")]);
                }
            }
            else
            {
                Audit(audit, "GH2003", "Risky policy accepted: the early duplicate PR check was skipped.");
            }

            ValidationReport preMutationValidation = await _preflight.ValidateAsync(
                request.LocalPlan.Preflight,
                cancellationToken).ConfigureAwait(false);
            if (!preMutationValidation.CanProceed(request.LocalPlan.WarningPolicy))
            {
                return Result(
                    GitHubLifecycleResultCode.ValidationFailed,
                    plan,
                    state,
                    audit,
                    [.. preMutationValidation.Findings.Select(static finding =>
                        new GitHubLifecycleDiagnostic(finding.Code, finding.Message, finding.Path))]);
            }

            FinalArtifactRevalidationResult preMutationArtifacts =
                await _artifactRevalidator.RevalidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (!preMutationArtifacts.IsValid)
            {
                return Result(
                    GitHubLifecycleResultCode.ValidationFailed,
                    plan,
                    state,
                    audit,
                    preMutationArtifacts.Diagnostics);
            }

            CaptureRecoveryDiagnostics(
                preMutationArtifacts,
                recoveryDiagnostics,
                audit,
                ref state);
            await VerifyLiveReleaseFreshnessAsync(request, cancellationToken).ConfigureAwait(false);
            await VerifyRemoteFilePreconditionsAsync(
                request,
                upstreamDefault.HeadSha,
                cancellationToken).ConfigureAwait(false);
            Audit(audit, "GH2031", "Completed full non-mutating validation before any remote mutation.");

            attemptedMutation = RemoteOperationKind.EnsureFork;
            (RepositoryInfo targetRepository, bool forkCreated) = await EnsureTargetAsync(
                request,
                target,
                audit,
                cancellationToken).ConfigureAwait(false);
            attemptedMutation = null;
            state = state with
            {
                Fork = targetRepository.Coordinates,
                ForkCreated = forkCreated,
            };
            mutationRepository = targetRepository.Coordinates;

            attemptedMutation = RemoteOperationKind.SyncFork;
            await EnsureTargetDefaultIsFreshAsync(
                request.UpstreamRepository,
                targetRepository,
                upstreamDefault,
                request.IdempotencyKey,
                audit,
                cancellationToken).ConfigureAwait(false);
            attemptedMutation = null;
            BranchState freshTargetDefault = await _gitHub.GetDefaultBranchAsync(
                targetRepository.Coordinates,
                cancellationToken).ConfigureAwait(false);
            BranchState refreshedUpstream = await _gitHub.GetDefaultBranchAsync(
                request.UpstreamRepository,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(refreshedUpstream.Name, upstreamDefault.Name, StringComparison.Ordinal)
                || !string.Equals(refreshedUpstream.HeadSha, upstreamDefault.HeadSha, StringComparison.Ordinal))
            {
                return Result(
                    GitHubLifecycleResultCode.Conflict,
                    plan,
                    state,
                    audit,
                    [new("GH2017", "Upstream default branch moved before fresh branch creation.")]);
            }

            if (!string.Equals(freshTargetDefault.HeadSha, upstreamDefault.HeadSha, StringComparison.Ordinal))
            {
                return Result(
                    GitHubLifecycleResultCode.Conflict,
                    plan,
                    state,
                    audit,
                    [new("GH2004", "The target default branch is not an exact fresh copy of upstream.")]);
            }

            string branchName = _branchNames.Create(new(
                request.LocalPlan.PackageIdentifier,
                request.LocalPlan.PackageVersion,
                request.Operation,
                request.SupersedesPullRequestNumber,
                upstreamDefault.Name,
                upstreamDefault.HeadSha,
                request.IdempotencyKey));

            GitReference? branch = null;
            ServerCommitResult? commit = null;
            ValidationReport finalPreflight = await _preflight.ExecuteAsync(
                request.LocalPlan.Preflight,
                async boundaryCancellation =>
                {
                    boundaryCancellation.ThrowIfCancellationRequested();
                    FinalArtifactRevalidationResult artifacts = await _artifactRevalidator.RevalidateAsync(
                        request,
                        boundaryCancellation).ConfigureAwait(false);
                    if (!artifacts.IsValid)
                    {
                        throw new FinalArtifactValidationException(artifacts.Diagnostics);
                    }

                    CaptureRecoveryDiagnostics(
                        artifacts,
                        recoveryDiagnostics,
                        audit,
                        ref state);
                    await VerifyLiveReleaseFreshnessAsync(request, boundaryCancellation)
                        .ConfigureAwait(false);
                    await VerifyRemoteFilePreconditionsAsync(
                        request,
                        upstreamDefault.HeadSha,
                        boundaryCancellation).ConfigureAwait(false);
                    BranchState currentUpstream = await _gitHub.GetDefaultBranchAsync(
                        request.UpstreamRepository,
                        boundaryCancellation).ConfigureAwait(false);
                    BranchState currentTarget = await _gitHub.GetDefaultBranchAsync(
                        targetRepository.Coordinates,
                        boundaryCancellation).ConfigureAwait(false);
                    if (!string.Equals(currentUpstream.Name, upstreamDefault.Name, StringComparison.Ordinal)
                        || !string.Equals(currentUpstream.HeadSha, upstreamDefault.HeadSha, StringComparison.Ordinal)
                        || !string.Equals(currentTarget.HeadSha, upstreamDefault.HeadSha, StringComparison.Ordinal))
                    {
                        throw new RemoteStateConflictException(
                            "Upstream or the target default branch moved during final validation.");
                    }

                    for (int attempt = 0; attempt < MaximumBranchReservationAttempts; attempt++)
                    {
                        boundaryCancellation.ThrowIfCancellationRequested();
                        string candidateName = attempt == 0
                            ? branchName
                            : $"{branchName}-{attempt + 1}";
                        GitReference? existing = await _gitHub.GetReferenceAsync(
                            targetRepository.Coordinates,
                            candidateName,
                            boundaryCancellation).ConfigureAwait(false);
                        if (existing is not null)
                        {
                            if (string.Equals(
                                    existing.Sha,
                                    upstreamDefault.HeadSha,
                                    StringComparison.Ordinal))
                            {
                                branchName = candidateName;
                                branch = existing;
                                state = state with
                                {
                                    BranchName = branchName,
                                    BranchHeadSha = existing.Sha,
                                    BranchAdopted = true,
                                };
                                Audit(
                                    audit,
                                    "GH2032",
                                    $"Adopted exact fresh tool reservation '{branchName}'.");
                                break;
                            }

                            continue;
                        }

                        attemptedMutation = RemoteOperationKind.CreateBranch;
                        expectedReservationSha = upstreamDefault.HeadSha;
                        state = state with { BranchName = candidateName };
                        try
                        {
                            branch = await _gitHub.CreateUniqueReferenceAsync(
                                targetRepository.Coordinates,
                                candidateName,
                                upstreamDefault.HeadSha,
                                Mutation(
                                    $"{request.IdempotencyKey}:branch:{candidateName}:{upstreamDefault.HeadSha}"),
                                boundaryCancellation).ConfigureAwait(false);
                            branchName = candidateName;
                            break;
                        }
                        catch (GitHubApiException exception) when (exception.IsConflict)
                        {
                            attemptedMutation = null;
                        }
                    }

                    if (branch is null)
                    {
                        throw new RemoteStateConflictException(
                            $"Unable to reserve a fresh tool branch after {MaximumBranchReservationAttempts} bounded attempts.");
                    }

                    state = state with
                    {
                        BranchName = branchName,
                        BranchHeadSha = branch.Sha,
                        BranchCreated = !state.BranchAdopted,
                    };
                    if (!state.BranchAdopted)
                    {
                        Audit(audit, "GH2006", $"Created fresh tool branch '{branchName}'.");
                    }

                    attemptedMutation = null;

                    GitReference? currentBranch = await _gitHub.GetReferenceAsync(
                        targetRepository.Coordinates,
                        branchName,
                        boundaryCancellation).ConfigureAwait(false);
                    currentUpstream = await _gitHub.GetDefaultBranchAsync(
                        request.UpstreamRepository,
                        boundaryCancellation).ConfigureAwait(false);
                    if (!string.Equals(currentUpstream.Name, upstreamDefault.Name, StringComparison.Ordinal)
                        || !string.Equals(currentUpstream.HeadSha, upstreamDefault.HeadSha, StringComparison.Ordinal)
                        || currentBranch is null
                        || !string.Equals(currentBranch.Sha, branch.Sha, StringComparison.Ordinal))
                    {
                        throw new RemoteStateConflictException(
                            "Upstream or the fresh branch moved before the server-side commit.");
                    }

                    boundaryCancellation.ThrowIfCancellationRequested();
                    attemptedMutation = RemoteOperationKind.CreateCommit;
                    commit = await _gitHub.CreateCommitAsync(
                        targetRepository.Coordinates,
                        CreateCommit(plan, branchName, branch.Sha),
                        Mutation($"{request.IdempotencyKey}:commit:{branchName}"),
                        boundaryCancellation).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (commit is null)
            {
                return Result(
                    GitHubLifecycleResultCode.ValidationFailed,
                    plan,
                    state,
                    audit,
                    [.. finalPreflight.Findings.Select(static finding =>
                        new GitHubLifecycleDiagnostic(finding.Code, finding.Message, finding.Path))]);
            }

            state = state with
            {
                CommitSha = commit.Sha,
                CommitUri = commit.WebUri,
                CommitCreated = true,
                BranchHeadSha = commit.Sha,
            };
            Audit(audit, "GH2007", $"Created server-side commit '{commit.Sha}'.");
            attemptedMutation = null;

            await VerifyLiveReleaseFreshnessAsync(request, cancellationToken).ConfigureAwait(false);
            await VerifyRemoteFilePreconditionsAsync(
                request,
                upstreamDefault.HeadSha,
                cancellationToken).ConfigureAwait(false);
            BranchState prePullRequestUpstream = await _gitHub.GetDefaultBranchAsync(
                request.UpstreamRepository,
                cancellationToken).ConfigureAwait(false);
            GitReference? prePullRequestBranch = await _gitHub.GetReferenceAsync(
                targetRepository.Coordinates,
                branchName,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(prePullRequestUpstream.Name, upstreamDefault.Name, StringComparison.Ordinal)
                || !string.Equals(prePullRequestUpstream.HeadSha, upstreamDefault.HeadSha, StringComparison.Ordinal)
                || prePullRequestBranch is null
                || !string.Equals(prePullRequestBranch.Sha, commit.Sha, StringComparison.Ordinal))
            {
                return Result(
                    GitHubLifecycleResultCode.Conflict,
                    plan,
                    state,
                    audit,
                    [new("GH2020", "Upstream or the validated branch moved immediately before pull request creation.")]);
            }

            PullRequestInfo? finalDuplicate = await FindDuplicateAsync(
                plan,
                upstreamDefault.Name,
                cancellationToken).ConfigureAwait(false);
            if (finalDuplicate is not null)
            {
                return Result(
                    GitHubLifecycleResultCode.DuplicatePullRequest,
                    plan,
                    state,
                    audit,
                    [new("GH2008", $"Pull request race detected; #{finalDuplicate.Number} now covers this package version.")]);
            }

            prePullRequestUpstream = await _gitHub.GetDefaultBranchAsync(
                request.UpstreamRepository,
                cancellationToken).ConfigureAwait(false);
            prePullRequestBranch = await _gitHub.GetReferenceAsync(
                targetRepository.Coordinates,
                branchName,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(prePullRequestUpstream.Name, upstreamDefault.Name, StringComparison.Ordinal)
                || !string.Equals(prePullRequestUpstream.HeadSha, upstreamDefault.HeadSha, StringComparison.Ordinal)
                || prePullRequestBranch is null
                || !string.Equals(prePullRequestBranch.Sha, commit.Sha, StringComparison.Ordinal))
            {
                return Result(
                    GitHubLifecycleResultCode.Conflict,
                    plan,
                    state,
                    audit,
                    [new("GH2033", "Upstream or the validated branch moved during the final duplicate check.")]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            attemptedMutation = RemoteOperationKind.CreatePullRequest;
            PullRequestInfo pullRequest = await _gitHub.CreatePullRequestAsync(
                request.UpstreamRepository,
                new(
                    plan.PullRequestTitle,
                    plan.PullRequestBody,
                    targetRepository.Coordinates.Owner,
                    branchName,
                    upstreamDefault.Name),
                Mutation($"{request.IdempotencyKey}:pull-request:{branchName}"),
                cancellationToken).ConfigureAwait(false);
            state = state with
            {
                PullRequestNumber = pullRequest.Number,
                PullRequestUri = pullRequest.WebUri,
                PullRequestCreated = true,
            };
            if (!string.Equals(pullRequest.HeadSha, commit.Sha, StringComparison.Ordinal)
                || !string.Equals(pullRequest.HeadOwner, targetRepository.Coordinates.Owner, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(pullRequest.HeadBranch, branchName, StringComparison.Ordinal)
                || !string.Equals(pullRequest.BaseBranch, upstreamDefault.Name, StringComparison.Ordinal))
            {
                state = state with
                {
                    LastAttemptedOperation = RemoteOperationKind.CreatePullRequest,
                    RemoteOutcomeUncertain = true,
                };
                return Result(
                    GitHubLifecycleResultCode.RemoteFailure,
                    plan,
                    state,
                    audit,
                    [new("GH2018", "Created pull request does not reference the final validated commit and branch.")]);
            }

            IReadOnlyList<PullRequestInfo> associated =
                await FindAssociatedPullRequestsAsync(
                    plan,
                    upstreamDefault.Name,
                    cancellationToken,
                    pullRequest.Number).ConfigureAwait(false);
            PullRequestInfo freshPullRequest = await _gitHub.GetPullRequestAsync(
                request.UpstreamRepository,
                pullRequest.Number,
                cancellationToken).ConfigureAwait(false);
            GitReference? finalBranch = await _gitHub.GetReferenceAsync(
                targetRepository.Coordinates,
                branchName,
                cancellationToken).ConfigureAwait(false);
            BranchState finalUpstream = await _gitHub.GetDefaultBranchAsync(
                request.UpstreamRepository,
                cancellationToken).ConfigureAwait(false);
            if (freshPullRequest.State != PullRequestState.Open
                || !string.Equals(freshPullRequest.HeadOwner, targetRepository.Coordinates.Owner, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(freshPullRequest.HeadBranch, branchName, StringComparison.Ordinal)
                || !string.Equals(freshPullRequest.BaseBranch, upstreamDefault.Name, StringComparison.Ordinal)
                || !string.Equals(freshPullRequest.HeadSha, commit.Sha, StringComparison.Ordinal)
                || finalBranch is null
                || !string.Equals(finalBranch.Sha, commit.Sha, StringComparison.Ordinal)
                || !string.Equals(finalUpstream.Name, upstreamDefault.Name, StringComparison.Ordinal)
                || !string.Equals(finalUpstream.HeadSha, upstreamDefault.HeadSha, StringComparison.Ordinal))
            {
                state = state with
                {
                    LastAttemptedOperation = RemoteOperationKind.CreatePullRequest,
                    RemoteOutcomeUncertain = true,
                };
                return Result(
                    GitHubLifecycleResultCode.RemoteFailure,
                    plan,
                    state,
                    audit,
                    [new("GH2021", "The pull request, branch, or upstream base moved before final verification.")]);
            }

            PullRequestInfo[] others =
            [
                .. associated.Where(candidate => candidate.Number != pullRequest.Number),
            ];
            if (others.Length > 0)
            {
                PullRequestInfo winner = associated.MinBy(static candidate => candidate.Number)!;
                if (winner.Number != pullRequest.Number)
                {
                    PullRequestInfo freshWinner = await _gitHub.GetPullRequestAsync(
                        request.UpstreamRepository,
                        winner.Number,
                        cancellationToken).ConfigureAwait(false);
                    if (!await IsAssociatedPullRequestAsync(
                            plan,
                            freshWinner,
                            upstreamDefault.Name,
                            cancellationToken).ConfigureAwait(false)
                        || !string.Equals(
                            freshWinner.BaseBranch,
                            pullRequest.BaseBranch,
                            StringComparison.Ordinal))
                    {
                        return Result(
                            GitHubLifecycleResultCode.HumanEscalationRequired,
                            plan,
                            state,
                            audit,
                            [new("GH2026", "The duplicate winner changed before reconciliation; the new PR remains open.")]);
                    }

                    try
                    {
                        attemptedMutation = RemoteOperationKind.Comment;
                        _ = await _gitHub.CommentOnPullRequestAsync(
                            request.UpstreamRepository,
                            pullRequest.Number,
                            $"Duplicate of #{winner.Number}. Closing this newly created tool-owned PR.",
                            Mutation($"{request.IdempotencyKey}:duplicate-comment"),
                            cancellationToken).ConfigureAwait(false);
                        state = state with { CommentCreated = true };
                        attemptedMutation = RemoteOperationKind.ClosePullRequest;
                        _ = await _gitHub.ClosePullRequestAsync(
                            request.UpstreamRepository,
                            pullRequest.Number,
                            Mutation($"{request.IdempotencyKey}:duplicate-close"),
                            cancellationToken).ConfigureAwait(false);
                        attemptedMutation = null;
                        state = state with { PullRequestClosed = true };
                        Audit(audit, "GH2022", $"Closed losing duplicate PR #{pullRequest.Number} in favor of #{winner.Number}.");
                        return Result(
                            GitHubLifecycleResultCode.DuplicatePullRequest,
                            plan,
                            state,
                            audit,
                            [new("GH2023", $"Repository-wide race reconciled in favor of PR #{winner.Number}.")]);
                    }
                    catch (Exception exception) when (
                        exception is GitHubApiException or OperationCanceledException)
                    {
                        state = MarkUncertain(state, attemptedMutation);
                        return Result(
                            exception is OperationCanceledException
                                ? GitHubLifecycleResultCode.Cancelled
                                : GitHubLifecycleResultCode.RemoteFailure,
                            plan,
                            state,
                            audit,
                            [new("GH2024", "Duplicate PR reconciliation has an uncertain outcome: " + exception.Message)]);
                    }
                }

                return Result(
                    GitHubLifecycleResultCode.HumanEscalationRequired,
                    plan,
                    state,
                    audit,
                    [new("GH2025", "A later duplicate PR exists; only its proven owner may close it.")]);
            }

            try
            {
                await VerifyLiveReleaseFreshnessAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (FinalArtifactValidationException exception)
            {
                return await CloseCreatedPullRequestAfterValidationFailureAsync(
                    request,
                    plan,
                    targetRepository.Coordinates,
                    branchName,
                    upstreamDefault,
                    commit,
                    pullRequest,
                    state,
                    audit,
                    exception.Diagnostics,
                    cancellationToken).ConfigureAwait(false);
            }

            Audit(audit, "GH2009", $"Created pull request #{pullRequest.Number}.");
            attemptedMutation = null;
            return Result(
                GitHubLifecycleResultCode.Succeeded,
                plan,
                state,
                audit,
                recoveryDiagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            state = MarkUncertain(state, attemptedMutation);
            return Result(
                GitHubLifecycleResultCode.Cancelled,
                plan,
                state,
                audit,
                [new("GH2010", "The operation was cancelled at a remote mutation boundary.")]);
        }
        catch (FinalArtifactValidationException exception)
        {
            return Result(
                GitHubLifecycleResultCode.ValidationFailed,
                plan,
                state,
                audit,
                exception.Diagnostics);
        }
        catch (RemoteOperationLockException exception)
        {
            return Result(
                GitHubLifecycleResultCode.Conflict,
                plan,
                state,
                audit,
                [new("GH2011", exception.Message)]);
        }
        catch (PullRequestEvidenceLimitException exception)
        {
            return Result(
                GitHubLifecycleResultCode.HumanEscalationRequired,
                plan,
                state,
                audit,
                [new("GH2034", exception.Message)]);
        }
        catch (ForkConsentException exception)
        {
            return Result(
                GitHubLifecycleResultCode.ConsentRequired,
                plan,
                state,
                audit,
                [new("GH2016", exception.Message)]);
        }
        catch (RemoteStateConflictException exception)
        {
            return Result(
                GitHubLifecycleResultCode.Conflict,
                plan,
                state,
                audit,
                [new("GH2012", exception.Message)]);
        }
        catch (GitHubApiException exception)
        {
            GitHubLifecycleDiagnostic? reconciliationDiagnostic = null;
            if (!exception.IsConflict
                && attemptedMutation == RemoteOperationKind.CreateBranch
                && mutationRepository is not null
                && state.BranchName is not null
                && expectedReservationSha is not null)
            {
                try
                {
                    GitReference? uncertainBranch = await _gitHub.GetReferenceAsync(
                        mutationRepository,
                        state.BranchName,
                        CancellationToken.None).ConfigureAwait(false);
                    if (uncertainBranch is not null
                        && string.Equals(uncertainBranch.Sha, expectedReservationSha, StringComparison.Ordinal))
                    {
                        state = state with
                        {
                            BranchCreated = true,
                            BranchHeadSha = uncertainBranch.Sha,
                        };
                    }
                }
                catch (GitHubApiException reconciliationException)
                {
                    reconciliationDiagnostic = new(
                        "GH2027",
                        "Unable to reconcile uncertain branch creation: " + reconciliationException.Message);
                }
            }

            state = exception.IsConflict ? state : MarkUncertain(state, attemptedMutation);
            return Result(
                exception.IsConflict
                    ? GitHubLifecycleResultCode.Conflict
                    : GitHubLifecycleResultCode.RemoteFailure,
                plan,
                state,
                audit,
                reconciliationDiagnostic is null
                    ? [new("GH2013", GitHubSubmissionFormatter.Redact(exception.Message))]
                    :
                    [
                        new("GH2013", GitHubSubmissionFormatter.Redact(exception.Message)),
                        reconciliationDiagnostic,
                    ]);
        }
    }

    private async Task<(RepositoryInfo Repository, bool Created)> EnsureTargetAsync(
        GitHubSubmissionRequest request,
        RepositoryCoordinates target,
        ImmutableArray<GitHubLifecycleAuditEntry>.Builder audit,
        CancellationToken cancellationToken)
    {
        RepositoryInfo? existing = await TryGetRepositoryAsync(target, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateTarget(request.UpstreamRepository, existing);
            return (existing, false);
        }

        if (request.Policy.ForkConsent != ForkConsentPolicy.AllowCreate)
        {
            throw new ForkConsentException(
                "The target fork does not exist and explicit fork-creation consent was not granted.");
        }

        if (!string.Equals(target.Name, request.UpstreamRepository.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteStateConflictException(
                "GitHub fork creation cannot provision a differently named target repository.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ForkResult fork = await _gitHub.EnsureForkAsync(
            request.UpstreamRepository,
            target.Owner,
            Mutation($"{request.IdempotencyKey}:fork"),
            cancellationToken).ConfigureAwait(false);
        ValidateTarget(request.UpstreamRepository, fork.Repository);
        Audit(audit, "GH2014", fork.AlreadyExisted ? "Discovered existing fork." : "Created consented fork.");
        return (fork.Repository, !fork.AlreadyExisted);
    }

    private async Task EnsureTargetDefaultIsFreshAsync(
        RepositoryCoordinates upstream,
        RepositoryInfo target,
        BranchState upstreamDefault,
        string idempotencyKey,
        ImmutableArray<GitHubLifecycleAuditEntry>.Builder audit,
        CancellationToken cancellationToken)
    {
        if (target.Coordinates == upstream
            || string.Equals(target.DefaultBranch.HeadSha, upstreamDefault.HeadSha, StringComparison.Ordinal))
        {
            return;
        }

        CompareResult comparison = await _gitHub.CompareAsync(
            upstream,
            upstreamDefault.Name,
            $"{target.Coordinates.Owner}:{target.DefaultBranch.Name}",
            cancellationToken).ConfigureAwait(false);
        if (comparison.AheadBy > 0 || comparison.Status is "ahead" or "diverged")
        {
            throw new RemoteStateConflictException(
                "The target default branch contains user commits and will not be force-updated.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = await _gitHub.SyncForkAsync(
            target.Coordinates,
            target.DefaultBranch.Name,
            Mutation($"{idempotencyKey}:sync"),
            cancellationToken).ConfigureAwait(false);
        Audit(audit, "GH2015", "Safely synchronized the fork default branch with upstream.");
    }

    private async Task<PullRequestInfo?> FindDuplicateAsync(
        GitHubSubmissionPlan plan,
        string expectedBaseBranch,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PullRequestInfo> associated =
            await FindAssociatedPullRequestsAsync(
                plan,
                expectedBaseBranch,
                cancellationToken).ConfigureAwait(false);
        return associated.Count == 0 ? null : associated[0];
    }

    private async Task<IReadOnlyList<PullRequestInfo>> FindAssociatedPullRequestsAsync(
        GitHubSubmissionPlan plan,
        string expectedBaseBranch,
        CancellationToken cancellationToken,
        long? additionallyExcludedPullRequestNumber = null)
    {
        GitHubSubmissionRequest request = plan.Request;
        HashSet<long> associationExcludedPullRequestNumbers = [];
        if (request.SupersedesPullRequestNumber is { } superseded)
        {
            associationExcludedPullRequestNumbers.Add(superseded);
        }

        var boundExcludedPullRequestNumbers =
            new HashSet<long>(associationExcludedPullRequestNumbers);
        if (additionallyExcludedPullRequestNumber is { } additionallyExcluded)
        {
            boundExcludedPullRequestNumbers.Add(additionallyExcluded);
        }

        int maximumSearchResults =
            PullRequestManifestEvidenceLimits.MaximumCandidates
            + boundExcludedPullRequestNumbers.Count;
        IReadOnlyList<PullRequestInfo> candidates;
        try
        {
            candidates = await _gitHub.SearchPullRequestsAsync(
                request.UpstreamRepository,
                new PullRequestSearch(
                    PullRequestState.Open,
                    BaseBranch: expectedBaseBranch)
                {
                    MaximumResults = maximumSearchResults,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException exception) when (exception.StatusCode is null)
        {
            throw new PullRequestEvidenceLimitException(
                "Pull request discovery failed a local transport safety bound: "
                + exception.Message);
        }

        PullRequestInfo[] boundedDiscoveryCandidates =
        [
            .. candidates.Where(pullRequest =>
                !boundExcludedPullRequestNumbers.Contains(pullRequest.Number)),
        ];
        if (boundedDiscoveryCandidates.Length > PullRequestManifestEvidenceLimits.MaximumCandidates)
        {
            throw new PullRequestEvidenceLimitException(
                $"Manifest evidence candidate count {boundedDiscoveryCandidates.Length} exceeds the safe limit of {PullRequestManifestEvidenceLimits.MaximumCandidates}.");
        }

        PullRequestInfo[] associationCandidates =
        [
            .. candidates.Where(pullRequest =>
                !associationExcludedPullRequestNumbers.Contains(pullRequest.Number)),
        ];
        var associated = new List<PullRequestInfo>();
        PullRequestInfo[] unassociated =
        [
            .. associationCandidates.Where(pullRequest =>
                pullRequest.State == PullRequestState.Open
                && string.Equals(
                    pullRequest.BaseBranch,
                    expectedBaseBranch,
                    StringComparison.Ordinal)
                && !GitHubSubmissionFormatter.IsCanonicalTitleFor(
                    pullRequest.Title,
                    request.LocalPlan.PackageIdentifier,
                    request.LocalPlan.PackageVersion)),
        ];
        associated.AddRange(associationCandidates.Where(pullRequest =>
            pullRequest.State == PullRequestState.Open
            && string.Equals(
                pullRequest.BaseBranch,
                expectedBaseBranch,
                StringComparison.Ordinal)
            && GitHubSubmissionFormatter.IsCanonicalTitleFor(
                pullRequest.Title,
                request.LocalPlan.PackageIdentifier,
                request.LocalPlan.PackageVersion)));
        IReadOnlyList<PullRequestInfo> evidenceCandidates =
            await _pullRequestEvidence.GetCandidatesAsync(
                plan,
                unassociated,
                cancellationToken).ConfigureAwait(false);
        HashSet<(
            long Number,
            string HeadOwner,
            string HeadSha,
            RepositoryCoordinates? HeadRepository,
            string BaseBranch,
            string? BaseSha)> allowed =
        [
            .. unassociated.Select(static pullRequest =>
                (
                    pullRequest.Number,
                    pullRequest.HeadOwner,
                    pullRequest.HeadSha,
                    pullRequest.HeadRepository,
                    pullRequest.BaseBranch,
                    pullRequest.BaseSha)),
        ];
        PullRequestInfo[] boundedCandidates =
        [
            .. evidenceCandidates
                .DistinctBy(static pullRequest =>
                    (
                        pullRequest.Number,
                        pullRequest.HeadOwner,
                        pullRequest.HeadSha,
                        pullRequest.HeadRepository,
                        pullRequest.BaseBranch,
                        pullRequest.BaseSha)),
        ];
        if (boundedCandidates.Length > PullRequestManifestEvidenceLimits.MaximumCandidates
            || boundedCandidates.Any(candidate => !allowed.Contains(
                (
                    candidate.Number,
                    candidate.HeadOwner,
                    candidate.HeadSha,
                    candidate.HeadRepository,
                    candidate.BaseBranch,
                    candidate.BaseSha))))
        {
            throw new PullRequestEvidenceLimitException(
                "Manifest evidence provider returned an unbounded, duplicate, or out-of-scope candidate set.");
        }

        foreach (PullRequestInfo pullRequest in boundedCandidates)
        {
            PullRequestManifestEvidence evidence = await _pullRequestEvidence.GetEvidenceAsync(
                plan,
                pullRequest,
                cancellationToken).ConfigureAwait(false);
            if (evidence.IsAssociated)
            {
                associated.Add(pullRequest);
            }
        }

        return [.. associated.OrderBy(static candidate => candidate.Number)];
    }

    private async Task<bool> IsAssociatedPullRequestAsync(
        GitHubSubmissionPlan plan,
        PullRequestInfo pullRequest,
        string expectedBaseBranch,
        CancellationToken cancellationToken)
    {
        if (pullRequest.State != PullRequestState.Open)
        {
            return false;
        }

        if (!string.Equals(pullRequest.BaseBranch, expectedBaseBranch, StringComparison.Ordinal))
        {
            return false;
        }

        if (GitHubSubmissionFormatter.IsCanonicalTitleFor(
                pullRequest.Title,
                plan.Request.LocalPlan.PackageIdentifier,
                plan.Request.LocalPlan.PackageVersion))
        {
            return true;
        }

        PullRequestManifestEvidence evidence = await _pullRequestEvidence.GetEvidenceAsync(
            plan,
            pullRequest,
            cancellationToken).ConfigureAwait(false);
        return evidence.IsAssociated;
    }

    private async Task VerifyRemoteFilePreconditionsAsync(
        GitHubSubmissionRequest request,
        string pinnedUpstreamSha,
        CancellationToken cancellationToken)
    {
        foreach (WorkflowFileChange change in request.LocalPlan.FileChanges)
        {
            RepositoryContent? remote = null;
            try
            {
                remote = await _gitHub.GetContentAsync(
                    request.UpstreamRepository,
                    change.RepositoryPath,
                    pinnedUpstreamSha,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
            }

            if (change.ExpectedState == ExpectedFileState.Absent)
            {
                if (remote is not null)
                {
                    throw new RemoteStateConflictException(
                        $"Remote path '{change.RepositoryPath}' was created after local planning.");
                }

                continue;
            }

            if (remote is null)
            {
                throw new RemoteStateConflictException(
                    $"Remote path '{change.RepositoryPath}' was removed after local planning.");
            }

            string actualHash = WorkflowFileChange.Hash(remote.Bytes.Span);
            if (!string.Equals(actualHash, change.ExpectedSha256, StringComparison.Ordinal))
            {
                throw new RemoteStateConflictException(
                    $"Remote path '{change.RepositoryPath}' changed after local planning.");
            }
        }
    }

    private async Task VerifyLiveReleaseFreshnessAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = request.Policy.MinimumReleaseFreshness;
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (request.ReleaseRepository is null || request.ReleaseId is null)
        {
            throw new FinalArtifactValidationException(
                [new("GH1016", "Live release freshness requires a release repository and release ID.")]);
        }

        IReadOnlyList<GitHubRelease> releases = await _gitHub.GetReleasesAsync(
            request.ReleaseRepository,
            cancellationToken).ConfigureAwait(false);
        GitHubRelease? release = releases.SingleOrDefault(candidate => candidate.Id == request.ReleaseId.Value);
        if (release is null)
        {
            throw new FinalArtifactValidationException(
                [new("GH1017", "The release used by the submission no longer exists.")]);
        }

        DateTimeOffset? latestUpdate = release.UpdatedAt ?? release.PublishedAt;
        foreach (ReleaseAsset asset in release.Assets)
        {
            DateTimeOffset assetUpdate = asset.UpdatedAt ?? asset.CreatedAt;
            if (latestUpdate is null || assetUpdate > latestUpdate)
            {
                latestUpdate = assetUpdate;
            }
        }

        if (latestUpdate is null || _clock.UtcNow < latestUpdate.Value + delay)
        {
            throw new FinalArtifactValidationException(
                [new("GH1018", "Current GitHub release metadata has not completed the configured freshness delay.")]);
        }
    }

    private async Task<GitHubLifecycleResult> CloseCreatedPullRequestAfterValidationFailureAsync(
        GitHubSubmissionRequest request,
        GitHubSubmissionPlan plan,
        RepositoryCoordinates targetRepository,
        string branchName,
        BranchState upstreamDefault,
        ServerCommitResult commit,
        PullRequestInfo createdPullRequest,
        RemoteMutationState state,
        ImmutableArray<GitHubLifecycleAuditEntry>.Builder audit,
        ImmutableArray<GitHubLifecycleDiagnostic> validationDiagnostics,
        CancellationToken cancellationToken)
    {
        PullRequestInfo freshPullRequest = await _gitHub.GetPullRequestAsync(
            request.UpstreamRepository,
            createdPullRequest.Number,
            cancellationToken).ConfigureAwait(false);
        GitReference? freshBranch = await _gitHub.GetReferenceAsync(
            targetRepository,
            branchName,
            cancellationToken).ConfigureAwait(false);
        if (freshPullRequest.State != PullRequestState.Open
            || !string.Equals(freshPullRequest.HeadOwner, targetRepository.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(freshPullRequest.HeadBranch, branchName, StringComparison.Ordinal)
            || !string.Equals(freshPullRequest.BaseBranch, upstreamDefault.Name, StringComparison.Ordinal)
            || !string.Equals(freshPullRequest.HeadSha, commit.Sha, StringComparison.Ordinal)
            || freshBranch is null
            || !string.Equals(freshBranch.Sha, commit.Sha, StringComparison.Ordinal))
        {
            return Result(
                GitHubLifecycleResultCode.HumanEscalationRequired,
                plan,
                state with { RemoteOutcomeUncertain = true },
                audit,
                [
                    .. validationDiagnostics,
                    new(
                        "GH2028",
                        "Final validation failed, but the created PR no longer has the exact proven tool-owned identity required for automatic closure."),
                ]);
        }

        RemoteOperationKind attempted = RemoteOperationKind.Comment;
        try
        {
            _ = await _gitHub.CommentOnPullRequestAsync(
                request.UpstreamRepository,
                freshPullRequest.Number,
                "Closing this tool-owned PR because final live artifact freshness validation failed.",
                Mutation($"{request.IdempotencyKey}:freshness-failure-comment"),
                cancellationToken).ConfigureAwait(false);
            state = state with { CommentCreated = true };
            attempted = RemoteOperationKind.ClosePullRequest;
            _ = await _gitHub.ClosePullRequestAsync(
                request.UpstreamRepository,
                freshPullRequest.Number,
                Mutation($"{request.IdempotencyKey}:freshness-failure-close"),
                cancellationToken).ConfigureAwait(false);
            state = state with { PullRequestClosed = true };
            Audit(audit, "GH2029", $"Closed PR #{freshPullRequest.Number} after final freshness validation failed.");
            return Result(
                GitHubLifecycleResultCode.ValidationFailed,
                plan,
                state,
                audit,
                validationDiagnostics);
        }
        catch (Exception exception) when (exception is GitHubApiException or OperationCanceledException)
        {
            return Result(
                exception is OperationCanceledException
                    ? GitHubLifecycleResultCode.Cancelled
                    : GitHubLifecycleResultCode.RemoteFailure,
                plan,
                state with
                {
                    LastAttemptedOperation = attempted,
                    RemoteOutcomeUncertain = true,
                },
                audit,
                [
                    .. validationDiagnostics,
                    new(
                        "GH2030",
                        "Final validation failed and automatic PR closure has an uncertain outcome: " +
                        GitHubSubmissionFormatter.Redact(exception.Message)),
                ]);
        }
    }

    private static void ValidateDuplicateHashes(
        GitHubSubmissionRequest request,
        ImmutableArray<GitHubLifecycleDiagnostic>.Builder diagnostics)
    {
        RepositoryInstallerEvidence? retired = request.RepositoryEvidence.FirstOrDefault(evidence =>
            evidence.RetiredIdentifier
            && string.Equals(
                evidence.PackageIdentifier.Value,
                request.LocalPlan.PackageIdentifier.Value,
                StringComparison.OrdinalIgnoreCase));
        if (retired is not null)
        {
            diagnostics.Add(new(
                "GH1013",
                $"Package identifier '{retired.PackageIdentifier.Value}' is retired by repository policy.",
                retired.ManifestPath));
        }

        IEnumerable<string> hashes = request.LocalPlan.Preflight.InstallerArtifacts
            .Select(static artifact => artifact.Download.Sha256.Value);
        foreach (string hash in hashes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool explicitlyAllowed = request.Policy.DuplicateHashes.AllowedSha256.Contains(hash)
                && !string.IsNullOrWhiteSpace(request.Policy.DuplicateHashes.OverrideAnnotation);
            if (request.Policy.DuplicateHashes.DeniedSha256.Contains(hash) && !explicitlyAllowed)
            {
                diagnostics.Add(new("GH1010", "Installer hash is denied by repository policy."));
                continue;
            }

            RepositoryInstallerEvidence? duplicate = request.RepositoryEvidence.FirstOrDefault(evidence =>
                !string.Equals(
                    evidence.PackageIdentifier.Value,
                    request.LocalPlan.PackageIdentifier.Value,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(evidence.InstallerSha256, hash, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null && !explicitlyAllowed)
            {
                diagnostics.Add(new(
                    "GH1011",
                    $"Installer hash already belongs to sibling or retired identifier '{duplicate.PackageIdentifier.Value}'.",
                    duplicate.ManifestPath));
            }
        }
    }

    private static void ValidateReleaseFreshness(
        GitHubSubmissionRequest request,
        DateTimeOffset now,
        ImmutableArray<GitHubLifecycleDiagnostic>.Builder diagnostics)
    {
        TimeSpan delay = request.Policy.MinimumReleaseFreshness;
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (request.ReleaseUpdatedAt is not { } updatedAt)
        {
            diagnostics.Add(new(
                "GH1014",
                "Release freshness delay requires supplied release updated-at evidence."));
            return;
        }

        if (request.ReleaseRepository is null || request.ReleaseId is null)
        {
            diagnostics.Add(new(
                "GH1016",
                "Release freshness delay requires live release repository and ID evidence."));
        }

        if (now < updatedAt + delay)
        {
            diagnostics.Add(new(
                "GH1015",
                $"Release assets remain inside the configured freshness delay until {(updatedAt + delay):O}."));
        }
    }

    private static ServerCommitRequest CreateCommit(
        GitHubSubmissionPlan plan,
        string branchName,
        string expectedHeadSha)
    {
        CommitFileAddition[] additions =
        [
            .. plan.Request.LocalPlan.FileChanges
                .Where(static change => change.Kind != PlannedChangeKind.Delete)
                .Select(static change => new CommitFileAddition(
                    change.RepositoryPath,
                    change.Content.AsMemory())),
        ];
        string[] deletions =
        [
            .. plan.Request.LocalPlan.FileChanges
                .Where(static change => change.Kind == PlannedChangeKind.Delete)
                .Select(static change => change.RepositoryPath),
        ];
        return new(
            branchName,
            expectedHeadSha,
            plan.CommitTitle,
            "Created by winmatsch after final preflight validation.",
            additions,
            deletions);
    }

    private async Task<RepositoryInfo?> TryGetRepositoryAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _gitHub.GetRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static void ValidateTarget(RepositoryCoordinates upstream, RepositoryInfo target)
    {
        if (target.Coordinates == upstream)
        {
            return;
        }

        if (!target.IsFork || target.Parent != upstream)
        {
            throw new RemoteStateConflictException(
                $"Target repository '{target.Coordinates}' is not a fork of '{upstream}'.");
        }
    }

    private static MutationRequest Mutation(string key) => new(key);

    private static RemoteMutationState MarkUncertain(
        RemoteMutationState state,
        RemoteOperationKind? attemptedMutation)
        => attemptedMutation is null
            ? state
            : state with
            {
                LastAttemptedOperation = attemptedMutation,
                RemoteOutcomeUncertain = true,
            };

    private void Audit(
        ImmutableArray<GitHubLifecycleAuditEntry>.Builder audit,
        string code,
        string message)
        => audit.Add(new(_clock.UtcNow, code, GitHubSubmissionFormatter.Redact(message)));

    private void CaptureRecoveryDiagnostics(
        FinalArtifactRevalidationResult result,
        ImmutableArray<GitHubLifecycleDiagnostic>.Builder recoveryDiagnostics,
        ImmutableArray<GitHubLifecycleAuditEntry>.Builder audit,
        ref RemoteMutationState state)
    {
        if (!result.IsValid && result.Diagnostics.IsEmpty)
        {
            return;
        }

        foreach (GitHubLifecycleDiagnostic diagnostic in result.Diagnostics)
        {
            if (!recoveryDiagnostics.Any(existing =>
                    string.Equals(existing.Code, diagnostic.Code, StringComparison.Ordinal)
                    && string.Equals(existing.Message, diagnostic.Message, StringComparison.Ordinal)
                    && string.Equals(existing.Path, diagnostic.Path, StringComparison.Ordinal)))
            {
                recoveryDiagnostics.Add(diagnostic);
                Audit(audit, diagnostic.Code, diagnostic.Message);
            }
        }

        if (!result.Diagnostics.IsEmpty)
        {
            state = state with { RecoveryRequired = true };
        }
    }

    private static GitHubLifecycleResult Result(
        GitHubLifecycleResultCode code,
        GitHubSubmissionPlan plan,
        RemoteMutationState? state = null,
        ImmutableArray<GitHubLifecycleAuditEntry>.Builder? audit = null,
        ImmutableArray<GitHubLifecycleDiagnostic>? diagnostics = null)
        => new()
        {
            Code = code,
            Plan = plan,
            RemoteState = state ?? new(),
            Audit = audit?.ToImmutable() ?? [],
            Diagnostics =
            [
                .. (diagnostics ?? []).Select(static diagnostic => diagnostic with
                {
                    Message = GitHubSubmissionFormatter.Redact(diagnostic.Message),
                    Path = diagnostic.Path is null
                        ? null
                        : GitHubSubmissionFormatter.Redact(diagnostic.Path),
                }),
            ],
        };

    private sealed class FinalArtifactValidationException(
        ImmutableArray<GitHubLifecycleDiagnostic> diagnostics) : Exception
    {
        public ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics { get; } = diagnostics;
    }

    private sealed class RemoteStateConflictException(string message) : Exception(message);

    private sealed class ForkConsentException(string message) : Exception(message);
}
