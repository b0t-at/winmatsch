using WinMatsch.Core;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public interface IFinalArtifactRevalidator
{
    public Task<FinalArtifactRevalidationResult> RevalidateAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken);
}

public interface IRemoteOperationLockProvider
{
    public ValueTask<IAsyncDisposable> AcquireAsync(
        string repository,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken);
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
        return $"winmatsch/submissions/{package}/{version}{replacement}";
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
