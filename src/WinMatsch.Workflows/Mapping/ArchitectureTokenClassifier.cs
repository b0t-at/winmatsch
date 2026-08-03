using System.Collections.Immutable;
using System.Text.RegularExpressions;
using WinMatsch.Core;

namespace WinMatsch.Workflows.Mapping;

public sealed record ArchitectureTokenEvidence(
    Architecture? Architecture,
    EvidenceConfidence Confidence,
    bool IsAmbiguous,
    ImmutableArray<string> MatchedTokens,
    ImmutableArray<Architecture> Candidates);

/// <summary>Classifies bounded architecture tokens without inventing neutral architecture.</summary>
public static partial class ArchitectureTokenClassifier
{
    private static readonly TokenDefinition[] _tokens =
    [
        new(Architecture.Arm64, "winarm64", 400),
        new(Architecture.Arm64, "win64a", 400),
        new(Architecture.Arm64, "aarch64", 400),
        new(Architecture.Arm64, "arm64", 400),
        new(Architecture.X64, "x86_64", 300),
        new(Architecture.X64, "64-bit", 300),
        new(Architecture.X64, "amd64", 300),
        new(Architecture.X64, "win64", 300),
        new(Architecture.X64, "x64", 300),
        new(Architecture.X64, "64bit", 300),
        new(Architecture.X64, "_64", 300, SuffixToken: true),
        new(Architecture.X86, "32-bit", 200),
        new(Architecture.X86, "win32", 200),
        new(Architecture.X86, "ia32", 200),
        new(Architecture.X86, "i386", 200),
        new(Architecture.X86, "x86", 200),
        new(Architecture.X86, "386", 200),
        new(Architecture.X86, "32bit", 200),
        new(Architecture.X86, "_32", 200, SuffixToken: true),
        new(Architecture.Arm, "arm", 100),
    ];

    public static ArchitectureTokenEvidence Classify(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        List<TokenMatch> matches = [];
        foreach (TokenDefinition token in _tokens)
        {
            foreach (Match match in CreateRegex(token).Matches(value))
            {
                matches.Add(new(token, match.Index, match.Length));
            }
        }

        RemoveContainedLowerPriorityMatches(matches);
        RemoveWin32PlatformMatches(value, matches);

        Architecture[] candidates = matches
            .Select(static match => match.Definition.Architecture)
            .Distinct()
            .Order()
            .ToArray();
        bool ambiguous = candidates.Length > 1;
        return new(
            candidates.Length == 1 ? candidates[0] : null,
            candidates.Length == 0 ? EvidenceConfidence.Low : EvidenceConfidence.Medium,
            ambiguous,
            [.. matches.OrderByDescending(static match => match.Definition.Priority).ThenBy(static match => match.Index).Select(static match => match.Definition.Token)],
            [.. candidates]);
    }

    private static Regex CreateRegex(TokenDefinition definition)
    {
        string pattern = definition.SuffixToken
            ? $@"{Regex.Escape(definition.Token)}(?![A-Za-z0-9])"
            : $@"(?<![A-Za-z0-9]){Regex.Escape(definition.Token)}(?![A-Za-z0-9])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void RemoveContainedLowerPriorityMatches(List<TokenMatch> matches)
    {
        matches.RemoveAll(match => matches.Any(other =>
            other.Definition.Priority > match.Definition.Priority
            && other.Index <= match.Index
            && other.Index + other.Length >= match.Index + match.Length));
    }

    private static void RemoveWin32PlatformMatches(string value, List<TokenMatch> matches)
    {
        foreach (TokenMatch win32 in matches
                     .Where(static match => string.Equals(match.Definition.Token, "win32", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            int separator = win32.Index + win32.Length;
            TokenMatch? rightHand = matches
                .Where(match => match.Index > separator
                    && match.Index - separator <= 2
                    && value.AsSpan(separator, match.Index - separator).IndexOfAnyExcept('-', '_', '.') < 0)
                .OrderByDescending(static match => match.Definition.Priority)
                .FirstOrDefault();
            if (rightHand is not null)
            {
                matches.Remove(win32);
            }
        }
    }

    private sealed record TokenDefinition(
        Architecture Architecture,
        string Token,
        int Priority,
        bool SuffixToken = false);

    private sealed record TokenMatch(TokenDefinition Definition, int Index, int Length);
}
