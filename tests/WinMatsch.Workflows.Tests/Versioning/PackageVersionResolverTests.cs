using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Versioning;
using Xunit;

namespace WinMatsch.Workflows.Tests.Versioning;

public sealed class PackageVersionResolverTests
{
    [Fact]
    public void Uses_required_precedence_and_records_all_candidates()
    {
        PackageIdentifier package = new("Vendor.Product");
        DiscoveredAsset asset = CreateAsset(
            "v2.0.0",
            "https://example.test/Product-1.5.0-x64.exe",
            productVersion: "1.9.0",
            trustworthy: true);

        PackageVersionResolution result = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = package,
            ExplicitPackageVersion = "3.0.0",
            Assets = [asset],
        });

        Assert.Equal("3.0.0", result.Version?.Value);
        Assert.Equal(PackageVersionSource.PackageOverride, result.Source);
        Assert.Contains(result.Candidates, candidate => candidate.Source == PackageVersionSource.InstallerProductVersion);
        Assert.Contains(result.Candidates, candidate => candidate.Source == PackageVersionSource.ReleaseTag);
        Assert.Contains(result.Candidates, candidate => candidate.Source == PackageVersionSource.UrlToken);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("Product-v1.2.3", "1.2.3")]
    [InlineData("Vendor.Product/1.2.3", "1.2.3")]
    [InlineData("release-1.2.3", "1.2.3")]
    public void Normalizes_standard_release_tag_prefixes(string tag, string expected)
    {
        Assert.Equal(expected, PackageVersionResolver.NormalizeReleaseTag(tag, new("Vendor.Product")));
    }

    [Fact]
    public void Conflicting_trustworthy_product_versions_are_ambiguous()
    {
        PackageVersionResolution result = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = new("Vendor.Product"),
            Assets =
            [
                CreateAsset("v3.0.0", "https://example.test/Product-3.0.0-x64.exe", "3.0.0", true),
                CreateAsset("v3.0.0", "https://example.test/Product-3.0.0-arm64.exe", "3.0.1", true),
            ],
        });

        Assert.True(result.IsAmbiguous);
        Assert.Null(result.Version);
        Assert.Equal(PackageVersionSource.InstallerProductVersion, result.Source);
    }

    [Fact]
    public void Override_pack_can_select_release_tag_over_product_version()
    {
        PackageIdentifier package = new("Vendor.Product");
        var packs = new OverridePackSet(
        [
            new OverridePack
            {
                PackageIdentifier = package,
                VersionSource = "release-tag",
            },
        ]);

        PackageVersionResolution result = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = package,
            OverridePacks = packs,
            Assets = [CreateAsset("Product-v2.0.0", "https://example.test/Product-2.0.0.exe", "1.0.0", true)],
        });

        Assert.Equal("2.0.0", result.Version?.Value);
        Assert.Equal(PackageVersionSource.ReleaseTag, result.Source);
    }

    [Fact]
    public void Url_architecture_suffix_is_not_part_of_version()
    {
        Uri uri = new("https://example.test/ExifTool_install_12.87_64.exe");

        Assert.Equal("12.87", PackageVersionResolver.ExtractUrlVersion(uri));
    }

    private static DiscoveredAsset CreateAsset(
        string tag,
        string url,
        string? productVersion = null,
        bool trustworthy = false)
        => new()
        {
            ReleaseId = 1,
            ReleaseTag = tag,
            ReleaseName = tag,
            ReleaseUri = new("https://example.test/releases/1"),
            IsPrerelease = false,
            ReleasePublishedAt = DateTimeOffset.UnixEpoch,
            AssetId = 1,
            AssetName = Path.GetFileName(new Uri(url).AbsolutePath),
            DownloadUri = new(url),
            DeclaredContentType = "application/octet-stream",
            DeclaredSize = 1,
            AssetCreatedAt = DateTimeOffset.UnixEpoch,
            Analysis = new AssetAnalysisEvidence
            {
                Format = DetectedInstallerFormat.GenericInstallerExe,
                ProductVersion = productVersion,
                IsProductVersionTrustworthy = trustworthy,
                InstallerTypes = [InstallerType.Exe],
            },
        };
}
