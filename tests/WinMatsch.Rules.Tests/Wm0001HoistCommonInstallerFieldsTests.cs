using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0001HoistCommonInstallerFieldsTests
{
    private static readonly HoistCommonInstallerFieldsRule _rule = new();

    [Fact]
    public void Hoists_scalar_field_shared_by_all_installers()
    {
        Installer a = TestManifests.CreateInstaller(Architecture.X64);
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        a.Scope = Scope.Machine;
        b.Scope = Scope.Machine;
        PackageManifests manifests = TestManifests.Create(a, b);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(Scope.Machine, manifests.Installer.Scope);
        Assert.Null(a.Scope);
        Assert.Null(b.Scope);
    }

    [Fact]
    public void Hoists_list_field_when_contents_are_equal_across_different_instances()
    {
        Installer a = TestManifests.CreateInstaller();
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        a.Commands = ["app", "app-cli"];
        b.Commands = ["app", "app-cli"];
        PackageManifests manifests = TestManifests.Create(a, b);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(2, manifests.Installer.Commands?.Count);
        Assert.Equal("app", manifests.Installer.Commands?[0]);
        Assert.Equal("app-cli", manifests.Installer.Commands?[1]);
        Assert.Null(a.Commands);
        Assert.Null(b.Commands);
    }

    [Fact]
    public void Does_not_hoist_when_values_differ()
    {
        Installer a = TestManifests.CreateInstaller();
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        a.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { ProductCode = "{AAAAAAAA-0000-0000-0000-000000000000}" }];
        b.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { ProductCode = "{BBBBBBBB-0000-0000-0000-000000000000}" }];
        PackageManifests manifests = TestManifests.Create(a, b);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(manifests.Installer.AppsAndFeaturesEntries);
        Assert.NotNull(a.AppsAndFeaturesEntries);
        Assert.NotNull(b.AppsAndFeaturesEntries);
    }

    [Fact]
    public void Does_not_hoist_when_one_installer_lacks_the_value()
    {
        Installer a = TestManifests.CreateInstaller();
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        a.UpgradeBehavior = UpgradeBehavior.Install;
        PackageManifests manifests = TestManifests.Create(a, b);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(manifests.Installer.UpgradeBehavior);
        Assert.Equal(UpgradeBehavior.Install, a.UpgradeBehavior);
    }

    [Fact]
    public void Hoists_complex_installer_switches_shared_by_all_installers()
    {
        Installer a = TestManifests.CreateInstaller();
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        a.InstallerSwitches = new InstallerSwitches { Silent = "/S" };
        b.InstallerSwitches = new InstallerSwitches { Silent = "/S" };
        PackageManifests manifests = TestManifests.Create(a, b);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal("/S", manifests.Installer.InstallerSwitches?.Silent);
        Assert.Null(a.InstallerSwitches);
        Assert.Null(b.InstallerSwitches);
    }

    [Fact]
    public void Clears_per_installer_copies_that_duplicate_an_existing_root_value()
    {
        Installer a = TestManifests.CreateInstaller();
        a.InstallModes = [InstallMode.Silent];
        PackageManifests manifests = TestManifests.Create(a);
        manifests.Installer.InstallModes = [InstallMode.Silent];

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(InstallMode.Silent, Assert.Single(manifests.Installer.InstallModes!));
        Assert.Null(a.InstallModes);
    }

    [Fact]
    public void Leaves_conflicting_root_value_alone()
    {
        Installer a = TestManifests.CreateInstaller();
        a.Scope = Scope.User;
        PackageManifests manifests = TestManifests.Create(a);
        manifests.Installer.Scope = Scope.Machine;

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(Scope.Machine, manifests.Installer.Scope);
        Assert.Equal(Scope.User, a.Scope);
    }

    [Fact]
    public void Hoists_everything_for_a_single_installer()
    {
        Installer a = TestManifests.CreateInstaller();
        a.ProductCode = "{11111111-2222-3333-4444-555555555555}";
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(InstallerType.Msi, manifests.Installer.InstallerType);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", manifests.Installer.ProductCode);
        Assert.Null(a.InstallerType);
        Assert.Null(a.ProductCode);
    }

    [Fact]
    public void Traces_hoisted_fields_when_explain_is_enabled()
    {
        Installer a = TestManifests.CreateInstaller();
        PackageManifests manifests = TestManifests.Create(a);
        ManifestContext context = TestManifests.CreateContext(manifests, explain: true);

        _rule.Apply(context);

        RuleTraceEntry entry = Assert.Single(context.Trace, t => t.Message.Contains("InstallerType", StringComparison.Ordinal));
        Assert.Equal(RuleIds.HoistCommonInstallerFields, entry.RuleId);
    }

    [Fact]
    public void No_op_when_there_are_no_installers()
    {
        PackageManifests manifests = TestManifests.Create();
        manifests.Installer.Installers = null;

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(manifests.Installer.InstallerType);
    }
}
