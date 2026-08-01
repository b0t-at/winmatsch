using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.Downloads;

namespace WinMatsch.Workflows.Mapping;

/// <summary>Relative strength of one mapping or version conclusion.</summary>
public enum EvidenceConfidence
{
    Low,
    Medium,
    High,
    Explicit,
}

/// <summary>A stable snapshot of downloaded bytes and their HTTP identity.</summary>
public sealed record AssetContentEvidence(
    DownloadContentIdentity Identity,
    string InitialUrl,
    string FinalUrl,
    string? ContentType,
    DateTimeOffset RetrievedAt)
{
    public static AssetContentEvidence FromDownload(DownloadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            result.ContentIdentity,
            result.InitialUrl,
            result.FinalUrl,
            result.ContentType,
            result.RetrievedAt);
    }
}

/// <summary>Immutable analyzer evidence used by discovery and mapping.</summary>
public sealed record AssetAnalysisEvidence
{
    public const int MaximumArchiveEntries = 10_000;

    public const int MaximumArchivePathLength = 1_024;

    public required DetectedInstallerFormat Format { get; init; }

    public string? ProductVersion { get; init; }

    public bool IsProductVersionTrustworthy { get; init; }

    public ImmutableArray<Architecture> PayloadArchitectures { get; init; } = [];

    public ImmutableArray<InstallerType> InstallerTypes { get; init; } = [];

    public ImmutableArray<Scope> Scopes { get; init; } = [];

    public ImmutableArray<string> ArchiveEntries { get; init; } = [];

    public ImmutableArray<string> NestedInstallerCandidates { get; init; } = [];

    public ImmutableArray<PayloadPathEvidence> PayloadEvidence { get; init; } = [];

    public bool? ArchiveBinariesDependOnPath { get; init; }

    public ImmutableArray<string> Diagnostics { get; init; } = [];

    public ImmutableArray<string> Validate()
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (ArchiveEntries.Length > MaximumArchiveEntries)
        {
            errors.Add($"Archive entry count exceeds {MaximumArchiveEntries}.");
        }

        foreach (string path in ArchiveEntries.Concat(NestedInstallerCandidates))
        {
            if (!IsSafeArchivePath(path))
            {
                errors.Add($"Archive path '{path}' is absolute, traversing, empty, or exceeds {MaximumArchivePathLength} characters.");
            }
        }

        if (!ArchiveEntries.IsEmpty)
        {
            foreach (string candidate in NestedInstallerCandidates)
            {
                if (!ArchiveEntries.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Nested installer candidate '{candidate}' is absent from the bounded archive entry set.");
                }
            }
        }

        return [.. errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public static AssetAnalysisEvidence FromAnalysis(
        InstallerAnalysis analysis,
        PayloadDependencyAnalysis? dependencyAnalysis = null,
        IEnumerable<string>? boundedArchiveEntries = null,
        bool isProductVersionTrustworthy = false)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        Installer[] installers = analysis.Installers.ToArray();
        bool?[] pathDependencyValues = installers
            .Select(static installer => installer.ArchiveBinariesDependOnPath)
            .Where(static value => value is not null)
            .Distinct()
            .ToArray();
        bool hasPathDependencyConflict = pathDependencyValues.Length > 1;
        return new AssetAnalysisEvidence
        {
            Format = analysis.Format,
            ProductVersion = analysis.ProductVersion,
            IsProductVersionTrustworthy = isProductVersionTrustworthy,
            PayloadArchitectures =
            [
                .. installers
                    .Select(static installer => installer.Architecture)
                    .OfType<Architecture>()
                    .Distinct()
                    .Order(),
            ],
            InstallerTypes =
            [
                .. installers
                    .Select(static installer => installer.InstallerType)
                    .OfType<InstallerType>()
                    .Distinct()
                    .Order(),
            ],
            Scopes =
            [
                .. installers
                    .Select(static installer => installer.Scope)
                    .OfType<Scope>()
                    .Distinct()
                    .Order(),
            ],
            ArchiveEntries = SnapshotArchivePaths(boundedArchiveEntries ?? []),
            NestedInstallerCandidates =
            [
                .. (analysis.Zip?.NestedInstallerCandidates ?? [])
                    .Select(NormalizeArchivePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal),
            ],
            PayloadEvidence =
            [
                .. (dependencyAnalysis?.Evidence ?? [])
                    .Select(static evidence => new PayloadPathEvidence(
                        NormalizeArchivePath(evidence.PayloadPath),
                        evidence.Architecture,
                        evidence.Status,
                        [.. evidence.Signals.Order(StringComparer.Ordinal)]))
                    .OrderBy(static evidence => evidence.Path, StringComparer.Ordinal)
                    .ThenBy(static evidence => evidence.Status),
            ],
            ArchiveBinariesDependOnPath =
                pathDependencyValues.Length == 1 ? pathDependencyValues[0] : null,
            Diagnostics =
            [
                .. analysis.Diagnostics
                    .Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")
                    .Concat(hasPathDependencyConflict
                        ? ["ANALYSIS_ARCHIVE_PATH_DEPENDENCY_CONFLICT:Analyzer entries disagree about ArchiveBinariesDependOnPath."]
                        : [])
                    .Order(StringComparer.Ordinal),
            ],
        };
    }

    private static ImmutableArray<string> SnapshotArchivePaths(IEnumerable<string> paths)
    {
        string[] snapshot = paths
            .Take(MaximumArchiveEntries + 1)
            .Select(NormalizeArchivePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (snapshot.Length > MaximumArchiveEntries)
        {
            throw new InvalidDataException($"Archive evidence exceeds {MaximumArchiveEntries} entries.");
        }

        foreach (string path in snapshot)
        {
            if (!IsSafeArchivePath(path))
            {
                throw new InvalidDataException($"Archive path '{path}' is unsafe or exceeds the path-length limit.");
            }
        }

        return [.. snapshot];
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/');

    private static bool IsSafeArchivePath(string path)
        => !string.IsNullOrWhiteSpace(path)
            && path.Length <= MaximumArchivePathLength
            && !path.StartsWith('/')
            && !(path.Length >= 3
                && char.IsAsciiLetter(path[0])
                && path[1] == ':'
                && path[2] == '/')
            && !Path.IsPathFullyQualified(path)
            && !path.Split('/').Contains("..", StringComparer.Ordinal);
}

/// <summary>Architecture and dependency evidence tied to one bounded payload path.</summary>
public sealed record PayloadPathEvidence(
    string Path,
    Architecture? Architecture,
    DependencyEvidenceStatus Status,
    ImmutableArray<string> Signals);
