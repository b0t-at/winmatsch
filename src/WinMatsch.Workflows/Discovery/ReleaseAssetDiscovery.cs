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

    public required long AssetId { get; init; }

    public required string AssetName { get; init; }

    public required Uri DownloadUri { get; init; }

    public required string DeclaredContentType { get; init; }

    public required long DeclaredSize { get; init; }

    public required DateTimeOffset AssetCreatedAt { get; init; }

    public AssetContentEvidence? Content { get; init; }

    public AssetAnalysisEvidence? Analysis { get; init; }
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
    {
        ArgumentNullException.ThrowIfNull(releases);
        evidenceByUrl ??= new Dictionary<string, ReleaseAssetEvidence>(StringComparer.Ordinal);

        return
        [
            .. releases
                .Where(static release => !release.IsDraft)
                .SelectMany(
                    static release => release.Assets.Select(asset => (Release: release, Asset: asset)))
                .Where(static pair => IsWindowsAsset(pair.Asset))
                .Select(pair =>
                {
                    evidenceByUrl.TryGetValue(pair.Asset.DownloadUri.AbsoluteUri, out ReleaseAssetEvidence? evidence);
                    return new DiscoveredAsset
                    {
                        ReleaseId = pair.Release.Id,
                        ReleaseTag = pair.Release.TagName,
                        ReleaseName = pair.Release.Name,
                        ReleaseUri = pair.Release.WebUri,
                        IsPrerelease = pair.Release.IsPrerelease,
                        ReleasePublishedAt = pair.Release.PublishedAt,
                        AssetId = pair.Asset.Id,
                        AssetName = pair.Asset.Name,
                        DownloadUri = pair.Asset.DownloadUri,
                        DeclaredContentType = pair.Asset.ContentType,
                        DeclaredSize = pair.Asset.Size,
                        AssetCreatedAt = pair.Asset.CreatedAt,
                        Content = evidence?.Content,
                        Analysis = evidence?.Analysis,
                    };
                })
                .OrderByDescending(static asset => asset.ReleasePublishedAt)
                .ThenByDescending(static asset => asset.ReleaseId)
                .ThenBy(static asset => asset.AssetName, StringComparer.Ordinal)
                .ThenBy(static asset => asset.AssetId),
        ];
    }

    private static bool IsWindowsAsset(ReleaseAsset asset)
    {
        string extension = Path.GetExtension(asset.Name);
        if (_windowsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        bool hasWindowsSignal = _windowsTokens.Any(token => ContainsBounded(asset.Name, token));
        if (hasWindowsSignal)
        {
            return true;
        }

        if (_nonWindowsTokens.Any(token => ContainsBounded(asset.Name, token)))
        {
            return false;
        }

        ArchitectureTokenEvidence architecture = ArchitectureTokenClassifier.Classify(asset.Name);
        return architecture.Architecture is not null || architecture.IsAmbiguous;
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
}
