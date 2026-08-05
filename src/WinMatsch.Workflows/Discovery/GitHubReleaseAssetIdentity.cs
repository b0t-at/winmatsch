using WinMatsch.GitHub;

namespace WinMatsch.Workflows.Discovery;

/// <summary>Stable repository and release identity parsed from a GitHub release download URL.</summary>
public sealed record GitHubReleaseAssetIdentity(
    string Authority,
    RepositoryCoordinates Repository,
    string ReleaseTag,
    string AssetName)
{
    public static bool TryParse(Uri uri, out GitHubReleaseAssetIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(uri);
        identity = null!;
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        string[] segments;
        try
        {
            segments =
            [
                .. uri.AbsolutePath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.UnescapeDataString),
            ];
        }
        catch (UriFormatException)
        {
            return false;
        }

        int releases = -1;
        bool latestAlias = false;
        for (int index = 2; index < segments.Length; index++)
        {
            if (!string.Equals(segments[index], "releases", StringComparison.OrdinalIgnoreCase)
                || index + 4 != segments.Length)
            {
                continue;
            }

            bool immutableDownload = string.Equals(
                segments[index + 1],
                "download",
                StringComparison.OrdinalIgnoreCase);
            latestAlias = string.Equals(
                    segments[index + 1],
                    "latest",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    segments[index + 2],
                    "download",
                    StringComparison.OrdinalIgnoreCase);
            if (immutableDownload || latestAlias)
            {
                releases = index;
                break;
            }
        }

        int tag = latestAlias ? releases + 1 : releases + 2;
        if (releases < 2
            || string.IsNullOrWhiteSpace(segments[releases - 2])
            || string.IsNullOrWhiteSpace(segments[releases - 1])
            || string.IsNullOrWhiteSpace(segments[tag]))
        {
            return false;
        }

        identity = new(
            uri.Authority,
            new RepositoryCoordinates(segments[releases - 2], segments[releases - 1]),
            segments[tag],
            segments[^1]);
        return true;
    }

    public bool IsSameRepository(GitHubReleaseAssetIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Authority, other.Authority, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Repository.Owner, other.Repository.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Repository.Name, other.Repository.Name, StringComparison.OrdinalIgnoreCase);
    }
}
