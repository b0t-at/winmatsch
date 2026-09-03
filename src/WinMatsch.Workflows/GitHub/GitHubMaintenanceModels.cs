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

    public ImmutableArray<PullRequestChangedFile> ChangedFiles { get; init; } = [];

    public string? EvidenceHeadSha { get; init; }

    public string? EvidenceBaseSha { get; init; }

    public bool IsMerged { get; init; }

    public bool ToolOwned { get; init; }

    public bool HasAuthoritativeChangeEvidence =>
        PullRequest.HeadRepository is not null
        && PullRequest.BaseSha is not null
        && string.Equals(EvidenceHeadSha, PullRequest.HeadSha, StringComparison.Ordinal)
        && string.Equals(EvidenceBaseSha, PullRequest.BaseSha, StringComparison.Ordinal);
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

public interface IDeadVersionInspector : IDisposable
{
    void IDisposable.Dispose()
    {
        GC.SuppressFinalize(this);
    }

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

    /// <summary>Defender or the ESRP installer scan blocked the binaries; the same bytes fail again.</summary>
    ScannerBlocked,

    /// <summary>The installer signature does not chain to a trusted root (<c>Validation-Certificate-Root</c>).</summary>
    UntrustedCertificate,

    /// <summary>One or more manifest URLs failed upstream validation (<c>URL-Validation-Error</c>).</summary>
    UrlValidationError,

    /// <summary>The installer could not be downloaded during upstream validation (<c>Error-Installer-Availability</c>).</summary>
    InstallerUnavailable,

    /// <summary>Installation testing failed: unattended, shell-execute, driver or dependency (<c>Validation-Unattended-Failed</c> and siblings).</summary>
    InstallationFailure,

    /// <summary>The pipeline passed and a moderator must validate the executable by hand (<c>Validation-Executable-Error</c>, <c>Validation-No-Executables</c>).</summary>
    AwaitingManualValidation,
}

public sealed record FeedbackPolicy
{
    public TimeSpan StaleEscalationWindow { get; init; } = TimeSpan.FromDays(3);

    public bool ApplyKnownSafeResponses { get; init; }

    public ImmutableHashSet<string> TrustedCommentAuthors { get; init; } =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "wingetbot",
            "wingetbot[bot]",
            "winget-bot",
            "wingetvalidator-prod",
            "wingetvalidator-prod[bot]",
            "microsoft-github-policy-service",
            "microsoft-github-policy-service[bot]",
            "github-actions[bot]");

    public ImmutableHashSet<string> TrustedLabels { get; init; } =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "duplicate-entry",
            "hash-mismatch",
            "dependency-infrastructure",
            "transient-internal-error",
            "Error-Hash-Mismatch",
            "Validation-Hash-Verification-Failed",
            "Possible-Duplicate",
            "Resolution-Duplicate",
            "Validation-Defender-Error",
            "Binary-Validation-Error",
            "Validation-Certificate-Root",
            "URL-Validation-Error",
            "Error-Installer-Availability",
            "Validation-Unattended-Failed",
            "Validation-Installation-Error",
            "Validation-Shell-Execute",
            "Internal-Error",
            "Retry-1",
            "Validation-Executable-Error",
            "Validation-No-Executables");
}

public sealed record FeedbackRetryMetadata(
    long PullRequestNumber,
    FeedbackClassification Classification,
    DateTimeOffset RetryAfter,
    string? LearnedOverrideSignal);

public enum FeedbackWorkState
{
    AwaitingApprovedRepair,
    RetryScheduled,
    Completed,
    Escalated,
}

public sealed record FeedbackWorkItem(
    string Repository,
    long PullRequestNumber,
    FeedbackClassification Classification,
    FeedbackWorkState State,
    DateTimeOffset RecordedAt,
    DateTimeOffset? RetryAfter,
    string? LearnedOverrideSignal,
    string Reason);

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

public interface IFeedbackStateStore
{
    public Task PersistAsync(
        FeedbackWorkItem item,
        CancellationToken cancellationToken);

    public Task<ImmutableArray<FeedbackWorkItem>> GetPendingAsync(
        string repository,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => Task.FromResult(ImmutableArray<FeedbackWorkItem>.Empty);
}

public interface IPullRequestFeedbackSource
{
    public Task<ImmutableArray<PullRequestObservation>> GetOpenToolPullRequestsAsync(
        RepositoryCoordinates upstream,
        CancellationToken cancellationToken);
}
