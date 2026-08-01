using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.GitHub;

namespace WinMatsch.Workflows.GitHub;

public sealed record GitHubMaintenancePlan
{
    public required string Operation { get; init; }

    public ImmutableArray<PlannedRemoteOperation> Operations { get; init; } = [];

    public ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics { get; init; } = [];

    public bool CanApply => Diagnostics.IsEmpty && !Operations.IsEmpty;
}

public sealed record GitHubMaintenanceResult
{
    public required GitHubLifecycleResultCode Code { get; init; }

    public required GitHubMaintenancePlan Plan { get; init; }

    public ImmutableArray<GitHubLifecycleAuditEntry> Audit { get; init; } = [];

    public ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics { get; init; } = [];

    public RemoteMutationState RemoteState { get; init; } = new();
}

public sealed record GitHubSyncRequest(
    RepositoryCoordinates Upstream,
    RepositoryCoordinates Fork,
    WorkflowExecutionMode ExecutionMode,
    string IdempotencyKey);

public sealed record GitHubCleanupRequest(
    RepositoryCoordinates Upstream,
    RepositoryCoordinates Fork,
    WorkflowExecutionMode ExecutionMode,
    string IdempotencyKey,
    string ToolBranchPrefix = "winmatsch/");

public sealed record PullRequestObservation
{
    public required PullRequestInfo PullRequest { get; init; }

    public required string Author { get; init; }

    public ImmutableArray<string> Labels { get; init; } = [];

    public ImmutableArray<PullRequestCommentObservation> Comments { get; init; } = [];

    public bool IsMerged { get; init; }

    public bool ToolOwned { get; init; }
}

public sealed record PullRequestCommentObservation(
    string Author,
    string Body,
    DateTimeOffset CreatedAt);

public enum PullRequestLifecycleAction
{
    None,
    Wait,
    RepairManifest,
    RerunChecks,
    CommentKeepAlive,
    EscalateToHuman,
    CloseSuperseded,
}

public sealed record PullRequestLifecycleStatus(
    long PullRequestNumber,
    string Status,
    PullRequestLifecycleAction RecommendedAction,
    string Reason);

public sealed record GitHubCompleteResult(
    ImmutableArray<PullRequestLifecycleStatus> PullRequests,
    ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics);

public enum DeadArtifactState
{
    Exists,
    PermanentlyMissing,
    TransientFailure,
    NetworkBlocked,
}

public sealed record DeadVersionInspection(
    PackageIdentifier PackageIdentifier,
    PackageVersion PackageVersion,
    bool ExistsUpstream,
    ImmutableArray<DeadArtifactState> ArtifactStates);

public interface IDeadVersionInspector
{
    public Task<DeadVersionInspection> InspectAsync(
        RepositoryCoordinates upstream,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken);
}

public sealed record RemoveDeadVersionsRequest(
    RepositoryCoordinates Upstream,
    ImmutableArray<(PackageIdentifier PackageIdentifier, PackageVersion PackageVersion)> Versions,
    bool AllowGroupingByRepositoryPolicy = false);

public sealed record RemoveDeadVersionPlan(
    PackageIdentifier PackageIdentifier,
    PackageVersion PackageVersion,
    bool CanRemove,
    ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics);

public enum FeedbackClassification
{
    None,
    DuplicateEntry,
    HashMismatch,
    DependencyInfrastructureOutage,
    TransientInternalError,
    Unknown,
}

public sealed record FeedbackPolicy
{
    public TimeSpan StaleEscalationWindow { get; init; } = TimeSpan.FromDays(3);

    public bool ApplyKnownSafeResponses { get; init; }

    public ImmutableHashSet<string> TrustedCommentAuthors { get; init; } =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "wingetbot",
            "winget-bot",
            "github-actions[bot]");

    public ImmutableHashSet<string> TrustedLabels { get; init; } =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "duplicate-entry",
            "hash-mismatch",
            "dependency-infrastructure",
            "transient-internal-error");
}

public sealed record FeedbackRetryMetadata(
    long PullRequestNumber,
    FeedbackClassification Classification,
    DateTimeOffset RetryAfter,
    string? LearnedOverrideSignal);

public sealed record FeedbackRemoteState(
    long PullRequestNumber,
    RemoteMutationState State);

public sealed record SupersessionResult(
    GitHubLifecycleDiagnostic? Diagnostic,
    RemoteMutationState State);

public sealed record FeedbackResult(
    ImmutableArray<PullRequestLifecycleStatus> Statuses,
    ImmutableArray<FeedbackRetryMetadata> RetryMetadata,
    ImmutableArray<FeedbackRemoteState> RemoteStates,
    ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics);

public interface IApprovedRepairPlanner
{
    public Task<GitHubSubmissionRequest?> PlanApprovedRepairAsync(
        PullRequestObservation pullRequest,
        FeedbackClassification classification,
        CancellationToken cancellationToken);
}

public interface IPullRequestFeedbackSource
{
    public Task<ImmutableArray<PullRequestObservation>> GetOpenToolPullRequestsAsync(
        RepositoryCoordinates upstream,
        CancellationToken cancellationToken);
}
