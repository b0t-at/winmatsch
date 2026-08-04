using WinMatsch.Cli.Output;
using WinMatsch.GitHub;

namespace WinMatsch.Cli.Hosting;

/// <summary>
/// Injected GitHub adapter that applies the CLI redaction contract to public submission text
/// without coupling the workflow formatter to the executable.
/// </summary>
public sealed class RedactingGitHubRepositoryClient : IGitHubRepositoryClient
{
    private readonly IGitHubRepositoryClient _inner;

    public RedactingGitHubRepositoryClient(IGitHubRepositoryClient inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public RateLimitInfo? LastRateLimit => _inner.LastRateLimit;

    public event EventHandler<RateLimitInfo>? RateLimitObserved
    {
        add => _inner.RateLimitObserved += value;
        remove => _inner.RateLimitObserved -= value;
    }

    public void Dispose()
    {
        _inner.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task<GitHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
        => _inner.GetAuthenticatedUserAsync(cancellationToken);

    public Task<RepositoryInfo> GetRepositoryAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => _inner.GetRepositoryAsync(repository, cancellationToken);

    public Task<BranchState> GetDefaultBranchAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => _inner.GetDefaultBranchAsync(repository, cancellationToken);

    public Task<RepositoryContent> GetContentAsync(
        RepositoryCoordinates repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default)
        => _inner.GetContentAsync(repository, path, reference, cancellationToken);

    public Task<IReadOnlyList<RepositoryTreeEntry>> GetTreeAsync(
        RepositoryCoordinates repository,
        string treeish,
        bool recursive = true,
        CancellationToken cancellationToken = default)
        => _inner.GetTreeAsync(repository, treeish, recursive, cancellationToken);

    public Task<IReadOnlyList<ManifestFile>> GetManifestFilesAsync(
        RepositoryCoordinates repository,
        string directory,
        string reference,
        CancellationToken cancellationToken = default)
        => _inner.GetManifestFilesAsync(repository, directory, reference, cancellationToken);

    public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => _inner.GetReleasesAsync(repository, cancellationToken);

    public Task<IReadOnlyList<BranchState>> GetBranchesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => _inner.GetBranchesAsync(repository, cancellationToken);

    public Task<GitReference?> GetReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        CancellationToken cancellationToken = default)
        => _inner.GetReferenceAsync(repository, branchName, cancellationToken);

    public Task<GitReference> CreateReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => _inner.CreateReferenceAsync(repository, branchName, sha, mutation, cancellationToken);

    public Task<GitReference> CreateUniqueReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => _inner.CreateUniqueReferenceAsync(repository, branchName, sha, mutation, cancellationToken);

    public Task<bool> DeleteReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => _inner.DeleteReferenceAsync(repository, branchName, mutation, cancellationToken);

    public Task<ServerCommitResult> CreateCommitAsync(
        RepositoryCoordinates repository,
        ServerCommitRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => _inner.CreateCommitAsync(
            repository,
            request with
            {
                Headline = CliRedactor.Redact(request.Headline),
                Body = CliRedactor.RedactNullable(request.Body),
            },
            mutation,
            cancellationToken);

    public Task<CompareResult> CompareAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default)
        => _inner.CompareAsync(repository, baseReference, head, cancellationToken);

    public Task<ForkResult> EnsureForkAsync(
        RepositoryCoordinates upstream,
        string owner,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => _inner.EnsureForkAsync(upstream, owner, mutation, cancellationToken);

    public Task<UpstreamSyncResult> SyncForkAsync(
        RepositoryCoordinates fork,
        string branch,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => _inner.SyncForkAsync(fork, branch, mutation, cancellationToken);

    public Task<IReadOnlyList<PullRequestInfo>> SearchPullRequestsAsync(
        RepositoryCoordinates repository,
        PullRequestSearch search,
        CancellationToken cancellationToken = default)
        => _inner.SearchPullRequestsAsync(repository, search, cancellationToken);

    public Task<PullRequestInfo> CreatePullRequestAsync(
        RepositoryCoordinates repository,
        CreatePullRequestRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => _inner.CreatePullRequestAsync(
            repository,
            request with
            {
                Title = CliRedactor.Redact(request.Title),
                Body = CliRedactor.RedactNullable(request.Body),
            },
            mutation,
            cancellationToken);

    public Task<PullRequestInfo> GetPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
        => _inner.GetPullRequestAsync(repository, number, cancellationToken);

    public Task<IReadOnlyList<PullRequestChangedFile>> GetPullRequestChangedFilesAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
        => _inner.GetPullRequestChangedFilesAsync(repository, number, cancellationToken);

    public Task<IReadOnlyDictionary<long, IReadOnlyList<PullRequestChangedFile>>>
        GetPullRequestChangedFilesBatchAsync(
            RepositoryCoordinates repository,
            IReadOnlyList<PullRequestInfo> pullRequests,
            CancellationToken cancellationToken = default)
        => _inner.GetPullRequestChangedFilesBatchAsync(
            repository,
            pullRequests,
            cancellationToken);

    public Task<IReadOnlyDictionary<long, PullRequestChangedFilesSnapshot>>
        GetPullRequestChangedFilesSnapshotsBatchAsync(
            RepositoryCoordinates repository,
            IReadOnlyList<PullRequestInfo> pullRequests,
            CancellationToken cancellationToken = default)
        => _inner.GetPullRequestChangedFilesSnapshotsBatchAsync(
            repository,
            pullRequests,
            cancellationToken);

    public Task<PullRequestComment> CommentOnPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        string body,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => _inner.CommentOnPullRequestAsync(
            repository,
            number,
            CliRedactor.Redact(body),
            mutation,
            cancellationToken);

    public Task<PullRequestInfo> ClosePullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => _inner.ClosePullRequestAsync(repository, number, mutation, cancellationToken);
}
