using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0005RemoveDuplicateInstallersTests
{
    private static readonly RemoveDuplicateInstallersRule _rule = new();

    [Fact]
    public void Exact_duplicates_are_removed_keeping_the_first()
    {
        Installer first = TestManifests.CreateInstaller();
        Installer duplicate = TestManifests.CreateInstaller();
        PackageManifests manifests = TestManifests.Create(first, duplicate);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Same(first, Assert.Single(manifests.Installer.Installers!));
    }

    [Fact]
    public void Url_comparison_is_case_insensitive()
    {
        Installer first = TestManifests.CreateInstaller(url: "https://example.com/App-x64.msi");
        Installer duplicate = TestManifests.CreateInstaller(url: "https://example.com/app-x64.msi");
        PackageManifests manifests = TestManifests.Create(first, duplicate);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Same(first, Assert.Single(manifests.Installer.Installers!));
    }

    [Fact]
    public void Same_key_with_different_urls_is_left_for_validation()
    {
        Installer a = TestManifests.CreateInstaller(url: "https://example.com/a.msi");
        Installer b = TestManifests.CreateInstaller(url: "https://example.com/b.msi");
        PackageManifests manifests = TestManifests.Create(a, b);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(2, manifests.Installer.Installers!.Count);
    }

    [Fact]
    public void Different_architectures_are_not_duplicates()
    {
        Installer a = TestManifests.CreateInstaller(Architecture.X64);
        Installer b = TestManifests.CreateInstaller(Architecture.X86);
        PackageManifests manifests = TestManifests.Create(a, b);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(2, manifests.Installer.Installers!.Count);
    }

    [Fact]
    public void Root_defaults_participate_in_the_duplicate_key()
    {
        Installer a = TestManifests.CreateInstaller(installerType: null);
        Installer b = TestManifests.CreateInstaller(installerType: InstallerType.Msi);
        PackageManifests manifests = TestManifests.Create(a, b);
        manifests.Installer.InstallerType = InstallerType.Msi;

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Same(a, Assert.Single(manifests.Installer.Installers!));
    }

    [Fact]
    public void Removal_is_traced_when_explain_is_enabled()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller(), TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(manifests, explain: true);

        _rule.Apply(context);

        RuleTraceEntry entry = Assert.Single(context.Trace);
        Assert.Equal(RuleIds.RemoveDuplicateInstallers, entry.RuleId);
    }
}
