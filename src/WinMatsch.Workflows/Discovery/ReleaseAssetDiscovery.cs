using System.Collections.Immutable;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Mapping;

namespace WinMatsch.Workflows.Discovery;

/// <summary>A release asset together with immutable provenance and locally collected evidence.</summary>
public sealed record DiscoveredAsset
{
    public required long ReleaseId { get; init; }

    public required string ReleaseTag { get; init; }

    public required string ReleaseName { get; init; }

    public required Uri ReleaseUri { get; init; }

    public required bool IsPrerelease { get; init; }

    public DateTimeOffset? ReleasePublishedAt { get; init; }

    public DateTimeOffset? ReleaseUpdatedAt { get; init; }

    public required long AssetId { get; init; }

    public required string AssetName { get; init; }

    public required Uri DownloadUri { get; init; }

    public required string DeclaredContentType { get; init; }

    public required long DeclaredSize { get; init; }

    public required DateTimeOffset AssetCreatedAt { get; init; }

    public DateTimeOffset? AssetUpdatedAt { get; init; }

    public AssetContentEvidence? Content { get; init; }

    public AssetAnalysisEvidence? Analysis { get; init; }

    public bool HasOperatingSystemConflict { get; init; }
}

/// <summary>External evidence associated with a release asset URL.</summary>
public sealed record ReleaseAssetEvidence(
    AssetContentEvidence? Content = null,
    AssetAnalysisEvidence? Analysis = null);

/// <summary>Deterministically enumerates Windows artifacts from supplied GitHub releases.</summary>
public static class ReleaseAssetDiscovery
{
    private static readonly string[] _windowsExtensions =
    [
        ".appx",
        ".appxbundle",
        ".exe",
        ".msi",
        ".msix",
        ".msixbundle",
    ];

    private static readonly string[] _windowsTokens =
    [
        "windows",
        "win32",
        "win64",
        "win64a",
        "winarm64",
        "mingw",
    ];

    private static readonly string[] _nonWindowsTokens =
    [
        "android",
        "darwin",
        "freebsd",
        "linux",
        "macos",
        "osx",
    ];

    public static async Task<ImmutableArray<DiscoveredAsset>> DiscoverAsync(
        Func<CancellationToken, Task<IReadOnlyList<GitHubRelease>>> releaseSource,
        IReadOnlyDictionary<string, ReleaseAssetEvidence>? evidenceByUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseSource);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<GitHubRelease> releases = await releaseSource(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Discover(releases, evidenceByUrl);
    }

    public static ImmutableArray<DiscoveredAsset> Discover(
        IEnumerable<GitHubRelease> releases,
        IReadOnlyDictionary<string, ReleaseAssetEvidence>? evidenceByUrl = null)
        => DiscoverCore(releases, evidenceByUrl, windowsOnly: true);

    /// <summary>Enumerates every non-draft release asset for bounded continuity matching.</summary>
    public static ImmutableArray<DiscoveredAsset> DiscoverAll(
        IEnumerable<GitHubRelease> releases,
        IReadOnlyDictionary<string, ReleaseAssetEvidence>? evidenceByUrl = null)
        => DiscoverCore(releases, evidenceByUrl, windowsOnly: false);

    private static ImmutableArray<DiscoveredAsset> DiscoverCore(
        IEnumerable<GitHubRelease> releases,
        IReadOnlyDictionary<string, ReleaseAssetEvidence>? evidenceByUrl,
        bool windowsOnly)
    {
        ArgumentNullException.ThrowIfNull(releases);
        evidenceByUrl ??= new Dictionary<string, ReleaseAssetEvidence>(StringComparer.Ordinal);

        return
        [
            .. releases
                .Where(static release => !release.IsDraft)
                .SelectMany(
                    static release => release.Assets.Select(asset => (Release: release, Asset: asset)))
                .Select(pair =>
                {
                    evidenceByUrl.TryGetValue(pair.Asset.DownloadUri.AbsoluteUri, out ReleaseAssetEvidence? evidence);
                    WindowsAssetClassification classification = ClassifyWindowsAsset(pair.Asset, evidence);
                    return (pair.Release, pair.Asset, Evidence: evidence, Classification: classification);
                })
                .Where(item => !windowsOnly || item.Classification.Include)
                .Select(item => new DiscoveredAsset
                {
                    ReleaseId = item.Release.Id,
                    ReleaseTag = item.Release.TagName,
                    ReleaseName = item.Release.Name,
                    ReleaseUri = item.Release.WebUri,
                    IsPrerelease = item.Release.IsPrerelease,
                    ReleasePublishedAt = item.Release.PublishedAt,
                    ReleaseUpdatedAt = item.Release.UpdatedAt,
                    AssetId = item.Asset.Id,
                    AssetName = item.Asset.Name,
                    DownloadUri = item.Asset.DownloadUri,
                    DeclaredContentType = item.Asset.ContentType,
                    DeclaredSize = item.Asset.Size,
                    AssetCreatedAt = item.Asset.CreatedAt,
                    AssetUpdatedAt = item.Asset.UpdatedAt,
                    Content = item.Evidence?.Content,
                    Analysis = item.Evidence?.Analysis,
                    HasOperatingSystemConflict = item.Classification.HasConflict,
                })
                .OrderByDescending(static asset => asset.ReleasePublishedAt)
                .ThenByDescending(static asset => asset.ReleaseId)
                .ThenBy(static asset => asset.AssetName, StringComparer.Ordinal)
                .ThenBy(static asset => asset.AssetId),
        ];
    }

    private static WindowsAssetClassification ClassifyWindowsAsset(
        ReleaseAsset asset,
        ReleaseAssetEvidence? evidence)
    {
        string extension = Path.GetExtension(asset.Name);
        bool hasWindowsSignal = _windowsTokens.Any(token => ContainsBounded(asset.Name, token));
        bool hasNonWindowsSignal = _nonWindowsTokens.Any(token => ContainsBounded(asset.Name, token));
        bool hasWindowsOnlyExtension = _windowsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        bool hasAnalyzedWindowsContent = evidence?.Analysis is not null;
        return new(
            hasWindowsOnlyExtension || hasWindowsSignal || hasAnalyzedWindowsContent,
            hasNonWindowsSignal && (hasWindowsOnlyExtension || hasWindowsSignal || hasAnalyzedWindowsContent));
    }

    private static bool ContainsBounded(string value, string token)
    {
        int index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int end = index + token.Length;
            if ((index == 0 || !char.IsAsciiLetterOrDigit(value[index - 1]))
                && (end == value.Length || !char.IsAsciiLetterOrDigit(value[end])))
            {
                return true;
            }

            index = value.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private readonly record struct WindowsAssetClassification(bool Include, bool HasConflict);
}
