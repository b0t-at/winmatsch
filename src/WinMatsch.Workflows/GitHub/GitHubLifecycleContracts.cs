using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public interface IFinalArtifactRevalidator
{
    public Task<FinalArtifactRevalidationResult> RevalidateAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken);
}

public interface ISubmissionProgressSink
{
    public Task RecordAsync(
        RemoteMutationState remoteState,
        SubmissionJournalState state,
        CancellationToken cancellationToken);
}

public interface IRemoteOperationLockProvider
{
    public ValueTask<IAsyncDisposable> AcquireAsync(
        string repository,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken);
}

public interface IRemoteLockIdentityResolver
{
    public string Resolve(string repository);
}

public interface IRepositorySubmissionEvidenceProvider
{
    public Task<RepositorySubmissionEvidence> GetEvidenceAsync(
        GitHubSubmissionRequest request,
        string upstreamHeadSha,
        CancellationToken cancellationToken);
}

public interface IPullRequestManifestEvidenceProvider
{
    public Task<IReadOnlyList<PullRequestInfo>> GetCandidatesAsync(
        GitHubSubmissionPlan plan,
        IReadOnlyList<PullRequestInfo> openPullRequests,
        CancellationToken cancellationToken)
        => Task.FromResult(openPullRequests);

    public Task<PullRequestManifestEvidence> GetEvidenceAsync(
        GitHubSubmissionPlan plan,
        PullRequestInfo pullRequest,
        CancellationToken cancellationToken);
}

public static class PullRequestManifestEvidenceLimits
{
    public const int MaximumOpenPullRequests = 5_000;
    public const int MaximumCandidates = 64;
    public const int MaximumContentFiles = 16;
    public const long MaximumContentBytes = 1_048_576;
}

public interface IRevalidationScratchSpace
{
    public string Create();

    public void Delete(string path);
}

public interface IGitHubBranchNameGenerator
{
    public string Create(GitHubBranchNameContext context);
}

public sealed class DefaultGitHubBranchNameGenerator : IGitHubBranchNameGenerator
{
    public string Create(GitHubBranchNameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string package = NormalizeSegment(context.PackageIdentifier.Value);
        string version = NormalizeSegment(context.PackageVersion.Value);
        string replacement = context.SupersedesPullRequestNumber is { } number
            ? $"/replacement-{number}"
            : "";
        string reservation = CreateReservationToken(context);
        return $"winmatsch/submissions/{context.Operation.ToString().ToLowerInvariant()}/{package}/{version}{replacement}{reservation}";
    }

    private static string CreateReservationToken(GitHubBranchNameContext context)
    {
        if (string.IsNullOrWhiteSpace(context.BaseBranch)
            || string.IsNullOrWhiteSpace(context.BaseSha)
            || string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            return "";
        }

        string identity = string.Join(
            '\n',
            context.BaseBranch,
            context.BaseSha,
            context.IdempotencyKey);
        string token = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)))[..16]
            .ToLowerInvariant();
        return $"/reservation-{token}";
    }

    private static string NormalizeSegment(string value)
    {
        string normalized = string.Concat(value.Select(static character =>
            char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-'));
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        normalized = normalized.Trim('-');
        if (normalized.Length == 0)
        {
            return "value";
        }

        return normalized.Length <= 64 ? normalized : normalized[..64].TrimEnd('-');
    }
}
