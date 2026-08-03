using WinMatsch.Core;

namespace WinMatsch.Analysis;

/// <summary>
/// Detects the target architecture from tokens in an installer URL (or any file name). A
/// token only matches when bounded by non-alphanumeric characters or the string edges, so
/// "charm" does not match "arm" and "x640" does not match "x64". More specific groups win:
/// arm64 over arm, and the x64 group (which contains "x86_64") over the x86 group.
/// </summary>
public static class UrlArchitectureDetector
{
    private static readonly string[] _arm64Tokens = ["arm64", "aarch64"];
    private static readonly string[] _armTokens = ["arm"];
    private static readonly string[] _x64Tokens = ["x86_64", "x86-64", "x64", "win64", "amd64", "64bit", "64-bit"];
    private static readonly string[] _x86Tokens = ["x86", "win32", "ia32", "i386", "i686", "386", "686", "32bit", "32-bit"];

    /// <summary>Returns the architecture implied by the URL, or null when no token matches.</summary>
    public static Architecture? Detect(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (ContainsToken(url, _arm64Tokens))
        {
            return Architecture.Arm64;
        }

        if (ContainsToken(url, _armTokens))
        {
            return Architecture.Arm;
        }

        if (ContainsToken(url, _x64Tokens))
        {
            return Architecture.X64;
        }

        if (ContainsToken(url, _x86Tokens))
        {
            return Architecture.X86;
        }

        return null;
    }

    private static bool ContainsToken(string url, string[] tokens)
    {
        foreach (string token in tokens)
        {
            int start = 0;
            while (start <= url.Length - token.Length)
            {
                int index = url.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                int end = index + token.Length;
                bool boundedBefore = index == 0 || !char.IsAsciiLetterOrDigit(url[index - 1]);
                bool boundedAfter = end == url.Length || !char.IsAsciiLetterOrDigit(url[end]);
                if (boundedBefore && boundedAfter)
                {
                    return true;
                }

                start = index + 1;
            }
        }

        return false;
    }
}
