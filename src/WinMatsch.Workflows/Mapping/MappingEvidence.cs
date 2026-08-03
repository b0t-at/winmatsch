using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.Downloads;

namespace WinMatsch.Workflows.Mapping;

public enum AnalysisEvidenceOrigin
{
    ContentAnalysis,
    MetadataFixture,
}

/// <summary>Relative strength of one mapping or version conclusion.</summary>
public enum EvidenceConfidence
{
    Low,
    Medium,
    High,
    Explicit,
}

public enum InstallerVersionEvidenceKind
{
    Unspecified,
    PackageMetadata,
    PeVersionInfoProductVersion,
    PeVersionInfoFileVersion,
    ArchiveConsensus,
    ArchiveFileVersionConsensus,
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

    public const int MaximumEvidenceItems = 10_000;

    public const int MaximumEvidenceTextLength = 65_536;

    public required DetectedInstallerFormat Format { get; init; }

    /// <summary>The exact downloaded bytes this analysis was derived from.</summary>
    public required DownloadContentIdentity AnalyzedContentIdentity { get; init; }

    /// <summary>The requested or final download URL whose bytes were analyzed.</summary>
    public required string AnalyzedUrl { get; init; }

    public AnalysisEvidenceOrigin Origin { get; init; } = AnalysisEvidenceOrigin.ContentAnalysis;

    public string? ProductVersion { get; init; }

    public bool IsProductVersionTrustworthy { get; init; }

    public InstallerVersionEvidenceKind ProductVersionEvidenceKind { get; init; }

    public EvidenceConfidence ProductVersionConfidence { get; init; } = EvidenceConfidence.Low;

    public string? FileVersion { get; init; }

    public bool IsFileVersionTrustworthy { get; init; }

    public InstallerVersionEvidenceKind FileVersionEvidenceKind { get; init; }

    public EvidenceConfidence FileVersionConfidence { get; init; } = EvidenceConfidence.Low;

    /// <summary>Whether dependency analysis inspected every relevant payload within its bounds.</summary>
    public bool DependencyAnalysisComplete { get; init; } = true;

    /// <summary>Correlated installer shapes emitted by the analyzer for this one asset.</summary>
    public ImmutableArray<AnalyzedInstallerShape> InstallerShapes { get; init; } = [];

    public ImmutableArray<string> ArchiveEntries { get; init; } = [];

    public ImmutableArray<string> NestedInstallerCandidates { get; init; } = [];

    public ImmutableArray<PayloadPathEvidence> PayloadEvidence { get; init; } = [];

    public bool? ArchiveBinariesDependOnPath { get; init; }

    public ImmutableArray<string> Diagnostics { get; init; } = [];

    public ImmutableArray<string> Validate()
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (ArchiveEntries.Length > MaximumArchiveEntries
            || NestedInstallerCandidates.Length > MaximumArchiveEntries
            || InstallerShapes.Length > MaximumEvidenceItems
            || PayloadEvidence.Length > MaximumEvidenceItems
            || Diagnostics.Length > MaximumEvidenceItems)
        {
            return [$"Analysis evidence collection exceeds the {MaximumEvidenceItems} item limit."];
        }

        long totalItems = ArchiveEntries.Length
            + NestedInstallerCandidates.Length
            + InstallerShapes.Length
            + PayloadEvidence.Length
            + Diagnostics.Length;
        foreach (AnalyzedInstallerShape shape in InstallerShapes)
        {
            totalItems += shape.NestedInstallerFiles.Length;
            if (totalItems > MaximumEvidenceItems)
            {
                return [$"Total analysis evidence count exceeds {MaximumEvidenceItems}."];
            }
        }

        foreach (PayloadPathEvidence evidence in PayloadEvidence)
        {
            totalItems += evidence.Signals.Length;
            if (totalItems > MaximumEvidenceItems)
            {
                return [$"Total analysis evidence count exceeds {MaximumEvidenceItems}."];
            }
        }

        foreach (string path in ArchiveEntries
                     .Concat(NestedInstallerCandidates)
                     .Concat(PayloadEvidence.Select(static evidence => evidence.Path))
                     .Concat(InstallerShapes.SelectMany(
                         static shape => shape.NestedInstallerFiles.Select(static file => file.RelativeFilePath))))
        {
            if (!IsSafeArchivePath(path))
            {
                errors.Add($"Archive path '{path}' is absolute, traversing, empty, or exceeds {MaximumArchivePathLength} characters.");
            }
        }

        if (HasCaseCollision(ArchiveEntries) || HasCaseCollision(NestedInstallerCandidates))
        {
            errors.Add("Archive paths contain case-insensitive collisions with different spelling.");
        }

        foreach (string text in Diagnostics.Concat(
                     PayloadEvidence.SelectMany(static evidence => evidence.Signals)))
        {
            if (text.Length > MaximumEvidenceTextLength)
            {
                errors.Add($"Analysis evidence text exceeds {MaximumEvidenceTextLength} characters.");
            }
        }

        foreach (AnalyzedInstallerShape shape in InstallerShapes)
        {
            if (shape.NestedInstallerFiles
                    .Select(static file => file.RelativeFilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()
                != shape.NestedInstallerFiles.Length
                || shape.NestedInstallerFiles
                    .Select(static file => file.PortableCommandAlias)
                    .Where(static alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()
                != shape.NestedInstallerFiles.Count(static file => !string.IsNullOrWhiteSpace(file.PortableCommandAlias)))
            {
                errors.Add("Analyzed nested installer paths and non-empty aliases must be distinct per installer shape.");
            }

            if (!ArchiveEntries.IsEmpty
                && shape.NestedInstallerFiles.Any(
                    file => !ArchiveEntries.Contains(file.RelativeFilePath, StringComparer.OrdinalIgnoreCase)))
            {
                errors.Add("An analyzed nested installer path is absent from the bounded archive entry set.");
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
        AssetContentEvidence content,
        PayloadDependencyAnalysis? dependencyAnalysis = null,
        IEnumerable<string>? boundedArchiveEntries = null,
        bool isProductVersionTrustworthy = false,
        InstallerVersionEvidenceKind productVersionEvidenceKind = InstallerVersionEvidenceKind.Unspecified,
        EvidenceConfidence productVersionConfidence = EvidenceConfidence.Low,
        bool isFileVersionTrustworthy = false,
        InstallerVersionEvidenceKind fileVersionEvidenceKind = InstallerVersionEvidenceKind.Unspecified,
        EvidenceConfidence fileVersionConfidence = EvidenceConfidence.Low)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(content);

        if (analysis.Installers.Count > MaximumEvidenceItems
            || (analysis.Zip?.NestedInstallerCandidates.Count ?? 0) > MaximumEvidenceItems
            || (dependencyAnalysis?.Evidence.Count ?? 0) > MaximumEvidenceItems
            || analysis.Diagnostics.Count > MaximumEvidenceItems)
        {
            throw new InvalidDataException($"Analysis evidence exceeds {MaximumEvidenceItems} items.");
        }

        Installer[] installers = analysis.Installers.ToArray();
        long aggregateItems = installers.Sum(
                static installer => (long)(installer.NestedInstallerFiles?.Count ?? 0))
            + (analysis.Zip?.NestedInstallerCandidates.Count ?? 0)
            + (dependencyAnalysis?.Evidence.Sum(static evidence => (long)evidence.Signals.Count) ?? 0)
            + installers.Length
            + (dependencyAnalysis?.Evidence.Count ?? 0)
            + analysis.Diagnostics.Count;
        if (aggregateItems > MaximumEvidenceItems)
        {
            throw new InvalidDataException($"Aggregate analysis evidence exceeds {MaximumEvidenceItems} items.");
        }

        bool?[] pathDependencyValues = installers
            .Select(static installer => installer.ArchiveBinariesDependOnPath)
            .Where(static value => value is not null)
            .Distinct()
            .ToArray();
        bool hasPathDependencyConflict = pathDependencyValues.Length > 1;
        var result = new AssetAnalysisEvidence
        {
            Format = analysis.Format,
            AnalyzedContentIdentity = content.Identity,
            AnalyzedUrl = content.FinalUrl,
            ProductVersion = analysis.ProductVersion,
            IsProductVersionTrustworthy = isProductVersionTrustworthy,
            ProductVersionEvidenceKind = productVersionEvidenceKind,
            ProductVersionConfidence = productVersionConfidence,
            FileVersion = analysis.FileVersion,
            IsFileVersionTrustworthy = isFileVersionTrustworthy,
            FileVersionEvidenceKind = fileVersionEvidenceKind,
            FileVersionConfidence = fileVersionConfidence,
            DependencyAnalysisComplete = dependencyAnalysis?.IsComplete ?? true,
            InstallerShapes =
            [
                .. installers
                    .Select(static installer => new AnalyzedInstallerShape
                    {
                        Architecture = installer.Architecture,
                        InstallerType = installer.InstallerType,
                        NestedInstallerType = installer.NestedInstallerType,
                        Scope = installer.Scope,
                        InstallerLocale = installer.InstallerLocale,
                        NestedInstallerFiles =
                        [
                            .. (installer.NestedInstallerFiles ?? [])
                                .Where(static file => !string.IsNullOrWhiteSpace(file.RelativeFilePath))
                                .Select(static file => new PlannedNestedInstallerFile(
                                    NormalizeArchivePath(file.RelativeFilePath!),
                                    file.PortableCommandAlias))
                                .OrderBy(static file => file.RelativeFilePath, StringComparer.Ordinal)
                                .ThenBy(static file => file.PortableCommandAlias, StringComparer.Ordinal),
                        ],
                        ArchiveBinariesDependOnPath = installer.ArchiveBinariesDependOnPath,
                    })
                    .OrderBy(static shape => shape.Architecture)
                    .ThenBy(static shape => shape.InstallerType)
                    .ThenBy(static shape => shape.Scope)
                    .ThenBy(static shape => shape.InstallerLocale?.Value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static shape => shape.NestedInstallerType)
                    .ThenBy(static shape => FormatShape(shape), StringComparer.Ordinal),
            ],
            ArchiveEntries = SnapshotArchivePaths(boundedArchiveEntries ?? []),
            NestedInstallerCandidates = SnapshotArchivePaths(
                analysis.Zip?.NestedInstallerCandidates ?? []),
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
                    .Concat((dependencyAnalysis?.Diagnostics ?? [])
                        .Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Message}"))
                    .Concat(dependencyAnalysis is { IsComplete: false }
                        ? ["DEPENDENCY_ANALYSIS_INCOMPLETE:Payload dependency evidence is incomplete and requires review."]
                        : [])
                    .Concat(hasPathDependencyConflict
                        ? ["ANALYSIS_ARCHIVE_PATH_DEPENDENCY_CONFLICT:Analyzer entries disagree about ArchiveBinariesDependOnPath."]
                        : [])
                    .Order(StringComparer.Ordinal),
            ],
        };
        ImmutableArray<string> validationErrors = result.Validate();
        if (!validationErrors.IsEmpty)
        {
            throw new InvalidDataException(string.Join(" ", validationErrors));
        }

        return result;
    }

    private static ImmutableArray<string> SnapshotArchivePaths(IEnumerable<string> paths)
    {
        string[] snapshot = paths
            .Take(MaximumArchiveEntries + 1)
            .Select(NormalizeArchivePath)
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

        if (HasCaseCollision(snapshot))
        {
            throw new InvalidDataException("Archive paths contain a case-insensitive collision.");
        }

        return
        [
            .. snapshot
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
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

    private static bool HasCaseCollision(IEnumerable<string> paths)
        => paths
            .GroupBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Any(static group => group.Distinct(StringComparer.Ordinal).Skip(1).Any());

    private static string FormatShape(AnalyzedInstallerShape shape)
        => string.Join(
            '|',
            shape.NestedInstallerFiles.Select(
                static file => $"{file.RelativeFilePath}=>{file.PortableCommandAlias}"));
}

/// <summary>One correlated architecture/type/scope variant emitted for an analyzed asset.</summary>
public sealed record AnalyzedInstallerShape
{
    public Architecture? Architecture { get; init; }

    public InstallerType? InstallerType { get; init; }

    public InstallerType? NestedInstallerType { get; init; }

    public Scope? Scope { get; init; }

    public LanguageTag? InstallerLocale { get; init; }

    public ImmutableArray<PlannedNestedInstallerFile> NestedInstallerFiles { get; init; } = [];

    public bool? ArchiveBinariesDependOnPath { get; init; }
}

/// <summary>Architecture and dependency evidence tied to one bounded payload path.</summary>
public sealed record PayloadPathEvidence(
    string Path,
    Architecture? Architecture,
    DependencyEvidenceStatus Status,
    ImmutableArray<string> Signals);
