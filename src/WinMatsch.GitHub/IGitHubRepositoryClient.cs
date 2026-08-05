namespace WinMatsch.GitHub;

/// <summary>GitHub repository operations used by WinMatsch workflows.</summary>
public interface IGitHubRepositoryClient : IDisposable
{
    void IDisposable.Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public RateLimitInfo? LastRateLimit { get; }

    /// <summary>OAuth scopes from the most recent GitHub response that reported them.</summary>
    public IReadOnlyList<string> LastOAuthScopes => [];

    public event EventHandler<RateLimitInfo>? RateLimitObserved;

    public Task<GitHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default);

    public Task<RepositoryInfo> GetRepositoryAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default);

    public Task<RepositoryMetadataInfo> GetRepositoryMetadataAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => Task.FromException<RepositoryMetadataInfo>(
            new NotSupportedException("Repository metadata is not supported by this client."));

    public Task<BranchState> GetDefaultBranchAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default);

    public Task<RepositoryContent> GetContentAsync(
        RepositoryCoordinates repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<RepositoryTreeEntry>> GetTreeAsync(
        RepositoryCoordinates repository,
        string treeish,
        bool recursive = true,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<ManifestFile>> GetManifestFilesAsync(
        RepositoryCoordinates repository,
        string directory,
        string reference,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<BranchState>> GetBranchesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default);

    public Task<GitReference?> GetReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        CancellationToken cancellationToken = default);

    public Task<GitReference> CreateReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates a branch and fails if any branch with the same name already exists,
    /// even when it points at the requested SHA. Use this as a repository-side CAS reservation.
    /// </summary>
    public Task<GitReference> CreateUniqueReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a branch without a head-SHA precondition. GitHub's REST delete endpoint is
    /// unconditional; callers must only use this when unconditional deletion is acceptable.
    /// </summary>
    public Task<bool> DeleteReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        MutationRequest mutation,
        CancellationToken cancellationToken = default);

    public Task<ServerCommitResult> CreateCommitAsync(
        RepositoryCoordinates repository,
        ServerCommitRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default);

    public Task<CompareResult> CompareAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default);

    public Task<string> GetMergeBaseAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<string>(
            new NotSupportedException(
                "This GitHub client does not support immutable merge-base evidence."));
    }

    public Task<ForkResult> EnsureForkAsync(
        RepositoryCoordinates upstream,
        string owner,
        MutationRequest mutation,
        CancellationToken cancellationToken = default);

    public Task<UpstreamSyncResult> SyncForkAsync(
        RepositoryCoordinates fork,
        string branch,
        MutationRequest mutation,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<PullRequestInfo>> SearchPullRequestsAsync(
        RepositoryCoordinates repository,
        PullRequestSearch search,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<PullRequestInfo>> SearchPullRequestsByTextAsync(
        RepositoryCoordinates repository,
        PullRequestTextSearch search,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<IReadOnlyList<PullRequestInfo>>(
            new NotSupportedException(
                "This GitHub client does not support server-side pull-request text search."));
    }

    public Task<PullRequestInfo> CreatePullRequestAsync(
        RepositoryCoordinates repository,
        CreatePullRequestRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default);

    public Task<PullRequestInfo> GetPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<PullRequestChangedFile>> GetPullRequestChangedFilesAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<IReadOnlyList<PullRequestChangedFile>>(
            new NotSupportedException(
                "This GitHub client does not support authoritative pull-request changed-file evidence."));
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<PullRequestChangedFile>>>
        GetPullRequestChangedFilesBatchAsync(
            RepositoryCoordinates repository,
            IReadOnlyList<PullRequestInfo> pullRequests,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(pullRequests);
        var result = new Dictionary<long, IReadOnlyList<PullRequestChangedFile>>();
        foreach (PullRequestInfo pullRequest in pullRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(
                pullRequest.Number,
                await GetPullRequestChangedFilesAsync(
                    repository,
                    pullRequest.Number,
                    cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<long, PullRequestChangedFilesSnapshot>>
        GetPullRequestChangedFilesSnapshotsBatchAsync(
            RepositoryCoordinates repository,
            IReadOnlyList<PullRequestInfo> pullRequests,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(pullRequests);
        IReadOnlyDictionary<long, IReadOnlyList<PullRequestChangedFile>> files =
            await GetPullRequestChangedFilesBatchAsync(
                repository,
                pullRequests,
                cancellationToken).ConfigureAwait(false);
        var pullRequestsByNumber = pullRequests.ToDictionary(
            static pullRequest => pullRequest.Number);
        return files.ToDictionary(
            static pair => pair.Key,
            pair => new PullRequestChangedFilesSnapshot(
                pullRequestsByNumber[pair.Key],
                pair.Value));
    }

    public async Task<IReadOnlyDictionary<long, PullRequestChangedFilesSnapshot>>
        GetPullRequestChangedFilesPathScreeningSnapshotsBatchAsync(
            RepositoryCoordinates repository,
            IReadOnlyList<PullRequestInfo> pullRequests,
            IReadOnlySet<string> paths,
            int maximumMatches,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMatches);
        IReadOnlyDictionary<long, PullRequestChangedFilesSnapshot> snapshots =
            await GetPullRequestChangedFilesSnapshotsBatchAsync(
                repository,
                pullRequests,
                cancellationToken).ConfigureAwait(false);
        int matchCount = snapshots.Count(pair =>
            pair.Value.RequiresContentFallback
            || pair.Value.Files.Any(file =>
                paths.Contains(file.Path)
                || (file.PreviousPath is not null && paths.Contains(file.PreviousPath))));
        if (matchCount > maximumMatches)
        {
            throw new GitHubApiException(
                $"Pull-request changed-file screening exceeded the safe match limit of {maximumMatches}.");
        }

        return snapshots;
    }

    public Task<PullRequestComment> CommentOnPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        string body,
        MutationRequest mutation,
        CancellationToken cancellationToken = default);

    public Task<PullRequestInfo> ClosePullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        MutationRequest mutation,
        CancellationToken cancellationToken = default);
}
