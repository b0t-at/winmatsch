using System.Net.Http.Headers;
using System.Text.Json;
using WinMatsch.GitHub;

namespace WinMatsch.Cli.Commands.Diagnostics;

/// <summary>
/// Minimal anonymous-capable GitHub REST adapter for public show/list operations. Mutating and
/// authenticated-user methods are intentionally unsupported.
/// </summary>
internal sealed class PublicReadOnlyGitHubClient : IGitHubRepositoryClient
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly GitHubClientOptions _options;
    private string? _token;

    public PublicReadOnlyGitHubClient(
        GitHubClientOptions options,
        string? token = null,
        HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _token = token;
        _httpClient = httpClient ?? new HttpClient();
    }

    public RateLimitInfo? LastRateLimit => null;

    public event EventHandler<RateLimitInfo>? RateLimitObserved
    {
        add { }
        remove { }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task<GitHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
        => throw Unsupported();

    public async Task<RepositoryInfo> GetRepositoryAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await GetJsonAsync(
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}",
            cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        string branchName = RequiredString(root, "default_branch");
        BranchState branch = await GetBranchAsync(repository, branchName, cancellationToken)
            .ConfigureAwait(false);
        return new RepositoryInfo(
            repository,
            RequiredString(root, "node_id"),
            RequiredUri(root, "html_url"),
            RequiredBoolean(root, "private"),
            RequiredBoolean(root, "fork"),
            branch,
            Parent(root));
    }

    public async Task<BranchState> GetDefaultBranchAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await GetJsonAsync(
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}",
            cancellationToken).ConfigureAwait(false);
        return await GetBranchAsync(
            repository,
            RequiredString(document.RootElement, "default_branch"),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RepositoryContent> GetContentAsync(
        RepositoryCoordinates repository,
        string path,
        string reference,
        CancellationToken cancellationToken = default)
    {
        string escapedPath = string.Join('/', path.Split('/').Select(Escape));
        using JsonDocument document = await GetJsonAsync(
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/contents/{escapedPath}"
            + $"?ref={Escape(reference)}",
            cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        string encoded = RequiredString(root, "content")
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("GitHub returned invalid base64 repository content.", exception);
        }

        return new RepositoryContent(
            RequiredString(root, "name"),
            RequiredString(root, "path"),
            RequiredString(root, "sha"),
            RequiredInt64(root, "size"),
            RequiredString(root, "encoding"),
            bytes);
    }

    public async Task<IReadOnlyList<RepositoryTreeEntry>> GetTreeAsync(
        RepositoryCoordinates repository,
        string treeish,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        string relativePath =
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/git/trees/{Escape(treeish)}";
        if (recursive)
        {
            relativePath += "?recursive=1";
        }

        using JsonDocument document = await GetJsonAsync(
            relativePath,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("truncated", out JsonElement truncated)
            && truncated.ValueKind == JsonValueKind.True)
        {
            throw new InvalidDataException(
                "GitHub returned a truncated repository tree; refusing an incomplete result.");
        }

        JsonElement tree = document.RootElement.GetProperty("tree");
        var entries = new List<RepositoryTreeEntry>(tree.GetArrayLength());
        foreach (JsonElement item in tree.EnumerateArray())
        {
            entries.Add(new RepositoryTreeEntry(
                RequiredString(item, "path"),
                RequiredString(item, "sha"),
                RequiredString(item, "type") switch
                {
                    "blob" => RepositoryTreeEntryType.Blob,
                    "tree" => RepositoryTreeEntryType.Tree,
                    "commit" => RepositoryTreeEntryType.Commit,
                    string type => throw new InvalidDataException(
                        $"GitHub returned unsupported tree entry type '{type}'."),
                },
                item.TryGetProperty("size", out JsonElement size)
                    && size.ValueKind == JsonValueKind.Number
                    ? size.GetInt64()
                    : null));
        }

        return entries;
    }

    public Task<IReadOnlyList<ManifestFile>> GetManifestFilesAsync(
        RepositoryCoordinates repository,
        string directory,
        string reference,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<IReadOnlyList<BranchState>> GetBranchesAsync(
        RepositoryCoordinates repository,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<GitReference?> GetReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<GitReference> CreateReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<GitReference> CreateUniqueReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        string sha,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<bool> DeleteReferenceAsync(
        RepositoryCoordinates repository,
        string branchName,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<ServerCommitResult> CreateCommitAsync(
        RepositoryCoordinates repository,
        ServerCommitRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<CompareResult> CompareAsync(
        RepositoryCoordinates repository,
        string baseReference,
        string head,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<ForkResult> EnsureForkAsync(
        RepositoryCoordinates upstream,
        string owner,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<UpstreamSyncResult> SyncForkAsync(
        RepositoryCoordinates fork,
        string branch,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<IReadOnlyList<PullRequestInfo>> SearchPullRequestsAsync(
        RepositoryCoordinates repository,
        PullRequestSearch search,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<PullRequestInfo> CreatePullRequestAsync(
        RepositoryCoordinates repository,
        CreatePullRequestRequest request,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<PullRequestInfo> GetPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<PullRequestComment> CommentOnPullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        string body,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<PullRequestInfo> ClosePullRequestAsync(
        RepositoryCoordinates repository,
        long number,
        MutationRequest mutation,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    private async Task<BranchState> GetBranchAsync(
        RepositoryCoordinates repository,
        string branchName,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetJsonAsync(
            $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/branches/{Escape(branchName)}",
            cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        return new BranchState(
            RequiredString(root, "name"),
            RequiredString(root.GetProperty("commit"), "sha"),
            RequiredBoolean(root, "protected"));
    }

    private async Task<JsonDocument> GetJsonAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        Uri uri = new(_options.ApiBaseUri, relativePath);
        string? token = Volatile.Read(ref _token);
        HttpResponseMessage response = await SendAsync(uri, token, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && !string.IsNullOrWhiteSpace(token))
        {
            _ = Interlocked.Exchange(ref _token, null);
            response.Dispose();
            response = await SendAsync(uri, token: null, cancellationToken)
                .ConfigureAwait(false);
        }

        using (response)
        {
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                throw new InvalidDataException("GitHub response exceeded the public read limit.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new GitHubApiException(
                    $"GitHub public repository read failed with HTTP {(int)response.StatusCode}.",
                    response.StatusCode,
                    response.Headers.TryGetValues("X-GitHub-Request-Id", out IEnumerable<string>? ids)
                        ? ids.FirstOrDefault()
                        : null);
            }

            await using Stream content = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var payload = new MemoryStream();
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (payload.Length + read > MaximumResponseBytes)
                {
                    throw new InvalidDataException("GitHub response exceeded the public read limit.");
                }

                await payload.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            return JsonDocument.Parse(payload.ToArray());
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static RepositoryCoordinates? Parent(JsonElement root)
    {
        if (!root.TryGetProperty("parent", out JsonElement parent)
            || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string fullName = RequiredString(parent, "full_name");
        return RepositoryCoordinates.Parse(fullName);
    }

    private static string RequiredString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"GitHub response is missing string property '{name}'.");

    private static bool RequiredBoolean(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"GitHub response is missing boolean property '{name}'.");

    private static long RequiredInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : throw new InvalidDataException($"GitHub response is missing numeric property '{name}'.");

    private static Uri RequiredUri(JsonElement element, string name)
        => new(RequiredString(element, name), UriKind.Absolute);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static NotSupportedException Unsupported()
        => new("The anonymous public GitHub adapter supports repository diagnostics only.");
}
