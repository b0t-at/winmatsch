using System.Text.Json.Serialization;

namespace WinMatsch.Testing.Fixtures;

public sealed record RegressionFixture(
    FixtureDescriptor Descriptor,
    IReadOnlyDictionary<string, byte[]> ExpectedManifests);

public sealed record FixtureDescriptor
{
    public required int SchemaVersion { get; init; }

    public required string Id { get; init; }

    public required PackageCoordinate Package { get; init; }

    public required FixtureRegression Regression { get; init; }

    public required FixtureProvenance Provenance { get; init; }

    public required IReadOnlyList<FixtureAsset> Assets { get; init; }

    public required FixtureScenario Scenario { get; init; }

    public required string ExpectedManifestDirectory { get; init; }
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

    [JsonPropertyName("sha256")]
    public required string UpstreamSha256 { get; init; }

    public required string SyntheticSha256 { get; init; }

    public required string ExpectedArchitecture { get; init; }

    public required string ExpectedInstallerType { get; init; }

    public FixtureSyntheticAsset Synthetic { get; init; } = new();
}

public sealed record FixtureSyntheticAsset
{
    public string? Kind { get; init; }

    public IReadOnlyList<string> NestedPayloadPaths { get; init; } = [];

    public IReadOnlyList<string> Imports { get; init; } = [];

    public IReadOnlyList<string> PayloadArchitectures { get; init; } = [];

    public string? ArchitectureExpression { get; init; }

    public string? ExplicitArchitecture { get; init; }
}

public sealed record FixtureScenario
{
    public string Operation { get; init; } = "new";

    public string? PreviousVersion { get; init; }

    public IReadOnlyList<FixturePreviousInstaller> PreviousInstallers { get; init; } = [];

    public FixtureLocale Locale { get; init; } = new();

    public bool ApproveReview { get; init; } = true;
}

public sealed record FixtureLocale
{
    public string PackageLocale { get; init; } = "en-US";

    public string Publisher { get; init; } = "WinMatsch synthetic fixture";

    public string? PackageName { get; init; }

    public string License { get; init; } = "MIT";

    public string? ShortDescription { get; init; }

    public string? ReleaseNotes { get; init; }

    public string? ReleaseNotesUrl { get; init; }
}

public sealed record FixturePreviousInstaller
{
    public required string AssetFileName { get; init; }

    public required string Architecture { get; init; }

    public required string InstallerType { get; init; }

    public string? NestedInstallerType { get; init; }

    public string? Scope { get; init; }

    public string? CustomSwitch { get; init; }

    public string? DisplayName { get; init; }

    public string? DisplayVersion { get; init; }

    public string? ProductCode { get; init; }

    public IReadOnlyList<string> NestedInstallerFiles { get; init; } = [];

    public IReadOnlyList<string> PackageDependencies { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(FixtureDescriptor))]
internal sealed partial class FixtureJsonContext : JsonSerializerContext;
