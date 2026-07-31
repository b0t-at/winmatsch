using System.Collections.Immutable;
using WinMatsch.Core;

namespace WinMatsch.Rules.OverridePacks;

/// <summary>A versioned, reviewable set of learned and manually maintained package overrides.</summary>
public sealed record OverridePack
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required PackageIdentifier PackageIdentifier { get; init; }

    public ImmutableDictionary<string, RuleMode> RuleModes { get; init; }
        = ImmutableDictionary.Create<string, RuleMode>(StringComparer.OrdinalIgnoreCase);

    public ImmutableArray<ForcedArchitectureOverride> ForcedArchitectures { get; init; } = [];

    public ImmutableArray<AssetMappingOverride> AssetMappings { get; init; } = [];

    public ScopeLayoutOverride? ScopeLayout { get; init; }

    public string? VersionSource { get; init; }

    public ImmutableDictionary<string, string> MetadataUrlReplacements { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    public ImmutableArray<string> PreservedFields { get; init; } = [];

    public ImmutableArray<string> DroppedFields { get; init; } = [];

    public ImmutableArray<string> VanityUrls { get; init; } = [];

    public bool ManualOnly { get; init; }

    public ImmutableArray<PolicyAnnotation> Policies { get; init; } = [];

    public PackageQuirks Quirks { get; init; } = new();
}

public sealed record ForcedArchitectureOverride
{
    public required string AssetPattern { get; init; }

    public required Architecture Architecture { get; init; }

    public required string SourceEvidence { get; init; }

    public RuleChangeConfidence Confidence { get; init; } = RuleChangeConfidence.High;
}

public sealed record AssetMappingOverride
{
    public required string AssetPattern { get; init; }

    public required string Entry { get; init; }

    public Architecture? Architecture { get; init; }

    public InstallerType? InstallerType { get; init; }

    public Scope? Scope { get; init; }
}

public enum ScopeLayoutOverride
{
    Preserve,
    Root,
    PerInstaller,
}

public sealed record PolicyAnnotation
{
    public required string Id { get; init; }

    public required string Annotation { get; init; }
}
