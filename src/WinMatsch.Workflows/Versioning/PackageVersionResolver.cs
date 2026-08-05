using System.Collections.Immutable;
using System.Text.RegularExpressions;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;

namespace WinMatsch.Workflows.Versioning;

public enum PackageVersionSource
{
    PackageOverride,
    InstallerProductVersion,
    InstallerFileVersion,
    ReleaseTag,
    UrlToken,
}

public sealed record PackageVersionCandidate(
    PackageVersion Version,
    PackageVersionSource Source,
    EvidenceConfidence Confidence,
    string Provenance);

public sealed record UrlVersionEvidence(
    string? Version,
    bool IsAmbiguous,
    ImmutableArray<string> Candidates);

public sealed record PackageVersionResolution(
    PackageVersion? Version,
    PackageVersionSource? Source,
    EvidenceConfidence Confidence,
    bool IsAmbiguous,
    ImmutableArray<PackageVersionCandidate> Candidates,
    ImmutableArray<string> Diagnostics)
{
    public bool IsResolved => Version is not null && !IsAmbiguous;
}

public sealed record PackageVersionResolutionInput
{
    public required PackageIdentifier PackageIdentifier { get; init; }

    public string? ExplicitPackageVersion { get; init; }

    public OverridePackSet OverridePacks { get; init; } = OverridePackSet.Empty;

    public required ImmutableArray<DiscoveredAsset> Assets { get; init; }
}

/// <summary>Resolves package versions by explicit, evidence-strength-ordered precedence.</summary>
public static partial class PackageVersionResolver
{
    private const string UrlArtifactQualifierPattern =
        @"(?:winarm64|win64a|aarch64|arm64|x86|x64|amd64|ia32|i386|i686|win32|win64|arm|win|windows|linux|macos|osx|neutral|universal|jre|jdk|java|runtime|setup|installer|install|portable|standalone|package)";
    private const string UrlPrereleaseIdentifierPattern =
        @"(?:[0-9]+|(?:alpha|beta|preview|pre|rc|dev)[0-9]*)";
    private const string UrlVersionIdentifierPattern =
        @"(?!" + UrlArtifactQualifierPattern + @"(?![A-Za-z0-9]))[0-9A-Za-z]+";
    private const string UrlVersionPattern =
        @"(?<![A-Za-z0-9])v?(?<version>[0-9]+(?:[._][0-9]+)+(?:-" +
        UrlPrereleaseIdentifierPattern +
        @"(?:[._-]" +
        UrlPrereleaseIdentifierPattern +
        @")*)?(?:\+" +
        UrlVersionIdentifierPattern +
        @"(?:[._-]" +
        UrlVersionIdentifierPattern +
        @")*)?)(?![A-Za-z0-9])";

    public static PackageVersionResolution Resolve(PackageVersionResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var candidates = ImmutableArray.CreateBuilder<PackageVersionCandidate>();

        input.OverridePacks.TryGet(input.PackageIdentifier, out OverridePack? pack);
        PackageVersionSource? preferredSource = GetSelectedSource(pack?.VersionSource);
        string? packageOverride = input.ExplicitPackageVersion ?? ParseLiteralOverride(pack?.VersionSource);
        if (!string.IsNullOrWhiteSpace(packageOverride)
            && !PackageVersion.TryCreate(packageOverride.Trim(), out _))
        {
            return new(
                null,
                PackageVersionSource.PackageOverride,
                EvidenceConfidence.Explicit,
                false,
                [],
                [$"VERSION_INVALID:PackageOverride:{packageOverride.Trim()}"]);
        }

        AddCandidate(
            packageOverride,
            PackageVersionSource.PackageOverride,
            EvidenceConfidence.Explicit,
            "package override",
            candidates,
            diagnostics);

        foreach (DiscoveredAsset asset in input.Assets)
        {
            if (asset.Analysis is { IsProductVersionTrustworthy: true } analysis
                && IsConsistentBinaryVersion(
                    input.PackageIdentifier,
                    asset,
                    analysis.ProductVersion,
                    analysis.ProductVersionEvidenceKind,
                    enforceReleaseConsistency:
                        preferredSource != PackageVersionSource.InstallerProductVersion,
                    diagnostics))
            {
                AddCandidate(
                    analysis.ProductVersion,
                    PackageVersionSource.InstallerProductVersion,
                    analysis.ProductVersionConfidence,
                    $"analysis:{analysis.ProductVersionEvidenceKind}:{asset.DownloadUri.AbsoluteUri}",
                    candidates,
                    diagnostics);
            }

            if (asset.Analysis is { IsFileVersionTrustworthy: true } fileAnalysis
                && IsConsistentBinaryVersion(
                    input.PackageIdentifier,
                    asset,
                    fileAnalysis.FileVersion,
                    fileAnalysis.FileVersionEvidenceKind,
                    enforceReleaseConsistency:
                        preferredSource != PackageVersionSource.InstallerFileVersion,
                    diagnostics))
            {
                AddCandidate(
                    fileAnalysis.FileVersion,
                    PackageVersionSource.InstallerFileVersion,
                    fileAnalysis.FileVersionConfidence,
                    $"analysis:{fileAnalysis.FileVersionEvidenceKind}:{asset.DownloadUri.AbsoluteUri}",
                    candidates,
                    diagnostics);
            }

            AddCandidate(
                NormalizeReleaseTag(asset.ReleaseTag, input.PackageIdentifier),
                PackageVersionSource.ReleaseTag,
                EvidenceConfidence.Medium,
                $"release-tag:{asset.ReleaseTag}",
                candidates,
                diagnostics);

            UrlVersionEvidence urlVersion = AnalyzeUrlVersion(asset.DownloadUri);
            if (urlVersion.IsAmbiguous)
            {
                diagnostics.Add(
                    $"VERSION_URL_AMBIGUOUS:{asset.DownloadUri.AbsoluteUri}:{string.Join(",", urlVersion.Candidates)}");
            }

            AddCandidate(
                urlVersion.Version,
                PackageVersionSource.UrlToken,
                EvidenceConfidence.Low,
                $"url:{asset.DownloadUri.AbsoluteUri}",
                candidates,
                diagnostics);
        }

        PackageVersionSource[] precedence = GetPrecedence(pack?.VersionSource);
        foreach (PackageVersionSource source in precedence)
        {
            PackageVersionCandidate[] tier = CollapseEquivalentCandidates(
                candidates.Where(candidate => candidate.Source == source));
            if (tier.Length == 0)
            {
                continue;
            }

            if (tier.Length > 1)
            {
                diagnostics.Add(
                    $"VERSION_AMBIGUOUS:{source} produced {string.Join(", ", tier.Select(static candidate => candidate.Version.Value))}.");
                return new(
                    null,
                    source,
                    tier[0].Confidence,
                    true,
                    [.. candidates.OrderBy(static candidate => candidate.Source).ThenBy(static candidate => candidate.Provenance, StringComparer.Ordinal)],
                    [.. diagnostics.Order(StringComparer.Ordinal)]);
            }

            return new(
                tier[0].Version,
                source,
                tier[0].Confidence,
                false,
                [.. candidates.OrderBy(static candidate => candidate.Source).ThenBy(static candidate => candidate.Provenance, StringComparer.Ordinal)],
                [.. diagnostics.Order(StringComparer.Ordinal)]);
        }

        diagnostics.Add("VERSION_UNRESOLVED:No valid package version evidence was available.");
        return new(
            null,
            null,
            EvidenceConfidence.Low,
            false,
            [.. candidates.OrderBy(static candidate => candidate.Source).ThenBy(static candidate => candidate.Provenance, StringComparer.Ordinal)],
            [.. diagnostics.Order(StringComparer.Ordinal)]);
    }

    public static string? NormalizeReleaseTag(string? tag, PackageIdentifier packageIdentifier)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string value = tag.Trim();
        string leaf = packageIdentifier.Value.Split('.')[^1];
        string[] prefixes =
        [
            packageIdentifier.Value,
            leaf,
            "release",
        ];

        bool changed;
        do
        {
            changed = false;
            if (value.Length > 1
                && (value[0] is 'v' or 'V')
                && char.IsAsciiDigit(value[1]))
            {
                value = value[1..];
                changed = true;
            }

            foreach (string prefix in prefixes)
            {
                if (TryStripPackagePrefix(value, prefix, out string? stripped))
                {
                    value = stripped;
                    changed = true;
                    break;
                }
            }
        }
        while (changed);

        return value;
    }

    public static string? ExtractUrlVersion(Uri uri)
        => AnalyzeUrlVersion(uri).Version;

    public static UrlVersionEvidence AnalyzeUrlVersion(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string? source = FindUrlVersionSource(uri);
        ImmutableArray<string> versions = source is null
            ? []
            : FindContextualVersions(source);

        if (versions.IsEmpty)
        {
            return new(null, false, []);
        }

        var representatives = new List<string>();
        foreach (string version in versions)
        {
            string normalized = NormalizeUrlVersion(version);
            if (!PackageVersion.TryCreate(normalized, out PackageVersion? parsed)
                || representatives.Any(existing =>
                    PackageVersion.TryCreate(existing, out PackageVersion? existingVersion)
                    && existingVersion!.IsEquivalentTo(parsed!)))
            {
                continue;
            }

            representatives.Add(normalized);
        }

        representatives.Sort(StringComparer.Ordinal);
        return representatives.Count == 1
            ? new(representatives[0], false, [.. representatives])
            : new(null, representatives.Count > 1, [.. representatives]);
    }

    internal static bool ContainsPreferredUrlVersionToken(Uri uri, string version)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return FindUrlVersionSource(uri) is { } source
            && ContainsVersionToken(source, version, rejectSuffixContinuation: true);
    }

    internal static bool ContainsVersionToken(
        string value,
        string version,
        bool rejectSuffixContinuation = false)
    {
        string[] representations =
        [
            version,
            version.Replace('.', '_'),
            version.Replace('.', '-'),
        ];
        return representations.Any(candidate =>
        {
            int index = value.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                int end = index + candidate.Length;
                bool hasValidStartBoundary = index == 0
                    || !char.IsAsciiLetterOrDigit(value[index - 1])
                    || (value[index - 1] is 'v' or 'V'
                        && (index == 1 || !char.IsAsciiLetterOrDigit(value[index - 2])));
                if (hasValidStartBoundary
                    && (end == value.Length || !char.IsAsciiLetterOrDigit(value[end]))
                    && (!rejectSuffixContinuation
                        || !HasVersionSuffixContinuation(value, end)))
                {
                    return true;
                }

                index = value.IndexOf(candidate, index + 1, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        });
    }

    private static bool HasVersionSuffixContinuation(string value, int end)
    {
        if (end >= value.Length || value[end] is not ('.' or '_' or '-'))
        {
            return false;
        }

        int tokenEnd = end + 1;
        while (tokenEnd < value.Length && char.IsAsciiLetterOrDigit(value[tokenEnd]))
        {
            tokenEnd++;
        }

        string token = value[(end + 1)..tokenEnd];
        return token.Length == 0 || !UrlArtifactQualifierRegex().IsMatch(token);
    }

    private static string? FindUrlVersionSource(Uri uri)
    {
        string[] segments = uri.Segments
            .Select(static segment => Uri.UnescapeDataString(segment).Trim('/'))
            .Where(static segment => segment.Length > 0)
            .ToArray();
        string fileName = segments.LastOrDefault() ?? "";
        string extension = Path.GetExtension(fileName);
        if (extension.Length > 0)
        {
            fileName = fileName[..^extension.Length];
        }

        if (!FindContextualVersions(fileName).IsEmpty)
        {
            return fileName;
        }

        int download = Array.FindLastIndex(
            segments,
            static segment => string.Equals(segment, "download", StringComparison.OrdinalIgnoreCase));
        return download >= 0
            && download + 1 < segments.Length
            && !FindContextualVersions(segments[download + 1]).IsEmpty
                ? segments[download + 1]
                : null;
    }

    private static ImmutableArray<string> FindContextualVersions(string value)
    {
        var versions = ImmutableArray.CreateBuilder<string>();
        MatchCollection matches = UrlVersionRegex().Matches(value);
        foreach (Match match in matches.Cast<Match>())
        {
            string prefix = value[..match.Index].TrimEnd('-', '_', '.');
            string context = (prefix.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
                .ToLowerInvariant();
            if (context is not ("win" or "windows" or "win32" or "win64"))
            {
                versions.Add(match.Groups["version"].Value);
            }
        }

        return [.. versions];
    }

    private static string NormalizeUrlVersion(string version)
    {
        if (version.EndsWith("_32", StringComparison.Ordinal)
            || version.EndsWith("_64", StringComparison.Ordinal))
        {
            version = version[..^3];
        }

        return version.Replace('_', '.');
    }

    private static void AddCandidate(
        string? value,
        PackageVersionSource source,
        EvidenceConfidence confidence,
        string provenance,
        ImmutableArray<PackageVersionCandidate>.Builder candidates,
        ImmutableArray<string>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string normalized = value.Trim();
        if (source is not PackageVersionSource.PackageOverride
            && !normalized.Any(char.IsAsciiDigit))
        {
            diagnostics.Add($"VERSION_INVALID:{source}:{normalized}");
            return;
        }

        if (source == PackageVersionSource.ReleaseTag
            && (CalendarDateRegex().IsMatch(normalized) || ReleaseBuildTagRegex().IsMatch(normalized)))
        {
            diagnostics.Add($"VERSION_INVALID:{source}:{normalized}");
            return;
        }

        if (PackageVersion.TryCreate(normalized, out PackageVersion? version))
        {
            candidates.Add(new(version!, source, confidence, provenance));
        }
        else
        {
            diagnostics.Add($"VERSION_INVALID:{source}:{normalized}");
        }
    }

    private static string? ParseLiteralOverride(string? versionSource)
    {
        if (string.IsNullOrWhiteSpace(versionSource))
        {
            return null;
        }

        const string literalPrefix = "literal:";
        if (versionSource.StartsWith(literalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return versionSource[literalPrefix.Length..].Trim();
        }

        return null;
    }

    private static bool IsConsistentBinaryVersion(
        PackageIdentifier packageIdentifier,
        DiscoveredAsset asset,
        string? version,
        InstallerVersionEvidenceKind evidenceKind,
        bool enforceReleaseConsistency,
        ImmutableArray<string>.Builder diagnostics)
    {
        if (evidenceKind is not (
                InstallerVersionEvidenceKind.PeVersionInfoProductVersion
                or InstallerVersionEvidenceKind.PeVersionInfoFileVersion
                or InstallerVersionEvidenceKind.ArchiveConsensus
                or InstallerVersionEvidenceKind.ArchiveFileVersionConsensus)
            || string.IsNullOrWhiteSpace(version))
        {
            return true;
        }

        if (!enforceReleaseConsistency)
        {
            return true;
        }

        string? normalizedTag = NormalizeReleaseTag(asset.ReleaseTag, packageIdentifier);
        if (string.IsNullOrWhiteSpace(normalizedTag)
            || ReleaseBuildTagRegex().IsMatch(normalizedTag)
            || !SemanticVersionTagRegex().IsMatch(normalizedTag)
            || !PackageVersion.TryCreate(normalizedTag, out PackageVersion? tagVersion)
            || !PackageVersion.TryCreate(version.Trim(), out PackageVersion? binaryVersion)
            || binaryVersion!.IsEquivalentTo(tagVersion!))
        {
            return true;
        }

        diagnostics.Add(
            $"VERSION_BINARY_INCONSISTENT:{version}:{asset.ReleaseTag}:{asset.DownloadUri.AbsoluteUri}");
        return false;
    }

    private static PackageVersionCandidate[] CollapseEquivalentCandidates(
        IEnumerable<PackageVersionCandidate> candidates)
    {
        var representatives = new List<PackageVersionCandidate>();
        foreach (PackageVersionCandidate candidate in candidates
                     .OrderBy(static item => item.Version.Value.Length)
                     .ThenBy(static item => item.Version.Value, StringComparer.Ordinal)
                     .ThenBy(static item => item.Provenance, StringComparer.Ordinal))
        {
            if (!representatives.Any(existing => existing.Version.IsEquivalentTo(candidate.Version)))
            {
                representatives.Add(candidate);
            }
        }

        return [.. representatives];
    }

    private static PackageVersionSource[] GetPrecedence(string? versionSource)
    {
        PackageVersionSource[] defaults =
        [
            PackageVersionSource.PackageOverride,
            PackageVersionSource.InstallerProductVersion,
            PackageVersionSource.InstallerFileVersion,
            PackageVersionSource.ReleaseTag,
            PackageVersionSource.UrlToken,
        ];

        PackageVersionSource? selected = GetSelectedSource(versionSource);

        return selected is null
            ? defaults
            :
            [
                PackageVersionSource.PackageOverride,
                selected.Value,
                .. defaults.Where(source => source is not PackageVersionSource.PackageOverride && source != selected),
            ];
    }

    private static PackageVersionSource? GetSelectedSource(string? versionSource)
        => versionSource?.Trim().ToLowerInvariant() switch
        {
            "installer" or "installer.productversion" or "product-version" =>
                PackageVersionSource.InstallerProductVersion,
            "installer.fileversion" or "file-version" =>
                PackageVersionSource.InstallerFileVersion,
            "release" or "release.tag" or "release-tag" or "tag" =>
                PackageVersionSource.ReleaseTag,
            "url" or "url.token" or "url-token" => PackageVersionSource.UrlToken,
            _ => null,
        };

    private static bool IsPrefixSeparator(char value) => value is '-' or '_' or '/' or ' ' or '.';

    private static bool TryStripPackagePrefix(
        string value,
        string prefix,
        out string stripped)
    {
        int valueIndex = 0;
        foreach (char prefixCharacter in prefix)
        {
            if (!char.IsAsciiLetterOrDigit(prefixCharacter))
            {
                continue;
            }

            while (valueIndex < value.Length
                   && !char.IsAsciiLetterOrDigit(value[valueIndex]))
            {
                valueIndex++;
            }

            if (valueIndex >= value.Length
                || char.ToUpperInvariant(value[valueIndex])
                    != char.ToUpperInvariant(prefixCharacter))
            {
                stripped = value;
                return false;
            }

            valueIndex++;
        }

        if (valueIndex >= value.Length || !IsPrefixSeparator(value[valueIndex]))
        {
            stripped = value;
            return false;
        }

        while (valueIndex < value.Length && IsPrefixSeparator(value[valueIndex]))
        {
            valueIndex++;
        }

        stripped = value[valueIndex..];
        return stripped.Length > 0;
    }

    [GeneratedRegex(UrlVersionPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlVersionRegex();

    [GeneratedRegex(
        "^" + UrlArtifactQualifierPattern + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlArtifactQualifierRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(?:[T ].*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CalendarDateRegex();

    [GeneratedRegex(
        @"^(?:b|build[-_.]?)\d+$|(?:^|[-_.])(?:build|nightly|snapshot)(?:[-_.]|$)|\d{4}-\d{2}-\d{2}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseBuildTagRegex();

    [GeneratedRegex(
        @"^[vV]?\d+(?:\.\d+)+(?:-(?:alpha|beta|preview|rc)\d*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionTagRegex();
}
