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

    public static LocalOperationPlan SynchronizePreflight(LocalOperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan with
        {
            Preflight = plan.Preflight with
            {
                BeforeDocuments = plan.BeforeDocuments,
                AfterDocuments = plan.AfterDocuments,
                Changes = plan.FileChanges,
            },
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
            Policy = policy ?? new GitHubSubmissionPolicy
            {
                MinimumReleaseFreshness = TimeSpan.Zero,
            },
            IdempotencyKey = "operation-1",
            CreatedWith = "winmatsch tests",
        };

    public static GitHubLifecycleWorkflow Workflow(
        FakeGitHubClient client,
        FakePreflight? preflight = null,
        FakeArtifactRevalidator? artifacts = null,
        IRemoteOperationLockProvider? locks = null,
        IRepositorySubmissionEvidenceProvider? repositoryEvidence = null,
        IPullRequestManifestEvidenceProvider? pullRequestEvidence = null)
        => new(
            client,
            preflight ?? new FakePreflight(),
            artifacts ?? new FakeArtifactRevalidator(),
            locks ?? new FakeLockProvider(),
            new FixedBranchNameGenerator(),
            new FakeClock(),
            repositoryEvidence ?? EmptyRepositorySubmissionEvidenceProvider.Instance,
            pullRequestEvidence ?? new GitHubPullRequestManifestEvidenceProvider(client));

    public static PullRequestInfo PullRequest(
        long number,
        PullRequestState state = PullRequestState.Open,
        string author = "someone",
        string branch = "other")
        => new(
            number,
            $"PR_{number}",
            "Update version: Example.App version 2.0.0",
            "<!-- winmatsch:package=Example.App;version=2.0.0 -->\n" +
            "Operation: Update\n" +
            "Manifest path: `manifests/e/Example/App/2.0.0`",
            state,
            false,
            author,
            branch,
            CommitSha,
            "main",
            new Uri($"https://github.invalid/upstream/repo/pull/{number}"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero))
        {
            HeadRepository = new RepositoryCoordinates(author, Upstream.Name),
            BaseSha = UpstreamSha,
        };
}

internal sealed class FakeGitHubClient : IGitHubRepositoryClient
{
    private readonly Dictionary<RepositoryCoordinates, RepositoryInfo> _repositories = [];
    private readonly Dictionary<(RepositoryCoordinates Repository, string Branch), GitReference> _references = [];
    private readonly Dictionary<(RepositoryCoordinates Repository, string Path, string Reference), byte[]> _contents = [];
    private readonly Dictionary<(RepositoryCoordinates Repository, string Treeish, bool Recursive), IReadOnlyList<RepositoryTreeEntry>> _trees = [];
    private readonly Dictionary<long, IReadOnlyList<PullRequestChangedFile>> _pullRequestFiles = [];
    private readonly List<PullRequestInfo> _pullRequests = [];
    private ServerCommitRequest? _lastCommitRequest;
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

    public RateLimitInfo? LastRateLimit { get; set; }

    public RepositoryMetadataInfo? RepositoryMetadata { get; set; }

    public Exception? RepositoryMetadataFailure { get; set; }

    public event EventHandler<RateLimitInfo>? RateLimitObserved
    {
        add => _rateLimitObserved += value;
        remove => _rateLimitObserved -= value;
    }

    public List<string> Mutations { get; } = [];

    public int SearchCalls { get; private set; }

    public List<PullRequestSearch> PullRequestSearches { get; } = [];

    public int ContentCalls { get; private set; }

    public List<(RepositoryCoordinates Repository, string Path, string Reference)> ContentRequests { get; } = [];

    public int PullRequestHeadContentCalls { get; private set; }

    public int PullRequestFilesCalls { get; private set; }

    public int PullRequestFileBatchCalls { get; private set; }

    public List<int> PullRequestFileBatchSizes { get; } = [];

    public List<(RepositoryCoordinates Repository, string Treeish, bool Recursive)> TreeCalls { get; } = [];

    public bool PullRequestChangedFilesUnsupported { get; set; }

    public GitHubApiException? PullRequestChangedFilesFailure { get; set; }

    public int FailNextPullRequestContentCalls { get; set; }

    public Action<FakeGitHubClient, int>? OnSearch { get; set; }

    public Action<FakeGitHubClient, long>? OnGetPullRequest { get; set; }

    public long? FailPullRequestReadNumber { get; set; }

    public Action<FakeGitHubClient, int>? OnGetReleases { get; set; }

    public string? MoveUpstreamBeforeCommitTo { get; set; }

    public string? FailMutation { get; set; }

    public string? CancelMutation { get; set; }

    public bool ForkAhead { get; set; }

    public bool BranchCreatedBeforeFailure { get; set; }

    public bool CommitCreatedBeforeFailure { get; set; }

    public Exception? TreeFailure { get; set; }

    public bool AutoConfigureCanonicalPullRequestEvidence { get; set; } = true;

    public string PullRequestHeadSha { get; set; } = GitHubLifecycleTestSupport.CommitSha;

    public string PullRequestMergeBaseSha { get; set; } = GitHubLifecycleTestSupport.UpstreamSha;

    public IReadOnlyList<GitHubRelease> Releases { get; set; } = [];

    public int GetReleasesCalls { get; private set; }

    public string? LastCommitMutationKey { get; private set; }

    public string? LastBranchMutationKey { get; private set; }

    public string? LastPullRequestMutationKey { get; private set; }

    public IReadOnlyList<PullRequestInfo> PullRequests => _pullRequests;

    public void AddPullRequest(PullRequestInfo pullRequest)
    {
        if (pullRequest.HeadRepository is { } headRepository
            && !_repositories.ContainsKey(headRepository))
        {
            _repositories.Add(
                headRepository,
                Repository(
                    headRepository,
                    isFork: true,
                    GitHubLifecycleTestSupport.Upstream,
                    GitHubLifecycleTestSupport.UpstreamSha));
        }

        _pullRequests.Add(pullRequest);
        if (AutoConfigureCanonicalPullRequestEvidence
            && GitHubSubmissionFormatter.IsCanonicalTitleFor(
                pullRequest.Title,
                new PackageIdentifier("Example.App"),
                new PackageVersion("2.0.0"))
            && pullRequest.HeadRepository is { } evidenceRepository)
        {
            WorkflowFileChange change = GitHubLifecycleTestSupport.Plan().FileChanges[0];
            _contents[(evidenceRepository, change.RepositoryPath, pullRequest.HeadSha)] =
                change.Content.ToArray();
            _pullRequestFiles[pullRequest.Number] =
                [new PullRequestChangedFile(change.RepositoryPath)];
        }
    }

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

    public void SetRepositoryPrivate(RepositoryCoordinates repository, bool isPrivate)
    {
        RepositoryInfo current = _repositories[repository];
        _repositories[repository] = current with { IsPrivate = isPrivate };
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

    public Task<RepositoryMetadataInfo> GetRepositoryMetadataAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RepositoryMetadataFailure is not null)
        {
            return Task.FromException<RepositoryMetadataInfo>(RepositoryMetadataFailure);
        }

        return RepositoryMetadata is not null
            ? Task.FromResult(RepositoryMetadata)
            : Task.FromException<RepositoryMetadataInfo>(
                new GitHubApiException("not found", HttpStatusCode.NotFound, requestId: null));
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
        ContentCalls++;
        ContentRequests.Add((repository, path, reference));
        if (repository != GitHubLifecycleTestSupport.Upstream)
        {
            PullRequestHeadContentCalls++;
            if (FailNextPullRequestContentCalls > 0)
            {
                FailNextPullRequestContentCalls--;
                return Task.FromException<RepositoryContent>(new GitHubApiException(
                    "transient content failure",
                    HttpStatusCode.ServiceUnavailable,
                    null));
            }
        }

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
    {
        cancellationToken.ThrowIfCancellationRequested();
        TreeCalls.Add((repository, treeish, recursive));
        if (TreeFailure is not null)
        {
            return Task.FromException<IReadOnlyList<RepositoryTreeEntry>>(TreeFailure);
        }

        return Task.FromResult(
            _trees.GetValueOrDefault((repository, treeish, recursive))
            ?? (IReadOnlyList<RepositoryTreeEntry>)[]);
    }

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
        LastBranchMutationKey = mutation.IdempotencyKey;
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
        if (CommitCreatedBeforeFailure && FailMutation == "commit")
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_references.TryGetValue((repository, request.BranchName), out GitReference? created)
                || !string.Equals(created.Sha, request.ExpectedHeadSha, StringComparison.Ordinal))
            {
                return Task.FromException<ServerCommitResult>(new GitHubApiException(
                    "expected head mismatch",
                    HttpStatusCode.Conflict,
                    null));
            }

            Mutations.Add("commit");
            _lastCommitRequest = request;
            _references[(repository, request.BranchName)] =
                new(request.BranchName, GitHubLifecycleTestSupport.CommitSha);
            foreach (CommitFileAddition addition in request.Additions)
            {
                _contents[(repository, addition.Path, GitHubLifecycleTestSupport.CommitSha)] =
                    addition.Contents.ToArray();
            }

            throw new GitHubApiException(
                "Synthetic commit response loss",
                HttpStatusCode.ServiceUnavailable,
                null);
        }

        BeforeMutation("commit", cancellationToken);
        LastCommitMutationKey = mutation.IdempotencyKey;
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
        _lastCommitRequest = request;
        foreach (CommitFileAddition addition in request.Additions)
        {
            _contents[(repository, addition.Path, GitHubLifecycleTestSupport.CommitSha)] =
                addition.Contents.ToArray();
        }
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

    public Task<string> GetMergeBaseAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PullRequestMergeBaseSha);
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
        PullRequestSearches.Add(search);
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

        if (search.BaseBranch is not null)
        {
            result = result.Where(pr => pr.BaseBranch == search.BaseBranch);
        }

        if (search.ExactTitleToken is not null)
        {
            result = result.Where(pr => pr.Title.Contains(search.ExactTitleToken, StringComparison.Ordinal));
        }

        PullRequestInfo[] matches = [.. result];
        if (search.MaximumResults is { } maximum && matches.Length > maximum)
        {
            return Task.FromException<IReadOnlyList<PullRequestInfo>>(
                new GitHubApiException(
                    $"GitHub pagination exceeded the safe result limit of {maximum}."));
        }

        return Task.FromResult<IReadOnlyList<PullRequestInfo>>(matches);
    }

    public Task<PullRequestInfo> CreatePullRequestAsync(
        RepositoryCoordinates repository,
        CreatePullRequestRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        BeforeMutation("pull-request", cancellationToken);
        LastPullRequestMutationKey = mutation.IdempotencyKey;
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
            DateTimeOffset.UtcNow)
        {
            HeadRepository = new RepositoryCoordinates(
                request.HeadOwner,
                GitHubLifecycleTestSupport.Upstream.Name),
            BaseSha = GitHubLifecycleTestSupport.UpstreamSha,
        };
        _pullRequests.Add(result);
        if (_lastCommitRequest is not null)
        {
            _pullRequestFiles[result.Number] =
            [
                .. _lastCommitRequest.Additions.Select(static addition =>
                    new PullRequestChangedFile(addition.Path)),
                .. _lastCommitRequest.Deletions.Select(static path =>
                    new PullRequestChangedFile(path)),
            ];
        }

        return Task.FromResult(result);
    }

    public Task<PullRequestInfo> GetPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnGetPullRequest?.Invoke(this, number);
        if (FailPullRequestReadNumber == number)
        {
            return Task.FromException<PullRequestInfo>(new GitHubApiException(
                "Synthetic pull request read failure",
                HttpStatusCode.ServiceUnavailable,
                null));
        }

        return Task.FromResult(_pullRequests.Single(pr => pr.Number == number));
    }

    public Task<IReadOnlyList<PullRequestChangedFile>> GetPullRequestChangedFilesAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PullRequestFilesCalls++;
        if (PullRequestChangedFilesUnsupported)
        {
            throw new NotSupportedException("Synthetic changed-file evidence is unavailable.");
        }

        return Task.FromResult(
            _pullRequestFiles.TryGetValue(number, out IReadOnlyList<PullRequestChangedFile>? files)
                ? files
                : (IReadOnlyList<PullRequestChangedFile>)[]);
    }

    public Task<IReadOnlyDictionary<long, IReadOnlyList<PullRequestChangedFile>>>
        GetPullRequestChangedFilesBatchAsync(
            RepositoryCoordinates repository,
            IReadOnlyList<PullRequestInfo> pullRequests,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PullRequestFileBatchCalls++;
        PullRequestFileBatchSizes.Add(pullRequests.Count);
        if (PullRequestChangedFilesFailure is not null)
        {
            throw PullRequestChangedFilesFailure;
        }

        if (PullRequestChangedFilesUnsupported)
        {
            throw new NotSupportedException("Synthetic changed-file evidence is unavailable.");
        }

        return Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<PullRequestChangedFile>>>(
            pullRequests.ToDictionary(
                static pullRequest => pullRequest.Number,
                pullRequest => _pullRequestFiles.TryGetValue(
                    pullRequest.Number,
                    out IReadOnlyList<PullRequestChangedFile>? files)
                        ? files
                        : (IReadOnlyList<PullRequestChangedFile>)[]));
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

    public void SetTree(
        RepositoryCoordinates repository,
        string treeish,
        bool recursive,
        params RepositoryTreeEntry[] entries)
        => _trees[(repository, treeish, recursive)] = entries;

    public void SetPullRequestChangedFiles(long number, params string[] paths)
        => _pullRequestFiles[number] = [.. paths.Select(static path => new PullRequestChangedFile(path))];

    public void SetPullRequestChangedFiles(
        long number,
        params PullRequestChangedFile[] files)
        => _pullRequestFiles[number] = files;

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

internal sealed class FakeSubmissionProgressSink : ISubmissionProgressSink
{
    public List<SubmissionJournalState> States { get; } = [];

    public Task RecordAsync(
        RemoteMutationState remoteState,
        SubmissionJournalState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        States.Add(state);
        return Task.CompletedTask;
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

internal sealed class FakeRepositorySubmissionEvidenceProvider
    : IRepositorySubmissionEvidenceProvider
{
    public Exception? Failure { get; init; }

    public RepositorySubmissionEvidence Evidence { get; init; } =
        RepositorySubmissionEvidence.Empty;

    public int Calls { get; private set; }

    public Task<RepositorySubmissionEvidence> GetEvidenceAsync(
        GitHubSubmissionRequest request,
        string upstreamHeadSha,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (Failure is not null)
        {
            return Task.FromException<RepositorySubmissionEvidence>(Failure);
        }

        return Task.FromResult(Evidence);
    }
}

internal sealed class FakePullRequestManifestEvidenceProvider(
    PullRequestManifestEvidence evidence)
    : IPullRequestManifestEvidenceProvider
{
    public Func<IReadOnlyList<PullRequestInfo>, IReadOnlyList<PullRequestInfo>>?
        CandidateSelector
    {
        get;
        init;
    }

    public int EvidenceCalls { get; private set; }

    public Task<IReadOnlyList<PullRequestInfo>> GetCandidatesAsync(
        GitHubSubmissionPlan plan,
        IReadOnlyList<PullRequestInfo> openPullRequests,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            CandidateSelector?.Invoke(openPullRequests) ?? openPullRequests);
    }

    public Task<PullRequestManifestEvidence> GetEvidenceAsync(
        GitHubSubmissionPlan plan,
        PullRequestInfo pullRequest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EvidenceCalls++;
        return Task.FromResult(evidence);
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

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
