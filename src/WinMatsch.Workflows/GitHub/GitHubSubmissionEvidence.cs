using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ConcurrentDictionary<EvidenceCacheKey, PullRequestManifestEvidence> _cache = [];

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
        cancellationToken.ThrowIfCancellationRequested();
        PullRequestInfo current = await ReadCurrentPullRequestAsync(
            plan,
            pullRequest,
            cancellationToken).ConfigureAwait(false);
        string fingerprint = CreatePlanFingerprint(plan);
        var key = new EvidenceCacheKey(
            plan.Request.UpstreamRepository.ToString(),
            fingerprint,
            current.Number,
            current.HeadRepository!.ToString(),
            current.HeadSha,
            current.BaseBranch,
            current.BaseSha!);
        if (_cache.TryGetValue(key, out PullRequestManifestEvidence? cached))
        {
            return cached;
        }

        PullRequestManifestEvidence evidence = await GetEvidenceCoreAsync(
            plan,
            current,
            cancellationToken).ConfigureAwait(false);
        _cache.TryAdd(key, evidence);
        return evidence;
    }

    private async Task<PullRequestManifestEvidence> GetEvidenceCoreAsync(
        GitHubSubmissionPlan plan,
        PullRequestInfo pullRequest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RepositoryInfo headRepository = await _gitHub.GetRepositoryAsync(
            pullRequest.HeadRepository!,
            cancellationToken).ConfigureAwait(false);
        if (headRepository.Coordinates != pullRequest.HeadRepository || headRepository.IsPrivate)
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{pullRequest.Number} head repository is private or no longer has the reported coordinates.");
        }

        string mergeBaseSha;
        try
        {
            mergeBaseSha = await _gitHub.GetMergeBaseAsync(
                plan.Request.UpstreamRepository,
                pullRequest.BaseSha!,
                pullRequest.HeadSha,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{pullRequest.Number} merge-base evidence is unavailable: {exception.Message}");
        }

        IReadOnlyList<PullRequestChangedFile> changedFiles;
        try
        {
            changedFiles = await _gitHub.GetPullRequestChangedFilesAsync(
                plan.Request.UpstreamRepository,
                pullRequest.Number,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{pullRequest.Number} changed-file evidence is unavailable: {exception.Message}");
        }
        catch (GitHubApiException exception) when (exception.StatusCode is null)
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{pullRequest.Number} changed-file evidence failed a local transport safety bound: {exception.Message}");
        }

        if (changedFiles.Any(static file =>
                string.IsNullOrWhiteSpace(file.Path)
                || (file.PreviousPath is not null
                    && string.IsNullOrWhiteSpace(file.PreviousPath))))
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{pullRequest.Number} returned an invalid changed-file path.");
        }

        string targetPrefix = plan.PackageVersionDirectory + "/";
        WorkflowFileChange[] plannedChanges =
        [
            .. plan.Request.LocalPlan.FileChanges.Where(change =>
                change.RepositoryPath.StartsWith(targetPrefix, StringComparison.Ordinal)),
        ];
        if (plannedChanges.Length > PullRequestManifestEvidenceLimits.MaximumContentFiles)
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{pullRequest.Number} requires more than {PullRequestManifestEvidenceLimits.MaximumContentFiles} manifest path comparisons.");
        }

        bool hasManifestPath = false;
        bool hasMatchingContent = false;
        foreach (WorkflowFileChange change in plannedChanges)
        {
            RepositoryContent? baseContent = await TryGetContentAsync(
                plan.Request.UpstreamRepository,
                change.RepositoryPath,
                mergeBaseSha,
                cancellationToken).ConfigureAwait(false);
            RepositoryContent? headContent = await TryGetContentAsync(
                pullRequest.HeadRepository!,
                change.RepositoryPath,
                pullRequest.HeadSha,
                cancellationToken).ConfigureAwait(false);
            ValidateContentSize(pullRequest.Number, baseContent);
            ValidateContentSize(pullRequest.Number, headContent);

            bool changedAtPinnedIdentity = baseContent is null != (headContent is null)
                || (baseContent is not null
                    && headContent is not null
                    && !baseContent.Bytes.Span.SequenceEqual(headContent.Bytes.Span));
            if (!changedAtPinnedIdentity)
            {
                continue;
            }

            hasManifestPath = true;
            if (change.Kind != PlannedChangeKind.Delete && headContent is not null)
            {
                hasMatchingContent |= string.Equals(
                    WorkflowFileChange.Hash(headContent.Bytes.Span),
                    WorkflowFileChange.Hash(change.Content.AsSpan()),
                    StringComparison.Ordinal);
            }
        }

        await VerifyPullRequestIdentityAsync(plan, pullRequest, cancellationToken).ConfigureAwait(false);
        return new(hasManifestPath, hasMatchingContent);
    }

    private async Task<RepositoryContent?> TryGetContentAsync(
        RepositoryCoordinates repository,
        string path,
        string reference,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _gitHub.GetContentAsync(
                repository,
                path,
                reference,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static void ValidateContentSize(
        long pullRequestNumber,
        RepositoryContent? content)
    {
        if (content is not null
            && (content.Size > PullRequestManifestEvidenceLimits.MaximumContentBytes
                || content.Bytes.Length > PullRequestManifestEvidenceLimits.MaximumContentBytes))
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{pullRequestNumber} manifest content exceeds the safe size limit.");
        }
    }

    private async Task<PullRequestInfo> ReadCurrentPullRequestAsync(
        GitHubSubmissionPlan plan,
        PullRequestInfo expected,
        CancellationToken cancellationToken)
    {
        PullRequestInfo current = await _gitHub.GetPullRequestAsync(
            plan.Request.UpstreamRepository,
            expected.Number,
            cancellationToken).ConfigureAwait(false);
        if (current.State != PullRequestState.Open
            || expected.HeadRepository is null
            || expected.BaseSha is null
            || current.HeadRepository is null
            || current.HeadRepository != expected.HeadRepository
            || !string.Equals(current.HeadSha, expected.HeadSha, StringComparison.Ordinal)
            || !string.Equals(current.BaseBranch, expected.BaseBranch, StringComparison.Ordinal)
            || !string.Equals(current.BaseSha, expected.BaseSha, StringComparison.Ordinal))
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{expected.Number} head or base identity changed while gathering manifest evidence.");
        }

        return current;
    }

    private async Task VerifyPullRequestIdentityAsync(
        GitHubSubmissionPlan plan,
        PullRequestInfo expected,
        CancellationToken cancellationToken)
        => _ = await ReadCurrentPullRequestAsync(plan, expected, cancellationToken).ConfigureAwait(false);

    private static string CreatePlanFingerprint(GitHubSubmissionPlan plan)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, plan.Request.LocalPlan.PackageIdentifier.Value);
        AppendHash(hash, plan.Request.LocalPlan.PackageVersion.Value);
        AppendHash(hash, plan.Request.LocalPlan.FileChanges.Length);
        foreach (WorkflowFileChange change in plan.Request.LocalPlan.FileChanges)
        {
            AppendHash(hash, (int)change.Kind);
            AppendHash(hash, change.RepositoryPath);
            AppendHash(hash, (int)change.ExpectedState);
            AppendHash(hash, change.ExpectedSha256);
            AppendHash(hash, (int)change.Provenance);
            AppendHash(hash, change.Content.AsSpan());
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHash(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            AppendHash(hash, -1);
            return;
        }

        AppendHash(hash, Encoding.UTF8.GetByteCount(value));
        hash.AppendData(Encoding.UTF8.GetBytes(value));
    }

    private static void AppendHash(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        AppendHash(hash, value.Length);
        hash.AppendData(value);
    }

    private static void AppendHash(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private readonly record struct EvidenceCacheKey(
        string UpstreamRepository,
        string PlanFingerprint,
        long PullRequestNumber,
        string HeadRepository,
        string HeadSha,
        string BaseBranch,
        string BaseSha);
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
