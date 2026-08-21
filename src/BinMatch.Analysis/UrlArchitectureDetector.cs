using System.Collections.Immutable;
using BinMatch.Core;

namespace BinMatch.Analysis;

/// <summary>
/// Detects the target architecture from tokens in an installer URL (or any file name). A
/// token only matches when bounded by non-alphanumeric characters or the string edges, so
/// "charm" does not match "arm" and "x640" does not match "x64". More specific groups win:
/// arm64 over arm, and the x64 group (which contains "x86_64") over the x86 group. The
/// token table is shared with the mapping-side classifier via
/// <see cref="ArchitectureTokens"/> so both components classify names identically.
/// </summary>
public static class UrlArchitectureDetector
{
    private static readonly ImmutableArray<ArchitectureTokenDefinition> _arm64Tokens = ArchitectureTokens.For(Architecture.Arm64);
    private static readonly ImmutableArray<ArchitectureTokenDefinition> _armTokens = ArchitectureTokens.For(Architecture.Arm);
    private static readonly ImmutableArray<ArchitectureTokenDefinition> _x64Tokens = ArchitectureTokens.For(Architecture.X64);
    private static readonly ImmutableArray<ArchitectureTokenDefinition> _x86Tokens = ArchitectureTokens.For(Architecture.X86);

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

    private static bool ContainsToken(string url, ImmutableArray<ArchitectureTokenDefinition> tokens)
    {
        foreach (ArchitectureTokenDefinition definition in tokens)
        {
            string token = definition.Token;
            int start = 0;
            while (start <= url.Length - token.Length)
            {
                int index = url.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                int end = index + token.Length;
                bool boundedBefore = definition.SuffixToken || index == 0 || !char.IsAsciiLetterOrDigit(url[index - 1]);
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
