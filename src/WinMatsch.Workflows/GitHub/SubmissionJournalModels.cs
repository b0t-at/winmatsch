using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Validation;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public enum SubmissionJournalState
{
    Pending,
    BranchCreated,
    CommitCreated,
    PullRequestCreated,
    EscalationRequired,
    Cancelled,
}

public sealed record SubmissionRepositoryIdentity(
    string CanonicalPath,
    string FileSystemIdentity);

public sealed record SubmissionJournalFileIdentity(
    PlannedChangeKind Kind,
    string RepositoryPath,
    ExpectedFileState ExpectedState,
    string? ExpectedSha256,
    WorkflowChangeProvenance Provenance,
    string? CommittedSha256,
    long CommittedLength);

public sealed record SubmissionJournalDocumentIdentity(
    string RepositoryPath,
    string Sha256,
    long Length);

public sealed record SubmissionJournalArtifactIdentity(
    string InstallerUrlSha256,
    string ContentSha256,
    long SizeInBytes);

public sealed record SubmissionJournalExistingVersion(
    string PackageVersion,
    ImmutableArray<string> DisplayVersions);

public sealed record SubmissionJournalLocalPlan
{
    public required string Operation { get; init; }

    public required PackageIdentifier PackageIdentifier { get; init; }

    public required PackageVersion PackageVersion { get; init; }

    public required string Fingerprint { get; init; }

    public required string PlanningInputsFingerprint { get; init; }

    public required string RuleEvaluationFingerprint { get; init; }

    public required string ValidationFingerprint { get; init; }

    public required string AuditFingerprint { get; init; }

    public required string PreflightEvidenceFingerprint { get; init; }

    public string? LearnedOverrideFingerprint { get; init; }

    public WarningPolicy WarningPolicy { get; init; }

    public NetworkValidationMode NetworkMode { get; init; }

    public bool ReviewApproved { get; init; }

    public string? LearnedOverrideContentSha256 { get; init; }

    public ImmutableArray<SubmissionJournalFileIdentity> FileChanges { get; init; } = [];

    public ImmutableArray<SubmissionJournalDocumentIdentity> BeforeDocuments { get; init; } = [];

    public ImmutableArray<SubmissionJournalDocumentIdentity> AfterDocuments { get; init; } = [];

    public ImmutableArray<SubmissionJournalArtifactIdentity> InstallerArtifacts { get; init; } = [];

    public ImmutableArray<SubmissionJournalExistingVersion> ExistingVersions { get; init; } = [];

    public WorkflowReleaseProvenance? Release { get; init; }
}

public sealed record SubmissionJournalRemoteRequest
{
    public required RepositoryCoordinates UpstreamRepository { get; init; }

    public RepositoryCoordinates? TargetRepository { get; init; }

    public string? ForkOwner { get; init; }

    public GitHubManifestOperation Operation { get; init; }

    public GitHubSubmissionPolicy Policy { get; init; } = new();

    public string CreatedWith { get; init; } = "winmatsch";

    public string? CustomTitle { get; init; }

    public string? Resolves { get; init; }

    public long? SupersedesPullRequestNumber { get; init; }

    public required string IdempotencyKey { get; init; }

    public ImmutableArray<RepositoryInstallerEvidence> RepositoryEvidence { get; init; } = [];

    public ImmutableArray<string> VanityUrlAnnotations { get; init; } = [];

    public DateTimeOffset? ReleaseUpdatedAt { get; init; }

    public RepositoryCoordinates? ReleaseRepository { get; init; }

    public long? ReleaseId { get; init; }

    public required GitHubSubmissionPresentation Presentation { get; init; }
}

public sealed record SubmissionJournalEntry
{
    public required string Id { get; init; }

    public long Revision { get; init; }

    public required SubmissionRepositoryIdentity Repository { get; init; }

    public required SubmissionJournalLocalPlan LocalPlan { get; init; }

    public required SubmissionJournalRemoteRequest RemoteRequest { get; init; }

    public SubmissionJournalState State { get; init; } = SubmissionJournalState.Pending;

    public RemoteMutationState RemoteState { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string? LastError { get; init; }
}

public sealed record SubmissionJournalHandle(
    string Id,
    string LocalPlanFingerprint);

public sealed record SubmissionJournalRecoveryResult(
    ImmutableArray<SubmissionJournalEntry> Activated,
    ImmutableArray<string> Diagnostics);

public sealed record SubmissionJournalOptions
{
    public string RootDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "winmatsch",
        "submission-journals");

    public string? OverrideStoreDirectory { get; init; }
}

public sealed class SubmissionJournalConflictException : IOException
{
    public SubmissionJournalConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class SubmissionJournalTamperedException : IOException
{
    public SubmissionJournalTamperedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface ISubmissionJournalStore
{
    public Task<SubmissionJournalHandle> PrepareAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken);

    public Task<SubmissionJournalEntry> ActivateAsync(
        SubmissionJournalHandle handle,
        CancellationToken cancellationToken);

    public Task<SubmissionJournalRecoveryResult> RecoverAsync(
        string outputDirectory,
        CancellationToken cancellationToken);

    public Task<ImmutableArray<SubmissionJournalEntry>> ListPendingAsync(
        CancellationToken cancellationToken);

    public Task<SubmissionJournalEntry?> GetAsync(
        string id,
        CancellationToken cancellationToken);

    public Task<SubmissionJournalEntry> RecordRemoteStateAsync(
        string id,
        long expectedRevision,
        RemoteMutationState remoteState,
        SubmissionJournalState state,
        string? errorMessage,
        CancellationToken cancellationToken);

    public Task CancelAsync(
        string id,
        long expectedRevision,
        CancellationToken cancellationToken);

    public Task CompleteAsync(
        string id,
        long expectedRevision,
        CancellationToken cancellationToken);
}
