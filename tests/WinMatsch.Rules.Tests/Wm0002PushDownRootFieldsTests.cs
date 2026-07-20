using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0002PushDownRootFieldsTests
{
    private static readonly PushDownRootFieldsRule _rule = new();

    [Fact]
    public void Pushes_root_value_down_when_an_installer_conflicts()
    {
        Installer a = TestManifests.CreateInstaller();
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        b.Scope = Scope.Machine;
        PackageManifests manifests = TestManifests.Create(a, b);
        manifests.Installer.Scope = Scope.User;

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(manifests.Installer.Scope);
        Assert.Equal(Scope.User, a.Scope);
        Assert.Equal(Scope.Machine, b.Scope);
    }

    [Fact]
    public void Keeps_root_value_when_no_installer_overrides_it()
    {
        Installer a = TestManifests.CreateInstaller();
        PackageManifests manifests = TestManifests.Create(a);
        manifests.Installer.Scope = Scope.Machine;

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(Scope.Machine, manifests.Installer.Scope);
        Assert.Null(a.Scope);
    }

    [Fact]
    public void Clears_redundant_installer_copies_equal_to_the_root_value()
    {
        Installer a = TestManifests.CreateInstaller();
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        a.Scope = Scope.Machine;
        b.Scope = Scope.Machine;
        PackageManifests manifests = TestManifests.Create(a, b);
        manifests.Installer.Scope = Scope.Machine;

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(Scope.Machine, manifests.Installer.Scope);
        Assert.Null(a.Scope);
        Assert.Null(b.Scope);
    }

    [Fact]
    public void Pushed_down_complex_values_are_deep_clones()
    {
        Installer a = TestManifests.CreateInstaller();
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        b.InstallerSwitches = new InstallerSwitches { Silent = "/quiet" };
        PackageManifests manifests = TestManifests.Create(a, b);
        var rootSwitches = new InstallerSwitches { Silent = "/S" };
        manifests.Installer.InstallerSwitches = rootSwitches;

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(manifests.Installer.InstallerSwitches);
        Assert.NotNull(a.InstallerSwitches);
        Assert.NotSame(rootSwitches, a.InstallerSwitches);
        Assert.Equal("/S", a.InstallerSwitches.Silent);
        Assert.Equal("/quiet", b.InstallerSwitches?.Silent);
    }

    [Fact]
    public void Traces_the_push_down_when_explain_is_enabled()
    {
        Installer a = TestManifests.CreateInstaller();
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        b.Scope = Scope.Machine;
        PackageManifests manifests = TestManifests.Create(a, b);
        manifests.Installer.Scope = Scope.User;
        ManifestContext context = TestManifests.CreateContext(manifests, explain: true);

        _rule.Apply(context);

        Assert.Contains(context.Trace, t => t.RuleId == RuleIds.PushDownRootFields && t.Message.Contains("Scope", StringComparison.Ordinal));
    }

    [Fact]
    public void No_op_when_the_root_has_no_values()
    {
        Installer a = TestManifests.CreateInstaller();
        a.Scope = Scope.User;
        PackageManifests manifests = TestManifests.Create(a);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(Scope.User, a.Scope);
        Assert.Empty(context.Findings);
    }
}
