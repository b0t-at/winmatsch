using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Scope1UserMachineTwinRuleTests
{
    private static readonly Scope1UserMachineTwinRule _rule = new();

    private const string SharedUrl = "https://example.com/pandoc-x64.msi";

    private static Installer Twin(string custom)
    {
        Installer installer = TestManifests.CreateInstaller(url: SharedUrl);
        installer.InstallerSwitches = new InstallerSwitches { Custom = custom };
        return installer;
    }

    [Fact]
    public void Allusers_twins_get_per_installer_scope_and_root_stays_free()
    {
        // Motivating regression: the Pandoc saga — root Scope with ALLUSERS twin entries (#210752).
        PackageManifests manifests = TestManifests.Create(Twin("ALLUSERS=1"), Twin("/CURRENTUSER"));
        manifests.Installer.Scope = Scope.User;
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(Scope.Machine, manifests.Installer.Installers![0].Scope);
        Assert.Equal(Scope.User, manifests.Installer.Installers[1].Scope);
        Assert.Null(manifests.Installer.Scope);
    }

    [Fact]
    public void Msiinstallperuser_and_allusers_pair_is_recognized()
    {
        PackageManifests manifests = TestManifests.Create(Twin("MSIINSTALLPERUSER=1"), Twin("ALLUSERS=1"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(Scope.User, manifests.Installer.Installers![0].Scope);
        Assert.Equal(Scope.Machine, manifests.Installer.Installers[1].Scope);
    }

    [Fact]
    public void Entries_with_different_urls_are_not_twins()
    {
        // Nonmatching control.
        Installer a = TestManifests.CreateInstaller(url: "https://example.com/a.msi");
        a.InstallerSwitches = new InstallerSwitches { Custom = "ALLUSERS=1" };
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/b.msi");
        b.InstallerSwitches = new InstallerSwitches { Custom = "/CURRENTUSER" };
        PackageManifests manifests = TestManifests.Create(a, b);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(manifests.Installer.Installers![0].Scope);
        Assert.Null(manifests.Installer.Installers[1].Scope);
    }

    [Fact]
    public void Ambiguous_switches_do_not_assign_scope()
    {
        // Conservative behavior: a value carrying both token classes stays untouched.
        PackageManifests manifests = TestManifests.Create(
            Twin("ALLUSERS=1 /CURRENTUSER"), Twin("/CURRENTUSER"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(manifests.Installer.Installers![0].Scope);
        Assert.Null(manifests.Installer.Installers[1].Scope);
    }

    [Fact]
    public void Existing_explicit_scope_is_never_overwritten()
    {
        Installer machineTwin = Twin("ALLUSERS=1");
        machineTwin.Scope = Scope.User; // explicitly (mis)declared
        PackageManifests manifests = TestManifests.Create(machineTwin, Twin("/CURRENTUSER"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(Scope.User, manifests.Installer.Installers![0].Scope);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("not changed", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Token_matching_requires_boundaries()
    {
        // "ALLUSERS=12" must not read as the machine token ALLUSERS=1.
        PackageManifests manifests = TestManifests.Create(Twin("ALLUSERS=12"), Twin("/CURRENTUSER"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(manifests.Installer.Installers![0].Scope);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = TestManifests.Create(Twin("ALLUSERS=1"), Twin("/CURRENTUSER"));

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.LogOnly);

        Assert.Null(manifests.Installer.Installers![0].Scope);
        Assert.Null(manifests.Installer.Installers[1].Scope);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = TestManifests.Create(Twin("ALLUSERS=1"), Twin("/CURRENTUSER"));

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.Disabled);

        Assert.Null(manifests.Installer.Installers![0].Scope);
        Assert.Empty(context.Changes);
    }
}
