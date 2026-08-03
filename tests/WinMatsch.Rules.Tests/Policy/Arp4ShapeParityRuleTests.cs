using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Arp4ShapeParityRuleTests
{
    private static Installer WithEntry(AppsAndFeaturesEntry? entry, string url = "https://example.com/app-x64.msi")
    {
        Installer installer = TestManifests.CreateInstaller(url: url);
        if (entry is not null)
        {
            installer.AppsAndFeaturesEntries = [entry];
        }

        return installer;
    }

    [Fact]
    public void Removed_arp_entries_produce_a_finding()
    {
        // Motivating regression: "removes Apps and Features entries present in previous versions" (IsoBuster #156670).
        PackageManifests current = TestManifests.Create(WithEntry(null));
        PackageManifests previous = PolicyTestSupport.CreatePrevious(
            "1.0.0", WithEntry(new AppsAndFeaturesEntry { DisplayName = "IsoBuster", DisplayVersion = "1.0.0" }));
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Arp4ShapeParityRule().Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleCatalogueIds.Arp4, finding.RuleId);
        Assert.Contains("shape changed", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Added_arp_keys_produce_a_finding()
    {
        // Motivating regression: "adds Apps and Features entries that aren't present..." (GitHubDesktop.Beta #156239).
        PackageManifests current = TestManifests.Create(
            WithEntry(new AppsAndFeaturesEntry { DisplayName = "App", DisplayVersion = "2.0.0", Publisher = "Pub" }));
        PackageManifests previous = PolicyTestSupport.CreatePrevious(
            "1.0.0", WithEntry(new AppsAndFeaturesEntry { DisplayName = "App", DisplayVersion = "1.0.0" }));
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Arp4ShapeParityRule().Apply(context);

        Assert.Single(context.Findings);
    }

    [Fact]
    public void Identical_shape_with_different_values_is_fine()
    {
        // Nonmatching control: same key-set, new values.
        PackageManifests current = TestManifests.Create(
            WithEntry(new AppsAndFeaturesEntry { DisplayName = "App 2.0.0", DisplayVersion = "2.0.0" }));
        PackageManifests previous = PolicyTestSupport.CreatePrevious(
            "1.0.0", WithEntry(new AppsAndFeaturesEntry { DisplayName = "App 1.0.0", DisplayVersion = "1.0.0" }));
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Arp4ShapeParityRule().Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Installer_reordering_is_not_a_shape_change()
    {
        PackageManifests current = TestManifests.Create(
            WithEntry(new AppsAndFeaturesEntry { DisplayVersion = "2.0" }, "https://example.com/b.msi"),
            WithEntry(new AppsAndFeaturesEntry { DisplayName = "App" }, "https://example.com/a.msi"));
        PackageManifests previous = PolicyTestSupport.CreatePrevious(
            "1.0.0",
            WithEntry(new AppsAndFeaturesEntry { DisplayName = "App" }, "https://example.com/a.msi"),
            WithEntry(new AppsAndFeaturesEntry { DisplayVersion = "1.0" }, "https://example.com/b.msi"));
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Arp4ShapeParityRule().Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Explicit_override_annotation_allows_the_change()
    {
        PackageManifests current = TestManifests.Create(WithEntry(null));
        PackageManifests previous = PolicyTestSupport.CreatePrevious(
            "1.0.0", WithEntry(new AppsAndFeaturesEntry { DisplayVersion = "1.0.0" }));
        var overrides = new OverridePackSet(
        [
            new OverridePack
            {
                PackageIdentifier = new PackageIdentifier("Test.App"),
                Policies = [new PolicyAnnotation { Id = "ARP-4", Annotation = "publisher dropped ARP data intentionally" }],
            },
        ]);
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Arp4ShapeParityRule(overrides).Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void New_packages_without_previous_are_skipped()
    {
        PackageManifests current = TestManifests.Create(
            WithEntry(new AppsAndFeaturesEntry { DisplayName = "App" }));
        ManifestContext context = TestManifests.CreateContext(current);

        new Arp4ShapeParityRule().Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Rule_never_mutates_the_manifests()
    {
        PackageManifests current = TestManifests.Create(WithEntry(null));
        PackageManifests previous = PolicyTestSupport.CreatePrevious(
            "1.0.0", WithEntry(new AppsAndFeaturesEntry { DisplayVersion = "1.0.0" }));

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Arp4ShapeParityRule(), current, RuleMode.Apply, previous);

        Assert.Empty(context.Changes);
        Assert.Single(context.Findings);
    }
}
