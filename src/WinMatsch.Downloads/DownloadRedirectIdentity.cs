using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinMatsch.Downloads;

public static class DownloadRedirectIdentity
{
    public static string Canonicalize(string url)
        => TryCanonicalize(url, out string? identity)
            ? identity!
            : throw new ArgumentException(
                "A redirect target must be an absolute URL with a host.",
                nameof(url));

    public static string ComputeSha256(string url)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(url))));

    public static bool AreEquivalent(string left, string right)
        => TryCanonicalize(left, out string? leftIdentity)
            && TryCanonicalize(right, out string? rightIdentity)
            && string.Equals(leftIdentity, rightIdentity, StringComparison.Ordinal);

    private static bool TryCanonicalize(string url, out string? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        string host = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{uri.IdnHost.ToLowerInvariant()}]"
            : uri.IdnHost.ToLowerInvariant();
        identity = string.Concat(
            uri.Scheme.ToLowerInvariant(),
            "://",
            host,
            ":",
            uri.Port.ToString(CultureInfo.InvariantCulture),
            uri.AbsolutePath);
        return true;
    }
}
