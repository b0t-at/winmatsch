using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Operations;
using YamlDotNet.Core;

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

public sealed class GitHubRepositorySubmissionEvidenceProvider(
    IGitHubRepositoryClient gitHub) : IRepositorySubmissionEvidenceProvider
{
    public const string PolicyPath = ".github/winmatsch/submission-evidence.json";
    private const int MaximumSiblingDirectories = 128;
    private const int MaximumInstallerFiles = 512;
    private const int MaximumPolicyItems = 1_024;
    private const long MaximumEvidenceFileBytes = 1_048_576;
    private readonly IGitHubRepositoryClient _gitHub =
        gitHub ?? throw new ArgumentNullException(nameof(gitHub));

    public async Task<RepositorySubmissionEvidence> GetEvidenceAsync(
        GitHubSubmissionRequest request,
        string upstreamHeadSha,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamHeadSha);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RepositoryEvidencePolicy policy = await ReadPolicyAsync(
                request.UpstreamRepository,
                upstreamHeadSha,
                cancellationToken).ConfigureAwait(false);
            ImmutableArray<RepositoryInstallerEvidence> installerEvidence =
                await ReadSiblingInstallerEvidenceAsync(
                    request.UpstreamRepository,
                    upstreamHeadSha,
                    request.LocalPlan.PackageIdentifier,
                    policy.RetiredIdentifiers,
                    cancellationToken).ConfigureAwait(false);
            var evidence = installerEvidence.ToBuilder();
            foreach (PackageIdentifier retired in policy.RetiredIdentifiers
                         .OrderBy(static identifier => identifier.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (!evidence.Any(item =>
                        item.RetiredIdentifier
                        && item.PackageIdentifier == retired))
                {
                    evidence.Add(new(
                        retired,
                        new PackageVersion("0"),
                        "",
                        PolicyPath,
                        RetiredIdentifier: true));
                }
            }

            policy.VanityAnnotations.TryGetValue(
                request.LocalPlan.PackageIdentifier.Value,
                out ImmutableArray<string> vanityAnnotations);
            return new()
            {
                InstallerEvidence =
                [
                    .. evidence
                        .OrderBy(static item => item.PackageIdentifier.Value, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static item => item.PackageVersion.Value, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static item => item.ManifestPath, StringComparer.Ordinal)
                        .ThenBy(static item => item.InstallerSha256, StringComparer.OrdinalIgnoreCase),
                ],
                DuplicateHashes = new()
                {
                    DeniedSha256 = policy.DeniedSha256,
                    AllowedSha256 = policy.AllowedSha256,
                    OverrideAnnotation = policy.OverrideAnnotation,
                },
                VanityUrlAnnotations = vanityAnnotations.IsDefault ? [] : vanityAnnotations,
            };
        }
        catch (GitHubApiException exception)
            when (exception.ErrorKind == GitHubApiErrorKind.TreeTruncated)
        {
            throw new RepositorySubmissionEvidenceException(
                "Pinned repository submission evidence tree was truncated and cannot be trusted.",
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or JsonException
                or YamlException
                or ArgumentException)
        {
            throw new RepositorySubmissionEvidenceException(
                "Pinned repository submission evidence is malformed or exceeds a safety limit.",
                exception);
        }
    }

    private async Task<ImmutableArray<RepositoryInstallerEvidence>> ReadSiblingInstallerEvidenceAsync(
        RepositoryCoordinates repository,
        string pinnedSha,
        PackageIdentifier packageIdentifier,
        ImmutableHashSet<PackageIdentifier> retiredIdentifiers,
        CancellationToken cancellationToken)
    {
        string[] identifierSegments = packageIdentifier.Value.Split('.');
        if (identifierSegments.Length < 2)
        {
            return [];
        }

        string[] parentSegments =
        [
            "manifests",
            char.ToLowerInvariant(packageIdentifier.Value[0]).ToString(),
            .. identifierSegments[..^1],
        ];
        string treeish = pinnedSha;
        var resolvedParentSegments = new List<string>(parentSegments.Length);
        foreach (string segment in parentSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RepositoryTreeEntry> entries = await _gitHub.GetTreeAsync(
                repository,
                treeish,
                recursive: false,
                cancellationToken).ConfigureAwait(false);
            RepositoryTreeEntry? next = FindTreeEntry(entries, segment);
            if (next is null)
            {
                return [];
            }

            treeish = next.Sha;
            resolvedParentSegments.Add(next.Path);
        }

        IReadOnlyList<RepositoryTreeEntry> siblings = await _gitHub.GetTreeAsync(
            repository,
            treeish,
            recursive: false,
            cancellationToken).ConfigureAwait(false);
        RepositoryTreeEntry[] siblingDirectories =
        [
            .. siblings
                .Where(static entry => entry.Type == RepositoryTreeEntryType.Tree)
                .OrderBy(static entry => entry.Path, StringComparer.Ordinal),
        ];
        if (siblingDirectories.Length > MaximumSiblingDirectories)
        {
            throw new InvalidDataException(
                $"Repository evidence sibling count exceeds {MaximumSiblingDirectories} at pinned commit {pinnedSha}.");
        }

        if (siblingDirectories
                .Select(static entry => entry.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != siblingDirectories.Length)
        {
            throw new InvalidDataException(
                "Pinned repository evidence contains case-colliding sibling directories.");
        }

        var installerFiles = new List<(string Path, string Sha)>();
        string parentPath = string.Join('/', resolvedParentSegments);
        foreach (RepositoryTreeEntry sibling in siblingDirectories)
        {
            IReadOnlyList<RepositoryTreeEntry> tree = await _gitHub.GetTreeAsync(
                repository,
                sibling.Sha,
                recursive: true,
                cancellationToken).ConfigureAwait(false);
            installerFiles.AddRange(tree
                .Where(static entry =>
                    entry.Type == RepositoryTreeEntryType.Blob
                    && entry.Path.EndsWith(".installer.yaml", StringComparison.OrdinalIgnoreCase))
                .Select(entry => (
                    $"{parentPath}/{sibling.Path}/{entry.Path}",
                    entry.Sha)));
            if (installerFiles.Count > MaximumInstallerFiles)
            {
                throw new InvalidDataException(
                    $"Repository evidence installer-file count exceeds {MaximumInstallerFiles} at pinned commit {pinnedSha}.");
            }
        }

        var evidence = ImmutableArray.CreateBuilder<RepositoryInstallerEvidence>();
        foreach ((string path, _) in installerFiles.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RepositoryContent content = await _gitHub.GetContentAsync(
                repository,
                path,
                pinnedSha,
                cancellationToken).ConfigureAwait(false);
            if (content.Size > MaximumEvidenceFileBytes
                || content.Bytes.Length > MaximumEvidenceFileBytes)
            {
                throw new InvalidDataException(
                    $"Repository evidence manifest '{path}' exceeds {MaximumEvidenceFileBytes} bytes.");
            }

            string yaml = new UTF8Encoding(false, true).GetString(content.Bytes.Span);
            InstallerManifest manifest = ManifestYamlReader.ReadInstaller(yaml);
            PackageIdentifier identifier = manifest.PackageIdentifier
                ?? throw new InvalidDataException($"Repository evidence manifest '{path}' has no package identifier.");
            PackageVersion version = manifest.PackageVersion
                ?? throw new InvalidDataException($"Repository evidence manifest '{path}' has no package version.");
            bool retired = retiredIdentifiers.Contains(identifier);
            foreach (Installer installer in manifest.Installers ?? [])
            {
                if (installer.InstallerSha256 is null)
                {
                    continue;
                }

                evidence.Add(new(
                    identifier,
                    version,
                    installer.InstallerSha256.Value,
                    path,
                    retired));
            }
        }

        return evidence.ToImmutable();
    }

    private static RepositoryTreeEntry? FindTreeEntry(
        IReadOnlyList<RepositoryTreeEntry> entries,
        string segment)
    {
        RepositoryTreeEntry[] matches =
        [
            .. entries.Where(entry =>
                entry.Type == RepositoryTreeEntryType.Tree
                && string.Equals(entry.Path, segment, StringComparison.OrdinalIgnoreCase)),
        ];
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException(
                $"Pinned repository evidence contains case-colliding path segment '{segment}'."),
        };
    }

    private async Task<RepositoryEvidencePolicy> ReadPolicyAsync(
        RepositoryCoordinates repository,
        string pinnedSha,
        CancellationToken cancellationToken)
    {
        RepositoryContent content;
        try
        {
            content = await _gitHub.GetContentAsync(
                repository,
                PolicyPath,
                pinnedSha,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return RepositoryEvidencePolicy.Empty;
        }

        if (content.Size > MaximumEvidenceFileBytes
            || content.Bytes.Length > MaximumEvidenceFileBytes)
        {
            throw new InvalidDataException(
                $"Repository evidence policy exceeds {MaximumEvidenceFileBytes} bytes.");
        }

        using JsonDocument document = JsonDocument.Parse(content.Bytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Repository evidence policy must be a JSON object.");
        }

        ImmutableHashSet<PackageIdentifier> retired = ReadIdentifiers(root, "retiredIdentifiers");
        ImmutableHashSet<string> denied = ImmutableHashSet<string>.Empty.WithComparer(
            StringComparer.OrdinalIgnoreCase);
        ImmutableHashSet<string> allowed = ImmutableHashSet<string>.Empty.WithComparer(
            StringComparer.OrdinalIgnoreCase);
        string? annotation = null;
        if (root.TryGetProperty("duplicateHashes", out JsonElement duplicateHashes))
        {
            if (duplicateHashes.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("duplicateHashes must be a JSON object.");
            }

            denied = ReadHashes(duplicateHashes, "deniedSha256");
            allowed = ReadHashes(duplicateHashes, "allowedSha256");
            annotation = ReadOptionalString(duplicateHashes, "overrideAnnotation");
        }

        ImmutableDictionary<string, ImmutableArray<string>> vanity =
            ReadVanityAnnotations(root);
        return new(retired, denied, allowed, annotation, vanity);
    }

    private static ImmutableHashSet<PackageIdentifier> ReadIdentifiers(
        JsonElement root,
        string property)
        => ReadStrings(root, property)
            .Select(static value => new PackageIdentifier(value))
            .ToImmutableHashSet();

    private static ImmutableHashSet<string> ReadHashes(
        JsonElement root,
        string property)
        => ReadStrings(root, property)
            .Select(static value => new Sha256Hash(value).Normalized)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    private static ImmutableArray<string> ReadStrings(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(property, out JsonElement values))
        {
            return [];
        }

        return ReadStringArray(values, property);
    }

    private static ImmutableArray<string> ReadStringArray(
        JsonElement values,
        string property)
    {
        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{property} must be a JSON array.");
        }

        string[] result =
        [
            .. values.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(item.GetString())
                    ? item.GetString()!.Trim()
                    : throw new InvalidDataException($"{property} entries must be non-empty strings.")),
        ];
        if (result.Length > MaximumPolicyItems)
        {
            throw new InvalidDataException(
                $"{property} exceeds the {MaximumPolicyItems}-item repository evidence limit.");
        }

        return
        [
            .. result.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static string? ReadOptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{property} must be a non-empty string when present.");
        }

        return value.GetString()!.Trim();
    }

    private static ImmutableDictionary<string, ImmutableArray<string>> ReadVanityAnnotations(
        JsonElement root)
    {
        if (!root.TryGetProperty("vanityUrlAnnotations", out JsonElement annotations))
        {
            return ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase);
        }

        if (annotations.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("vanityUrlAnnotations must be a JSON object.");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (JsonProperty entry in annotations.EnumerateObject())
        {
            PackageIdentifier identifier = new(entry.Name);
            ImmutableArray<string> values = ReadStringArray(
                entry.Value,
                $"vanityUrlAnnotations.{entry.Name}");
            count += values.Length + 1;
            if (count > MaximumPolicyItems)
            {
                throw new InvalidDataException(
                    $"vanityUrlAnnotations exceeds the {MaximumPolicyItems}-item repository evidence limit.");
            }

            builder.Add(identifier.Value, values);
        }

        return builder.ToImmutable();
    }

    private sealed record RepositoryEvidencePolicy(
        ImmutableHashSet<PackageIdentifier> RetiredIdentifiers,
        ImmutableHashSet<string> DeniedSha256,
        ImmutableHashSet<string> AllowedSha256,
        string? OverrideAnnotation,
        ImmutableDictionary<string, ImmutableArray<string>> VanityAnnotations)
    {
        public static RepositoryEvidencePolicy Empty { get; } = new(
            ImmutableHashSet<PackageIdentifier>.Empty,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase),
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase),
            null,
            ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class GitHubPullRequestManifestEvidenceProvider(IGitHubRepositoryClient gitHub)
    : IPullRequestManifestEvidenceProvider
{
    private readonly IGitHubRepositoryClient _gitHub =
        gitHub ?? throw new ArgumentNullException(nameof(gitHub));
    private readonly ConcurrentDictionary<EvidenceCacheKey, PullRequestManifestEvidence> _cache = [];
    private readonly ConcurrentDictionary<ChangedFilesCacheKey, ChangedFilesCacheEntry>
        _changedFilesCache = [];

    public async Task<IReadOnlyList<PullRequestInfo>> GetCandidatesAsync(
        GitHubSubmissionPlan plan,
        IReadOnlyList<PullRequestInfo> openPullRequests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(openPullRequests);
        cancellationToken.ThrowIfCancellationRequested();
        if (openPullRequests.Count > PullRequestManifestEvidenceLimits.MaximumOpenPullRequests)
        {
            throw new PullRequestEvidenceLimitException(
                "Open pull-request discovery exceeds the safe evidence limit of " +
                $"{PullRequestManifestEvidenceLimits.MaximumOpenPullRequests}.");
        }

        var plannedPaths = new HashSet<string>(
            plan.Request.LocalPlan.FileChanges.Select(static change => change.RepositoryPath),
            StringComparer.Ordinal);
        var candidates = new List<PullRequestInfo>();
        bool canVerifyEveryOpenPullRequest =
            openPullRequests.Count <= PullRequestManifestEvidenceLimits.MaximumCandidates;
        await EnsureChangedFilesAsync(
            plan.Request.UpstreamRepository,
            openPullRequests,
            cancellationToken).ConfigureAwait(false);
        foreach (PullRequestInfo pullRequest in openPullRequests.OrderBy(static item => item.Number))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PullRequestChangedFile> changedFiles =
                GetCachedChangedFiles(plan.Request.UpstreamRepository, pullRequest);

            bool hasTargetPath = changedFiles.Any(file =>
                plannedPaths.Contains(file.Path)
                || (file.PreviousPath is not null && plannedPaths.Contains(file.PreviousPath)));
            bool hasCanonicalTitleHint = GitHubSubmissionFormatter.IsCanonicalTitleFor(
                pullRequest.Title,
                plan.Request.LocalPlan.PackageIdentifier,
                plan.Request.LocalPlan.PackageVersion);
            if (!canVerifyEveryOpenPullRequest
                && !hasTargetPath
                && !hasCanonicalTitleHint)
            {
                continue;
            }

            candidates.Add(pullRequest);
            if (candidates.Count > PullRequestManifestEvidenceLimits.MaximumCandidates)
            {
                throw new PullRequestEvidenceLimitException(
                    $"Manifest evidence candidate count exceeds the safe limit of {PullRequestManifestEvidenceLimits.MaximumCandidates}.");
            }
        }

        return candidates;
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
        await EnsureChangedFilesAsync(
            plan.Request.UpstreamRepository,
            [pullRequest],
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PullRequestChangedFile> changedFiles =
            GetCachedChangedFiles(plan.Request.UpstreamRepository, pullRequest);
        string targetPrefix = plan.PackageVersionDirectory + "/";
        WorkflowFileChange[] plannedChanges =
        [
            .. plan.Request.LocalPlan.FileChanges.Where(change =>
                change.RepositoryPath.StartsWith(targetPrefix, StringComparison.Ordinal)),
        ];
        bool hasCanonicalTitle = GitHubSubmissionFormatter.IsCanonicalTitleFor(
            pullRequest.Title,
            plan.Request.LocalPlan.PackageIdentifier,
            plan.Request.LocalPlan.PackageVersion);
        var plannedPaths = new HashSet<string>(
            plannedChanges.Select(static change => change.RepositoryPath),
            StringComparer.Ordinal);
        bool hasManifestPath = changedFiles.Any(file =>
            plannedPaths.Contains(file.Path)
            || (file.PreviousPath is not null && plannedPaths.Contains(file.PreviousPath)));
        if (hasCanonicalTitle || hasManifestPath)
        {
            await VerifyPullRequestIdentityAsync(
                plan,
                pullRequest,
                cancellationToken).ConfigureAwait(false);
            return new(hasManifestPath, false, hasCanonicalTitle);
        }

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

        if (plannedChanges.Length > PullRequestManifestEvidenceLimits.MaximumContentFiles)
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{pullRequest.Number} requires more than {PullRequestManifestEvidenceLimits.MaximumContentFiles} manifest path comparisons.");
        }

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
        return new(hasManifestPath, hasMatchingContent, hasCanonicalTitle);
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

    private async Task EnsureChangedFilesAsync(
        RepositoryCoordinates repository,
        IReadOnlyList<PullRequestInfo> pullRequests,
        CancellationToken cancellationToken)
    {
        PullRequestInfo[] missing =
        [
            .. pullRequests
                .DistinctBy(static pullRequest => pullRequest.Number)
                .Where(pullRequest => !HasCurrentChangedFiles(repository, pullRequest)),
        ];
        if (missing.Length == 0)
        {
            return;
        }

        IReadOnlyDictionary<long, IReadOnlyList<PullRequestChangedFile>> fetched;
        try
        {
            fetched = await _gitHub.GetPullRequestChangedFilesBatchAsync(
                repository,
                missing,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request changed-file evidence is unavailable: {exception.Message}");
        }
        catch (GitHubApiException exception) when (exception.StatusCode is null)
        {
            throw new PullRequestEvidenceLimitException(
                "Pull request changed-file evidence failed a local transport safety bound: "
                + exception.Message);
        }

        if (fetched.Count != missing.Length)
        {
            throw new PullRequestEvidenceLimitException(
                "Pull request changed-file evidence returned an incomplete batch.");
        }

        foreach (PullRequestInfo pullRequest in missing)
        {
            if (!fetched.TryGetValue(
                    pullRequest.Number,
                    out IReadOnlyList<PullRequestChangedFile>? changedFiles))
            {
                throw new PullRequestEvidenceLimitException(
                    $"Pull request #{pullRequest.Number} changed-file evidence is missing.");
            }

            PullRequestChangedFile[] snapshot = [.. changedFiles];
            ValidateChangedFiles(pullRequest.Number, snapshot);
            var key = CreateChangedFilesCacheKey(repository, pullRequest);
            _changedFilesCache[key] = new(
                pullRequest.NodeId,
                pullRequest.BaseSha,
                snapshot);
            foreach (ChangedFilesCacheKey stale in _changedFilesCache.Keys.Where(candidate =>
                         string.Equals(
                             candidate.Repository,
                             key.Repository,
                             StringComparison.Ordinal)
                         && candidate.PullRequestNumber == key.PullRequestNumber
                         && !string.Equals(
                             candidate.HeadSha,
                             key.HeadSha,
                             StringComparison.Ordinal)))
            {
                _changedFilesCache.TryRemove(stale, out _);
            }

            foreach (EvidenceCacheKey stale in _cache.Keys.Where(candidate =>
                         string.Equals(
                             candidate.UpstreamRepository,
                             key.Repository,
                             StringComparison.Ordinal)
                         && candidate.PullRequestNumber == key.PullRequestNumber
                         && !string.Equals(
                             candidate.HeadSha,
                             key.HeadSha,
                             StringComparison.Ordinal)))
            {
                _cache.TryRemove(stale, out _);
            }
        }
    }

    private bool HasCurrentChangedFiles(
        RepositoryCoordinates repository,
        PullRequestInfo pullRequest)
    {
        var key = CreateChangedFilesCacheKey(repository, pullRequest);
        return _changedFilesCache.TryGetValue(key, out ChangedFilesCacheEntry? cached)
            && string.Equals(cached.NodeId, pullRequest.NodeId, StringComparison.Ordinal)
            && string.Equals(cached.BaseSha, pullRequest.BaseSha, StringComparison.Ordinal);
    }

    private IReadOnlyList<PullRequestChangedFile> GetCachedChangedFiles(
        RepositoryCoordinates repository,
        PullRequestInfo pullRequest)
    {
        var key = CreateChangedFilesCacheKey(repository, pullRequest);
        if (_changedFilesCache.TryGetValue(key, out ChangedFilesCacheEntry? cached)
            && string.Equals(cached.NodeId, pullRequest.NodeId, StringComparison.Ordinal)
            && string.Equals(cached.BaseSha, pullRequest.BaseSha, StringComparison.Ordinal))
        {
            return cached.Files;
        }

        throw new PullRequestEvidenceLimitException(
            $"Pull request #{pullRequest.Number} changed-file evidence was not cached at its pinned identity.");
    }

    private static void ValidateChangedFiles(
        long pullRequestNumber,
        IReadOnlyList<PullRequestChangedFile> changedFiles)
    {
        if (changedFiles.Any(static file =>
                string.IsNullOrWhiteSpace(file.Path)
                || (file.PreviousPath is not null
                    && string.IsNullOrWhiteSpace(file.PreviousPath))))
        {
            throw new PullRequestEvidenceLimitException(
                $"Pull request #{pullRequestNumber} returned an invalid changed-file path.");
        }
    }

    private static ChangedFilesCacheKey CreateChangedFilesCacheKey(
        RepositoryCoordinates repository,
        PullRequestInfo pullRequest)
        => new(repository.ToString(), pullRequest.Number, pullRequest.HeadSha);

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

    private readonly record struct ChangedFilesCacheKey(
        string Repository,
        long PullRequestNumber,
        string HeadSha);

    private sealed record ChangedFilesCacheEntry(
        string NodeId,
        string? BaseSha,
        IReadOnlyList<PullRequestChangedFile> Files);
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

public sealed class RepositorySubmissionEvidenceException(
    string message,
    Exception innerException) : Exception(message, innerException);
