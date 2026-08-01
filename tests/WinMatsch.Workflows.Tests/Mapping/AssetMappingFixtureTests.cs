using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Testing.Fixtures;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Versioning;
using Xunit;

namespace WinMatsch.Workflows.Tests.Mapping;

public sealed class AssetMappingFixtureTests
{
    public static TheoryData<string> RequiredFixtures =>
        new(
            "super-productivity",
            "notesnook",
            "uhk-agent",
            "curl",
            "electron",
            "pandoc",
            "surrealdb",
            "exiftool",
            "keeper-commander",
            "buf");

    [Theory]
    [MemberData(nameof(RequiredFixtures))]
    public void Preserves_expected_fixture_layouts_without_claiming_live_validation(string fixtureId)
    {
        // These published snapshots cover deterministic topology only. MetadataFixture evidence
        // deliberately blocks apply; content-analyzer behavior is covered by hermetic synthetic tests.
        RegressionFixture fixture = FixtureCatalog.Get(fixtureId);
        ImmutableArray<DiscoveredAsset> assets =
        [
            .. fixture.Descriptor.Assets.Select((asset, index) => CreateAsset(fixture, asset, index)),
        ];
        ImmutableArray<PreviousInstallerEntry> previous =
        [
            .. fixture.Expected.Installers.Select((installer, index) => CreatePrevious(fixture, installer, index)),
        ];
        PackageVersionResolution version = ResolvedVersion(fixture.Descriptor.Package.Version);

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(new()
        {
            PackageIdentifier = new(fixture.Descriptor.Package.Identifier),
            Version = version,
            Assets = assets,
            PreviousInstallers = previous,
        });

        foreach (PreviousInstallerEntry expected in previous)
        {
            AssetMappingDecision decision = Assert.Single(
                plan.Decisions,
                decision => decision.PreviousPosition == expected.Position);
            PlannedInstaller actual = Assert.IsType<PlannedInstaller>(decision.Installer);
            Assert.Equal(expected.Url, actual.Url);
            Assert.Equal(expected.Architecture, actual.Architecture);
            Assert.Equal(expected.InstallerType, actual.InstallerType);
            Assert.Equal(expected.NestedInstallerType, actual.NestedInstallerType);
            Assert.Equal(expected.Scope, actual.Scope);
            Assert.Equal(
                expected.NestedInstallerFiles.Select(static item => item.RelativeFilePath),
                actual.NestedInstallerFiles.Select(static item => item.RelativeFilePath));
        }

        FixtureAsset[] intentionallyUnmapped = fixture.Descriptor.Assets
            .Where(static asset => !asset.IncludeInExpectedManifest)
            .ToArray();
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "ANALYSIS_METADATA_ONLY");
        if (intentionallyUnmapped.Length > 0)
        {
            Assert.All(
                intentionallyUnmapped,
                asset => Assert.Contains(
                    plan.UnresolvedQuestions,
                    question => question.AssetUrl == asset.Url.AbsoluteUri));
        }
    }

    [Fact]
    public void Fixture_inputs_are_metadata_evidence_not_live_installer_validation()
    {
        RegressionFixture fixture = FixtureCatalog.Get("notesnook");
        DiscoveredAsset asset = CreateAsset(fixture, fixture.Descriptor.Assets[0], 0);

        Assert.Null(asset.Analysis?.ProductVersion);
        Assert.False(asset.Analysis?.IsProductVersionTrustworthy);
        Assert.Empty(asset.Analysis?.PayloadEvidence ?? []);
        Assert.Equal(AnalysisEvidenceOrigin.MetadataFixture, asset.Analysis?.Origin);
    }

    private static DiscoveredAsset CreateAsset(
        RegressionFixture fixture,
        FixtureAsset asset,
        int index)
    {
        Architecture architecture = ParseArchitecture(asset.ExpectedArchitecture);
        InstallerType installerType = ParseInstallerType(asset.ExpectedInstallerType);
        string[] nestedPaths = fixture.Expected.Installers
            .Where(installer => installer.InstallerUrl == asset.Url)
            .SelectMany(installer => installer.NestedInstallerFiles ?? [])
            .Concat(fixture.Expected.NestedInstallerFiles ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var identity = new DownloadContentIdentity(new Sha256Hash(asset.Sha256), 1);
        var content = new AssetContentEvidence(
            identity,
            asset.Url.AbsoluteUri,
            asset.Url.AbsoluteUri,
            "application/octet-stream",
            fixture.Descriptor.Provenance.ObservedAt);
        return new()
        {
            ReleaseId = 1,
            ReleaseTag = $"v{fixture.Descriptor.Package.Version}",
            ReleaseName = fixture.Descriptor.Package.Version,
            ReleaseUri = new("https://example.test/release"),
            IsPrerelease = false,
            ReleasePublishedAt = fixture.Descriptor.Provenance.ObservedAt,
            AssetId = index + 1,
            AssetName = asset.FileName,
            DownloadUri = asset.Url,
            DeclaredContentType = "application/octet-stream",
            DeclaredSize = 1,
            AssetCreatedAt = fixture.Descriptor.Provenance.ObservedAt,
            Content = content,
            Analysis = new AssetAnalysisEvidence
            {
                Format = FormatFor(installerType),
                AnalyzedContentIdentity = identity,
                AnalyzedUrl = asset.Url.AbsoluteUri,
                Origin = AnalysisEvidenceOrigin.MetadataFixture,
                InstallerShapes =
                [
                    new()
                    {
                        Architecture = asset.IncludeInExpectedManifest
                            && fixture.Descriptor.Id is not "super-productivity"
                            ? architecture
                            : null,
                        InstallerType = installerType,
                        NestedInstallerType = fixture.Expected.NestedInstallerType is null
                            ? null
                            : ParseInstallerType(fixture.Expected.NestedInstallerType),
                        NestedInstallerFiles =
                        [
                            .. nestedPaths.Select(static path => new PlannedNestedInstallerFile(path, null)),
                        ],
                    },
                ],
                ArchiveEntries = [.. nestedPaths],
                NestedInstallerCandidates = [.. nestedPaths],
            },
        };
    }

    private static PreviousInstallerEntry CreatePrevious(
        RegressionFixture fixture,
        ExpectedInstallerSnapshot installer,
        int index)
    {
        string? type = installer.InstallerType ?? fixture.Expected.InstallerType;
        string[] nested = installer.NestedInstallerFiles is { Count: > 0 }
            ? [.. installer.NestedInstallerFiles]
            : [.. fixture.Expected.NestedInstallerFiles ?? []];
        return new()
        {
            Position = index,
            Url = installer.InstallerUrl,
            Sha256 = new(installer.InstallerSha256),
            Architecture = ParseArchitecture(installer.Architecture),
            InstallerType = type is null ? null : ParseInstallerType(type),
            NestedInstallerType = fixture.Expected.NestedInstallerType is null
                ? null
                : ParseInstallerType(fixture.Expected.NestedInstallerType),
            Scope = ParseScope(installer.Scope),
            DisplayVersion = (installer.AppsAndFeaturesEntries ?? [])
                .Select(static entry => entry.DisplayVersion)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            PackageVersion = new(fixture.Descriptor.Package.Version),
            NestedInstallerFiles =
            [
                .. nested.Select(static path => new PlannedNestedInstallerFile(path, null)),
            ],
        };
    }

    private static PackageVersionResolution ResolvedVersion(string value)
        => new(
            new PackageVersion(value),
            PackageVersionSource.PackageOverride,
            EvidenceConfidence.Explicit,
            false,
            [],
            []);

    private static Architecture ParseArchitecture(string value) => value.ToLowerInvariant() switch
    {
        "x86" => Architecture.X86,
        "x64" => Architecture.X64,
        "arm" => Architecture.Arm,
        "arm64" => Architecture.Arm64,
        "neutral" => Architecture.Neutral,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static InstallerType ParseInstallerType(string value) => value.ToLowerInvariant() switch
    {
        "appx" => InstallerType.Appx,
        "burn" => InstallerType.Burn,
        "exe" => InstallerType.Exe,
        "inno" => InstallerType.Inno,
        "msi" => InstallerType.Msi,
        "msix" => InstallerType.Msix,
        "nullsoft" => InstallerType.Nullsoft,
        "portable" => InstallerType.Portable,
        "wix" => InstallerType.Wix,
        "zip" => InstallerType.Zip,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static Scope? ParseScope(string? value) => value?.ToLowerInvariant() switch
    {
        null => null,
        "user" => Scope.User,
        "machine" => Scope.Machine,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static DetectedInstallerFormat FormatFor(InstallerType type) => type switch
    {
        InstallerType.Inno => DetectedInstallerFormat.InnoSetup,
        InstallerType.Nullsoft => DetectedInstallerFormat.Nullsoft,
        InstallerType.Portable => DetectedInstallerFormat.PortableExe,
        InstallerType.Wix => DetectedInstallerFormat.Msi,
        InstallerType.Msi => DetectedInstallerFormat.Msi,
        InstallerType.Zip => DetectedInstallerFormat.Zip,
        _ => DetectedInstallerFormat.GenericInstallerExe,
    };
}
