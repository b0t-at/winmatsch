using System.Text;
using WinMatsch.Core;
using WinMatsch.Testing.Fixtures;
using Xunit;

namespace WinMatsch.Testing.Tests;

public sealed class FixtureCatalogTests
{
    private static readonly string[] _expectedFixtureIds =
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
    public void Catalog_contains_complete_descriptors_and_full_yaml_golden_sets()
    {
        Assert.Equal(
            _expectedFixtureIds,
            FixtureCatalog.All.Select(static fixture => fixture.Descriptor.Id));
        foreach (RegressionFixture fixture in FixtureCatalog.All)
        {
            Assert.Equal(3, fixture.ExpectedManifests.Count);
            string yaml = string.Join(
                "\n",
                fixture.ExpectedManifests.Values.Select(Encoding.UTF8.GetString));
            Assert.Contains(
                $"PackageIdentifier: {fixture.Descriptor.Package.Identifier}",
                yaml,
                StringComparison.Ordinal);
            Assert.True(
                yaml.Contains(
                    $"PackageVersion: {fixture.Descriptor.Package.Version}",
                    StringComparison.Ordinal)
                || yaml.Contains(
                    $"PackageVersion: \"{fixture.Descriptor.Package.Version}\"",
                    StringComparison.Ordinal));
            Assert.Contains("ManifestType: installer", yaml, StringComparison.Ordinal);
            Assert.Contains("ManifestType: defaultLocale", yaml, StringComparison.Ordinal);
            Assert.Contains("ManifestType: version", yaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Descriptor_semantics_are_shared_and_ambiguity_is_explicit()
    {
        Assert.Equal(Architecture.X64, FixtureSemantics.ParseArchitecture("x64"));
        Assert.Equal(InstallerType.Nullsoft, FixtureSemantics.ParseInstallerType("nullsoft"));
        Assert.Equal(Scope.Machine, FixtureSemantics.ParseScope("machine"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FixtureSemantics.ParseArchitecture("implicit-guess"));

        FixtureAsset tokenless = FixtureCatalog.Get("super-productivity").Descriptor.Assets
            .Single(static asset => asset.FileName == "Super-Productivity-Setup.exe");
        Assert.Equal("x86", tokenless.ExpectedArchitecture);
        Assert.Null(tokenless.Synthetic.ExplicitArchitecture);

        FixtureAsset ambiguous = FixtureCatalog.Get("uhk-agent").Descriptor.Assets
            .Single(static asset => asset.FileName.EndsWith("-win.exe", StringComparison.Ordinal));
        Assert.Equal(["x86", "x64"], ambiguous.Synthetic.PayloadArchitectures);
        Assert.Equal("neutral", ambiguous.Synthetic.ExplicitArchitecture);
    }

    [Fact]
    public void Provenance_and_upstream_acquisition_are_https_and_commit_or_checksum_pinned()
    {
        foreach (RegressionFixture fixture in FixtureCatalog.All)
        {
            FixtureProvenance provenance = fixture.Descriptor.Provenance;
            Assert.Equal(Uri.UriSchemeHttps, provenance.ManifestUrl.Scheme);
            Assert.Contains(provenance.HeadCommit, provenance.ManifestUrl.AbsoluteUri);
            Assert.Equal(40, provenance.HeadCommit.Length);
            Assert.Equal(40, provenance.MergeCommit.Length);
            Assert.NotEqual(default, provenance.ObservedAt);
            Assert.All(
                fixture.Descriptor.Assets,
                static asset =>
                {
                    Assert.Equal(Uri.UriSchemeHttps, asset.Url.Scheme);
                    Assert.Equal(64, asset.UpstreamSha256.Length);
                    Assert.Equal(64, asset.SyntheticSha256.Length);
                });
        }
    }
}
