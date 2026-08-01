using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Versioning;

namespace WinMatsch.Workflows.Mapping;

public enum AssetMappingDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record AssetMappingDiagnostic(
    string Code,
    AssetMappingDiagnosticSeverity Severity,
    string Message,
    string? AssetUrl = null,
    int? PreviousPosition = null);

public enum AssetMappingDecisionKind
{
    Preserved,
    Updated,
    Proposed,
    Removed,
    Unresolved,
}

/// <summary>An immutable installer shape emitted by the mapping planner.</summary>
public sealed record PlannedInstaller
{
    public required Uri Url { get; init; }

    public Sha256Hash? Sha256 { get; init; }

    public required Architecture Architecture { get; init; }

    public InstallerType? InstallerType { get; init; }

    public InstallerType? NestedInstallerType { get; init; }

    public Scope? Scope { get; init; }

    public string? DisplayVersion { get; init; }

    public ImmutableArray<PlannedNestedInstallerFile> NestedInstallerFiles { get; init; } = [];

    public bool? ArchiveBinariesDependOnPath { get; init; }
}

public sealed record PlannedNestedInstallerFile(string RelativeFilePath, string? PortableCommandAlias);

public sealed record AssetMappingDecision(
    AssetMappingDecisionKind Kind,
    int? PreviousPosition,
    PlannedInstaller? Installer,
    string Reason,
    EvidenceConfidence Confidence);

public sealed record AssetMappingQuestion(
    string Code,
    string Prompt,
    ImmutableArray<string> Options,
    string? AssetUrl = null,
    int? PreviousPosition = null);

/// <summary>The deterministic, immutable output of release-asset mapping.</summary>
public sealed record AssetMappingPlan(
    PackageVersionResolution Version,
    ImmutableArray<AssetMappingDecision> Decisions,
    ImmutableArray<AssetMappingDiagnostic> Diagnostics,
    ImmutableArray<AssetMappingQuestion> UnresolvedQuestions)
{
    public bool CanApply => Version.IsResolved
        && UnresolvedQuestions.IsEmpty
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == AssetMappingDiagnosticSeverity.Error);

    /// <summary>A stable content representation suitable for deterministic-output assertions and caches.</summary>
    public string DeterministicKey => string.Join(
        '\n',
        [
            $"version|{Version.Version?.Value}|{Version.Source}|{Version.Confidence}|{Version.IsAmbiguous}",
            .. Decisions.Select(static decision =>
                $"decision|{decision.Kind}|{decision.PreviousPosition}|{decision.Installer?.Url.AbsoluteUri}|{decision.Installer?.Sha256}|{decision.Installer?.Architecture}|{decision.Installer?.InstallerType}|{decision.Installer?.NestedInstallerType}|{decision.Installer?.Scope}|{decision.Installer?.DisplayVersion}|{decision.Confidence}|{decision.Reason}|{FormatNested(decision.Installer)}"),
            .. Diagnostics.Select(static diagnostic =>
                $"diagnostic|{diagnostic.Code}|{diagnostic.Severity}|{diagnostic.AssetUrl}|{diagnostic.PreviousPosition}|{diagnostic.Message}"),
            .. UnresolvedQuestions.Select(static question =>
                $"question|{question.Code}|{question.AssetUrl}|{question.PreviousPosition}|{question.Prompt}|{string.Join(',', question.Options)}"),
        ]);

    private static string FormatNested(PlannedInstaller? installer)
        => installer is null
            ? ""
            : string.Join(
                ',',
                installer.NestedInstallerFiles.Select(
                    static file => $"{file.RelativeFilePath}=>{file.PortableCommandAlias}"));
}

/// <summary>An immutable snapshot of a previously accepted installer entry.</summary>
public sealed record PreviousInstallerEntry
{
    public required int Position { get; init; }

    /// <summary>Optional stable logical key used by an override pack's asset-mapping entry.</summary>
    public string? Entry { get; init; }

    public required Uri Url { get; init; }

    public Sha256Hash? Sha256 { get; init; }

    public required Architecture Architecture { get; init; }

    public InstallerType? InstallerType { get; init; }

    public InstallerType? NestedInstallerType { get; init; }

    public Scope? Scope { get; init; }

    public string? DisplayVersion { get; init; }

    public required PackageVersion PackageVersion { get; init; }

    public ImmutableArray<PlannedNestedInstallerFile> NestedInstallerFiles { get; init; } = [];

    public bool? ArchiveBinariesDependOnPath { get; init; }

    public static ImmutableArray<PreviousInstallerEntry> FromManifests(PackageManifests manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        InstallerManifest manifest = manifests.Installer;
        PackageVersion version = manifest.PackageVersion
            ?? throw new ArgumentException("The previous installer manifest must have a package version.", nameof(manifests));

        return
        [
            .. (manifest.Installers ?? [])
                .Select((installer, index) => new PreviousInstallerEntry
                {
                    Position = index,
                    Entry = null,
                    Url = new Uri(
                        installer.InstallerUrl
                            ?? throw new ArgumentException($"Previous installer {index} has no URL.", nameof(manifests)),
                        UriKind.Absolute),
                    Sha256 = installer.InstallerSha256,
                    Architecture = installer.Architecture
                        ?? throw new ArgumentException($"Previous installer {index} has no architecture.", nameof(manifests)),
                    InstallerType = installer.InstallerType ?? manifest.InstallerType,
                    NestedInstallerType = installer.NestedInstallerType ?? manifest.NestedInstallerType,
                    Scope = installer.Scope ?? manifest.Scope,
                    DisplayVersion = (installer.AppsAndFeaturesEntries ?? manifest.AppsAndFeaturesEntries)?
                        .Select(static entry => entry.DisplayVersion)
                        .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
                    PackageVersion = version,
                    NestedInstallerFiles =
                    [
                        .. (installer.NestedInstallerFiles ?? manifest.NestedInstallerFiles ?? [])
                            .Where(static file => !string.IsNullOrWhiteSpace(file.RelativeFilePath))
                            .Select(static file => new PlannedNestedInstallerFile(
                                file.RelativeFilePath!.Replace('\\', '/'),
                                file.PortableCommandAlias)),
                    ],
                    ArchiveBinariesDependOnPath =
                        installer.ArchiveBinariesDependOnPath ?? manifest.ArchiveBinariesDependOnPath,
                }),
        ];
    }
}

public sealed record AssetMappingRequest
{
    public required PackageIdentifier PackageIdentifier { get; init; }

    public required PackageVersionResolution Version { get; init; }

    public required ImmutableArray<DiscoveredAsset> Assets { get; init; }

    public ImmutableArray<PreviousInstallerEntry> PreviousInstallers { get; init; } = [];

    public OverridePackSet OverridePacks { get; init; } = OverridePackSet.Empty;

    public ImmutableArray<UrlOverride> UrlOverrides { get; init; } = [];

    /// <summary>Explicit opt-in for replacing an accepted architecture/type/scope layout.</summary>
    public bool AllowStructuralRewrite { get; init; }

    /// <summary>Explicit approval for a stable URL whose downloaded SHA-256 changed.</summary>
    public bool AllowStableUrlContentChange { get; init; }
}
