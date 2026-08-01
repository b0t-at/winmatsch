using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WinMatsch.Cli.Output;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
using WinMatsch.Workflows.GitHub;
using YamlDotNet.RepresentationModel;

namespace WinMatsch.Cli.Commands.Maintenance;

/// <summary>
/// Validates a token by asking the GitHub API for the authenticated user. The token travels
/// only inside the repository client's Authorization header; failure messages are redacted
/// and never contain the secret.
/// </summary>
public sealed class GitHubTokenValidator : ITokenValidator
{
    private readonly Func<string, IGitHubRepositoryClient> _clientFactory;

    public GitHubTokenValidator(Func<string, IGitHubRepositoryClient>? clientFactory = null)
    {
        _clientFactory = clientFactory
            ?? (token => new GitHubRepositoryClient(new HttpClient(), token));
    }

    public async Task<TokenValidationResult> ValidateAsync(
        GitHubToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        using IGitHubRepositoryClient client = _clientFactory(token.RevealValue());
        try
        {
            GitHubUser user = await client
                .GetAuthenticatedUserAsync(cancellationToken)
                .ConfigureAwait(false);
            return TokenValidationResult.Valid(user.Login);
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            return TokenValidationResult.Invalid("GitHub rejected the token as unauthorized.");
        }
        catch (GitHubApiException exception)
        {
            return TokenValidationResult.Invalid(
                $"Token validation failed: {CliRedactor.Redact(exception.Message)}");
        }
    }
}

/// <summary>
/// Observes the open tool-owned pull requests a fork owner has against the upstream
/// repository. Tool ownership is proven by the <c>winmatsch/</c> head-branch prefix and the
/// association marker in the pull request body; anything else is reported as not tool-owned
/// and is never acted on. The core REST surface exposes neither labels nor comments, so those
/// collections stay empty here; richer sources can be injected where available.
/// </summary>
public sealed class ToolPullRequestObservationSource : IPullRequestFeedbackSource, IDisposable
{
    /// <summary>The head-branch prefix that marks a branch as tool-created.</summary>
    public const string ToolBranchPrefix = "winmatsch/";

    /// <summary>The body marker that binds a tool PR to its package association.</summary>
    public const string AssociationMarker = "<!-- winmatsch:package=";

    private readonly IGitHubRepositoryClient _gitHub;
    private readonly string _forkOwner;
    private readonly IPullRequestMetadataSource? _metadata;

    public ToolPullRequestObservationSource(
        IGitHubRepositoryClient gitHub,
        string forkOwner,
        IPullRequestMetadataSource? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(gitHub);
        ArgumentException.ThrowIfNullOrWhiteSpace(forkOwner);
        _gitHub = gitHub;
        _forkOwner = forkOwner;
        _metadata = metadata;
    }

    public async Task<ImmutableArray<PullRequestObservation>> GetOpenToolPullRequestsAsync(
        RepositoryCoordinates upstream,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PullRequestInfo> pullRequests = await _gitHub
            .SearchPullRequestsAsync(
                upstream,
                new PullRequestSearch(PullRequestState.Open, HeadOwner: _forkOwner),
                cancellationToken)
            .ConfigureAwait(false);
        var observations = ImmutableArray.CreateBuilder<PullRequestObservation>();
        foreach (PullRequestInfo pullRequest in pullRequests.Where(pullRequest =>
                     pullRequest.HeadOwner.Equals(_forkOwner, StringComparison.OrdinalIgnoreCase)))
        {
            PullRequestMetadata metadata = _metadata is null || !IsToolOwned(pullRequest)
                ? PullRequestMetadata.Empty
                : await _metadata.GetAsync(upstream, pullRequest.Number, cancellationToken)
                    .ConfigureAwait(false);
            observations.Add(new PullRequestObservation
            {
                PullRequest = pullRequest,
                Author = pullRequest.HeadOwner,
                ToolOwned = IsToolOwned(pullRequest),
                Labels = metadata.Labels,
                Comments = metadata.Comments,
            });
        }

        return observations.ToImmutable();
    }

    /// <summary>Whether the pull request carries both tool-ownership proofs.</summary>
    public static bool IsToolOwned(PullRequestInfo pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        return pullRequest.HeadBranch.StartsWith(ToolBranchPrefix, StringComparison.Ordinal)
            && pullRequest.Body?.Contains(AssociationMarker, StringComparison.Ordinal) == true;
    }

    public void Dispose()
    {
        _metadata?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed record PullRequestMetadata(
    ImmutableArray<string> Labels,
    ImmutableArray<PullRequestCommentObservation> Comments)
{
    public static PullRequestMetadata Empty { get; } = new([], []);
}

public interface IPullRequestMetadataSource : IDisposable
{
    public Task<PullRequestMetadata> GetAsync(
        RepositoryCoordinates repository,
        long pullRequestNumber,
        CancellationToken cancellationToken);
}

/// <summary>Loads trusted pull-request labels and issue comments for feedback classification.</summary>
public sealed class GitHubPullRequestMetadataSource : IPullRequestMetadataSource
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly GitHubClientOptions _options;
    private readonly string _token;

    public GitHubPullRequestMetadataSource(
        GitHubClientOptions options,
        string token,
        HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _token = token;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<PullRequestMetadata> GetAsync(
        RepositoryCoordinates repository,
        long pullRequestNumber,
        CancellationToken cancellationToken)
    {
        string root = $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}/issues/"
            + pullRequestNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using JsonPage issue = await GetJsonPageAsync(
            new Uri(_options.ApiBaseUri, root),
            cancellationToken).ConfigureAwait(false);
        ImmutableArray<string> labels =
        [
            .. issue.Document.RootElement.GetProperty("labels")
                .EnumerateArray()
                .Select(static label => label.GetProperty("name").GetString())
                .OfType<string>(),
        ];
        var observations = ImmutableArray.CreateBuilder<PullRequestCommentObservation>();
        Uri? next = new(_options.ApiBaseUri, root + "/comments?per_page=100");
        for (int pageNumber = 0;
             next is not null && pageNumber < 10 && observations.Count < 1000;
             pageNumber++)
        {
            using JsonPage comments = await GetJsonPageAsync(next, cancellationToken)
                .ConfigureAwait(false);
            foreach (JsonElement comment in comments.Document.RootElement.EnumerateArray())
            {
                if (!comment.TryGetProperty("user", out JsonElement user)
                    || user.ValueKind != JsonValueKind.Object
                    || !user.TryGetProperty("login", out JsonElement login)
                    || login.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                observations.Add(new(
                    login.GetString()!,
                    comment.GetProperty("body").GetString() ?? "",
                    comment.GetProperty("created_at").GetDateTimeOffset()));
                if (observations.Count == 1000)
                {
                    break;
                }
            }

            next = comments.Next;
        }

        return new(labels, observations.ToImmutable());
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<JsonPage> GetJsonPageAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (!SameAuthority(uri, _options.ApiBaseUri))
        {
            throw new InvalidDataException(
                "GitHub pagination attempted to leave the configured API authority.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException(
                $"GitHub pull-request metadata read failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                response.Headers.TryGetValues("X-GitHub-Request-Id", out IEnumerable<string>? ids)
                    ? ids.FirstOrDefault()
                    : null);
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("GitHub pull-request metadata exceeded the response limit.");
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var content = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (content.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException(
                    "GitHub pull-request metadata exceeded the response limit.");
            }

            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return new(
            JsonDocument.Parse(content.ToArray()),
            NextLink(response.Headers));
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static Uri? NextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string part in string.Join(',', values).Split(','))
        {
            string trimmed = part.Trim();
            if (!trimmed.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int start = trimmed.IndexOf('<');
            int end = trimmed.IndexOf('>');
            if (start >= 0
                && end > start
                && Uri.TryCreate(trimmed[(start + 1)..end], UriKind.Absolute, out Uri? next))
            {
                return next;
            }
        }

        return null;
    }

    private static bool SameAuthority(Uri left, Uri right)
        => left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
            && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;

    private sealed record JsonPage(JsonDocument Document, Uri? Next) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }
}

/// <summary>Probes one installer URL and classifies the artifact's liveness.</summary>
public interface IInstallerUrlProber : IDisposable
{
    void IDisposable.Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public Task<DeadArtifactState> ProbeAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// The production prober over <see cref="InstallerDownloader.ProbeAsync"/>. Only a confirmed
/// absence counts as dead: a 404/410 seen by the probe (which HEADs first) is re-verified
/// with an independent ranged GET before it is reported as missing, because some origins
/// reject HEAD while serving GET. Authentication, authorization, redirect, and other
/// rejections classify as blocked, and transient transport failures stay transient, so the
/// removal workflow escalates instead of treating them as proof of death.
/// </summary>
public sealed class HttpInstallerUrlProber : IInstallerUrlProber
{
    private readonly InstallerDownloader _downloader;
    private readonly bool _ownsDownloader;
    private readonly Func<HttpMessageHandler> _confirmationHandlerFactory;

    public HttpInstallerUrlProber(
        InstallerDownloader? downloader = null,
        Func<HttpMessageHandler>? confirmationHandlerFactory = null)
    {
        _ownsDownloader = downloader is null;
        _downloader = downloader ?? new InstallerDownloader();
        _confirmationHandlerFactory = confirmationHandlerFactory
            ?? (static () => new HttpClientHandler());
    }

    public void Dispose()
    {
        if (_ownsDownloader)
        {
            _downloader.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public async Task<DeadArtifactState> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        try
        {
            _ = await _downloader.ProbeAsync(url, cancellationToken).ConfigureAwait(false);
            return DeadArtifactState.Exists;
        }
        catch (DownloadHttpException exception)
            when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return await ConfirmMissingAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadException exception)
        {
            return Classify(exception);
        }
        catch (HttpRequestException)
        {
            return DeadArtifactState.NetworkBlocked;
        }
    }

    /// <summary>Maps a download failure to the artifact state the removal workflow acts on.</summary>
    internal static DeadArtifactState Classify(DownloadException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is DownloadHttpException http)
        {
            return http.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                ? DeadArtifactState.PermanentlyMissing
                : DeadArtifactState.NetworkBlocked;
        }

        return exception.FailureKind == DownloadFailureKind.TransientNetwork
            ? DeadArtifactState.TransientFailure
            : DeadArtifactState.NetworkBlocked;
    }

    /// <summary>
    /// Double-checks an absence status with a ranged GET; only a second 404/410 counts as
    /// missing, anything indeterminate escalates.
    /// </summary>
    private async Task<DeadArtifactState> ConfirmMissingAsync(string url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(_confirmationHandlerFactory(), disposeHandler: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return DeadArtifactState.PermanentlyMissing;
            }

            return response.IsSuccessStatusCode
                ? DeadArtifactState.Exists
                : DeadArtifactState.NetworkBlocked;
        }
        catch (HttpRequestException)
        {
            return DeadArtifactState.TransientFailure;
        }
    }
}

/// <summary>
/// Inspects one exact package version against the live upstream repository: whether the
/// version directory still exists on the default branch, and whether each declared installer
/// URL is dead. Every read is fresh — nothing is answered from caches — so removal plans are
/// grounded in current upstream state.
/// </summary>
public sealed class GitHubDeadVersionInspector : IDeadVersionInspector
{
    private readonly IGitHubRepositoryClient _gitHub;
    private readonly IInstallerUrlProber _prober;

    public GitHubDeadVersionInspector(IGitHubRepositoryClient gitHub, IInstallerUrlProber prober)
    {
        ArgumentNullException.ThrowIfNull(gitHub);
        ArgumentNullException.ThrowIfNull(prober);
        _gitHub = gitHub;
        _prober = prober;
    }

    public void Dispose()
    {
        _prober.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<DeadVersionInspection> InspectAsync(
        RepositoryCoordinates upstream,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        ArgumentNullException.ThrowIfNull(packageVersion);

        BranchState defaultBranch = await _gitHub
            .GetDefaultBranchAsync(upstream, cancellationToken)
            .ConfigureAwait(false);
        string versionDirectory = ManifestPaths.GetVersionDirectory(packageIdentifier, packageVersion);

        IReadOnlyList<ManifestFile> files;
        try
        {
            files = await GetManifestFilesAsync(
                    upstream,
                    versionDirectory,
                    defaultBranch.HeadSha,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new DeadVersionInspection(packageIdentifier, packageVersion, ExistsUpstream: false, []);
        }
        catch (GitHubApiException)
        {
            // An indeterminate read never proves anything; surface it as transient so the
            // removal workflow escalates instead of planning a deletion.
            return new DeadVersionInspection(
                packageIdentifier,
                packageVersion,
                ExistsUpstream: true,
                [DeadArtifactState.TransientFailure]);
        }

        if (files.Count == 0)
        {
            return new DeadVersionInspection(packageIdentifier, packageVersion, ExistsUpstream: false, []);
        }

        string installerFileName = ManifestPaths.GetInstallerFileName(packageIdentifier);
        ManifestFile? installerManifest = files.FirstOrDefault(file =>
            string.Equals(Path.GetFileName(file.Path), installerFileName, StringComparison.OrdinalIgnoreCase));
        if (installerManifest is null)
        {
            return new DeadVersionInspection(
                packageIdentifier,
                packageVersion,
                ExistsUpstream: true,
                [DeadArtifactState.TransientFailure]);
        }

        IReadOnlyList<string> urls;
        try
        {
            urls = ExtractInstallerUrls(installerManifest.Bytes);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // A manifest we cannot parse proves nothing; classify as indeterminate so the
            // removal workflow escalates instead of planning a deletion.
            return new DeadVersionInspection(
                packageIdentifier,
                packageVersion,
                ExistsUpstream: true,
                [DeadArtifactState.TransientFailure]);
        }

        if (urls.Count == 0)
        {
            return new DeadVersionInspection(
                packageIdentifier,
                packageVersion,
                ExistsUpstream: true,
                [DeadArtifactState.TransientFailure]);
        }

        var states = ImmutableArray.CreateBuilder<DeadArtifactState>(urls.Count);
        foreach (string url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            states.Add(await _prober.ProbeAsync(url, cancellationToken).ConfigureAwait(false));
        }

        return new DeadVersionInspection(
            packageIdentifier,
            packageVersion,
            ExistsUpstream: true,
            states.MoveToImmutable());
    }

    private async Task<IReadOnlyList<ManifestFile>> GetManifestFilesAsync(
        RepositoryCoordinates repository,
        string directory,
        string reference,
        CancellationToken cancellationToken)
    {
        string treeish = reference;
        foreach (string segment in directory.Split('/'))
        {
            IReadOnlyList<RepositoryTreeEntry> entries = await _gitHub
                .GetTreeAsync(repository, treeish, recursive: false, cancellationToken)
                .ConfigureAwait(false);
            RepositoryTreeEntry? next = entries.FirstOrDefault(entry =>
                entry.Type == RepositoryTreeEntryType.Tree
                && string.Equals(entry.Path, segment, StringComparison.Ordinal));
            if (next is null)
            {
                return [];
            }

            treeish = next.Sha;
        }

        IReadOnlyList<RepositoryTreeEntry> versionEntries = await _gitHub
            .GetTreeAsync(repository, treeish, recursive: false, cancellationToken)
            .ConfigureAwait(false);
        var files = new List<ManifestFile>();
        foreach (RepositoryTreeEntry entry in versionEntries.Where(static entry =>
                     entry.Type == RepositoryTreeEntryType.Blob
                     && entry.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            string path = $"{directory}/{entry.Path}";
            RepositoryContent content = await _gitHub
                .GetContentAsync(repository, path, reference, cancellationToken)
                .ConfigureAwait(false);
            files.Add(new(path, content.Sha, content.Bytes));
        }

        return files;
    }

    /// <summary>Collects every <c>InstallerUrl</c> scalar in the installer manifest, in order.</summary>
    internal static IReadOnlyList<string> ExtractInstallerUrls(ReadOnlyMemory<byte> manifestBytes)
    {
        const int maximumDepth = 64;
        const int maximumNodes = 4096;
        const int maximumUrls = 256;
        string text = System.Text.Encoding.UTF8.GetString(manifestBytes.Span);
        var stream = new YamlStream();
        using (var reader = new StringReader(text))
        {
            stream.Load(reader);
        }

        var urls = new List<string>();
        var pending = new Queue<(YamlNode Node, int Depth)>();
        foreach (YamlDocument document in stream.Documents)
        {
            pending.Enqueue((document.RootNode, 0));
        }

        var visited = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        int nodeCount = 0;
        while (pending.TryDequeue(out (YamlNode Node, int Depth) item))
        {
            if (item.Depth > maximumDepth
                || ++nodeCount > maximumNodes
                || !item.Node.Anchor.IsEmpty
                || !visited.Add(item.Node))
            {
                throw new YamlDotNet.Core.YamlException(
                    "Installer manifest YAML aliases, cycles, or excessive nesting are not supported.");
            }

            switch (item.Node)
            {
                case YamlMappingNode mapping:
                    foreach ((YamlNode key, YamlNode value) in mapping.Children)
                    {
                        if (key is YamlScalarNode { Value: "InstallerUrl" }
                            && value is YamlScalarNode { Value: { } url }
                            && !string.IsNullOrWhiteSpace(url))
                        {
                            urls.Add(url.Trim());
                            if (urls.Count > maximumUrls)
                            {
                                throw new YamlDotNet.Core.YamlException(
                                    "Installer manifest contains too many InstallerUrl values.");
                            }
                        }
                        else
                        {
                            pending.Enqueue((value, item.Depth + 1));
                        }
                    }

                    break;
                case YamlSequenceNode sequence:
                    foreach (YamlNode child in sequence.Children)
                    {
                        pending.Enqueue((child, item.Depth + 1));
                    }

                    break;
                default:
                    break;
            }
        }

        return urls;
    }
}
