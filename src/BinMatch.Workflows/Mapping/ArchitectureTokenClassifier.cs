using System.Collections.Immutable;
using System.Text.RegularExpressions;
using BinMatch.Core;

namespace BinMatch.Workflows.Mapping;

public sealed record ArchitectureTokenEvidence(
    Architecture? Architecture,
    EvidenceConfidence Confidence,
    bool IsAmbiguous,
    ImmutableArray<string> MatchedTokens,
    ImmutableArray<Architecture> Candidates);

/// <summary>
/// Classifies bounded architecture tokens without inventing neutral architecture. The token
/// table is shared with the analysis-side detector via <see cref="ArchitectureTokens"/> so
/// both components classify names identically.
/// </summary>
public static partial class ArchitectureTokenClassifier
{
    public static ArchitectureTokenEvidence Classify(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        List<TokenMatch> matches = [];
        foreach (ArchitectureTokenDefinition token in ArchitectureTokens.All)
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

    private static Regex CreateRegex(ArchitectureTokenDefinition definition)
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

    private sealed record TokenMatch(ArchitectureTokenDefinition Definition, int Index, int Length);
}
