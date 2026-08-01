using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Downloads;
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

    [Fact]
    public void Invalid_explicit_version_does_not_fall_back()
    {
        PackageVersionResolution result = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = new("Vendor.Product"),
            ExplicitPackageVersion = "invalid|version",
            Assets = [CreateAsset("v2.0.0", "https://example.test/Product-2.0.0.exe")],
        });

        Assert.False(result.IsResolved);
        Assert.Equal(PackageVersionSource.PackageOverride, result.Source);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.StartsWith("VERSION_INVALID", StringComparison.Ordinal));
    }

    [Fact]
    public void Calendar_release_tag_is_not_selected_as_version()
    {
        PackageVersionResolution result = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = new("Vendor.Product"),
            Assets = [CreateAsset("release-2025-01-01", "https://example.test/tool.exe")],
        });

        Assert.False(result.IsResolved);
    }

    [Fact]
    public void Platform_version_token_is_not_treated_as_package_version()
    {
        Assert.Null(PackageVersionResolver.ExtractUrlVersion(
            new Uri("https://example.test/tool-windows-10.0-x64.exe")));
    }

    [Fact]
    public void Trustworthy_product_version_may_use_date_format()
    {
        PackageVersionResolution result = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = new("Vendor.Product"),
            Assets =
            [
                CreateAsset(
                    "latest",
                    "https://example.test/tool.exe",
                    productVersion: "2025-01-01",
                    trustworthy: true),
            ],
        });

        Assert.Equal("2025-01-01", result.Version?.Value);
        Assert.Equal(PackageVersionSource.InstallerProductVersion, result.Source);
    }

    [Fact]
    public void Multiple_package_and_dependency_versions_are_ambiguous()
    {
        UrlVersionEvidence evidence = PackageVersionResolver.AnalyzeUrlVersion(
            new Uri("https://example.test/tool-v2.0.0-jre-17.0.exe"));

        Assert.True(evidence.IsAmbiguous);
        Assert.Null(evidence.Version);
        Assert.Collection(
            evidence.Candidates,
            candidate => Assert.Equal("17.0", candidate),
            candidate => Assert.Equal("2.0.0", candidate));
    }

    [Fact]
    public void Nightly_date_tag_is_not_selected_as_release_version()
    {
        PackageVersionResolution result = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = new("Vendor.Product"),
            Assets = [CreateAsset("release-nightly-2025-01-01", "https://example.test/tool.exe")],
        });

        Assert.False(result.IsResolved);
    }

    [Fact]
    public void Equivalent_version_spellings_are_not_ambiguous()
    {
        PackageVersionResolution result = PackageVersionResolver.Resolve(new()
        {
            PackageIdentifier = new("Vendor.Product"),
            Assets =
            [
                CreateAsset("latest", "https://example.test/tool-x64.exe", "1.0", true),
                CreateAsset("latest", "https://example.test/tool-arm64.exe", "1.0.0", true),
            ],
        });

        Assert.False(result.IsAmbiguous);
        Assert.Equal("1.0", result.Version?.Value);
    }

    private static DiscoveredAsset CreateAsset(
        string tag,
        string url,
        string? productVersion = null,
        bool trustworthy = false)
    {
        var uri = new Uri(url);
        var identity = new DownloadContentIdentity(
            new Sha256Hash("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"),
            1);
        return new()
        {
            ReleaseId = 1,
            ReleaseTag = tag,
            ReleaseName = tag,
            ReleaseUri = new("https://example.test/releases/1"),
            IsPrerelease = false,
            ReleasePublishedAt = DateTimeOffset.UnixEpoch,
            AssetId = 1,
            AssetName = Path.GetFileName(uri.AbsolutePath),
            DownloadUri = uri,
            DeclaredContentType = "application/octet-stream",
            DeclaredSize = 1,
            AssetCreatedAt = DateTimeOffset.UnixEpoch,
            Content = new(
                identity,
                uri.AbsoluteUri,
                uri.AbsoluteUri,
                "application/octet-stream",
                DateTimeOffset.UnixEpoch),
            Analysis = new AssetAnalysisEvidence
            {
                Format = DetectedInstallerFormat.GenericInstallerExe,
                AnalyzedContentIdentity = identity,
                AnalyzedUrl = uri.AbsoluteUri,
                ProductVersion = productVersion,
                IsProductVersionTrustworthy = trustworthy,
                InstallerShapes = [new() { InstallerType = InstallerType.Exe }],
            },
        };
    }
}
