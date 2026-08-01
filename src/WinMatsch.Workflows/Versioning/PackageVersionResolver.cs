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
    ReleaseTag,
    UrlToken,
}

public sealed record PackageVersionCandidate(
    PackageVersion Version,
    PackageVersionSource Source,
    EvidenceConfidence Confidence,
    string Provenance);

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
    public static PackageVersionResolution Resolve(PackageVersionResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var candidates = ImmutableArray.CreateBuilder<PackageVersionCandidate>();

        input.OverridePacks.TryGet(input.PackageIdentifier, out OverridePack? pack);
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
            if (asset.Analysis is { IsProductVersionTrustworthy: true } analysis)
            {
                AddCandidate(
                    analysis.ProductVersion,
                    PackageVersionSource.InstallerProductVersion,
                    EvidenceConfidence.High,
                    $"analysis:{asset.DownloadUri.AbsoluteUri}",
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

            AddCandidate(
                ExtractUrlVersion(asset.DownloadUri),
                PackageVersionSource.UrlToken,
                EvidenceConfidence.Low,
                $"url:{asset.DownloadUri.AbsoluteUri}",
                candidates,
                diagnostics);
        }

        PackageVersionSource[] precedence = GetPrecedence(pack?.VersionSource);
        foreach (PackageVersionSource source in precedence)
        {
            PackageVersionCandidate[] tier = candidates
                .Where(candidate => candidate.Source == source)
                .GroupBy(static candidate => candidate.Version.Value, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static candidate => candidate.Version.Value, StringComparer.Ordinal)
                .ToArray();
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
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && value.Length > prefix.Length
                    && IsPrefixSeparator(value[prefix.Length]))
                {
                    value = value[(prefix.Length + 1)..];
                    changed = true;
                    break;
                }
            }
        }
        while (changed);

        return value;
    }

    public static string? ExtractUrlVersion(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
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

        string? version = FindContextualVersion(fileName);
        if (version is null)
        {
            int download = Array.FindLastIndex(
                segments,
                static segment => string.Equals(segment, "download", StringComparison.OrdinalIgnoreCase));
            if (download >= 0 && download + 1 < segments.Length)
            {
                version = FindContextualVersion(segments[download + 1]);
            }
        }

        if (version is null)
        {
            return null;
        }

        if (version.EndsWith("_32", StringComparison.Ordinal)
            || version.EndsWith("_64", StringComparison.Ordinal))
        {
            version = version[..^3];
        }

        return version.Replace('_', '.');
    }

    private static string? FindContextualVersion(string value)
    {
        MatchCollection matches = UrlVersionRegex().Matches(value);
        foreach (Match match in matches.Cast<Match>().Reverse())
        {
            string prefix = value[..match.Index].TrimEnd('-', '_', '.');
            string context = (prefix.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
                .ToLowerInvariant();
            if (context is not ("win" or "windows" or "win32" or "win64"))
            {
                return match.Groups["version"].Value;
            }
        }

        return null;
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
            && CalendarDateRegex().IsMatch(normalized))
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

    private static PackageVersionSource[] GetPrecedence(string? versionSource)
    {
        PackageVersionSource[] defaults =
        [
            PackageVersionSource.PackageOverride,
            PackageVersionSource.InstallerProductVersion,
            PackageVersionSource.ReleaseTag,
            PackageVersionSource.UrlToken,
        ];

        PackageVersionSource? selected = versionSource?.Trim().ToLowerInvariant() switch
        {
            "installer" or "installer.productversion" or "product-version" =>
                PackageVersionSource.InstallerProductVersion,
            "release" or "release.tag" or "release-tag" or "tag" =>
                PackageVersionSource.ReleaseTag,
            "url" or "url.token" or "url-token" => PackageVersionSource.UrlToken,
            _ => null,
        };

        return selected is null
            ? defaults
            :
            [
                PackageVersionSource.PackageOverride,
                selected.Value,
                .. defaults.Where(source => source is not PackageVersionSource.PackageOverride && source != selected),
            ];
    }

    private static bool IsPrefixSeparator(char value) => value is '-' or '_' or '/' or ' ' or '.';

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])v?(?<version>[0-9]+(?:[._][0-9]+)+(?:-(?:alpha|beta|preview|rc)[0-9]*)?)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlVersionRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(?:[T ].*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CalendarDateRegex();
}
