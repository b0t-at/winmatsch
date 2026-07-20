using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0004ScrubEmptyStringsTests
{
    private static readonly ScrubEmptyStringsRule _rule = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Whitespace_only_strings_become_null(string value)
    {
        Installer a = TestManifests.CreateInstaller();
        a.ProductCode = value;
        PackageManifests manifests = TestManifests.Create(a);
        manifests.DefaultLocale.Description = value;

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(a.ProductCode);
        Assert.Null(manifests.DefaultLocale.Description);
    }

    [Fact]
    public void Non_empty_values_are_untouched()
    {
        Installer a = TestManifests.CreateInstaller();
        a.ProductCode = "{11111111-2222-3333-4444-555555555555}";
        a.Commands = ["app"];
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal("{11111111-2222-3333-4444-555555555555}", a.ProductCode);
        Assert.Equal("app", Assert.Single(a.Commands!));
        Assert.Equal("A test app.", manifests.DefaultLocale.ShortDescription);
    }

    [Fact]
    public void Empty_lists_are_dropped_and_empty_items_removed_first()
    {
        Installer a = TestManifests.CreateInstaller();
        a.Commands = ["", "  "];
        a.Protocols = [];
        a.InstallModes = [];
        PackageManifests manifests = TestManifests.Create(a);
        manifests.DefaultLocale.Tags = ["good", ""];

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(a.Commands);
        Assert.Null(a.Protocols);
        Assert.Null(a.InstallModes);
        Assert.Equal("good", Assert.Single(manifests.DefaultLocale.Tags!));
    }

    [Fact]
    public void Installer_switches_that_become_empty_are_removed()
    {
        Installer a = TestManifests.CreateInstaller();
        a.InstallerSwitches = new InstallerSwitches { Silent = "  ", Custom = "" };
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(a.InstallerSwitches);
    }

    [Fact]
    public void Empty_arp_entries_and_installation_metadata_are_pruned()
    {
        Installer a = TestManifests.CreateInstaller();
        a.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayName = " " }, new AppsAndFeaturesEntry()];
        a.InstallationMetadata = new InstallationMetadata { DefaultInstallLocation = "", Files = [] };
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(a.AppsAndFeaturesEntries);
        Assert.Null(a.InstallationMetadata);
    }

    [Fact]
    public void Empty_dependencies_and_markets_are_pruned()
    {
        Installer a = TestManifests.CreateInstaller();
        a.Dependencies = new Dependencies { WindowsFeatures = [], PackageDependencies = [new PackageDependency()] };
        a.Markets = new Markets { AllowedMarkets = [""] };
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(a.Dependencies);
        Assert.Null(a.Markets);
    }

    [Fact]
    public void Locale_sub_objects_are_pruned()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.Documentations = [new Documentation { DocumentLabel = " " }];
        manifests.DefaultLocale.Agreements = [new PackageAgreement { Agreement = "" }];
        manifests.DefaultLocale.Icons = [new Icon { IconUrl = "  " }];
        manifests.DefaultLocale.Moniker = " ";

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(manifests.DefaultLocale.Documentations);
        Assert.Null(manifests.DefaultLocale.Agreements);
        Assert.Null(manifests.DefaultLocale.Icons);
        Assert.Null(manifests.DefaultLocale.Moniker);
    }

    [Fact]
    public void Trace_reports_the_number_of_scrubbed_values()
    {
        Installer a = TestManifests.CreateInstaller();
        a.ProductCode = "";
        PackageManifests manifests = TestManifests.Create(a);
        ManifestContext context = TestManifests.CreateContext(manifests, explain: true);

        _rule.Apply(context);

        RuleTraceEntry entry = Assert.Single(context.Trace);
        Assert.Equal(RuleIds.ScrubEmptyStrings, entry.RuleId);
        Assert.Contains("1", entry.Message, StringComparison.Ordinal);
    }
}
