using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Scope3SwitchHygieneRuleTests
{
    private static readonly Scope3SwitchHygieneRule _rule = new();

    [Fact]
    public void Blank_switch_values_are_dropped()
    {
        // Motivating regression: wire shipped SilentWithProgress: ' ' (#194941).
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSwitches = new InstallerSwitches { SilentWithProgress = " ", Silent = "/S" };
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(installer.InstallerSwitches.SilentWithProgress);
        Assert.Equal("/S", installer.InstallerSwitches.Silent);
    }

    [Fact]
    public void Values_are_trimmed()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSwitches = new InstallerSwitches { Custom = "  /quiet  " };
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal("/quiet", installer.InstallerSwitches.Custom);
    }

    [Fact]
    public void Fully_blank_switch_mappings_are_removed()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSwitches = new InstallerSwitches { Silent = "   " };
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(installer.InstallerSwitches);
    }

    [Fact]
    public void Root_switches_are_cleaned_too()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.InstallerSwitches = new InstallerSwitches { Silent = " " };
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(manifests.Installer.InstallerSwitches);
    }

    [Fact]
    public void Clean_switches_are_untouched()
    {
        // Nonmatching control.
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSwitches = new InstallerSwitches { Silent = "/S", Custom = "--silent" };
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal("/S", installer.InstallerSwitches.Silent);
        Assert.Equal("--silent", installer.InstallerSwitches.Custom);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Switches_carried_across_installer_family_change_are_flagged()
    {
        // Motivating regression: Fork switched silent args /s -> --silent between versions (#233659).
        Installer current = TestManifests.CreateInstaller(installerType: InstallerType.Nullsoft, url: "https://example.com/fork-2.exe");
        current.InstallerSwitches = new InstallerSwitches { Silent = "/s" };
        PackageManifests manifests = TestManifests.Create(current);

        Installer previousInstaller = TestManifests.CreateInstaller(installerType: InstallerType.Inno, url: "https://example.com/fork-1.exe");
        previousInstaller.InstallerSwitches = new InstallerSwitches { Silent = "/s" };
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", previousInstaller);

        ManifestContext context = TestManifests.CreateContext(manifests, previous: previous);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleCatalogueIds.Scope3, finding.RuleId);
        Assert.Contains("carried over verbatim", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Changed_switches_after_family_change_are_not_flagged()
    {
        Installer current = TestManifests.CreateInstaller(installerType: InstallerType.Nullsoft, url: "https://example.com/fork-2.exe");
        current.InstallerSwitches = new InstallerSwitches { Silent = "--silent" };
        PackageManifests manifests = TestManifests.Create(current);

        Installer previousInstaller = TestManifests.CreateInstaller(installerType: InstallerType.Inno, url: "https://example.com/fork-1.exe");
        previousInstaller.InstallerSwitches = new InstallerSwitches { Silent = "/s" };
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", previousInstaller);

        ManifestContext context = TestManifests.CreateContext(manifests, previous: previous);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Same_family_carry_is_not_flagged()
    {
        Installer current = TestManifests.CreateInstaller(installerType: InstallerType.Msi, url: "https://example.com/app-2.msi");
        current.InstallerSwitches = new InstallerSwitches { Silent = "/qn" };
        PackageManifests manifests = TestManifests.Create(current);

        Installer previousInstaller = TestManifests.CreateInstaller(installerType: InstallerType.Msi, url: "https://example.com/app-1.msi");
        previousInstaller.InstallerSwitches = new InstallerSwitches { Silent = "/qn" };
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", previousInstaller);

        ManifestContext context = TestManifests.CreateContext(manifests, previous: previous);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSwitches = new InstallerSwitches { SilentWithProgress = " " };
        PackageManifests manifests = TestManifests.Create(installer);

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.LogOnly);

        Assert.Equal(" ", installer.InstallerSwitches.SilentWithProgress);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSwitches = new InstallerSwitches { SilentWithProgress = " " };
        PackageManifests manifests = TestManifests.Create(installer);

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.Disabled);

        Assert.Equal(" ", installer.InstallerSwitches.SilentWithProgress);
        Assert.Empty(context.Changes);
    }
}
