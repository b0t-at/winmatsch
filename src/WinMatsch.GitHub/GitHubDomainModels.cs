namespace WinMatsch.GitHub;

public sealed record GitHubUser(
    string Login,
    string? Name,
    string? Email,
    Uri AvatarUri);

public sealed record RepositoryInfo(
    RepositoryCoordinates Coordinates,
    string NodeId,
    Uri WebUri,
    bool IsPrivate,
    bool IsFork,
    BranchState DefaultBranch,
    RepositoryCoordinates? Parent);

public sealed record BranchState(
    string Name,
    string HeadSha,
    bool IsProtected);

public sealed record RepositoryContent(
    string Name,
    string Path,
    string Sha,
    long Size,
    string Encoding,
    ReadOnlyMemory<byte> Bytes)
{
    public string GetText()
        => System.Text.Encoding.UTF8.GetString(Bytes.Span);
}

public enum RepositoryTreeEntryType
{
    Blob,
    Tree,
    Commit,
}

public sealed record RepositoryTreeEntry(
    string Path,
    string Sha,
    RepositoryTreeEntryType Type,
    long? Size);

public sealed record ManifestFile(
    string Path,
    string Sha,
    ReadOnlyMemory<byte> Bytes)
{
    public string GetText()
        => System.Text.Encoding.UTF8.GetString(Bytes.Span);
}

public sealed record ReleaseAsset(
    long Id,
    string Name,
    Uri DownloadUri,
    string ContentType,
    long Size,
    int DownloadCount,
    DateTimeOffset CreatedAt);

public sealed record GitHubRelease(
    long Id,
    string TagName,
    string Name,
    string? Body,
    Uri WebUri,
    bool IsDraft,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record GitReference(
    string Name,
    string Sha);

public sealed record CommitFileAddition(
    string Path,
    ReadOnlyMemory<byte> Contents);

public sealed record ServerCommitRequest(
    string BranchName,
    string ExpectedHeadSha,
    string Headline,
    string? Body,
    IReadOnlyList<CommitFileAddition> Additions,
    IReadOnlyList<string> Deletions);

public sealed record ServerCommitResult(
    string Sha,
    Uri WebUri);

public sealed record ComparedCommit(
    string Sha,
    string Message,
    Uri WebUri);

public sealed record CompareResult(
    string Status,
    int AheadBy,
    int BehindBy,
    int TotalCommits,
    IReadOnlyList<ComparedCommit> Commits);

public sealed record ForkResult(
    RepositoryInfo Repository,
    bool AlreadyExisted);

public sealed record UpstreamSyncResult(
    string Message,
    string MergeType,
    string? HeadSha);

public enum PullRequestState
{
    Open,
    Closed,
    All,
}

public sealed record PullRequestSearch(
    PullRequestState State = PullRequestState.Open,
    string? HeadOwner = null,
    string? HeadBranch = null,
    string? BaseBranch = null,
    string? ExactTitleToken = null);

public sealed record PullRequestInfo(
    long Number,
    string NodeId,
    string Title,
    string? Body,
    PullRequestState State,
    bool IsDraft,
    string HeadOwner,
    string HeadBranch,
    string HeadSha,
    string BaseBranch,
    Uri WebUri,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreatePullRequestRequest(
    string Title,
    string? Body,
    string HeadOwner,
    string HeadBranch,
    string BaseBranch,
    bool Draft = false);

public sealed record PullRequestComment(
    long Id,
    string Body,
    Uri WebUri,
    DateTimeOffset CreatedAt);

/// <summary>
/// Identifies a logical mutation. Reusing the key for the same operation returns the original
/// in-process outcome, including an uncertain transport failure or cancellation, so an unsafe
/// mutation is never silently submitted twice. Reusing it for different inputs is rejected.
/// </summary>
public sealed record MutationRequest
{
    public MutationRequest(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        IdempotencyKey = idempotencyKey;
    }

    public string IdempotencyKey { get; }
}

public sealed record RateLimitInfo(
    string Resource,
    int Limit,
    int Remaining,
    int Used,
    DateTimeOffset ResetAt);
