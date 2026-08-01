using System.Collections.Immutable;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public sealed class GitHubMaintenanceWorkflow
{
    private readonly IGitHubRepositoryClient _gitHub;
    private readonly IWorkflowClock _clock;

    public GitHubMaintenanceWorkflow(
        IGitHubRepositoryClient gitHub,
        IWorkflowClock? clock = null)
    {
        _gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        _clock = clock ?? new SystemWorkflowClock();
    }

    public async Task<GitHubMaintenanceResult> SyncAsync(
        GitHubSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        BranchState upstream = await _gitHub.GetDefaultBranchAsync(
            request.Upstream,
            cancellationToken).ConfigureAwait(false);
        BranchState fork = await _gitHub.GetDefaultBranchAsync(
            request.Fork,
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(upstream.HeadSha, fork.HeadSha, StringComparison.Ordinal))
        {
            return Result(GitHubLifecycleResultCode.NoAction, "sync");
        }

        CompareResult comparison = await _gitHub.CompareAsync(
            request.Upstream,
            upstream.Name,
            $"{request.Fork.Owner}:{fork.Name}",
            cancellationToken).ConfigureAwait(false);
        if (comparison.AheadBy > 0 || comparison.Status is "ahead" or "diverged")
        {
            return Result(
                GitHubLifecycleResultCode.Conflict,
                "sync",
                diagnostics:
                [
                    new("GH3001", "Fork default branch has unknown user commits and will not be force-updated."),
                ]);
        }

        GitHubMaintenancePlan plan = new()
        {
            Operation = "sync",
            Operations =
            [
                new(RemoteOperationKind.SyncFork, request.Fork.ToString(), "Merge upstream into the fork default branch."),
            ],
        };
        if (request.ExecutionMode == WorkflowExecutionMode.Plan)
        {
            return new() { Code = GitHubLifecycleResultCode.Planned, Plan = plan };
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = await _gitHub.SyncForkAsync(
            request.Fork,
            fork.Name,
            new MutationRequest($"{request.IdempotencyKey}:sync"),
            cancellationToken).ConfigureAwait(false);
        BranchState refreshed = await _gitHub.GetDefaultBranchAsync(
            request.Fork,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(refreshed.HeadSha, upstream.HeadSha, StringComparison.Ordinal))
        {
            return new()
            {
                Code = GitHubLifecycleResultCode.Conflict,
                Plan = plan,
                Diagnostics = [new("GH3002", "Fork synchronization did not produce the exact upstream head.")],
            };
        }

        return new()
        {
            Code = GitHubLifecycleResultCode.Succeeded,
            Plan = plan,
            Audit = [Audit("GH3003", "Synchronized fork default branch.")],
        };
    }

    public async Task<GitHubMaintenanceResult> CleanupAsync(
        GitHubCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BranchState> branches = await _gitHub.GetBranchesAsync(
            request.Fork,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PullRequestInfo> pullRequests = await _gitHub.SearchPullRequestsAsync(
            request.Upstream,
            new PullRequestSearch(PullRequestState.All, HeadOwner: request.Fork.Owner),
            cancellationToken).ConfigureAwait(false);
        var candidates = new List<(BranchState Branch, PullRequestInfo PullRequest)>();
        foreach (BranchState branch in branches.Where(branch =>
                     branch.Name.StartsWith(request.ToolBranchPrefix, StringComparison.Ordinal)))
        {
            PullRequestInfo? pullRequest = pullRequests.SingleOrDefault(pr =>
                string.Equals(pr.HeadOwner, request.Fork.Owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pr.HeadBranch, branch.Name, StringComparison.Ordinal)
                && pr.Body?.Contains("<!-- winmatsch:package=", StringComparison.Ordinal) == true);
            if (pullRequest is not null
                && pullRequest.State == PullRequestState.Closed
                && string.Equals(pullRequest.HeadSha, branch.HeadSha, StringComparison.Ordinal))
            {
                candidates.Add((branch, pullRequest));
            }
        }

        GitHubMaintenancePlan plan = new()
        {
            Operation = "cleanup",
            Operations =
            [
                .. candidates.Select(candidate => new PlannedRemoteOperation(
                    RemoteOperationKind.DeleteBranch,
                    $"{request.Fork}:{candidate.Branch.Name}",
                    $"Delete tool-owned branch associated with closed PR #{candidate.PullRequest.Number}.")),
            ],
        };
        if (candidates.Count == 0)
        {
            return new() { Code = GitHubLifecycleResultCode.NoAction, Plan = plan };
        }

        if (request.ExecutionMode == WorkflowExecutionMode.Plan)
        {
            return new() { Code = GitHubLifecycleResultCode.Planned, Plan = plan };
        }

        var audit = ImmutableArray.CreateBuilder<GitHubLifecycleAuditEntry>();
        foreach ((BranchState candidate, PullRequestInfo pullRequest) in candidates)
        {
            PullRequestInfo freshPullRequest = await _gitHub.GetPullRequestAsync(
                request.Upstream,
                pullRequest.Number,
                cancellationToken).ConfigureAwait(false);
            GitReference? fresh = await _gitHub.GetReferenceAsync(
                request.Fork,
                candidate.Name,
                cancellationToken).ConfigureAwait(false);
            if (fresh is null
                || freshPullRequest.State != PullRequestState.Closed
                || !string.Equals(fresh.Sha, candidate.HeadSha, StringComparison.Ordinal)
                || !string.Equals(freshPullRequest.HeadSha, candidate.HeadSha, StringComparison.Ordinal))
            {
                return new()
                {
                    Code = GitHubLifecycleResultCode.Conflict,
                    Plan = plan,
                    Audit = audit.ToImmutable(),
                    Diagnostics = [new("GH3004", $"Branch '{candidate.Name}' changed during cleanup.")],
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = await _gitHub.DeleteReferenceAsync(
                request.Fork,
                candidate.Name,
                new MutationRequest($"{request.IdempotencyKey}:delete:{candidate.Name}"),
                cancellationToken).ConfigureAwait(false);
            audit.Add(Audit("GH3005", $"Deleted proven tool-owned stale branch '{candidate.Name}'."));
        }

        return new()
        {
            Code = GitHubLifecycleResultCode.Succeeded,
            Plan = plan,
            Audit = audit.ToImmutable(),
        };
    }

    public static GitHubCompleteResult Complete(IEnumerable<PullRequestObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return new(
            [
                .. observations.Select(static observation =>
                {
                    PullRequestInfo pullRequest = observation.PullRequest;
                    if (!observation.ToolOwned)
                    {
                        return new PullRequestLifecycleStatus(
                            pullRequest.Number,
                            "unowned",
                            PullRequestLifecycleAction.None,
                            "The pull request is not tool-owned.");
                    }

                    if (observation.IsMerged)
                    {
                        return new(
                            pullRequest.Number,
                            "merged",
                            PullRequestLifecycleAction.None,
                            "The pull request is merged.");
                    }

                    if (pullRequest.State == PullRequestState.Closed)
                    {
                        return new(
                            pullRequest.Number,
                            "closed",
                            PullRequestLifecycleAction.None,
                            "The pull request is closed.");
                    }

                    return new(
                        pullRequest.Number,
                        "open",
                        PullRequestLifecycleAction.Wait,
                        "The pull request remains open.");
                }),
            ],
            []);
    }

    public async Task<GitHubMaintenanceResult> CloseSupersededAsync(
        RepositoryCoordinates upstream,
        PullRequestObservation oldPullRequest,
        PullRequestInfo replacement,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        GitHubMaintenancePlan plan = new()
        {
            Operation = "close-superseded",
            Operations =
            [
                new(RemoteOperationKind.Comment, oldPullRequest.PullRequest.WebUri.ToString(), "Cross-link the replacement PR."),
                new(RemoteOperationKind.ClosePullRequest, oldPullRequest.PullRequest.WebUri.ToString(), "Close the superseded tool PR."),
            ],
        };
        if (!oldPullRequest.ToolOwned
            || !oldPullRequest.PullRequest.HeadBranch.StartsWith("winmatsch/", StringComparison.Ordinal)
            || oldPullRequest.PullRequest.Body?.Contains(
                "<!-- winmatsch:package=",
                StringComparison.Ordinal) != true)
        {
            return new()
            {
                Code = GitHubLifecycleResultCode.InvalidPlan,
                Plan = plan,
                Diagnostics = [new("GH3006", "Only tool-owned pull requests may be closed as superseded.")],
            };
        }

        PullRequestInfo freshReplacement = await _gitHub.GetPullRequestAsync(
            upstream,
            replacement.Number,
            cancellationToken).ConfigureAwait(false);
        PullRequestInfo freshOld = await _gitHub.GetPullRequestAsync(
            upstream,
            oldPullRequest.PullRequest.Number,
            cancellationToken).ConfigureAwait(false);
        if (freshReplacement.Number == freshOld.Number
            || freshReplacement.State != PullRequestState.Open
            || freshOld.State != PullRequestState.Open
            || !string.Equals(
                freshOld.HeadOwner,
                oldPullRequest.PullRequest.HeadOwner,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                freshOld.HeadBranch,
                oldPullRequest.PullRequest.HeadBranch,
                StringComparison.Ordinal)
            || freshOld.Body?.Contains("<!-- winmatsch:package=", StringComparison.Ordinal) != true)
        {
            return new()
            {
                Code = GitHubLifecycleResultCode.Conflict,
                Plan = plan,
                Diagnostics = [new("GH3007", "Replacement must exist and both pull requests must still be open.")],
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = await _gitHub.CommentOnPullRequestAsync(
            upstream,
            freshOld.Number,
            $"Superseded by #{freshReplacement.Number}. Closing this tool-owned PR for the stable reason `superseded`.",
            new MutationRequest($"{idempotencyKey}:superseded-comment"),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await _gitHub.ClosePullRequestAsync(
            upstream,
            freshOld.Number,
            new MutationRequest($"{idempotencyKey}:superseded-close"),
            cancellationToken).ConfigureAwait(false);
        return new()
        {
            Code = GitHubLifecycleResultCode.Succeeded,
            Plan = plan,
            Audit = [Audit("GH3008", $"Closed tool-owned PR #{freshOld.Number} as superseded by #{freshReplacement.Number}.")],
        };
    }

    private GitHubLifecycleAuditEntry Audit(string code, string message)
        => new(_clock.UtcNow, code, message);

    private static GitHubMaintenanceResult Result(
        GitHubLifecycleResultCode code,
        string operation,
        ImmutableArray<GitHubLifecycleDiagnostic>? diagnostics = null)
        => new()
        {
            Code = code,
            Plan = new() { Operation = operation },
            Diagnostics = diagnostics ?? [],
        };
}

public sealed class RemoveDeadVersionsWorkflow(IDeadVersionInspector inspector)
{
    private readonly IDeadVersionInspector _inspector =
        inspector ?? throw new ArgumentNullException(nameof(inspector));

    public async Task<ImmutableArray<RemoveDeadVersionPlan>> PlanAsync(
        RemoveDeadVersionsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Versions.Length > 1 && !request.AllowGroupingByRepositoryPolicy)
        {
            return
            [
                .. request.Versions.Select(version => new RemoveDeadVersionPlan(
                    version.PackageIdentifier,
                    version.PackageVersion,
                    false,
                    [new("GH3101", "Dead-version removals require one version per PR unless repository policy allows grouping.")]))
            ];
        }

        var plans = ImmutableArray.CreateBuilder<RemoveDeadVersionPlan>();
        foreach ((WinMatsch.Core.PackageIdentifier packageIdentifier, WinMatsch.Core.PackageVersion packageVersion) in request.Versions)
        {
            DeadVersionInspection inspection = await _inspector.InspectAsync(
                request.Upstream,
                packageIdentifier,
                packageVersion,
                cancellationToken).ConfigureAwait(false);
            var diagnostics = ImmutableArray.CreateBuilder<GitHubLifecycleDiagnostic>();
            if (!inspection.ExistsUpstream)
            {
                diagnostics.Add(new("GH3102", "The target version no longer exists upstream."));
            }

            if (inspection.ArtifactStates.Any(static state =>
                    state is DeadArtifactState.TransientFailure or DeadArtifactState.NetworkBlocked))
            {
                diagnostics.Add(new("GH3103", "Transient or network-blocked failures never prove a dead version."));
            }

            if (inspection.ArtifactStates.IsEmpty
                || inspection.ArtifactStates.Any(static state => state == DeadArtifactState.Exists))
            {
                diagnostics.Add(new("GH3104", "At least one installer artifact still exists."));
            }

            plans.Add(new(packageIdentifier, packageVersion, diagnostics.Count == 0, diagnostics.ToImmutable()));
        }

        return plans.ToImmutable();
    }
}
