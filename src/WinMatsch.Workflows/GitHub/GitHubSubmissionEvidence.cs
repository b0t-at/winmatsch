using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public sealed class EmptyRepositorySubmissionEvidenceProvider : IRepositorySubmissionEvidenceProvider
{
    public static EmptyRepositorySubmissionEvidenceProvider Instance { get; } = new();

    private EmptyRepositorySubmissionEvidenceProvider()
    {
    }

    public Task<RepositorySubmissionEvidence> GetEvidenceAsync(
        GitHubSubmissionRequest request,
        string upstreamHeadSha,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RepositorySubmissionEvidence.Empty);
    }
}

public sealed class GitHubPullRequestManifestEvidenceProvider(IGitHubRepositoryClient gitHub)
    : IPullRequestManifestEvidenceProvider
{
    private readonly IGitHubRepositoryClient _gitHub =
        gitHub ?? throw new ArgumentNullException(nameof(gitHub));
    private readonly ConcurrentDictionary<EvidenceCacheKey, Task<PullRequestManifestEvidence>> _cache = [];

    public Task<IReadOnlyList<PullRequestInfo>> GetCandidatesAsync(
        GitHubSubmissionPlan plan,
        IReadOnlyList<PullRequestInfo> openPullRequests,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PullRequestInfo[] candidates = [.. openPullRequests];
        if (candidates.Length > PullRequestManifestEvidenceLimits.MaximumCandidates)
        {
            throw new PullRequestEvidenceLimitException(
                $"Manifest evidence candidate count {candidates.Length} exceeds the safe limit of {PullRequestManifestEvidenceLimits.MaximumCandidates}.");
        }

        return Task.FromResult<IReadOnlyList<PullRequestInfo>>(candidates);
    }

    public async Task<PullRequestManifestEvidence> GetEvidenceAsync(
        GitHubSubmissionPlan plan,
        PullRequestInfo pullRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pullRequest);
        string fingerprint = string.Join(
            '\n',
            plan.Request.LocalPlan.FileChanges.Select(static change =>
                $"{change.Kind}|{change.RepositoryPath}|{change.ExpectedState}|"
                + $"{change.ExpectedSha256}|{WorkflowFileChange.Hash(change.Content.AsSpan())}"));
        var key = new EvidenceCacheKey(
            plan.Request.UpstreamRepository.ToString(),
            plan.Request.LocalPlan.PackageIdentifier.Value,
            plan.Request.LocalPlan.PackageVersion.Value,
            fingerprint,
            pullRequest.Number,
            pullRequest.HeadOwner,
            pullRequest.HeadSha);
        Task<PullRequestManifestEvidence> task = _cache.GetOrAdd(
            key,
            _ => GetEvidenceCoreAsync(plan, pullRequest));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<PullRequestManifestEvidence> GetEvidenceCoreAsync(
        GitHubSubmissionPlan plan,
        PullRequestInfo pullRequest)
    {
        var headRepository = new RepositoryCoordinates(
            pullRequest.HeadOwner,
            plan.Request.UpstreamRepository.Name);
        string targetPrefix = plan.PackageVersionDirectory + "/";
        foreach (WorkflowFileChange change in plan.Request.LocalPlan.FileChanges.Where(change =>
                     change.RepositoryPath.StartsWith(targetPrefix, StringComparison.Ordinal)))
        {
            try
            {
                RepositoryContent content = await _gitHub.GetContentAsync(
                    headRepository,
                    change.RepositoryPath,
                    pullRequest.HeadSha,
                    CancellationToken.None).ConfigureAwait(false);
                if (change.ExpectedState == ExpectedFileState.Absent)
                {
                    return new(true, false);
                }

                if (change.Kind == PlannedChangeKind.Delete)
                {
                    continue;
                }

                if (string.Equals(
                        WorkflowFileChange.Hash(content.Bytes.Span),
                        WorkflowFileChange.Hash(change.Content.AsSpan()),
                        StringComparison.Ordinal))
                {
                    return new(false, true);
                }

            }
            catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                if (change.Kind == PlannedChangeKind.Delete)
                {
                    throw new PullRequestEvidenceLimitException(
                        "A noncanonical pull request omits the planned deletion path, but changed-file evidence is unavailable; refusing to create a possibly duplicate removal.");
                }
            }
        }

        return PullRequestManifestEvidence.None;
    }

    private readonly record struct EvidenceCacheKey(
        string UpstreamRepository,
        string PackageIdentifier,
        string PackageVersion,
        string PlanFingerprint,
        long PullRequestNumber,
        string HeadOwner,
        string HeadSha);
}

internal static class RepositorySubmissionEvidenceMerger
{
    public static GitHubSubmissionRequest Merge(
        GitHubSubmissionRequest request,
        RepositorySubmissionEvidence evidence)
    {
        DuplicateHashPolicy requestedHashes = request.Policy.DuplicateHashes;
        DuplicateHashPolicy repositoryHashes = evidence.DuplicateHashes;
        return request with
        {
            RepositoryEvidence = MergeInstallerEvidence(
                request.RepositoryEvidence,
                evidence.InstallerEvidence),
            VanityUrlAnnotations =
            [
                .. request.VanityUrlAnnotations,
                .. evidence.VanityUrlAnnotations
                    .Where(annotation => !request.VanityUrlAnnotations.Contains(
                        annotation,
                        StringComparer.Ordinal))
                    .Order(StringComparer.Ordinal),
            ],
            Policy = request.Policy with
            {
                DuplicateHashes = new()
                {
                    DeniedSha256 = requestedHashes.DeniedSha256.Union(
                        repositoryHashes.DeniedSha256),
                    AllowedSha256 = requestedHashes.AllowedSha256.Union(
                        repositoryHashes.AllowedSha256),
                    OverrideAnnotation = requestedHashes.OverrideAnnotation
                        ?? repositoryHashes.OverrideAnnotation,
                },
            },
        };
    }

    private static ImmutableArray<RepositoryInstallerEvidence> MergeInstallerEvidence(
        ImmutableArray<RepositoryInstallerEvidence> requested,
        ImmutableArray<RepositoryInstallerEvidence> repository)
    {
        var merged = requested.ToList();
        foreach (RepositoryInstallerEvidence candidate in repository)
        {
            int index = merged.FindIndex(existing =>
                string.Equals(
                    existing.PackageIdentifier.Value,
                    candidate.PackageIdentifier.Value,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    existing.PackageVersion.Value,
                    candidate.PackageVersion.Value,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    existing.InstallerSha256,
                    candidate.InstallerSha256,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    existing.ManifestPath,
                    candidate.ManifestPath,
                    StringComparison.Ordinal));
            if (index < 0)
            {
                merged.Add(candidate);
            }
            else if (candidate.RetiredIdentifier && !merged[index].RetiredIdentifier)
            {
                merged[index] = merged[index] with { RetiredIdentifier = true };
            }
        }

        return [.. merged];
    }
}

public sealed class PullRequestEvidenceLimitException(string message) : Exception(message);
