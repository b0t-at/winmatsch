using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Arp2DisplayVersionRedundancyRuleTests
{
    private static PackageManifests CreateWithDisplayVersion(string displayVersion)
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayVersion = displayVersion }];
        return TestManifests.Create(installer);
    }

    [Fact]
    public void Display_version_equal_to_package_version_is_removed()
    {
        // Motivating regression: redundant DisplayVersion removed by moderators (KONNEKT #193899).
        PackageManifests manifests = CreateWithDisplayVersion(TestManifests.DefaultVersion);
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Arp2DisplayVersionRedundancyRule().Apply(context);

        Assert.Null(manifests.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayVersion);
    }

    [Fact]
    public void Display_version_equivalent_by_winget_ordering_is_removed()
    {
        // KONNEKT: PackageVersion 1.2.3 with DisplayVersion 1.2.3.0 — trailing zero parts are
        // insignificant in WinGet ordering, so the value is redundant.
        PackageManifests manifests = CreateWithDisplayVersion(TestManifests.DefaultVersion + ".0");
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Arp2DisplayVersionRedundancyRule().Apply(context);

        Assert.Null(manifests.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayVersion);
    }

    [Fact]
    public void Overlapping_display_version_is_dropped_with_a_finding()
    {
        // Motivating regression: static "1.0" DisplayVersion overlap killed Sonarr/CloudDrive2 PRs (#267360, #287069).
        PackageManifests manifests = CreateWithDisplayVersion("1.0");
        var rule = new Arp2DisplayVersionRedundancyRule(new PolicyEvidence
        {
            ExistingDisplayVersions = ["1.0"],
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Null(manifests.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayVersion);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleCatalogueIds.Arp2, finding.RuleId);
        Assert.Contains("overlaps", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_index_evidence_no_overlap_guessing_happens()
    {
        // "No index guessing": a static-looking value survives when no evidence was supplied.
        PackageManifests manifests = CreateWithDisplayVersion("1.0");
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Arp2DisplayVersionRedundancyRule().Apply(context);

        Assert.Equal("1.0", manifests.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayVersion);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Distinct_per_release_display_version_is_kept()
    {
        // Nonmatching control: a marketing version differing from PackageVersion stays.
        PackageManifests manifests = CreateWithDisplayVersion("1.2.3000");
        var rule = new Arp2DisplayVersionRedundancyRule(new PolicyEvidence
        {
            ExistingDisplayVersions = ["1.2.2000"],
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal("1.2.3000", manifests.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayVersion);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Root_level_entries_are_processed()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayVersion = TestManifests.DefaultVersion }];
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Arp2DisplayVersionRedundancyRule().Apply(context);

        Assert.Null(manifests.Installer.AppsAndFeaturesEntries[0].DisplayVersion);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = CreateWithDisplayVersion(TestManifests.DefaultVersion);

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Arp2DisplayVersionRedundancyRule(), manifests, RuleMode.LogOnly);

        Assert.Equal(TestManifests.DefaultVersion, manifests.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayVersion);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = CreateWithDisplayVersion(TestManifests.DefaultVersion);

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Arp2DisplayVersionRedundancyRule(), manifests, RuleMode.Disabled);

        Assert.Equal(TestManifests.DefaultVersion, manifests.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayVersion);
        Assert.Empty(context.Changes);
    }
}
