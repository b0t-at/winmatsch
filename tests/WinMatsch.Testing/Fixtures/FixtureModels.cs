using System.Text.Json.Serialization;

namespace WinMatsch.Testing.Fixtures;

public sealed record RegressionFixture(
    FixtureDescriptor Descriptor,
    ExpectedManifestSnapshot Expected);

public sealed record FixtureDescriptor
{
    public required int SchemaVersion { get; init; }

    public required string Id { get; init; }

    public required PackageCoordinate Package { get; init; }

    public required FixtureRegression Regression { get; init; }

    public required FixtureProvenance Provenance { get; init; }

    public required IReadOnlyList<FixtureAsset> Assets { get; init; }

    public required string ExpectedSnapshot { get; init; }
}

public sealed record PackageCoordinate
{
    public required string Identifier { get; init; }

    public required string Version { get; init; }
}

public sealed record FixtureRegression
{
    public required string Summary { get; init; }

    public required IReadOnlyList<string> RuleIds { get; init; }

    public required string ExpectedBehavior { get; init; }
}

public sealed record FixtureProvenance
{
    public required string SourceRepository { get; init; }

    public required int PullRequest { get; init; }

    public required string Outcome { get; init; }

    public required string HeadCommit { get; init; }

    public required string MergeCommit { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required string ManifestPath { get; init; }

    public required Uri ManifestUrl { get; init; }

    public required string ManifestSha256 { get; init; }
}

public sealed record FixtureAsset
{
    public required string FileName { get; init; }

    public required Uri Url { get; init; }

    public required string Sha256 { get; init; }

    public required string ExpectedArchitecture { get; init; }

    public required string ExpectedInstallerType { get; init; }

    [JsonPropertyName("includeInExpectedManifest")]
    public bool? IncludeInExpectedManifestOverride { get; init; }

    [JsonIgnore]
    public bool IncludeInExpectedManifest => IncludeInExpectedManifestOverride ?? true;
}

public sealed record ExpectedManifestSnapshot
{
    public required string PackageIdentifier { get; init; }

    public required string PackageVersion { get; init; }

    public string? InstallerType { get; init; }

    public string? NestedInstallerType { get; init; }

    public IReadOnlyList<string> NestedInstallerFiles { get; init; } = [];

    public IReadOnlyList<ExpectedAppsAndFeaturesEntry> AppsAndFeaturesEntries { get; init; } = [];

    public required IReadOnlyList<ExpectedInstallerSnapshot> Installers { get; init; }

    public ExpectedLocaleSnapshot? Locale { get; init; }
}

public sealed record ExpectedInstallerSnapshot
{
    public required string Architecture { get; init; }

    public string? InstallerType { get; init; }

    public required Uri InstallerUrl { get; init; }

    public required string InstallerSha256 { get; init; }

    public string? Scope { get; init; }

    public string? CustomSwitch { get; init; }

    public IReadOnlyList<string> NestedInstallerFiles { get; init; } = [];

    public IReadOnlyList<string> PackageDependencies { get; init; } = [];

    public IReadOnlyList<ExpectedAppsAndFeaturesEntry> AppsAndFeaturesEntries { get; init; } = [];
}

public sealed record ExpectedAppsAndFeaturesEntry
{
    public string? DisplayName { get; init; }

    public string? DisplayVersion { get; init; }

    public string? ProductCode { get; init; }
}

public sealed record ExpectedLocaleSnapshot
{
    public string? ReleaseNotes { get; init; }

    public Uri? ReleaseNotesUrl { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(FixtureDescriptor))]
[JsonSerializable(typeof(ExpectedManifestSnapshot))]
[JsonSerializable(typeof(List<HttpInteractionRecording>))]
internal sealed partial class FixtureJsonContext : JsonSerializerContext;
