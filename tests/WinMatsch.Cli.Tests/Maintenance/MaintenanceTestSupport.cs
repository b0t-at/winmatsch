using System.Collections.Immutable;
using WinMatsch.GitHub;
using WinMatsch.Workflows.GitHub;

namespace WinMatsch.Cli.Tests.Maintenance;

/// <summary>
/// A configurable in-memory <see cref="IGitHubRepositoryClient"/> for the maintenance command
/// tests. Reads are served from settable state; every mutation is recorded so tests can prove
/// dry runs and declined confirmations never touch the remote. Unconfigured members throw.
/// </summary>
internal sealed class FakeMaintenanceGitHubClient : IGitHubRepositoryClient
{
    public GitHubUser User { get; set; } = new(
        "octocat",
        null,
        null,
        new Uri("https://example.invalid/avatar"));

    /// <summary>Default branches keyed by <c>owner/name</c>.</summary>
    public Dictionary<string, BranchState> DefaultBranches { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Branch lists keyed by <c>owner/name</c>.</summary>
    public Dictionary<string, IReadOnlyList<BranchState>> Branches { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<PullRequestInfo> PullRequests { get; } = [];

    public Dictionary<long, IReadOnlyList<PullRequestChangedFile>> PullRequestFiles { get; } = [];

    public bool IgnoreHeadOwnerFilter { get; set; }

    public CompareResult Comparison { get; set; } = new("identical", 0, 0, 0, []);

    public string MergeBaseSha { get; set; } = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>Manifest file lists keyed by repository-relative directory.</summary>
    public Dictionary<string, IReadOnlyList<ManifestFile>> ManifestFiles { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IReadOnlyList<RepositoryTreeEntry>> Trees { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, RepositoryContent> Contents { get; } = new(StringComparer.Ordinal);

    public Exception? SyncForkFailure { get; set; }

    /// <summary>Runs just before a comment mutation is recorded (for cancellation tests).</summary>
    public Action? OnComment { get; set; }

    /// <summary>Thrown by <see cref="CommentOnPullRequestAsync"/> after <see cref="OnComment"/> ran.</summary>
    public Exception? CommentFailure { get; set; }

    public Dictionary<long, Exception> CommentFailures { get; } = [];

    /// <summary>The fork head SHA reported after a successful sync.</summary>
    public string? SyncedHeadSha { get; set; }

    public List<string> Mutations { get; } = [];

    public ServerCommitRequest? LastCommitRequest { get; private set; }

    public CreatePullRequestRequest? LastCreatePullRequestRequest { get; private set; }

    public RateLimitInfo? LastRateLimit => null;

#pragma warning disable CS0067 // Part of the interface; never raised by the fake.
    public event EventHandler<RateLimitInfo>? RateLimitObserved;
#pragma warning restore CS0067

    public Task<GitHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(User);
    }

    public Task<BranchState> GetDefaultBranchAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DefaultBranches.TryGetValue(repository.ToString(), out BranchState? branch)
            ? Task.FromResult(branch)
            : throw new GitHubApiException(
                $"Repository {repository} not found.",
                System.Net.HttpStatusCode.NotFound,
                requestId: null);
    }

    public Task<IReadOnlyList<BranchState>> GetBranchesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BranchState> result = Branches.TryGetValue(
            repository.ToString(),
            out IReadOnlyList<BranchState>? list)
            ? list
            : [];
        return Task.FromResult(result);
    }

    public Task<CompareResult> CompareAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Comparison);

    public Task<string> GetMergeBaseAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default)
        => Task.FromResult(MergeBaseSha);

    public Task<UpstreamSyncResult> SyncForkAsync(
        RepositoryCoordinates fork,
        string branch,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        if (SyncForkFailure is not null)
        {
            throw SyncForkFailure;
        }

        Mutations.Add($"syncFork:{fork}:{branch}");
        if (SyncedHeadSha is not null)
        {
            DefaultBranches[fork.ToString()] = DefaultBranches[fork.ToString()] with
            {
                HeadSha = SyncedHeadSha,
            };
        }

        return Task.FromResult(new UpstreamSyncResult("synced", "merge", SyncedHeadSha));
    }

    public Task<IReadOnlyList<PullRequestInfo>> SearchPullRequestsAsync(
        RepositoryCoordinates repository,
        PullRequestSearch search,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PullRequestInfo>>(
        [
            .. PullRequests.Where(pullRequest =>
                (search.State == PullRequestState.All || pullRequest.State == search.State)
                && (IgnoreHeadOwnerFilter
                    || search.HeadOwner is null
                    || string.Equals(pullRequest.HeadOwner, search.HeadOwner, StringComparison.OrdinalIgnoreCase))),
        ]);

    public Task<PullRequestInfo> GetPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PullRequests.Single(pullRequest => pullRequest.Number == number));

    public Task<IReadOnlyList<PullRequestChangedFile>> GetPullRequestChangedFilesAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
        => PullRequestFiles.TryGetValue(number, out IReadOnlyList<PullRequestChangedFile>? files)
            ? Task.FromResult(files)
            : throw new NotSupportedException();

    public Task<PullRequestComment> CommentOnPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        string body,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        OnComment?.Invoke();
        if (CommentFailures.TryGetValue(number, out Exception? specificFailure))
        {
            throw specificFailure;
        }

        if (CommentFailure is not null)
        {
            throw CommentFailure;
        }

        Mutations.Add($"comment:{number}:{body}");
        return Task.FromResult(new PullRequestComment(
            1,
            body,
            new Uri($"https://example.invalid/pr/{number}"),
            DateTimeOffset.UnixEpoch));
    }

    public Task<PullRequestInfo> ClosePullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        Mutations.Add($"close:{number}");
        return Task.FromResult(PullRequests.Single(pullRequest => pullRequest.Number == number));
    }

    public Task<RepositoryInfo> GetRepositoryAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RepositoryContent> GetContentAsync(
        RepositoryCoordinates repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default)
        => Contents.TryGetValue(path, out RepositoryContent? content)
            ? Task.FromResult(content)
            : throw new GitHubApiException(
                $"Content '{path}' not found.",
                System.Net.HttpStatusCode.NotFound,
                requestId: null);

    public Task<IReadOnlyList<RepositoryTreeEntry>> GetTreeAsync(
        RepositoryCoordinates repository,
        string treeish,
        bool recursive = true,
        CancellationToken cancellationToken = default)
        => Trees.TryGetValue(treeish, out IReadOnlyList<RepositoryTreeEntry>? entries)
            ? Task.FromResult(entries)
            : throw new GitHubApiException(
                $"Tree '{treeish}' not found.",
                System.Net.HttpStatusCode.NotFound,
                requestId: null);

    public Task<IReadOnlyList<ManifestFile>> GetManifestFilesAsync(
        RepositoryCoordinates repository,
        string directory,
        string reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ManifestFiles.TryGetValue(directory, out IReadOnlyList<ManifestFile>? files))
        {
            throw new GitHubApiException(
                $"Directory '{directory}' not found.",
                System.Net.HttpStatusCode.NotFound,
                requestId: null);
        }

        return Task.FromResult(files);
    }

    public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GitReference?> GetReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GitReference> CreateReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GitReference> CreateUniqueReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> DeleteReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ServerCommitResult> CreateCommitAsync(
        RepositoryCoordinates repository,
        ServerCommitRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        LastCommitRequest = request;
        return Task.FromResult(new ServerCommitResult(
            "commit-sha",
            new Uri("https://example.invalid/commit/commit-sha")));
    }

    public Task<ForkResult> EnsureForkAsync(
        RepositoryCoordinates upstream,
        string owner,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<PullRequestInfo> CreatePullRequestAsync(
        RepositoryCoordinates repository,
        CreatePullRequestRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        LastCreatePullRequestRequest = request;
        return Task.FromResult(MaintenancePullRequests.ToolOwned(42) with
        {
            Title = request.Title,
            Body = request.Body,
        });
    }
}

/// <summary>A scripted <see cref="IDeadVersionInspector"/> keyed by version string.</summary>
internal sealed class FakeDeadVersionInspector : IDeadVersionInspector
{
    public Dictionary<string, DeadVersionInspection> Inspections { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Queue<DeadVersionInspection>> InspectionSequences { get; } =
        new(StringComparer.Ordinal);

    public int InspectCallCount { get; private set; }

    public Task<DeadVersionInspection> InspectAsync(
        RepositoryCoordinates upstream,
        WinMatsch.Core.PackageIdentifier packageIdentifier,
        WinMatsch.Core.PackageVersion packageVersion,
        CancellationToken cancellationToken)
    {
        InspectCallCount++;
        if (InspectionSequences.TryGetValue(
                packageVersion.Value,
                out Queue<DeadVersionInspection>? sequence)
            && sequence.Count > 0)
        {
            return Task.FromResult(sequence.Dequeue());
        }

        return Task.FromResult(Inspections[packageVersion.Value]);
    }
}

/// <summary>Builders for the pull request shapes the maintenance workflows recognize.</summary>
internal static class MaintenancePullRequests
{
    public static PullRequestInfo ToolOwned(
        long number,
        string headOwner = "octocat",
        PullRequestState state = PullRequestState.Open,
        string headBranch = "winmatsch/submissions/pkg/1.0.0",
        string headSha = "sha-tool")
        => new(
            number,
            $"node-{number}",
            $"Tool PR #{number}",
            "<!-- winmatsch:package=Contoso.App/1.0.0 -->\nAutomated.",
            state,
            IsDraft: false,
            headOwner,
            headBranch,
            headSha,
            "master",
            new Uri($"https://example.invalid/pr/{number}"),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch)
        {
            HeadRepository = new RepositoryCoordinates(headOwner, "winget-pkgs"),
            BaseSha = "sha-upstream",
        };

    public static PullRequestInfo UserOwned(long number, string headOwner = "octocat")
        => new(
            number,
            $"node-{number}",
            $"User PR #{number}",
            "A hand-written change.",
            PullRequestState.Open,
            IsDraft: false,
            headOwner,
            "feature/manual-change",
            "sha-user",
            "master",
            new Uri($"https://example.invalid/pr/{number}"),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    public static ImmutableArray<PullRequestObservation> Observe(params PullRequestInfo[] pullRequests)
        =>
        [
            .. pullRequests.Select(pullRequest => new PullRequestObservation
            {
                PullRequest = pullRequest,
                Author = pullRequest.HeadOwner,
                ToolOwned = WinMatsch.Cli.Commands.Maintenance.ToolPullRequestObservationSource
                    .IsToolOwned(pullRequest),
            }),
        ];
}
