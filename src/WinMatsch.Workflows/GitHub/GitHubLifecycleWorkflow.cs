using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
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
    private const int MaximumUpstreamReanchorAttempts = 4;

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
        string title = request.Presentation?.CommitTitle ?? GitHubSubmissionFormatter.CreateTitle(
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
            PullRequestTitle = request.Presentation?.PullRequestTitle ?? title,
            PullRequestBody = request.Presentation?.PullRequestBody
                ?? GitHubSubmissionFormatter.CreateBody(request, versionDirectory),
            PackageVersionDirectory = versionDirectory,
            Operations = operations.ToImmutable(),
            Diagnostics = diagnostics.ToImmutable(),
        };
    }

    public Task<GitHubLifecycleResult> ExecuteAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(request, progress: null, cancellationToken);

    public Task<GitHubLifecycleResult> ExecuteAsync(
        GitHubSubmissionRequest request,
        ISubmissionProgressSink? progress,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(
            request,
            progress,
            allowCommitResponseLossRecovery: false,
            cancellationToken);

    public Task<GitHubLifecycleResult> ExecuteJournaledAsync(
        VerifiedSubmissionRecoveryRequest recovery,
        ISubmissionProgressSink progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        return ExecuteCoreAsync(
            recovery.Request,
            progress,
            allowCommitResponseLossRecovery: true,
            cancellationToken);
    }

    internal Task<GitHubLifecycleResult> ExecuteJournaledAsync(
        GitHubSubmissionRequest request,
        ISubmissionProgressSink progress,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(
            request,
            progress,
            allowCommitResponseLossRecovery: true,
            cancellationToken);

    private async Task<GitHubLifecycleResult> ExecuteCoreAsync(
        GitHubSubmissionRequest request,
        ISubmissionProgressSink? progress,
        bool allowCommitResponseLossRecovery,
        CancellationToken cancellationToken)
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
        RemoteMutationState state = request.ResumeFrom ?? new();
        bool recoverCommitResponseLoss = allowCommitResponseLossRecovery
            && IsRecoverableCommitResponseLoss(state);
        if (state.RemoteOutcomeUncertain && !recoverCommitResponseLoss)
        {
            return Result(
                GitHubLifecycleResultCode.HumanEscalationRequired,
                plan,
                state,
                diagnostics:
                [
                    new(
                        "GH2035",
                        "The previous remote mutation outcome is uncertain; automatic retry is forbidden."),
                ]);
        }
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

            if (!request.Policy.SkipPullRequestCheck
                && request.ResumeFrom?.PullRequestCreated != true)
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

            if (request.ResumeFrom?.PullRequestCreated == true)
            {
                return await ReconcileResumedPullRequestAsync(
                    request,
                    plan,
                    target,
                    upstreamDefault,
                    state,
                    audit,
                    cancellationToken).ConfigureAwait(false);
            }

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

            UpstreamAnchor refreshedAnchor = await ReanchorUpstreamAsync(
                request,
                plan,
                upstreamDefault,
                targetRepository,
                checkPullRequestDuplicates: true,
                "fresh branch creation",
                audit,
                kind => attemptedMutation = kind,
                cancellationToken).ConfigureAwait(false);
            request = refreshedAnchor.Request;
            plan = refreshedAnchor.Plan;
            upstreamDefault = refreshedAnchor.Upstream;

            string branchName = request.ResumeFrom?.BranchName
                ?? _branchNames.Create(new(
                    request.LocalPlan.PackageIdentifier,
                    request.LocalPlan.PackageVersion,
                    request.Operation,
                    request.SupersedesPullRequestNumber,
                    upstreamDefault.Name,
                    upstreamDefault.HeadSha,
                    request.IdempotencyKey));
            BranchState branchBase = request.ResumeFrom?.BranchHeadSha is { } resumedBaseSha
                ? new BranchState(upstreamDefault.Name, resumedBaseSha, false)
                : upstreamDefault;

            GitReference? branch = null;
            ServerCommitResult? commit = null;
            bool recoveredCommit = false;
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
                    refreshedAnchor = await ReanchorUpstreamAsync(
                        request,
                        plan,
                        upstreamDefault,
                        targetRepository,
                        checkPullRequestDuplicates: true,
                        "final pre-mutation validation",
                        audit,
                        kind => attemptedMutation = kind,
                        boundaryCancellation).ConfigureAwait(false);
                    request = refreshedAnchor.Request;
                    plan = refreshedAnchor.Plan;
                    upstreamDefault = refreshedAnchor.Upstream;

                    if (request.ResumeFrom?.CommitCreated == true)
                    {
                        string resumedBranchName = request.ResumeFrom.BranchName
                            ?? throw new RemoteStateConflictException(
                                "The resumed commit has no journaled branch name.");
                        string resumedCommitSha = request.ResumeFrom.CommitSha
                            ?? throw new RemoteStateConflictException(
                                "The resumed commit has no journaled commit SHA.");
                        GitReference? resumedBranch = await _gitHub.GetReferenceAsync(
                            targetRepository.Coordinates,
                            resumedBranchName,
                            boundaryCancellation).ConfigureAwait(false);
                        if (resumedBranch is null
                            || !string.Equals(
                                resumedBranch.Sha,
                                resumedCommitSha,
                                StringComparison.Ordinal))
                        {
                            throw new RemoteStateConflictException(
                                "The journaled remote commit no longer owns the exact target branch.");
                        }

                        branchName = resumedBranchName;
                        branch = resumedBranch;
                        commit = new(
                            resumedCommitSha,
                            request.ResumeFrom.CommitUri
                                ?? new Uri(
                                    $"https://github.com/{targetRepository.Coordinates}/commit/{resumedCommitSha}"));
                        return;
                    }

                    if (recoverCommitResponseLoss
                        && state.Fork != targetRepository.Coordinates)
                    {
                        throw new CommitRecoveryException(
                            "The journaled in-flight commit is not bound to the current fork.");
                    }

                    if (!recoverCommitResponseLoss
                        && request.ResumeFrom?.BranchName is null)
                    {
                        branchBase = upstreamDefault;
                        branchName = _branchNames.Create(new(
                            request.LocalPlan.PackageIdentifier,
                            request.LocalPlan.PackageVersion,
                            request.Operation,
                            request.SupersedesPullRequestNumber,
                            branchBase.Name,
                            branchBase.HeadSha,
                            request.IdempotencyKey));
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
                        bool isJournaledCommitRecovery =
                            recoverCommitResponseLoss
                            && attempt == 0
                            && string.Equals(
                                candidateName,
                                state.BranchName,
                                StringComparison.Ordinal);
                        if (existing is not null)
                        {
                            if (isJournaledCommitRecovery)
                            {
                                if (string.Equals(
                                        existing.Sha,
                                        branchBase.HeadSha,
                                        StringComparison.Ordinal))
                                {
                                    throw new CommitRecoveryException(
                                        "The journaled commit attempt did not produce a remotely verifiable commit.");
                                }

                                ServerCommitResult? recovered = await TryRecoverCommitAsync(
                                    plan,
                                    targetRepository,
                                    existing,
                                    branchBase,
                                    boundaryCancellation).ConfigureAwait(false);
                                if (recovered is null)
                                {
                                    throw new CommitRecoveryException(
                                        "The journaled commit attempt does not match the exact planned commit.");
                                }

                                branchName = candidateName;
                                branch = existing;
                                commit = recovered;
                                recoveredCommit = true;
                                state = state with
                                {
                                    BranchName = branchName,
                                    BranchHeadSha = existing.Sha,
                                    BranchAdopted = true,
                                };
                                Audit(
                                    audit,
                                    "GH2041",
                                    $"Recovered exact planned commit '{existing.Sha}' on tool branch '{branchName}'.");
                                break;
                            }

                            if (string.Equals(
                                    existing.Sha,
                                    branchBase.HeadSha,
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

                        if (isJournaledCommitRecovery)
                        {
                            throw new CommitRecoveryException(
                                "The journaled commit branch no longer exists.");
                        }

                        attemptedMutation = RemoteOperationKind.CreateBranch;
                        expectedReservationSha = branchBase.HeadSha;
                        state = state with
                        {
                            BranchName = candidateName,
                            LastAttemptedOperation = RemoteOperationKind.CreateBranch,
                            RemoteOutcomeUncertain = true,
                        };
                        await RecordProgressAsync(
                            progress,
                            state,
                            SubmissionJournalState.Pending).ConfigureAwait(false);
                        try
                        {
                            branch = await _gitHub.CreateUniqueReferenceAsync(
                                targetRepository.Coordinates,
                                candidateName,
                                branchBase.HeadSha,
                                Mutation(
                                    $"{request.IdempotencyKey}:branch:{candidateName}:{branchBase.HeadSha}"),
                                boundaryCancellation).ConfigureAwait(false);
                            branchName = candidateName;
                            break;
                        }
                        catch (GitHubApiException exception) when (exception.IsConflict)
                        {
                            attemptedMutation = null;
                            state = state with
                            {
                                LastAttemptedOperation = null,
                                RemoteOutcomeUncertain = false,
                            };
                            await RecordProgressAsync(
                                progress,
                                state,
                                SubmissionJournalState.Pending).ConfigureAwait(false);
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
                        LastAttemptedOperation = null,
                        RemoteOutcomeUncertain = false,
                    };
                    if (!state.BranchAdopted)
                    {
                        Audit(audit, "GH2006", $"Created fresh tool branch '{branchName}'.");
                    }
                    await RecordProgressAsync(
                        progress,
                        state,
                        SubmissionJournalState.BranchCreated).ConfigureAwait(false);

                    attemptedMutation = null;

                    refreshedAnchor = await ReanchorUpstreamAsync(
                        request,
                        plan,
                        upstreamDefault,
                        targetRepository: null,
                        checkPullRequestDuplicates: false,
                        "server-side commit",
                        audit,
                        kind => attemptedMutation = kind,
                        boundaryCancellation).ConfigureAwait(false);
                    request = refreshedAnchor.Request;
                    plan = refreshedAnchor.Plan;
                    upstreamDefault = refreshedAnchor.Upstream;
                    GitReference? currentBranch = await _gitHub.GetReferenceAsync(
                        targetRepository.Coordinates,
                        branchName,
                        boundaryCancellation).ConfigureAwait(false);
                    if (currentBranch is null
                        || !string.Equals(
                            currentBranch.Sha,
                            commit?.Sha ?? branch.Sha,
                            StringComparison.Ordinal))
                    {
                        throw new RemoteStateConflictException(
                            "The validated branch moved before the server-side commit.");
                    }

                    if (commit is null)
                    {
                        boundaryCancellation.ThrowIfCancellationRequested();
                        attemptedMutation = RemoteOperationKind.CreateCommit;
                        state = state with
                        {
                            LastAttemptedOperation = RemoteOperationKind.CreateCommit,
                            RemoteOutcomeUncertain = true,
                        };
                        await RecordProgressAsync(
                            progress,
                            state,
                            SubmissionJournalState.BranchCreated).ConfigureAwait(false);
                        commit = await _gitHub.CreateCommitAsync(
                            targetRepository.Coordinates,
                            CreateCommit(plan, branchName, branch.Sha),
                            Mutation($"{request.IdempotencyKey}:commit:{branchName}"),
                            boundaryCancellation).ConfigureAwait(false);
                    }
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
                LastAttemptedOperation = null,
                RemoteOutcomeUncertain = false,
            };
            if (!recoveredCommit)
            {
                Audit(audit, "GH2007", $"Created server-side commit '{commit.Sha}'.");
            }
            await RecordProgressAsync(
                progress,
                state,
                SubmissionJournalState.CommitCreated).ConfigureAwait(false);
            attemptedMutation = null;

            await VerifyLiveReleaseFreshnessAsync(request, cancellationToken).ConfigureAwait(false);
            refreshedAnchor = await ReanchorUpstreamAsync(
                request,
                plan,
                upstreamDefault,
                targetRepository: null,
                checkPullRequestDuplicates: false,
                "pull request creation",
                audit,
                kind => attemptedMutation = kind,
                cancellationToken).ConfigureAwait(false);
            request = refreshedAnchor.Request;
            plan = refreshedAnchor.Plan;
            upstreamDefault = refreshedAnchor.Upstream;
            GitReference? prePullRequestBranch = await _gitHub.GetReferenceAsync(
                targetRepository.Coordinates,
                branchName,
                cancellationToken).ConfigureAwait(false);
            if (prePullRequestBranch is null
                || !string.Equals(prePullRequestBranch.Sha, commit.Sha, StringComparison.Ordinal))
            {
                return Result(
                    GitHubLifecycleResultCode.Conflict,
                    plan,
                    state,
                    audit,
                    [new("GH2020", "The validated branch moved immediately before pull request creation.")]);
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

            refreshedAnchor = await ReanchorUpstreamAsync(
                request,
                plan,
                upstreamDefault,
                targetRepository: null,
                checkPullRequestDuplicates: true,
                "final duplicate check",
                audit,
                kind => attemptedMutation = kind,
                cancellationToken).ConfigureAwait(false);
            request = refreshedAnchor.Request;
            plan = refreshedAnchor.Plan;
            upstreamDefault = refreshedAnchor.Upstream;
            prePullRequestBranch = await _gitHub.GetReferenceAsync(
                targetRepository.Coordinates,
                branchName,
                cancellationToken).ConfigureAwait(false);
            if (prePullRequestBranch is null
                || !string.Equals(prePullRequestBranch.Sha, commit.Sha, StringComparison.Ordinal))
            {
                return Result(
                    GitHubLifecycleResultCode.Conflict,
                    plan,
                    state,
                    audit,
                    [new("GH2033", "The validated branch moved during the final duplicate check.")]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            attemptedMutation = RemoteOperationKind.CreatePullRequest;
            state = state with
            {
                LastAttemptedOperation = RemoteOperationKind.CreatePullRequest,
                RemoteOutcomeUncertain = true,
            };
            await RecordProgressAsync(
                progress,
                state,
                SubmissionJournalState.CommitCreated).ConfigureAwait(false);
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
                LastAttemptedOperation = null,
                RemoteOutcomeUncertain = false,
            };
            await RecordProgressAsync(
                progress,
                state,
                SubmissionJournalState.PullRequestCreated).ConfigureAwait(false);
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

            try
            {
                refreshedAnchor = await ReanchorUpstreamAsync(
                    request,
                    plan,
                    upstreamDefault,
                    targetRepository: null,
                    checkPullRequestDuplicates: false,
                    "post-creation final verification",
                    audit,
                    kind => attemptedMutation = kind,
                    cancellationToken).ConfigureAwait(false);
                request = refreshedAnchor.Request;
                plan = refreshedAnchor.Plan;
                upstreamDefault = refreshedAnchor.Upstream;
            }
            catch (UpstreamRevalidationException exception)
            {
                plan = exception.Plan;
                return await CloseCreatedPullRequestAfterFinalValidationFailureAsync(
                    request,
                    plan,
                    targetRepository.Coordinates,
                    branchName,
                    upstreamDefault,
                    commit,
                    pullRequest,
                    state,
                    audit,
                    exception.ResultCode,
                    "final upstream revalidation invalidated the submission",
                    "upstream-revalidation-failure",
                    exception.Diagnostics,
                    cancellationToken).ConfigureAwait(false);
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
            if (freshPullRequest.State != PullRequestState.Open
                || !string.Equals(freshPullRequest.HeadOwner, targetRepository.Coordinates.Owner, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(freshPullRequest.HeadBranch, branchName, StringComparison.Ordinal)
                || !string.Equals(freshPullRequest.BaseBranch, upstreamDefault.Name, StringComparison.Ordinal)
                || !string.Equals(freshPullRequest.HeadSha, commit.Sha, StringComparison.Ordinal)
                || finalBranch is null
                || !string.Equals(finalBranch.Sha, commit.Sha, StringComparison.Ordinal))
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
                    [new("GH2021", "The pull request or validated branch moved before final verification.")]);
            }

            associated =
            [
                freshPullRequest,
                .. associated.Where(candidate => candidate.Number != freshPullRequest.Number),
            ];
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
                return await CloseCreatedPullRequestAfterFinalValidationFailureAsync(
                    request,
                    plan,
                    targetRepository.Coordinates,
                    branchName,
                    upstreamDefault,
                    commit,
                    pullRequest,
                    state,
                    audit,
                    GitHubLifecycleResultCode.ValidationFailed,
                    "final live artifact freshness validation failed",
                    "freshness-failure",
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
        catch (RepositorySubmissionEvidenceException exception)
        {
            return Result(
                GitHubLifecycleResultCode.HumanEscalationRequired,
                plan,
                state,
                audit,
                [new("GH2040", exception.Message)]);
        }
        catch (CommitRecoveryException exception)
        {
            return Result(
                GitHubLifecycleResultCode.HumanEscalationRequired,
                plan,
                state,
                audit,
                [new("GH2042", exception.Message)]);
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
        catch (UpstreamDuplicatePullRequestException exception)
        {
            plan = exception.Plan;
            return Result(
                GitHubLifecycleResultCode.DuplicatePullRequest,
                plan,
                state,
                audit,
                [new("GH2008", $"Pull request race detected; #{exception.PullRequest.Number} now covers this package version.")]);
        }
        catch (UpstreamRevalidationException exception)
        {
            plan = exception.Plan;
            return Result(
                exception.ResultCode,
                plan,
                state,
                audit,
                exception.Diagnostics);
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

            state = exception.IsConflict
                ? state with
                {
                    LastAttemptedOperation = null,
                    RemoteOutcomeUncertain = false,
                }
                : MarkUncertain(state, attemptedMutation);
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

    private async Task<GitHubLifecycleResult> ReconcileResumedPullRequestAsync(
        GitHubSubmissionRequest request,
        GitHubSubmissionPlan plan,
        RepositoryCoordinates target,
        BranchState upstream,
        RemoteMutationState state,
        ImmutableArray<GitHubLifecycleAuditEntry>.Builder audit,
        CancellationToken cancellationToken)
    {
        if (state.PullRequestNumber is null
            || state.CommitSha is null
            || state.BranchName is null
            || state.Fork is null)
        {
            return Result(
                GitHubLifecycleResultCode.HumanEscalationRequired,
                plan,
                state with { RemoteOutcomeUncertain = true },
                audit,
                [new("GH2036", "The pull-request recovery journal is incomplete.")]);
        }

        if (state.Fork != target)
        {
            return Result(
                GitHubLifecycleResultCode.Conflict,
                plan,
                state,
                audit,
                [new("GH2037", "The journaled fork differs from the intended recovery target.")]);
        }

        PullRequestInfo pullRequest = await _gitHub.GetPullRequestAsync(
            request.UpstreamRepository,
            state.PullRequestNumber.Value,
            cancellationToken).ConfigureAwait(false);
        GitReference? branch = await _gitHub.GetReferenceAsync(
            state.Fork,
            state.BranchName,
            cancellationToken).ConfigureAwait(false);
        if (pullRequest.State != PullRequestState.Open
            || branch is null
            || !string.Equals(branch.Sha, state.CommitSha, StringComparison.Ordinal)
            || !string.Equals(pullRequest.HeadSha, state.CommitSha, StringComparison.Ordinal)
            || !string.Equals(
                pullRequest.HeadOwner,
                state.Fork.Owner,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pullRequest.HeadBranch, state.BranchName, StringComparison.Ordinal)
            || !string.Equals(pullRequest.BaseBranch, upstream.Name, StringComparison.Ordinal))
        {
            return Result(
                GitHubLifecycleResultCode.HumanEscalationRequired,
                plan,
                state with { RemoteOutcomeUncertain = true },
                audit,
                [new("GH2038", "The journaled pull request no longer has its exact proven identity.")]);
        }

        Audit(audit, "GH2039", $"Recovered verified pull request #{pullRequest.Number}.");
        return Result(
            GitHubLifecycleResultCode.Succeeded,
            plan,
            state with
            {
                PullRequestUri = pullRequest.WebUri,
                PullRequestCreated = true,
            },
            audit,
            []);
    }

    private static Task RecordProgressAsync(
        ISubmissionProgressSink? progress,
        RemoteMutationState state,
        SubmissionJournalState journalState)
        => progress is null
            ? Task.CompletedTask
            : progress.RecordAsync(state, journalState, CancellationToken.None);

    private async Task<UpstreamAnchor> ReanchorUpstreamAsync(
        GitHubSubmissionRequest request,
        GitHubSubmissionPlan plan,
        BranchState upstream,
        RepositoryInfo? targetRepository,
        bool checkPullRequestDuplicates,
        string stage,
        ImmutableArray<GitHubLifecycleAuditEntry>.Builder audit,
        Action<RemoteOperationKind?> setAttemptedMutation,
        CancellationToken cancellationToken)
    {
        GitHubSubmissionRequest currentRequest = request;
        GitHubSubmissionPlan currentPlan = plan;
        string validatedSha = upstream.HeadSha;
        for (int attempt = 1; attempt <= MaximumUpstreamReanchorAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BranchState observed = await _gitHub.GetDefaultBranchAsync(
                currentRequest.UpstreamRepository,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(observed.Name, upstream.Name, StringComparison.Ordinal))
            {
                throw new UpstreamRevalidationException(
                    GitHubLifecycleResultCode.Conflict,
                    currentPlan,
                    [new("GH2044", "The upstream default branch name changed during scoped revalidation.")]);
            }

            bool moved = !string.Equals(observed.HeadSha, validatedSha, StringComparison.Ordinal);
            if (moved)
            {
                Audit(
                    audit,
                    "GH2043",
                    $"Re-anchoring {stage} from upstream '{validatedSha}' to '{observed.HeadSha}' " +
                    $"(attempt {attempt}/{MaximumUpstreamReanchorAttempts}).");
            }

            try
            {
                await VerifyRemoteFilePreconditionsAsync(
                    currentRequest,
                    observed.HeadSha,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteStateConflictException exception)
            {
                throw new UpstreamRevalidationException(
                    GitHubLifecycleResultCode.Conflict,
                    currentPlan,
                    [new("GH2044", exception.Message)]);
            }

            if (moved)
            {
                RepositorySubmissionEvidence repositoryEvidence =
                    await _repositoryEvidence.GetEvidenceAsync(
                        currentRequest,
                        observed.HeadSha,
                        cancellationToken).ConfigureAwait(false);
                currentRequest = RepositorySubmissionEvidenceMerger.Merge(
                    currentRequest,
                    repositoryEvidence);
                currentPlan = CreatePlan(currentRequest, _clock.UtcNow);
                if (!currentPlan.CanApply)
                {
                    throw new UpstreamRevalidationException(
                        GitHubLifecycleResultCode.InvalidPlan,
                        currentPlan,
                        currentPlan.Diagnostics);
                }

                if (checkPullRequestDuplicates)
                {
                    PullRequestInfo? duplicate = await FindDuplicateAsync(
                        currentPlan,
                        observed.Name,
                        cancellationToken).ConfigureAwait(false);
                    if (duplicate is not null)
                    {
                        throw new UpstreamDuplicatePullRequestException(currentPlan, duplicate);
                    }
                }

                validatedSha = observed.HeadSha;
            }

            BranchState? targetDefault = null;
            if (targetRepository is not null)
            {
                RepositoryInfo currentTarget = await _gitHub.GetRepositoryAsync(
                    targetRepository.Coordinates,
                    cancellationToken).ConfigureAwait(false);
                ValidateTarget(currentRequest.UpstreamRepository, currentTarget);
                setAttemptedMutation(RemoteOperationKind.SyncFork);
                await EnsureTargetDefaultIsFreshAsync(
                    currentRequest.UpstreamRepository,
                    currentTarget,
                    observed,
                    currentRequest.IdempotencyKey,
                    audit,
                    cancellationToken).ConfigureAwait(false);
                setAttemptedMutation(null);
                targetDefault = await _gitHub.GetDefaultBranchAsync(
                    currentTarget.Coordinates,
                    cancellationToken).ConfigureAwait(false);
            }

            BranchState confirmed = await _gitHub.GetDefaultBranchAsync(
                currentRequest.UpstreamRepository,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(confirmed.Name, observed.Name, StringComparison.Ordinal))
            {
                throw new UpstreamRevalidationException(
                    GitHubLifecycleResultCode.Conflict,
                    currentPlan,
                    [new("GH2044", "The upstream default branch name changed during scoped revalidation.")]);
            }

            bool upstreamStable = string.Equals(
                confirmed.HeadSha,
                observed.HeadSha,
                StringComparison.Ordinal);
            bool targetStable = targetDefault is null
                || string.Equals(
                    targetDefault.HeadSha,
                    observed.HeadSha,
                    StringComparison.Ordinal);
            if (upstreamStable && targetStable)
            {
                return new(currentRequest, currentPlan, observed);
            }

            if (attempt < MaximumUpstreamReanchorAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100 * attempt),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        throw new UpstreamRevalidationException(
            GitHubLifecycleResultCode.Conflict,
            currentPlan,
            [
                new(
                    "GH2045",
                    $"Upstream continued moving during {stage}; scoped revalidation exhausted " +
                    $"{MaximumUpstreamReanchorAttempts} bounded attempts."),
            ]);
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
            Mutation($"{idempotencyKey}:sync:{upstreamDefault.HeadSha}"),
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

        if (additionallyExcludedPullRequestNumber is { } additionallyExcluded)
        {
            associationExcludedPullRequestNumbers.Add(additionallyExcluded);
        }

        if (additionallyExcludedPullRequestNumber is null)
        {
            IReadOnlyList<PullRequestInfo> narrowedCandidates =
                await SearchNarrowedPullRequestCandidatesAsync(
                    plan,
                    expectedBaseBranch,
                    cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PullRequestInfo> narrowedAssociated =
                await FindAssociatedPullRequestsFromCandidatesAsync(
                    plan,
                    expectedBaseBranch,
                    associationExcludedPullRequestNumbers,
                    narrowedCandidates,
                    cancellationToken).ConfigureAwait(false);
            if (narrowedAssociated.Count > 0)
            {
                return narrowedAssociated;
            }
        }

        IReadOnlyList<PullRequestInfo> candidates;
        try
        {
            candidates = await _gitHub.SearchPullRequestsAsync(
                request.UpstreamRepository,
                new PullRequestSearch(
                    PullRequestState.Open,
                    BaseBranch: expectedBaseBranch)
                {
                    MaximumResults =
                        PullRequestManifestEvidenceLimits.MaximumOpenPullRequests
                        + associationExcludedPullRequestNumbers.Count,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException exception) when (exception.StatusCode is null)
        {
            throw new PullRequestEvidenceLimitException(
                "Pull request discovery failed a local transport safety bound: "
                + exception.Message);
        }

        return await FindAssociatedPullRequestsFromCandidatesAsync(
            plan,
            expectedBaseBranch,
            associationExcludedPullRequestNumbers,
            candidates,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PullRequestInfo>> SearchNarrowedPullRequestCandidatesAsync(
        GitHubSubmissionPlan plan,
        string expectedBaseBranch,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _gitHub.SearchPullRequestsByTextAsync(
                plan.Request.UpstreamRepository,
                new(
                    [
                        plan.Request.LocalPlan.PackageIdentifier.Value,
                        plan.Request.LocalPlan.PackageVersion.Value,
                    ],
                    PullRequestState.Open,
                    expectedBaseBranch)
                {
                    MaximumResults = PullRequestManifestEvidenceLimits.MaximumCandidates,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return [];
        }
        catch (GitHubApiException exception) when (
            exception.ErrorKind != GitHubApiErrorKind.RateLimited)
        {
            // Search is only an optimization. The exhaustive evidence pass below is the
            // authoritative fallback for index lag, incomplete results, and transport errors.
            return [];
        }
        catch (HttpRequestException exception) when (exception is not GitHubApiException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<PullRequestInfo>> FindAssociatedPullRequestsFromCandidatesAsync(
        GitHubSubmissionPlan plan,
        string expectedBaseBranch,
        HashSet<long> associationExcludedPullRequestNumbers,
        IReadOnlyList<PullRequestInfo> candidates,
        CancellationToken cancellationToken)
    {
        PullRequestInfo[] associationCandidates =
        [
            .. candidates.Where(pullRequest =>
                !associationExcludedPullRequestNumbers.Contains(pullRequest.Number)
                && pullRequest.State == PullRequestState.Open
                && string.Equals(
                    pullRequest.BaseBranch,
                    expectedBaseBranch,
                    StringComparison.Ordinal)),
        ];
        if (associationCandidates.Length == 0)
        {
            return [];
        }

        if (associationCandidates.Length > PullRequestManifestEvidenceLimits.MaximumOpenPullRequests)
        {
            throw new PullRequestEvidenceLimitException(
                "Open pull-request discovery exceeds the safe evidence limit of " +
                $"{PullRequestManifestEvidenceLimits.MaximumOpenPullRequests}.");
        }

        var associated = new List<PullRequestInfo>();
        IReadOnlyList<PullRequestInfo> evidenceCandidates =
            await _pullRequestEvidence.GetCandidatesAsync(
                plan,
                associationCandidates,
                cancellationToken).ConfigureAwait(false);
        HashSet<(
            long Number,
            string HeadOwner,
            string HeadSha,
            RepositoryCoordinates? HeadRepository,
            string BaseBranch,
            string? BaseSha)> allowed =
        [
            .. associationCandidates.Select(static pullRequest =>
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

        PullRequestManifestEvidence evidence = await _pullRequestEvidence.GetEvidenceAsync(
            plan,
            pullRequest,
            cancellationToken).ConfigureAwait(false);
        return evidence.IsAssociated;
    }

    private async Task<ServerCommitResult?> TryRecoverCommitAsync(
        GitHubSubmissionPlan plan,
        RepositoryInfo targetRepository,
        GitReference existing,
        BranchState upstreamDefault,
        CancellationToken cancellationToken)
    {
        CompareResult comparison = await _gitHub.CompareAsync(
            plan.Request.UpstreamRepository,
            upstreamDefault.HeadSha,
            $"{targetRepository.Coordinates.Owner}:{existing.Name}",
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(comparison.Status, "ahead", StringComparison.OrdinalIgnoreCase)
            || comparison.AheadBy != 1
            || comparison.BehindBy != 0
            || comparison.TotalCommits != 1)
        {
            return null;
        }

        IReadOnlyList<RepositoryTreeEntry> upstreamTree = await _gitHub.GetTreeAsync(
            plan.Request.UpstreamRepository,
            upstreamDefault.HeadSha,
            recursive: true,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RepositoryTreeEntry> candidateTree = await _gitHub.GetTreeAsync(
            targetRepository.Coordinates,
            existing.Sha,
            recursive: true,
            cancellationToken).ConfigureAwait(false);
        if (!HasOnlyPlannedRepositoryEntryChanges(
                plan.Request.LocalPlan.FileChanges,
                upstreamTree,
                candidateTree))
        {
            return null;
        }

        foreach (WorkflowFileChange change in plan.Request.LocalPlan.FileChanges)
        {
            RepositoryContent? content = await TryGetRepositoryContentAsync(
                targetRepository.Coordinates,
                change.RepositoryPath,
                existing.Sha,
                cancellationToken).ConfigureAwait(false);
            if (change.Kind == PlannedChangeKind.Delete)
            {
                if (content is not null)
                {
                    return null;
                }

                continue;
            }

            if (content is null
                || !content.Bytes.Span.SequenceEqual(change.Content.AsSpan()))
            {
                return null;
            }
        }

        Uri commitUri = new(
            targetRepository.WebUri.AbsoluteUri.TrimEnd('/') +
            "/commit/" +
            Uri.EscapeDataString(existing.Sha));
        return new(existing.Sha, commitUri);
    }

    private static bool HasOnlyPlannedRepositoryEntryChanges(
        ImmutableArray<WorkflowFileChange> changes,
        IReadOnlyList<RepositoryTreeEntry> upstreamTree,
        IReadOnlyList<RepositoryTreeEntry> candidateTree)
    {
        var expectedPaths = new HashSet<string>(
            changes.Select(static change => change.RepositoryPath),
            StringComparer.Ordinal);
        Dictionary<string, RepositoryTreeEntry> upstreamEntries = ToNonStructuralEntryMap(upstreamTree);
        Dictionary<string, RepositoryTreeEntry> candidateEntries = ToNonStructuralEntryMap(candidateTree);
        foreach ((string path, RepositoryTreeEntry upstream) in upstreamEntries)
        {
            if (expectedPaths.Contains(path))
            {
                continue;
            }

            if (!candidateEntries.TryGetValue(path, out RepositoryTreeEntry? candidate)
                || candidate.Type != upstream.Type
                || !string.Equals(candidate.Sha, upstream.Sha, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return candidateEntries.Keys.All(path =>
            expectedPaths.Contains(path) || upstreamEntries.ContainsKey(path));
    }

    private static Dictionary<string, RepositoryTreeEntry> ToNonStructuralEntryMap(
        IReadOnlyList<RepositoryTreeEntry> entries)
    {
        var entriesByPath = new Dictionary<string, RepositoryTreeEntry>(StringComparer.Ordinal);
        foreach (RepositoryTreeEntry entry in entries)
        {
            if (entry.Type == RepositoryTreeEntryType.Tree)
            {
                continue;
            }

            if (!entriesByPath.TryAdd(entry.Path, entry))
            {
                throw new RemoteStateConflictException(
                    "Commit recovery tree evidence contains an invalid or duplicate non-tree path.");
            }
        }

        return entriesByPath;
    }

    private async Task<RepositoryContent?> TryGetRepositoryContentAsync(
        RepositoryCoordinates repository,
        string path,
        string reference,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _gitHub.GetContentAsync(
                repository,
                path,
                reference,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
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

    private async Task<GitHubLifecycleResult> CloseCreatedPullRequestAfterFinalValidationFailureAsync(
        GitHubSubmissionRequest request,
        GitHubSubmissionPlan plan,
        RepositoryCoordinates targetRepository,
        string branchName,
        BranchState upstreamDefault,
        ServerCommitResult commit,
        PullRequestInfo createdPullRequest,
        RemoteMutationState state,
        ImmutableArray<GitHubLifecycleAuditEntry>.Builder audit,
        GitHubLifecycleResultCode resultCode,
        string closureReason,
        string idempotencyScope,
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
                        "Final validation invalidated the submission, but the created PR no longer has the exact proven tool-owned identity required for automatic closure."),
                ]);
        }

        RemoteOperationKind attempted = RemoteOperationKind.Comment;
        try
        {
            _ = await _gitHub.CommentOnPullRequestAsync(
                request.UpstreamRepository,
                freshPullRequest.Number,
                $"Closing this tool-owned PR because {closureReason}.",
                Mutation($"{request.IdempotencyKey}:{idempotencyScope}:comment"),
                cancellationToken).ConfigureAwait(false);
            state = state with { CommentCreated = true };
            attempted = RemoteOperationKind.ClosePullRequest;
            _ = await _gitHub.ClosePullRequestAsync(
                request.UpstreamRepository,
                freshPullRequest.Number,
                Mutation($"{request.IdempotencyKey}:{idempotencyScope}:close"),
                cancellationToken).ConfigureAwait(false);
            state = state with { PullRequestClosed = true };
            Audit(audit, "GH2029", $"Closed PR #{freshPullRequest.Number} because {closureReason}.");
            return Result(
                resultCode,
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

    private static bool IsRecoverableCommitResponseLoss(RemoteMutationState state)
        => state.RemoteOutcomeUncertain
            && state.LastAttemptedOperation == RemoteOperationKind.CreateCommit
            && state.Fork is not null
            && !string.IsNullOrWhiteSpace(state.BranchName)
            && !string.IsNullOrWhiteSpace(state.BranchHeadSha)
            && (state.BranchCreated || state.BranchAdopted)
            && !state.CommitCreated
            && !state.PullRequestCreated
            && state.CommitSha is null
            && state.PullRequestNumber is null;

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

    private sealed class UpstreamDuplicatePullRequestException(
        GitHubSubmissionPlan plan,
        PullRequestInfo pullRequest) : Exception
    {
        public GitHubSubmissionPlan Plan { get; } = plan;

        public PullRequestInfo PullRequest { get; } = pullRequest;
    }

    private sealed class UpstreamRevalidationException(
        GitHubLifecycleResultCode resultCode,
        GitHubSubmissionPlan plan,
        ImmutableArray<GitHubLifecycleDiagnostic> diagnostics) : Exception
    {
        public GitHubLifecycleResultCode ResultCode { get; } = resultCode;

        public GitHubSubmissionPlan Plan { get; } = plan;

        public ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics { get; } = diagnostics;
    }

    private sealed record UpstreamAnchor(
        GitHubSubmissionRequest Request,
        GitHubSubmissionPlan Plan,
        BranchState Upstream);

    private sealed class RemoteStateConflictException(string message) : Exception(message);

    private sealed class ForkConsentException(string message) : Exception(message);

    private sealed class CommitRecoveryException(string message) : Exception(message);
}
