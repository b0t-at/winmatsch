using System.Collections.Immutable;
using System.Net;
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
        IGitHubRepositoryClient client = _clientFactory(token.RevealValue());
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
                $"Token validation failed: {GitHubSubmissionFormatter.Redact(exception.Message)}");
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
public sealed class ToolPullRequestObservationSource : IPullRequestFeedbackSource
{
    /// <summary>The head-branch prefix that marks a branch as tool-created.</summary>
    public const string ToolBranchPrefix = "winmatsch/";

    /// <summary>The body marker that binds a tool PR to its package association.</summary>
    public const string AssociationMarker = "<!-- winmatsch:package=";

    private readonly IGitHubRepositoryClient _gitHub;
    private readonly string _forkOwner;

    public ToolPullRequestObservationSource(IGitHubRepositoryClient gitHub, string forkOwner)
    {
        ArgumentNullException.ThrowIfNull(gitHub);
        ArgumentException.ThrowIfNullOrWhiteSpace(forkOwner);
        _gitHub = gitHub;
        _forkOwner = forkOwner;
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
        return
        [
            .. pullRequests.Select(pullRequest => new PullRequestObservation
            {
                PullRequest = pullRequest,
                Author = pullRequest.HeadOwner,
                ToolOwned = IsToolOwned(pullRequest),
            }),
        ];
    }

    /// <summary>Whether the pull request carries both tool-ownership proofs.</summary>
    public static bool IsToolOwned(PullRequestInfo pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        return pullRequest.HeadBranch.StartsWith(ToolBranchPrefix, StringComparison.Ordinal)
            && pullRequest.Body?.Contains(AssociationMarker, StringComparison.Ordinal) == true;
    }
}

/// <summary>Probes one installer URL and classifies the artifact's liveness.</summary>
public interface IInstallerUrlProber
{
    public Task<DeadArtifactState> ProbeAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// The production prober over <see cref="InstallerDownloader.ProbeAsync"/>. Permanent HTTP
/// rejection is the only state that counts as dead; transient transport failures and
/// unclassified network errors map to states the removal workflow escalates instead of
/// treating as proof of death.
/// </summary>
public sealed class HttpInstallerUrlProber : IInstallerUrlProber
{
    private readonly InstallerDownloader _downloader;

    public HttpInstallerUrlProber(InstallerDownloader? downloader = null)
    {
        _downloader = downloader ?? new InstallerDownloader();
    }

    public async Task<DeadArtifactState> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        try
        {
            _ = await _downloader.ProbeAsync(url, cancellationToken).ConfigureAwait(false);
            return DeadArtifactState.Exists;
        }
        catch (DownloadException exception)
        {
            return exception.FailureKind switch
            {
                DownloadFailureKind.PermanentHttp => DeadArtifactState.PermanentlyMissing,
                DownloadFailureKind.TransientNetwork => DeadArtifactState.TransientFailure,
                _ => DeadArtifactState.NetworkBlocked,
            };
        }
        catch (HttpRequestException)
        {
            return DeadArtifactState.NetworkBlocked;
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
            files = await _gitHub
                .GetManifestFilesAsync(upstream, versionDirectory, defaultBranch.HeadSha, cancellationToken)
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

        IReadOnlyList<string> urls = ExtractInstallerUrls(installerManifest.Bytes);
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

    /// <summary>Collects every <c>InstallerUrl</c> scalar in the installer manifest, in order.</summary>
    internal static IReadOnlyList<string> ExtractInstallerUrls(ReadOnlyMemory<byte> manifestBytes)
    {
        string text = System.Text.Encoding.UTF8.GetString(manifestBytes.Span);
        var stream = new YamlStream();
        using (var reader = new StringReader(text))
        {
            stream.Load(reader);
        }

        var urls = new List<string>();
        foreach (YamlDocument document in stream.Documents)
        {
            CollectInstallerUrls(document.RootNode, urls);
        }

        return urls;
    }

    private static void CollectInstallerUrls(YamlNode node, List<string> urls)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach ((YamlNode key, YamlNode value) in mapping.Children)
                {
                    if (key is YamlScalarNode { Value: "InstallerUrl" }
                        && value is YamlScalarNode { Value: { } url }
                        && !string.IsNullOrWhiteSpace(url))
                    {
                        urls.Add(url.Trim());
                    }
                    else
                    {
                        CollectInstallerUrls(value, urls);
                    }
                }

                break;
            case YamlSequenceNode sequence:
                foreach (YamlNode child in sequence.Children)
                {
                    CollectInstallerUrls(child, urls);
                }

                break;
            default:
                break;
        }
    }
}

/// <summary>
/// The default repair planner for the <c>complete</c> command: it never plans a repair, so the
/// feedback workflow can only escalate, wait, or apply its fixed known-safe responses. Actual
/// manifest repair remains an explicit, separately reviewed operation.
/// </summary>
public sealed class NullApprovedRepairPlanner : IApprovedRepairPlanner
{
    public Task<GitHubSubmissionRequest?> PlanApprovedRepairAsync(
        PullRequestObservation pullRequest,
        FeedbackClassification classification,
        CancellationToken cancellationToken)
        => Task.FromResult<GitHubSubmissionRequest?>(null);
}
