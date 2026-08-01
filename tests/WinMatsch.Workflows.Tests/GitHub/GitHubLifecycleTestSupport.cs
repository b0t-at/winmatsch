using System.Collections.Immutable;
using System.Net;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.Tests.GitHub;

internal static class GitHubLifecycleTestSupport
{
    public static readonly RepositoryCoordinates Upstream = new("upstream", "repo");
    public static readonly RepositoryCoordinates Fork = new("contributor", "repo");
    public const string UpstreamSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static LocalOperationPlan Plan(
        ImmutableArray<WorkflowFileChange>? changes = null,
        string operation = "Update")
    {
        var package = new PackageIdentifier("Example.App");
        var version = new PackageVersion("2.0.0");
        ImmutableArray<WorkflowFileChange> actualChanges = changes ??
        [
            new(
                PlannedChangeKind.Add,
                $"{ManifestPaths.GetVersionDirectory(package, version)}/Example.App.yaml",
                "PackageIdentifier: Example.App"u8,
                ExpectedFileState.Absent),
        ];
        ImmutableArray<RawManifestDocument> documents =
        [
            .. actualChanges
                .Where(static change => change.Kind != PlannedChangeKind.Delete)
                .Select(static change => new RawManifestDocument(
                    change.RepositoryPath,
                    change.Content.AsSpan())),
        ];
        var preflight = new WorkflowPreflightRequest
        {
            BeforeDocuments = [],
            AfterDocuments = documents,
            Changes = actualChanges,
            Options = new PreflightOptions { NetworkMode = NetworkValidationMode.Skip },
        };
        return new()
        {
            Operation = operation,
            PackageIdentifier = package,
            PackageVersion = version,
            OutputDirectory = "unused",
            FileChanges = actualChanges,
            BeforeDocuments = [],
            AfterDocuments = documents,
            Validation = new ValidationReport(),
            Preflight = preflight,
            Rules = RuleRunSummary.Empty,
        };
    }

    public static GitHubSubmissionRequest Request(
        WorkflowExecutionMode mode = WorkflowExecutionMode.Apply,
        GitHubSubmissionPolicy? policy = null,
        RepositoryCoordinates? target = null)
        => new()
        {
            LocalPlan = Plan(),
            UpstreamRepository = Upstream,
            TargetRepository = target ?? Fork,
            ExecutionMode = mode,
            Operation = GitHubManifestOperation.Update,
            Policy = policy ?? new GitHubSubmissionPolicy(),
            IdempotencyKey = "operation-1",
            CreatedWith = "winmatsch tests",
        };

    public static GitHubLifecycleWorkflow Workflow(
        FakeGitHubClient client,
        FakePreflight? preflight = null,
        FakeArtifactRevalidator? artifacts = null,
        IRemoteOperationLockProvider? locks = null)
        => new(
            client,
            preflight ?? new FakePreflight(),
            artifacts ?? new FakeArtifactRevalidator(),
            locks ?? new FakeLockProvider(),
            new FixedBranchNameGenerator(),
            new FakeClock());

    public static PullRequestInfo PullRequest(
        long number,
        PullRequestState state = PullRequestState.Open,
        string author = "someone",
        string branch = "other")
        => new(
            number,
            $"PR_{number}",
            "Update: Example.App version 2.0.0",
            "<!-- winmatsch:package=Example.App;version=2.0.0 -->\n" +
            "Manifest path: `manifests/e/Example/App/2.0.0`",
            state,
            false,
            author,
            branch,
            CommitSha,
            "main",
            new Uri($"https://github.invalid/upstream/repo/pull/{number}"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
}

internal sealed class FakeGitHubClient : IGitHubRepositoryClient
{
    private readonly Dictionary<RepositoryCoordinates, RepositoryInfo> _repositories = [];
    private readonly Dictionary<(RepositoryCoordinates Repository, string Branch), GitReference> _references = [];
    private readonly Dictionary<(RepositoryCoordinates Repository, string Path, string Reference), byte[]> _contents = [];
    private readonly List<PullRequestInfo> _pullRequests = [];
    private EventHandler<RateLimitInfo>? _rateLimitObserved;

    public FakeGitHubClient(bool includeFork = true)
    {
        RepositoryInfo upstream = Repository(
            GitHubLifecycleTestSupport.Upstream,
            isFork: false,
            parent: null,
            GitHubLifecycleTestSupport.UpstreamSha);
        _repositories.Add(upstream.Coordinates, upstream);
        if (includeFork)
        {
            RepositoryInfo fork = Repository(
                GitHubLifecycleTestSupport.Fork,
                isFork: true,
                GitHubLifecycleTestSupport.Upstream,
                GitHubLifecycleTestSupport.UpstreamSha);
            _repositories.Add(fork.Coordinates, fork);
            _references.Add((fork.Coordinates, "main"), new("main", GitHubLifecycleTestSupport.UpstreamSha));
        }
    }

    public RateLimitInfo? LastRateLimit => null;

    public event EventHandler<RateLimitInfo>? RateLimitObserved
    {
        add => _rateLimitObserved += value;
        remove => _rateLimitObserved -= value;
    }

    public List<string> Mutations { get; } = [];

    public int SearchCalls { get; private set; }

    public Action<FakeGitHubClient, int>? OnSearch { get; set; }

    public Action<FakeGitHubClient, long>? OnGetPullRequest { get; set; }

    public Action<FakeGitHubClient, int>? OnGetReleases { get; set; }

    public string? MoveUpstreamBeforeCommitTo { get; set; }

    public string? FailMutation { get; set; }

    public string? CancelMutation { get; set; }

    public bool ForkAhead { get; set; }

    public bool BranchCreatedBeforeFailure { get; set; }

    public string PullRequestHeadSha { get; set; } = GitHubLifecycleTestSupport.CommitSha;

    public IReadOnlyList<GitHubRelease> Releases { get; set; } = [];

    public int GetReleasesCalls { get; private set; }

    public IReadOnlyList<PullRequestInfo> PullRequests => _pullRequests;

    public void AddPullRequest(PullRequestInfo pullRequest) => _pullRequests.Add(pullRequest);

    public void UpdatePullRequest(long number, Func<PullRequestInfo, PullRequestInfo> update)
    {
        int index = _pullRequests.FindIndex(pullRequest => pullRequest.Number == number);
        _pullRequests[index] = update(_pullRequests[index]);
    }

    public void MoveUpstream(string sha)
    {
        RepositoryInfo current = _repositories[GitHubLifecycleTestSupport.Upstream];
        _repositories[GitHubLifecycleTestSupport.Upstream] = current with
        {
            DefaultBranch = current.DefaultBranch with { HeadSha = sha },
        };
    }

    public Task<GitHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GitHubUser(
            "contributor",
            "Contributor",
            null,
            new Uri("https://github.invalid/avatar")));
    }

    public Task<RepositoryInfo> GetRepositoryAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _repositories.TryGetValue(repository, out RepositoryInfo? result)
            ? Task.FromResult(result)
            : Task.FromException<RepositoryInfo>(new GitHubApiException(
                "not found",
                HttpStatusCode.NotFound,
                null));
    }

    public async Task<BranchState> GetDefaultBranchAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => (await GetRepositoryAsync(repository, cancellationToken)).DefaultBranch;

    public Task<RepositoryContent> GetContentAsync(
        RepositoryCoordinates repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_contents.TryGetValue((repository, path, reference), out byte[]? bytes))
        {
            return Task.FromException<RepositoryContent>(new GitHubApiException(
                "not found",
                HttpStatusCode.NotFound,
                null));
        }

        return Task.FromResult(new RepositoryContent(
            Path.GetFileName(path),
            path,
            WorkflowFileChange.Hash(bytes),
            bytes.Length,
            "base64",
            bytes));
    }

    public Task<IReadOnlyList<RepositoryTreeEntry>> GetTreeAsync(
        RepositoryCoordinates repository,
        string treeish,
        bool recursive = true,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RepositoryTreeEntry>>([]);

    public Task<IReadOnlyList<ManifestFile>> GetManifestFilesAsync(
        RepositoryCoordinates repository,
        string directory,
        string reference,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ManifestFile>>([]);

    public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetReleasesCalls++;
        OnGetReleases?.Invoke(this, GetReleasesCalls);
        return Task.FromResult(Releases);
    }

    public Task<IReadOnlyList<BranchState>> GetBranchesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<BranchState>>(
            [.. _references
                .Where(item => item.Key.Repository == repository)
                .Select(static item => new BranchState(item.Key.Branch, item.Value.Sha, false))]);
    }

    public Task<GitReference?> GetReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MoveUpstreamBeforeCommitTo is not null
            && branchName == "winmatsch/update/example-app/2.0.0/test")
        {
            MoveUpstream(MoveUpstreamBeforeCommitTo);
            MoveUpstreamBeforeCommitTo = null;
        }

        _references.TryGetValue((repository, branchName), out GitReference? result);
        return Task.FromResult(result);
    }

    public Task<GitReference> CreateReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        BeforeMutation("branch", cancellationToken);
        var result = new GitReference(branchName, sha);
        _references.Add((repository, branchName), result);
        return Task.FromResult(result);
    }

    public Task<GitReference> CreateUniqueReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        if (_references.ContainsKey((repository, branchName)))
        {
            return Task.FromException<GitReference>(new GitHubApiException(
                "reference exists",
                HttpStatusCode.UnprocessableEntity,
                null));
        }

        if (BranchCreatedBeforeFailure && FailMutation == "branch")
        {
            _references.Add((repository, branchName), new(branchName, sha));
            throw new GitHubApiException(
                "Synthetic branch response loss",
                HttpStatusCode.ServiceUnavailable,
                null);
        }

        BeforeMutation("branch", cancellationToken);
        var result = new GitReference(branchName, sha);
        _references.Add((repository, branchName), result);
        return Task.FromResult(result);
    }

    public Task<bool> DeleteReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        BeforeMutation("delete", cancellationToken);
        return Task.FromResult(_references.Remove((repository, branchName)));
    }

    public Task<ServerCommitResult> CreateCommitAsync(
        RepositoryCoordinates repository,
        ServerCommitRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        BeforeMutation("commit", cancellationToken);
        if (!_references.TryGetValue((repository, request.BranchName), out GitReference? current)
            || !string.Equals(current.Sha, request.ExpectedHeadSha, StringComparison.Ordinal))
        {
            return Task.FromException<ServerCommitResult>(new GitHubApiException(
                "expected head mismatch",
                HttpStatusCode.Conflict,
                null));
        }

        _references[(repository, request.BranchName)] =
            new(request.BranchName, GitHubLifecycleTestSupport.CommitSha);
        return Task.FromResult(new ServerCommitResult(
            GitHubLifecycleTestSupport.CommitSha,
            new Uri($"https://github.invalid/{repository}/commit/{GitHubLifecycleTestSupport.CommitSha}")));
    }

    public Task<CompareResult> CompareAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ForkAhead
            ? new CompareResult("ahead", 1, 0, 1, [])
            : new CompareResult("behind", 0, 1, 0, []));
    }

    public Task<ForkResult> EnsureForkAsync(
        RepositoryCoordinates upstream,
        string owner,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        BeforeMutation("fork", cancellationToken);
        var coordinates = new RepositoryCoordinates(owner, upstream.Name);
        RepositoryInfo fork = Repository(
            coordinates,
            true,
            upstream,
            GitHubLifecycleTestSupport.UpstreamSha);
        _repositories[coordinates] = fork;
        _references[(coordinates, "main")] = new("main", GitHubLifecycleTestSupport.UpstreamSha);
        return Task.FromResult(new ForkResult(fork, false));
    }

    public Task<UpstreamSyncResult> SyncForkAsync(
        RepositoryCoordinates fork,
        string branch,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        BeforeMutation("sync", cancellationToken);
        string upstreamSha = _repositories[GitHubLifecycleTestSupport.Upstream].DefaultBranch.HeadSha;
        RepositoryInfo current = _repositories[fork];
        _repositories[fork] = current with
        {
            DefaultBranch = current.DefaultBranch with { HeadSha = upstreamSha },
        };
        _references[(fork, branch)] = new(branch, upstreamSha);
        return Task.FromResult(new UpstreamSyncResult("synced", "fast-forward", upstreamSha));
    }

    public Task<IReadOnlyList<PullRequestInfo>> SearchPullRequestsAsync(
        RepositoryCoordinates repository,
        PullRequestSearch search,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchCalls++;
        OnSearch?.Invoke(this, SearchCalls);
        IEnumerable<PullRequestInfo> result = _pullRequests;
        if (search.State != PullRequestState.All)
        {
            result = result.Where(pr => pr.State == search.State);
        }

        if (search.HeadOwner is not null)
        {
            result = result.Where(pr => pr.HeadOwner == search.HeadOwner);
        }

        if (search.ExactTitleToken is not null)
        {
            result = result.Where(pr => pr.Title.Contains(search.ExactTitleToken, StringComparison.Ordinal));
        }

        return Task.FromResult<IReadOnlyList<PullRequestInfo>>([.. result]);
    }

    public Task<PullRequestInfo> CreatePullRequestAsync(
        RepositoryCoordinates repository,
        CreatePullRequestRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        BeforeMutation("pull-request", cancellationToken);
        PullRequestInfo result = new(
            42,
            "PR_42",
            request.Title,
            request.Body,
            PullRequestState.Open,
            request.Draft,
            request.HeadOwner,
            request.HeadBranch,
            PullRequestHeadSha,
            request.BaseBranch,
            new Uri("https://github.invalid/upstream/repo/pull/42"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        _pullRequests.Add(result);
        return Task.FromResult(result);
    }

    public Task<PullRequestInfo> GetPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnGetPullRequest?.Invoke(this, number);
        return Task.FromResult(_pullRequests.Single(pr => pr.Number == number));
    }

    public Task<PullRequestComment> CommentOnPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        string body,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        BeforeMutation("comment", cancellationToken);
        return Task.FromResult(new PullRequestComment(
            number,
            body,
            new Uri($"https://github.invalid/issues/{number}#comment"),
            DateTimeOffset.UtcNow));
    }

    public Task<PullRequestInfo> ClosePullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        BeforeMutation("close", cancellationToken);
        int index = _pullRequests.FindIndex(pr => pr.Number == number);
        PullRequestInfo closed = _pullRequests[index] with { State = PullRequestState.Closed };
        _pullRequests[index] = closed;
        return Task.FromResult(closed);
    }

    public void AddBranch(RepositoryCoordinates repository, string name, string sha)
        => _references[(repository, name)] = new(name, sha);

    public void SetContent(
        RepositoryCoordinates repository,
        string path,
        string reference,
        ReadOnlySpan<byte> content)
        => _contents[(repository, path, reference)] = content.ToArray();

    public void SetForkHead(string sha)
    {
        RepositoryInfo current = _repositories[GitHubLifecycleTestSupport.Fork];
        _repositories[GitHubLifecycleTestSupport.Fork] = current with
        {
            DefaultBranch = current.DefaultBranch with { HeadSha = sha },
        };
        _references[(GitHubLifecycleTestSupport.Fork, "main")] = new("main", sha);
    }

    private void BeforeMutation(string mutation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CancelMutation == mutation)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (FailMutation == mutation)
        {
            throw new GitHubApiException(
                $"Synthetic {mutation} failure",
                mutation == "commit" ? HttpStatusCode.Conflict : HttpStatusCode.ServiceUnavailable,
                null);
        }

        Mutations.Add(mutation);
    }

    private static RepositoryInfo Repository(
        RepositoryCoordinates coordinates,
        bool isFork,
        RepositoryCoordinates? parent,
        string sha)
        => new(
            coordinates,
            $"NODE_{coordinates.Owner}_{coordinates.Name}",
            new Uri($"https://github.invalid/{coordinates}"),
            false,
            isFork,
            new BranchState("main", sha, false),
            parent);
}

internal sealed class FakePreflight : IWorkflowPreflight
{
    public int BoundaryCalls { get; private set; }

    public ValidationReport Report { get; set; } = new();

    public Task<ValidationReport> ValidateAsync(
        WorkflowPreflightRequest request,
        CancellationToken cancellationToken)
        => Task.FromResult(Report);

    public async Task<ValidationReport> ExecuteAsync(
        WorkflowPreflightRequest request,
        Func<CancellationToken, Task> boundary,
        CancellationToken cancellationToken)
    {
        if (Report.CanProceed(request.Options.WarningPolicy))
        {
            BoundaryCalls++;
            await boundary(cancellationToken);
        }

        return Report;
    }
}

internal sealed class FakeArtifactRevalidator : IFinalArtifactRevalidator
{
    public int Calls { get; private set; }

    public FinalArtifactRevalidationResult Result { get; set; } =
        FinalArtifactRevalidationResult.Valid;

    public Task<FinalArtifactRevalidationResult> RevalidateAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeLockProvider : IRemoteOperationLockProvider
{
    private int _held;

    public ValueTask<IAsyncDisposable> AcquireAsync(
        string repository,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _held, 1) == 1)
        {
            throw new RemoteOperationLockException("locked");
        }

        return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
    }

    private sealed class Lease(FakeLockProvider owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref owner._held, 0);
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FixedBranchNameGenerator : IGitHubBranchNameGenerator
{
    public string Create(GitHubBranchNameContext context)
        => "winmatsch/update/example-app/2.0.0/test";
}

internal sealed class FakeClock : IWorkflowClock
{
    public DateTimeOffset UtcNow { get; set; } =
        new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
}
