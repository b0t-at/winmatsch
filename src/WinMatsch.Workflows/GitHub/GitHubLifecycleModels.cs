using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public enum GitHubLifecycleResultCode
{
    Succeeded,
    Planned,
    InvalidPlan,
    ConsentRequired,
    DuplicatePullRequest,
    DuplicateInstallerHash,
    Conflict,
    Cancelled,
    ValidationFailed,
    RemoteFailure,
    HumanEscalationRequired,
    NoAction,
}

public enum ForkConsentPolicy
{
    ExistingOnly,
    AllowCreate,
}

public enum GitHubManifestOperation
{
    New,
    Update,
    Add,
    Remove,
    Replace,
}

public enum RemoteOperationKind
{
    EnsureFork,
    CreateBranch,
    CreateCommit,
    CreatePullRequest,
    SyncFork,
    Comment,
    ClosePullRequest,
    DeleteBranch,
}

public sealed record GitHubLifecycleDiagnostic(
    string Code,
    string Message,
    string? Path = null);

public sealed record PlannedRemoteOperation(
    RemoteOperationKind Kind,
    string Target,
    string Description);

public sealed record GitHubLifecycleAuditEntry(
    DateTimeOffset Timestamp,
    string Code,
    string Message);

public sealed record RemoteMutationState
{
    public RepositoryCoordinates? Fork { get; init; }

    public string? BranchName { get; init; }

    public string? BranchHeadSha { get; init; }

    public string? CommitSha { get; init; }

    public Uri? CommitUri { get; init; }

    public long? PullRequestNumber { get; init; }

    public Uri? PullRequestUri { get; init; }

    public bool ForkCreated { get; init; }

    public bool BranchCreated { get; init; }

    public bool CommitCreated { get; init; }

    public bool PullRequestCreated { get; init; }

    public bool PullRequestClosed { get; init; }

    public bool CommentCreated { get; init; }

    public RemoteOperationKind? LastAttemptedOperation { get; init; }

    public bool RemoteOutcomeUncertain { get; init; }
}

public sealed record RepositoryInstallerEvidence(
    PackageIdentifier PackageIdentifier,
    PackageVersion PackageVersion,
    string InstallerSha256,
    string ManifestPath,
    bool RetiredIdentifier = false);

public sealed record DuplicateHashPolicy
{
    public ImmutableHashSet<string> DeniedSha256 { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    public ImmutableHashSet<string> AllowedSha256 { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    public string? OverrideAnnotation { get; init; }
}

public sealed record GitHubSubmissionPolicy
{
    public ForkConsentPolicy ForkConsent { get; init; } = ForkConsentPolicy.ExistingOnly;

    public bool SkipPullRequestCheck { get; init; }

    public bool ReplacePreviousVersion { get; init; }

    public PackageVersion? PreviousVersion { get; init; }

    public TimeSpan MinimumReleaseFreshness { get; init; }

    public DuplicateHashPolicy DuplicateHashes { get; init; } = new();
}

public sealed record GitHubSubmissionRequest
{
    public required LocalOperationPlan LocalPlan { get; init; }

    public required RepositoryCoordinates UpstreamRepository { get; init; }

    public RepositoryCoordinates? TargetRepository { get; init; }

    public string? ForkOwner { get; init; }

    public WorkflowExecutionMode ExecutionMode { get; init; } = WorkflowExecutionMode.Plan;

    public GitHubManifestOperation Operation { get; init; } = GitHubManifestOperation.Update;

    public GitHubSubmissionPolicy Policy { get; init; } = new();

    public string CreatedWith { get; init; } = "winmatsch";

    public string? CustomTitle { get; init; }

    public string? Resolves { get; init; }

    public long? SupersedesPullRequestNumber { get; init; }

    public string IdempotencyKey { get; init; } = Guid.NewGuid().ToString("N");

    public ImmutableArray<RepositoryInstallerEvidence> RepositoryEvidence { get; init; } = [];

    public ImmutableArray<string> VanityUrlAnnotations { get; init; } = [];

    public DateTimeOffset? ReleaseUpdatedAt { get; init; }

    public RepositoryCoordinates? ReleaseRepository { get; init; }

    public long? ReleaseId { get; init; }
}

public sealed record GitHubSubmissionPlan
{
    public required GitHubSubmissionRequest Request { get; init; }

    public required string CommitTitle { get; init; }

    public required string PullRequestTitle { get; init; }

    public required string PullRequestBody { get; init; }

    public required string PackageVersionDirectory { get; init; }

    public ImmutableArray<PlannedRemoteOperation> Operations { get; init; } = [];

    public ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics { get; init; } = [];

    public bool CanApply => Request.LocalPlan.CanApply && Diagnostics.IsEmpty && Operations.Length > 0;
}

public sealed record GitHubLifecycleResult
{
    public required GitHubLifecycleResultCode Code { get; init; }

    public required GitHubSubmissionPlan Plan { get; init; }

    public RemoteMutationState RemoteState { get; init; } = new();

    public ImmutableArray<GitHubLifecycleAuditEntry> Audit { get; init; } = [];

    public ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics { get; init; } = [];

    public bool Applied => Code == GitHubLifecycleResultCode.Succeeded
        && RemoteState.PullRequestCreated;
}

public sealed record GitHubLifecycleOutput(
    GitHubLifecycleResultCode Code,
    ImmutableArray<PlannedRemoteOperation> Operations,
    RemoteMutationState RemoteState,
    ImmutableArray<GitHubLifecycleAuditEntry> Audit,
    ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics)
{
    public static GitHubLifecycleOutput FromResult(GitHubLifecycleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            result.Code,
            result.Plan.Operations,
            result.RemoteState,
            result.Audit,
            result.Diagnostics);
    }
}

public sealed record FinalArtifactRevalidationResult(
    bool IsValid,
    ImmutableArray<GitHubLifecycleDiagnostic> Diagnostics)
{
    public static FinalArtifactRevalidationResult Valid { get; } = new(true, []);
}

public sealed record RemoteOperationLockOptions
{
    public string RootDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "winmatsch-remote-operation-locks");

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(2);
}

public sealed record GitHubBranchNameContext(
    PackageIdentifier PackageIdentifier,
    PackageVersion PackageVersion,
    GitHubManifestOperation Operation,
    long? SupersedesPullRequestNumber);
