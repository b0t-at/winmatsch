using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Testing.Fixtures;
using WinMatsch.Testing.Infrastructure;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Versioning;
using Xunit;
using Xunit.Sdk;

namespace WinMatsch.E2E.Tests;

public sealed class RegressionDescriptorE2ETests
{
    private static readonly string[] _expectedIds =
    [
        "buf",
        "clouddrive2",
        "curl",
        "electron",
        "exiftool",
        "keeper-commander",
        "mise",
        "notesnook",
        "pandoc",
        "sonarr",
        "super-productivity",
        "surrealdb",
        "uhk-agent",
    ];

    [Fact]
    public void Complete_metadata_corpus_drives_mapping_snapshots_and_remains_non_applicable()
    {
        Assert.Equal(
            _expectedIds,
            FixtureCatalog.All.Select(static fixture => fixture.Descriptor.Id));
        foreach (string id in _expectedIds)
        {
            RegressionFixture fixture = FixtureCatalog.Get(id);
            ImmutableArray<DiscoveredAsset> assets =
            [
                .. fixture.Descriptor.Assets.Select((asset, index) =>
                    CreateAsset(fixture, asset, index)),
            ];
            AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(new AssetMappingRequest
            {
                PackageIdentifier = new PackageIdentifier(fixture.Descriptor.Package.Identifier),
                Version = new PackageVersionResolution(
                    new PackageVersion(fixture.Descriptor.Package.Version),
                    PackageVersionSource.PackageOverride,
                    EvidenceConfidence.Explicit,
                    false,
                    [],
                    []),
                Assets = assets,
            });

            Assert.False(plan.CanApply);
            Assert.Contains(
                plan.Diagnostics,
                static diagnostic => diagnostic.Code == "ANALYSIS_METADATA_ONLY");
            Assert.NotEmpty(fixture.Descriptor.Regression.RuleIds);
            Assert.NotEmpty(fixture.Expected.Installers);
            Assert.All(
                fixture.Expected.Installers,
                installer => Assert.Contains(
                    fixture.Descriptor.Assets,
                    asset => asset.IncludeInExpectedManifest
                        && asset.Url == installer.InstallerUrl
                        && asset.Sha256 == installer.InstallerSha256));
        }

        Assert.Equal(
            ["user", "machine"],
            FixtureCatalog.Get("pandoc").Expected.Installers.Select(static item => item.Scope));
        Assert.Single(
            FixtureCatalog.Get("surrealdb").Expected.Installers
                .Select(static item => item.InstallerUrl)
                .Distinct());
        Assert.Contains(
            FixtureCatalog.Get("uhk-agent").Descriptor.Assets,
            static asset => !asset.IncludeInExpectedManifest);
        Assert.All(
            FixtureCatalog.Get("clouddrive2").Expected.AppsAndFeaturesEntries,
            static entry => Assert.Null(entry.DisplayVersion));
        Assert.All(
            FixtureCatalog.Get("sonarr").Expected.AppsAndFeaturesEntries,
            static entry => Assert.Null(entry.DisplayVersion));
    }

    [Fact]
    public async Task Acquisition_is_checksum_pinned_and_hermetic_by_default()
    {
        var handler = new StubHttpMessageHandler(
            _ => throw new InvalidOperationException("Default regression tests must not use network."));
        var fileSystem = new InMemoryFileSystem();
        var acquirer = new FixtureAcquirer(new HttpClient(handler), fileSystem);

        foreach (FixtureAsset asset in FixtureCatalog.All.SelectMany(static fixture => fixture.Descriptor.Assets))
        {
            FixtureAcquisitionResult result = await acquirer.AcquireAsync(
                asset,
                new FixtureAcquisitionOptions { CacheDirectory = "C:\\bounded-fixture-cache" });
            Assert.Equal(FixtureAcquisitionStatus.Unavailable, result.Status);
            Assert.Contains("network acquisition is disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(handler.Requests);
        Assert.Empty(fileSystem.Paths);
    }

    [EnvironmentFact("WINMATSCH_E2E_ACQUIRE_FIXTURES", "1")]
    public async Task Opt_in_acquisition_remains_checksum_pinned_and_skips_clearly_when_offline()
    {
        using var temporary = new TemporaryDirectory();
        var acquirer = new FixtureAcquirer(
            new HttpClient(),
            PhysicalTestFileSystem.Instance);
        FixtureAsset asset = FixtureCatalog.Get("buf").Descriptor.Assets[0];

        FixtureAcquisitionResult result = await acquirer.AcquireAsync(
            asset,
            new FixtureAcquisitionOptions
            {
                CacheDirectory = temporary.Path,
                AllowNetwork = true,
            });

        if (!result.IsAvailable)
        {
            bool clearlyOffline =
                result.Message.Contains(
                    "network acquisition is unavailable",
                    StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
            Assert.True(clearlyOffline, result.Message);
            throw SkipException.ForSkip(
                $"Checksum-pinned fixture acquisition skipped: {result.Message}");
        }

        Assert.NotNull(result.Path);
        Assert.True(File.Exists(result.Path));
    }

    private static DiscoveredAsset CreateAsset(
        RegressionFixture fixture,
        FixtureAsset asset,
        int index)
    {
        InstallerType installerType = ParseInstallerType(asset.ExpectedInstallerType);
        var identity = new DownloadContentIdentity(new Sha256Hash(asset.Sha256), 1);
        var content = new AssetContentEvidence(
            identity,
            asset.Url.AbsoluteUri,
            asset.Url.AbsoluteUri,
            "application/octet-stream",
            fixture.Descriptor.Provenance.ObservedAt);
        return new DiscoveredAsset
        {
            ReleaseId = 1,
            ReleaseTag = $"v{fixture.Descriptor.Package.Version}",
            ReleaseName = fixture.Descriptor.Package.Version,
            ReleaseUri = new Uri("https://fixtures.invalid/release"),
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
                    new AnalyzedInstallerShape
                    {
                        Architecture = asset.IncludeInExpectedManifest
                            && fixture.Descriptor.Id != "super-productivity"
                            ? ParseArchitecture(asset.ExpectedArchitecture)
                            : null,
                        InstallerType = installerType,
                    },
                ],
            },
        };
    }

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
