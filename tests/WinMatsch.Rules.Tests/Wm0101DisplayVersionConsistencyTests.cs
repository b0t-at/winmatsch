using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0101DisplayVersionConsistencyTests
{
    private static readonly DisplayVersionConsistencyRule _rule = new();

    private static Installer WithDisplayVersion(Architecture architecture, string? displayVersion, string url)
    {
        Installer installer = TestManifests.CreateInstaller(architecture, url: url);
        if (displayVersion is not null)
        {
            installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayVersion = displayVersion }];
        }

        return installer;
    }

    [Fact]
    public void Disagreeing_values_produce_a_warning()
    {
        PackageManifests manifests = TestManifests.Create(
            WithDisplayVersion(Architecture.X64, "1.2.3000", "https://example.com/a.msi"),
            WithDisplayVersion(Architecture.X86, "1.2.4000", "https://example.com/b.msi"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings, f => f.Severity == RuleSeverity.Warning);
        Assert.Equal(RuleIds.DisplayVersionConsistency, finding.RuleId);
        Assert.Contains("disagree", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_presence_produces_a_warning()
    {
        PackageManifests manifests = TestManifests.Create(
            WithDisplayVersion(Architecture.X64, "1.2.3000", "https://example.com/a.msi"),
            WithDisplayVersion(Architecture.X86, null, "https://example.com/b.msi"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleSeverity.Warning, finding.Severity);
        Assert.Contains("1 of 2", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_equal_to_the_package_version_produces_an_info_finding()
    {
        PackageManifests manifests = TestManifests.Create(
            WithDisplayVersion(Architecture.X64, TestManifests.DefaultVersion, "https://example.com/a.msi"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleSeverity.Info, finding.Severity);
        Assert.Contains("redundant", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Consistent_values_produce_no_findings()
    {
        PackageManifests manifests = TestManifests.Create(
            WithDisplayVersion(Architecture.X64, "1.2.3000", "https://example.com/a.msi"),
            WithDisplayVersion(Architecture.X86, "1.2.3000", "https://example.com/b.msi"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Root_level_entries_apply_to_every_installer()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(Architecture.X64, url: "https://example.com/a.msi"),
            TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/b.msi"));
        manifests.Installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayVersion = "1.2.3000" }];
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void No_arp_entries_produce_no_findings()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }
}
