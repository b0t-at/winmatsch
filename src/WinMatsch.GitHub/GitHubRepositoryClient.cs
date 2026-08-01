using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.GitHub.Internal;

namespace WinMatsch.GitHub;

/// <summary>A raw-HTTP, AOT-friendly GitHub GraphQL and REST client.</summary>
public sealed class GitHubRepositoryClient : IGitHubRepositoryClient
{
    private const string ViewerQuery =
        """
        query {
          viewer { login name email avatarUrl }
          rateLimit { limit remaining used resetAt }
        }
        """;

    private const string RepositoryQuery =
        """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            id nameWithOwner url isPrivate isFork
            parent { nameWithOwner }
            defaultBranchRef { name target { ... on Commit { oid } } }
          }
          rateLimit { limit remaining used resetAt }
        }
        """;

    private const string CreateCommitMutation =
        """
        mutation($input: CreateCommitOnBranchInput!) {
          createCommitOnBranch(input: $input) {
            commit { oid url }
            clientMutationId
          }
          rateLimit { limit remaining used resetAt }
        }
        """;

    private readonly GitHubHttpTransport _transport;
    private readonly GitHubClientOptions _options;
    private readonly ConcurrentDictionary<string, MutationEntry> _mutations =
        new(StringComparer.Ordinal);
    private RateLimitInfo? _lastRateLimit;

    public GitHubRepositoryClient(
        HttpClient httpClient,
        string accessToken,
        GitHubClientOptions? options = null)
    {
        _options = options ?? new GitHubClientOptions();
        _transport = new GitHubHttpTransport(httpClient, accessToken, _options);
        _transport.RateLimitObserved += OnRateLimitObserved;
    }

    public RateLimitInfo? LastRateLimit => Volatile.Read(ref _lastRateLimit);

    public event EventHandler<RateLimitInfo>? RateLimitObserved;

    public async Task<GitHubUser> GetAuthenticatedUserAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new GraphQlViewerRequestDto { Query = ViewerQuery };
            GraphQlViewerResponseDto response = await _transport.GraphQlQueryAsync(
                request,
                GitHubJsonContext.Default.GraphQlViewerRequestDto,
                GitHubJsonContext.Default.GraphQlViewerResponseDto,
                cancellationToken).ConfigureAwait(false);
            ThrowIfGraphQlErrors(response.Errors);
            GraphQlViewerDataDto data = response.Data ??
                throw InvalidGraphQlResponse("viewer data");
            ObserveGraphQlRateLimit(data.RateLimit);
            GraphQlUserDto viewer = data.Viewer ??
                throw InvalidGraphQlResponse("authenticated viewer");
            return new GitHubUser(
                viewer.Login,
                viewer.Name,
                viewer.Email,
                ParseAbsoluteUri(viewer.AvatarUrl, "viewer avatar URL"));
        }
        catch (GitHubApiException exception) when (IsGraphQlUnavailable(exception))
        {
            RestUserDto user = await _transport.GetAsync(
                "user",
                GitHubJsonContext.Default.RestUserDto,
                cancellationToken).ConfigureAwait(false);
            return new GitHubUser(
                user.Login,
                user.Name,
                user.Email,
                ParseAbsoluteUri(user.AvatarUrl, "viewer avatar URL"));
        }
    }

    public async Task<RepositoryInfo> GetRepositoryAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        try
        {
            var request = new GraphQlRepositoryRequestDto
            {
                Query = RepositoryQuery,
                Variables = new GraphQlRepositoryVariablesDto
                {
                    Owner = repository.Owner,
                    Name = repository.Name,
                },
            };
            GraphQlRepositoryResponseDto response = await _transport.GraphQlQueryAsync(
                request,
                GitHubJsonContext.Default.GraphQlRepositoryRequestDto,
                GitHubJsonContext.Default.GraphQlRepositoryResponseDto,
                cancellationToken).ConfigureAwait(false);
            ThrowIfGraphQlErrors(response.Errors);
            GraphQlRepositoryDataDto data = response.Data ??
                throw InvalidGraphQlResponse("repository data");
            ObserveGraphQlRateLimit(data.RateLimit);
            GraphQlRepositoryDto dto = data.Repository ??
                throw new GitHubApiException(
                    $"GitHub repository '{repository}' was not found.",
                    HttpStatusCode.NotFound,
                    requestId: null);
            GraphQlBranchDto branch = dto.DefaultBranchRef ??
                throw InvalidGraphQlResponse("default branch");
            RestBranchDto branchState = await GetBranchAsync(
                repository,
                branch.Name,
                cancellationToken).ConfigureAwait(false);
            return new RepositoryInfo(
                RepositoryCoordinates.Parse(dto.NameWithOwner),
                dto.Id,
                ParseAbsoluteUri(dto.Url, "repository URL"),
                dto.IsPrivate,
                dto.IsFork,
                new BranchState(
                    branchState.Name,
                    branchState.Commit.Sha,
                    branchState.Protected),
                dto.Parent is null
                    ? null
                    : RepositoryCoordinates.Parse(dto.Parent.NameWithOwner));
        }
        catch (GitHubApiException exception) when (IsGraphQlUnavailable(exception))
        {
            return await GetRepositoryViaRestAsync(repository, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<BranchState> GetDefaultBranchAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => (await GetRepositoryAsync(repository, cancellationToken).ConfigureAwait(false))
            .DefaultBranch;

    public async Task<RepositoryContent> GetContentAsync(
        RepositoryCoordinates repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidatePath(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        string route =
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/contents/{EscapePath(path)}" +
            $"?ref={Escape(reference)}";
        RestContentDto content = await _transport.GetAsync(
            route,
            GitHubJsonContext.Default.RestContentDto,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(content.Encoding, "base64", StringComparison.OrdinalIgnoreCase) ||
            content.Content is null)
        {
            throw new GitHubApiException(
                $"GitHub content '{path}' did not contain inline base64 data. " +
                "Large files must be downloaded through their Git blob endpoint.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(content.Content);
        }
        catch (FormatException exception)
        {
            throw new GitHubApiException(
                $"GitHub returned invalid base64 content for '{path}'.",
                exception);
        }

        return new RepositoryContent(
            content.Name,
            content.Path,
            content.Sha,
            content.Size,
            content.Encoding,
            bytes);
    }

    public async Task<IReadOnlyList<RepositoryTreeEntry>> GetTreeAsync(
        RepositoryCoordinates repository,
        string treeish,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeish);
        string route =
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/git/trees/{Escape(treeish)}" +
            (recursive ? "?recursive=1" : "");
        RestTreeDto tree = await _transport.GetAsync(
            route,
            GitHubJsonContext.Default.RestTreeDto,
            cancellationToken).ConfigureAwait(false);
        if (tree.Truncated)
        {
            throw new GitHubApiException(
                $"GitHub truncated tree '{treeish}'. Query a narrower subtree.");
        }

        return tree.Tree.Select(static entry => new RepositoryTreeEntry(
            entry.Path,
            entry.Sha,
            ParseTreeEntryType(entry.Type),
            entry.Size)).ToArray();
    }

    public async Task<IReadOnlyList<ManifestFile>> GetManifestFilesAsync(
        RepositoryCoordinates repository,
        string directory,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        string prefix = directory.TrimEnd('/') + "/";
        IReadOnlyList<RepositoryTreeEntry> tree = await GetTreeAsync(
            repository,
            reference,
            recursive: true,
            cancellationToken).ConfigureAwait(false);
        RepositoryTreeEntry[] files = tree
            .Where(entry =>
                entry.Type == RepositoryTreeEntryType.Blob &&
                entry.Path.StartsWith(prefix, StringComparison.Ordinal) &&
                entry.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

        var manifests = new List<ManifestFile>(files.Length);
        foreach (RepositoryTreeEntry file in files)
        {
            RepositoryContent content = await GetContentAsync(
                repository,
                file.Path,
                reference,
                cancellationToken).ConfigureAwait(false);
            manifests.Add(new ManifestFile(content.Path, content.Sha, content.Bytes));
        }

        return manifests;
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        Uri first = new(
            _options.NormalizedApiBaseUri,
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/releases?per_page=100");
        List<RestReleaseDto> releases = await GetAllPagesAsync(
            first,
            GitHubJsonContext.Default.ListRestReleaseDto,
            cancellationToken).ConfigureAwait(false);
        return releases.Select(static release => new GitHubRelease(
            release.Id,
            release.TagName,
            release.Name ?? release.TagName,
            release.Body,
            ParseAbsoluteUri(release.HtmlUrl, "release URL"),
            release.Draft,
            release.Prerelease,
            release.PublishedAt,
            release.Assets.Select(static asset => new ReleaseAsset(
                asset.Id,
                asset.Name,
                ParseAbsoluteUri(asset.BrowserDownloadUrl, "release asset URL"),
                asset.ContentType,
                asset.Size,
                asset.DownloadCount,
                asset.CreatedAt)).ToArray())).ToArray();
    }

    public async Task<IReadOnlyList<BranchState>> GetBranchesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        Uri first = new(
            _options.NormalizedApiBaseUri,
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/branches?per_page=100");
        List<RestBranchDto> branches = await GetAllPagesAsync(
            first,
            GitHubJsonContext.Default.ListRestBranchDto,
            cancellationToken).ConfigureAwait(false);
        return branches.Select(static branch => new BranchState(
            branch.Name,
            branch.Commit.Sha,
            branch.Protected)).ToArray();
    }

    public async Task<GitReference?> GetReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        try
        {
            RestReferenceDto reference = await _transport.GetAsync(
                $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/git/ref/heads/{Escape(branchName)}",
                GitHubJsonContext.Default.RestReferenceDto,
                cancellationToken).ConfigureAwait(false);
            return new GitReference(RemoveHeadsPrefix(reference.Ref), reference.Object.Sha);
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<GitReference> CreateReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ValidateSha(sha);
        ArgumentNullException.ThrowIfNull(mutation);
        string fingerprint = $"create-ref|{repository}|{branchName}|{sha}";
        return ExecuteMutationAsync(
            mutation,
            fingerprint,
            async () =>
            {
                GitReference? existing = await GetReferenceAsync(
                    repository,
                    branchName,
                    cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (string.Equals(existing.Sha, sha, StringComparison.OrdinalIgnoreCase))
                    {
                        return existing;
                    }

                    throw new GitHubApiException(
                        $"Branch '{branchName}' already exists at a different commit.",
                        HttpStatusCode.Conflict,
                        requestId: null);
                }

                var request = new CreateReferenceDto
                {
                    Ref = $"refs/heads/{branchName}",
                    Sha = sha,
                };
                RestReferenceDto created = await _transport.PostAsync(
                    $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/git/refs",
                    request,
                    GitHubJsonContext.Default.CreateReferenceDto,
                    GitHubJsonContext.Default.RestReferenceDto,
                    cancellationToken).ConfigureAwait(false);
                return new GitReference(RemoveHeadsPrefix(created.Ref), created.Object.Sha);
            });
    }

    public Task<GitReference> CreateUniqueReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ValidateSha(sha);
        ArgumentNullException.ThrowIfNull(mutation);
        string fingerprint = $"create-unique-ref|{repository}|{branchName}|{sha}";
        return ExecuteMutationAsync(
            mutation,
            fingerprint,
            async () =>
            {
                var request = new CreateReferenceDto
                {
                    Ref = $"refs/heads/{branchName}",
                    Sha = sha,
                };
                RestReferenceDto created = await _transport.PostAsync(
                    $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/git/refs",
                    request,
                    GitHubJsonContext.Default.CreateReferenceDto,
                    GitHubJsonContext.Default.RestReferenceDto,
                    cancellationToken).ConfigureAwait(false);
                return new GitReference(RemoveHeadsPrefix(created.Ref), created.Object.Sha);
            });
    }

    public Task<bool> DeleteReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentNullException.ThrowIfNull(mutation);
        string fingerprint = $"delete-ref-unconditional|{repository}|{branchName}";
        return ExecuteMutationAsync(
            mutation,
            fingerprint,
            async () =>
            {
                try
                {
                    await _transport.DeleteAsync(
                        $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/git/refs/heads/" +
                        Escape(branchName),
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (GitHubApiException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }
            });
    }

    public Task<ServerCommitResult> CreateCommitAsync(
        RepositoryCoordinates repository,
        ServerCommitRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidateCommitRequest(request);
        ArgumentNullException.ThrowIfNull(mutation);
        string fingerprint = GetCommitFingerprint(repository, request);
        return ExecuteMutationAsync(
            mutation,
            fingerprint,
            () => CreateCommitCoreAsync(repository, request, mutation, cancellationToken));
    }

    public async Task<CompareResult> CompareAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(head);
        Uri? next = new(
            _options.NormalizedApiBaseUri,
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/compare/" +
            $"{Escape(baseReference)}...{Escape(head)}?per_page=100");
        RestCompareDto? comparison = null;
        var commits = new List<RestComparedCommitDto>();
        while (next is not null)
        {
            TransportResponse<RestCompareDto> page = await _transport.GetPageAsync(
            next,
            GitHubJsonContext.Default.RestCompareDto,
            cancellationToken).ConfigureAwait(false);
            comparison ??= page.Value;
            commits.AddRange(page.Value.Commits);
            next = page.NextUri;
        }

        if (comparison is null)
        {
            throw new GitHubApiException("GitHub compare returned no response pages.");
        }

        return new CompareResult(
            comparison.Status,
            comparison.AheadBy,
            comparison.BehindBy,
            comparison.TotalCommits,
            commits.Select(static commit => new ComparedCommit(
            commit.Sha,
            commit.Commit.Message,
            ParseAbsoluteUri(commit.HtmlUrl, "commit URL"))).ToArray());
    }

    public async Task<ForkResult> EnsureForkAsync(
        RepositoryCoordinates upstream,
        string owner,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(mutation);
        string fingerprint = $"ensure-fork|{upstream}|{owner}";
        ForkMutationResult mutationResult = await ExecuteMutationAsync(
            mutation,
            fingerprint,
            async () =>
            {
                var forkCoordinates = new RepositoryCoordinates(owner, upstream.Name);
                try
                {
                    RepositoryInfo existing = await GetRepositoryAsync(
                        forkCoordinates,
                        cancellationToken).ConfigureAwait(false);
                    if (!CoordinatesEqual(existing.Parent, upstream))
                    {
                        throw new GitHubApiException(
                            $"Repository '{forkCoordinates}' exists but is not a fork of '{upstream}'.",
                            HttpStatusCode.Conflict,
                            requestId: null);
                    }

                    return new ForkMutationResult(existing);
                }
                catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    GitHubUser authenticatedUser = await GetAuthenticatedUserAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var request = new CreateForkDto
                    {
                        Organization = string.Equals(
                            authenticatedUser.Login,
                            owner,
                            StringComparison.OrdinalIgnoreCase)
                            ? null
                            : owner,
                        DefaultBranchOnly = true,
                    };
                    await _transport.PostAsync(
                        $"repos/{Escape(upstream.Owner)}/{Escape(upstream.Name)}/forks",
                        request,
                        GitHubJsonContext.Default.CreateForkDto,
                        GitHubJsonContext.Default.RestRepositoryDto,
                        cancellationToken).ConfigureAwait(false);
                    return new ForkMutationResult(Existing: null);
                }
            }).ConfigureAwait(false);
        if (mutationResult.Existing is not null)
        {
            return new ForkResult(mutationResult.Existing, AlreadyExisted: true);
        }

        RepositoryInfo created = await WaitForForkAsync(
            upstream,
            new RepositoryCoordinates(owner, upstream.Name),
            cancellationToken).ConfigureAwait(false);
        return new ForkResult(created, AlreadyExisted: false);
    }

    public Task<UpstreamSyncResult> SyncForkAsync(
        RepositoryCoordinates fork,
        string branch,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fork);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(mutation);
        string fingerprint = $"sync-fork|{fork}|{branch}";
        return ExecuteMutationAsync(
            mutation,
            fingerprint,
            async () =>
            {
                var request = new MergeUpstreamDto { Branch = branch };
                RestMergeUpstreamResultDto result = await _transport.PostAsync(
                    $"repos/{Escape(fork.Owner)}/{Escape(fork.Name)}/merge-upstream",
                    request,
                    GitHubJsonContext.Default.MergeUpstreamDto,
                    GitHubJsonContext.Default.RestMergeUpstreamResultDto,
                    cancellationToken).ConfigureAwait(false);
                GitReference? head = await GetReferenceAsync(
                    fork,
                    branch,
                    cancellationToken).ConfigureAwait(false);
                return new UpstreamSyncResult(result.Message, result.MergeType, head?.Sha);
            });
    }

    public async Task<IReadOnlyList<PullRequestInfo>> SearchPullRequestsAsync(
        RepositoryCoordinates repository,
        PullRequestSearch search,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(search);
        var query = new StringBuilder(
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/pulls?per_page=100");
        query.Append("&state=");
        query.Append(search.State switch
        {
            PullRequestState.Open => "open",
            PullRequestState.Closed => "closed",
            PullRequestState.All => "all",
            _ => throw new ArgumentOutOfRangeException(nameof(search)),
        });
        if (!string.IsNullOrWhiteSpace(search.HeadBranch))
        {
            string head = string.IsNullOrWhiteSpace(search.HeadOwner)
                ? search.HeadBranch
                : $"{search.HeadOwner}:{search.HeadBranch}";
            query.Append("&head=");
            query.Append(Escape(head));
        }

        if (!string.IsNullOrWhiteSpace(search.BaseBranch))
        {
            query.Append("&base=");
            query.Append(Escape(search.BaseBranch));
        }

        Uri first = new(_options.NormalizedApiBaseUri, query.ToString());
        List<RestPullRequestDto> pullRequests = await GetAllPagesAsync(
            first,
            GitHubJsonContext.Default.ListRestPullRequestDto,
            cancellationToken).ConfigureAwait(false);
        IEnumerable<RestPullRequestDto> filtered = pullRequests;
        if (!string.IsNullOrWhiteSpace(search.ExactTitleToken))
        {
            filtered = filtered.Where(pullRequest =>
                ContainsExactToken(pullRequest.Title, search.ExactTitleToken));
        }

        return filtered.Select(MapPullRequest).ToArray();
    }

    public Task<PullRequestInfo> CreatePullRequestAsync(
        RepositoryCoordinates repository,
        CreatePullRequestRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidatePullRequest(request);
        ArgumentNullException.ThrowIfNull(mutation);
        string fingerprint =
            $"create-pr|{repository}|{request.HeadOwner}|{request.HeadBranch}|" +
            $"{request.BaseBranch}|{request.Title}|{request.Body}|{request.Draft}";
        return ExecuteMutationAsync(
            mutation,
            fingerprint,
            async () =>
            {
                IReadOnlyList<PullRequestInfo> existing = await SearchPullRequestsAsync(
                    repository,
                    new PullRequestSearch(
                        PullRequestState.Open,
                        request.HeadOwner,
                        request.HeadBranch,
                        request.BaseBranch),
                    cancellationToken).ConfigureAwait(false);
                PullRequestInfo? matching = existing.FirstOrDefault(pullRequest =>
                    string.Equals(pullRequest.Title, request.Title, StringComparison.Ordinal));
                if (matching is not null)
                {
                    return matching;
                }

                var dto = new CreatePullRequestDto
                {
                    Title = request.Title,
                    Body = request.Body,
                    Head = $"{request.HeadOwner}:{request.HeadBranch}",
                    Base = request.BaseBranch,
                    Draft = request.Draft,
                };
                RestPullRequestDto created = await _transport.PostAsync(
                    $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/pulls",
                    dto,
                    GitHubJsonContext.Default.CreatePullRequestDto,
                    GitHubJsonContext.Default.RestPullRequestDto,
                    cancellationToken).ConfigureAwait(false);
                return MapPullRequest(created);
            });
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        RestPullRequestDto pullRequest = await _transport.GetAsync(
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/pulls/" +
            number.ToString(CultureInfo.InvariantCulture),
            GitHubJsonContext.Default.RestPullRequestDto,
            cancellationToken).ConfigureAwait(false);
        return MapPullRequest(pullRequest);
    }

    public Task<PullRequestComment> CommentOnPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        string body,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentNullException.ThrowIfNull(mutation);
        string fingerprint = $"comment-pr|{repository}|{number}|{body}";
        return ExecuteMutationAsync(
            mutation,
            fingerprint,
            async () =>
            {
                var request = new CreateCommentDto { Body = body };
                RestCommentDto comment = await _transport.PostAsync(
                    $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/issues/" +
                    $"{number.ToString(CultureInfo.InvariantCulture)}/comments",
                    request,
                    GitHubJsonContext.Default.CreateCommentDto,
                    GitHubJsonContext.Default.RestCommentDto,
                    cancellationToken).ConfigureAwait(false);
                return new PullRequestComment(
                    comment.Id,
                    comment.Body,
                    ParseAbsoluteUri(comment.HtmlUrl, "comment URL"),
                    comment.CreatedAt);
            });
    }

    public Task<PullRequestInfo> ClosePullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentNullException.ThrowIfNull(mutation);
        string fingerprint = $"close-pr|{repository}|{number}";
        return ExecuteMutationAsync(
            mutation,
            fingerprint,
            async () =>
            {
                PullRequestInfo existing = await GetPullRequestAsync(
                    repository,
                    number,
                    cancellationToken).ConfigureAwait(false);
                if (existing.State == PullRequestState.Closed)
                {
                    return existing;
                }

                var request = new UpdatePullRequestDto { State = "closed" };
                RestPullRequestDto closed = await _transport.PatchAsync(
                    $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/pulls/" +
                    number.ToString(CultureInfo.InvariantCulture),
                    request,
                    GitHubJsonContext.Default.UpdatePullRequestDto,
                    GitHubJsonContext.Default.RestPullRequestDto,
                    cancellationToken).ConfigureAwait(false);
                return MapPullRequest(closed);
            });
    }

    private async Task<RepositoryInfo> GetRepositoryViaRestAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken)
    {
        RestRepositoryDto dto = await _transport.GetAsync(
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}",
            GitHubJsonContext.Default.RestRepositoryDto,
            cancellationToken).ConfigureAwait(false);
        RestBranchDto branch = await GetBranchAsync(
            repository,
            dto.DefaultBranch,
            cancellationToken).ConfigureAwait(false);
        return new RepositoryInfo(
            RepositoryCoordinates.Parse(dto.FullName),
            dto.NodeId,
            ParseAbsoluteUri(dto.HtmlUrl, "repository URL"),
            dto.Private,
            dto.Fork,
            new BranchState(branch.Name, branch.Commit.Sha, branch.Protected),
            dto.Parent is null
                ? null
                : RepositoryCoordinates.Parse(dto.Parent.FullName));
    }

    private async Task<ServerCommitResult> CreateCommitCoreAsync(
        RepositoryCoordinates repository,
        ServerCommitRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken)
    {
        var graphQlRequest = new GraphQlCommitRequestDto
        {
            Query = CreateCommitMutation,
            Variables = new GraphQlCommitVariablesDto
            {
                Input = new GraphQlCommitInputDto
                {
                    Branch = new GraphQlCommitBranchDto
                    {
                        RepositoryNameWithOwner = repository.ToString(),
                        BranchName = request.BranchName,
                    },
                    ExpectedHeadOid = request.ExpectedHeadSha,
                    Message = new GraphQlCommitMessageDto
                    {
                        Headline = request.Headline,
                        Body = request.Body,
                    },
                    FileChanges = new GraphQlFileChangesDto
                    {
                        Additions = request.Additions.Select(static addition =>
                            new GraphQlFileAdditionDto
                            {
                                Path = addition.Path,
                                Contents = Convert.ToBase64String(addition.Contents.Span),
                            }).ToList(),
                        Deletions = request.Deletions.Select(static path =>
                            new GraphQlFileDeletionDto { Path = path }).ToList(),
                    },
                    ClientMutationId = mutation.IdempotencyKey,
                },
            },
        };

        try
        {
            GraphQlCommitResponseDto response = await _transport.GraphQlMutationAsync(
                graphQlRequest,
                GitHubJsonContext.Default.GraphQlCommitRequestDto,
                GitHubJsonContext.Default.GraphQlCommitResponseDto,
                cancellationToken).ConfigureAwait(false);
            ThrowIfGraphQlErrors(response.Errors);
            GraphQlCommitDataDto data = response.Data ??
                throw InvalidGraphQlResponse("commit mutation data");
            ObserveGraphQlRateLimit(data.RateLimit);
            GraphQlCreatedCommitDto commit = data.CreateCommitOnBranch?.Commit ??
                throw InvalidGraphQlResponse("created commit");
            return new ServerCommitResult(
                commit.Oid,
                ParseAbsoluteUri(commit.Url, "commit URL"));
        }
        catch (GitHubApiException exception) when (IsGraphQlUnavailable(exception))
        {
            return await CreateCommitViaRestAsync(repository, request, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<ServerCommitResult> CreateCommitViaRestAsync(
        RepositoryCoordinates repository,
        ServerCommitRequest request,
        CancellationToken cancellationToken)
    {
        string root = $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/git";
        RestGitCommitDto parent = await _transport.GetAsync(
            $"{root}/commits/{Escape(request.ExpectedHeadSha)}",
            GitHubJsonContext.Default.RestGitCommitDto,
            cancellationToken).ConfigureAwait(false);

        var entries = new List<CreateTreeEntryDto>(
            request.Additions.Count + request.Deletions.Count);
        foreach (CommitFileAddition addition in request.Additions)
        {
            var blobRequest = new CreateBlobDto
            {
                Content = Convert.ToBase64String(addition.Contents.Span),
            };
            CreatedBlobDto blob = await _transport.PostAsync(
                $"{root}/blobs",
                blobRequest,
                GitHubJsonContext.Default.CreateBlobDto,
                GitHubJsonContext.Default.CreatedBlobDto,
                cancellationToken).ConfigureAwait(false);
            entries.Add(new CreateTreeEntryDto
            {
                Path = addition.Path,
                Sha = blob.Sha,
            });
        }

        entries.AddRange(request.Deletions.Select(static path => new CreateTreeEntryDto
        {
            Path = path,
            Sha = null,
        }));

        var treeRequest = new CreateTreeDto
        {
            BaseTree = parent.Tree.Sha,
            Tree = entries,
        };
        CreatedTreeDto tree = await _transport.PostAsync(
            $"{root}/trees",
            treeRequest,
            GitHubJsonContext.Default.CreateTreeDto,
            GitHubJsonContext.Default.CreatedTreeDto,
            cancellationToken).ConfigureAwait(false);

        var commitRequest = new CreateGitCommitDto
        {
            Message = string.IsNullOrWhiteSpace(request.Body)
                ? request.Headline
                : $"{request.Headline}\n\n{request.Body}",
            Tree = tree.Sha,
            Parents = [request.ExpectedHeadSha],
        };
        CreatedGitCommitDto commit = await _transport.PostAsync(
            $"{root}/commits",
            commitRequest,
            GitHubJsonContext.Default.CreateGitCommitDto,
            GitHubJsonContext.Default.CreatedGitCommitDto,
            cancellationToken).ConfigureAwait(false);

        var update = new UpdateReferenceDto
        {
            Sha = commit.Sha,
            Force = false,
        };
        await _transport.PatchAsync(
            $"{root}/refs/heads/{Escape(request.BranchName)}",
            update,
            GitHubJsonContext.Default.UpdateReferenceDto,
            GitHubJsonContext.Default.RestReferenceDto,
            cancellationToken).ConfigureAwait(false);
        return new ServerCommitResult(
            commit.Sha,
            ParseAbsoluteUri(commit.HtmlUrl, "commit URL"));
    }

    private async Task<List<T>> GetAllPagesAsync<T>(
        Uri first,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> responseType,
        CancellationToken cancellationToken)
    {
        var results = new List<T>();
        Uri? next = first;
        while (next is not null)
        {
            TransportResponse<List<T>> page = await _transport.GetPageAsync(
                next,
                responseType,
                cancellationToken).ConfigureAwait(false);
            results.AddRange(page.Value);
            next = page.NextUri;
        }

        return results;
    }

    private Task<RestBranchDto> GetBranchAsync(
        RepositoryCoordinates repository,
        string branchName,
        CancellationToken cancellationToken)
        => _transport.GetAsync(
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/branches/" +
            Escape(branchName),
            GitHubJsonContext.Default.RestBranchDto,
            cancellationToken);

    private async Task<RepositoryInfo> WaitForForkAsync(
        RepositoryCoordinates upstream,
        RepositoryCoordinates fork,
        CancellationToken cancellationToken)
    {
        GitHubApiException? lastException = null;
        for (int attempt = 0; attempt < _options.ForkAvailabilityMaxAttempts; attempt++)
        {
            try
            {
                RepositoryInfo repository = await GetRepositoryAsync(
                    fork,
                    cancellationToken).ConfigureAwait(false);
                if (!CoordinatesEqual(repository.Parent, upstream))
                {
                    throw new GitHubApiException(
                        $"Repository '{fork}' exists but is not a fork of '{upstream}'.",
                        HttpStatusCode.Conflict,
                        requestId: null);
                }

                return repository;
            }
            catch (GitHubApiException exception) when (
                IsForkNotReady(exception) &&
                attempt + 1 < _options.ForkAvailabilityMaxAttempts)
            {
                lastException = exception;
                TimeSpan delay = TimeSpan.FromMilliseconds(Math.Clamp(
                    (_options.ForkAvailabilityBaseDelay * Math.Pow(2, attempt)).TotalMilliseconds,
                    0,
                    5_000));
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw lastException ??
            new GitHubApiException($"GitHub fork '{fork}' did not become available.");
    }

    private Task<T> ExecuteMutationAsync<T>(
        MutationRequest mutation,
        string fingerprint,
        Func<Task<T>> action)
    {
        MutationEntry entry = _mutations.GetOrAdd(
            mutation.IdempotencyKey,
            _ => new MutationEntry(
                fingerprint,
                new Lazy<Task<object>>(
                    async () => (object)(await action().ConfigureAwait(false))!,
                    LazyThreadSafetyMode.ExecutionAndPublication)));
        if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Idempotency key '{mutation.IdempotencyKey}' was already used for different inputs.");
        }

        return AwaitMutationAsync<T>(entry.Result);
    }

    private static async Task<T> AwaitMutationAsync<T>(Lazy<Task<object>> result)
        => (T)await result.Value.ConfigureAwait(false);

    private void OnRateLimitObserved(object? sender, RateLimitInfo rateLimit)
        => PublishRateLimit(rateLimit);

    private void ObserveGraphQlRateLimit(GraphQlRateLimitDto? rateLimit)
    {
        if (rateLimit is not null)
        {
            PublishRateLimit(new RateLimitInfo(
                "graphql",
                rateLimit.Limit,
                rateLimit.Remaining,
                rateLimit.Used,
                rateLimit.ResetAt));
        }
    }

    private void PublishRateLimit(RateLimitInfo rateLimit)
    {
        Volatile.Write(ref _lastRateLimit, rateLimit);
        RateLimitObserved?.Invoke(this, rateLimit);
    }

    private static void ThrowIfGraphQlErrors(List<GraphQlErrorDto>? errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return;
        }

        HttpStatusCode? status;
        if (errors.Any(static error =>
                string.Equals(error.Type, "NOT_FOUND", StringComparison.OrdinalIgnoreCase)))
        {
            status = HttpStatusCode.NotFound;
        }
        else if (errors.Any(static error =>
                     error.Message.Contains("expectedHeadOid", StringComparison.OrdinalIgnoreCase) ||
                     error.Message.Contains("head oid", StringComparison.OrdinalIgnoreCase)))
        {
            status = HttpStatusCode.Conflict;
        }
        else
        {
            status = null;
        }

        throw new GitHubApiException(
            errors[0].Message,
            status,
            requestId: null,
            errors.Select(static error => error.Message).ToArray());
    }

    private static GitHubApiException InvalidGraphQlResponse(string expected)
        => new($"GitHub GraphQL response did not contain {expected}.");

    private static bool IsGraphQlUnavailable(GitHubApiException exception)
        => exception.StatusCode is HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented ||
            (exception.StatusCode == HttpStatusCode.NotFound &&
             exception.Errors.Count == 0 &&
             !exception.Message.StartsWith(
                 "GitHub repository '",
                 StringComparison.Ordinal));

    private static RepositoryTreeEntryType ParseTreeEntryType(string type)
        => type switch
        {
            "blob" => RepositoryTreeEntryType.Blob,
            "tree" => RepositoryTreeEntryType.Tree,
            "commit" => RepositoryTreeEntryType.Commit,
            _ => throw new GitHubApiException($"GitHub returned unknown tree entry type '{type}'."),
        };

    private static PullRequestInfo MapPullRequest(RestPullRequestDto pullRequest)
    {
        string? headOwner = pullRequest.Head.Repo?.Owner.Login;
        if (string.IsNullOrWhiteSpace(headOwner))
        {
            headOwner = pullRequest.Head.User?.Login;
        }

        if (string.IsNullOrWhiteSpace(headOwner))
        {
            int separator = pullRequest.Head.Label.IndexOf(':');
            if (separator > 0)
            {
                headOwner = pullRequest.Head.Label[..separator];
            }
        }

        if (string.IsNullOrWhiteSpace(headOwner))
        {
            throw new GitHubApiException(
                $"GitHub pull request #{pullRequest.Number} did not identify its head owner.");
        }

        return new(
            pullRequest.Number,
            pullRequest.NodeId,
            pullRequest.Title,
            pullRequest.Body,
            ParsePullRequestState(pullRequest.State),
            pullRequest.Draft,
            headOwner,
            pullRequest.Head.Ref,
            pullRequest.Head.Sha,
            pullRequest.Base.Ref,
            ParseAbsoluteUri(pullRequest.HtmlUrl, "pull request URL"),
            pullRequest.CreatedAt,
            pullRequest.UpdatedAt);
    }

    private static bool IsForkNotReady(GitHubApiException exception)
        => exception.StatusCode == HttpStatusCode.NotFound ||
            exception.Message.Contains("default branch", StringComparison.OrdinalIgnoreCase);

    private static PullRequestState ParsePullRequestState(string state)
        => state switch
        {
            "open" => PullRequestState.Open,
            "closed" => PullRequestState.Closed,
            _ => throw new GitHubApiException(
                $"GitHub returned unknown pull request state '{state}'."),
        };

    private static bool ContainsExactToken(string title, string token)
    {
        int index = title.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int end = index + token.Length;
            bool startsAtBoundary = index == 0 || !char.IsLetterOrDigit(title[index - 1]);
            bool endsAtBoundary = end == title.Length || !char.IsLetterOrDigit(title[end]);
            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            index = title.IndexOf(token, end, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void ValidateCommitRequest(ServerCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BranchName);
        ValidateSha(request.ExpectedHeadSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Headline);
        ArgumentNullException.ThrowIfNull(request.Additions);
        ArgumentNullException.ThrowIfNull(request.Deletions);
        if (request.Additions.Count == 0 && request.Deletions.Count == 0)
        {
            throw new ArgumentException("A server-side commit must contain at least one file change.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (CommitFileAddition addition in request.Additions)
        {
            ArgumentNullException.ThrowIfNull(addition);
            ValidatePath(addition.Path);
            if (!paths.Add(addition.Path))
            {
                throw new ArgumentException($"Duplicate commit path '{addition.Path}'.");
            }
        }

        foreach (string deletion in request.Deletions)
        {
            ValidatePath(deletion);
            if (!paths.Add(deletion))
            {
                throw new ArgumentException($"Duplicate commit path '{deletion}'.");
            }
        }
    }

    private static void ValidatePullRequest(CreatePullRequestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HeadOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HeadBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseBranch);
    }

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] == '/' ||
            path.EndsWith('/') ||
            path.Split('/').Any(static part =>
                part.Length == 0 || part is "." or ".."))
        {
            throw new ArgumentException(
                "GitHub paths must be relative, normalized, and contain no empty, '.' or '..' segments.",
                nameof(path));
        }
    }

    private static void ValidateSha(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        if (sha.Length is < 7 or > 64 ||
            sha.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A Git object ID must contain 7 to 64 hexadecimal characters.");
        }
    }

    private static string GetCommitFingerprint(
        RepositoryCoordinates repository,
        ServerCommitRequest request)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, repository.ToString());
        AppendHash(hash, request.BranchName);
        AppendHash(hash, request.ExpectedHeadSha);
        AppendHash(hash, request.Headline);
        AppendHash(hash, request.Body ?? "");
        foreach (CommitFileAddition addition in request.Additions)
        {
            AppendHash(hash, addition.Path);
            hash.AppendData(addition.Contents.Span);
        }

        foreach (string deletion in request.Deletions)
        {
            AppendHash(hash, deletion);
        }

        return "create-commit|" + Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHash(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string Escape(string value)
        => Uri.EscapeDataString(value);

    private static string EscapePath(string path)
        => string.Join('/', path.Split('/').Select(Escape));

    private static string RemoveHeadsPrefix(string reference)
        => reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : reference;

    private static bool CoordinatesEqual(
        RepositoryCoordinates? left,
        RepositoryCoordinates right)
        => left is not null &&
            string.Equals(left.Owner, right.Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private static Uri ParseAbsoluteUri(string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw new GitHubApiException($"GitHub returned an invalid {field}.");
        }

        return uri;
    }

    private sealed record MutationEntry(
        string Fingerprint,
        Lazy<Task<object>> Result);

    private sealed record ForkMutationResult(RepositoryInfo? Existing);
}
