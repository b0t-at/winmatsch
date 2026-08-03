using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0103InstallerTypeConsistencyTests
{
    private static readonly InstallerTypeConsistencyRule _rule = new();

    [Theory]
    [InlineData(InstallerType.Exe)]
    [InlineData(InstallerType.Msi)]
    [InlineData(null)]
    public void Nested_installer_type_outside_zip_produces_an_error(InstallerType? installerType)
    {
        Installer a = TestManifests.CreateInstaller(installerType: installerType, url: "https://example.com/a.bin");
        a.NestedInstallerType = InstallerType.Portable;
        PackageManifests manifests = TestManifests.Create(a);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleIds.InstallerTypeConsistency, finding.RuleId);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        Assert.Equal("Installers[0].NestedInstallerType", finding.Path);
    }

    [Fact]
    public void Nested_installer_files_without_a_nested_type_produce_an_error()
    {
        Installer a = TestManifests.CreateInstaller(installerType: InstallerType.Zip, url: "https://example.com/a.zip");
        a.NestedInstallerFiles = [new NestedInstallerFile { RelativeFilePath = "app.exe" }];
        PackageManifests manifests = TestManifests.Create(a);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal("Installers[0].NestedInstallerFiles", finding.Path);
    }

    [Fact]
    public void Valid_zip_with_nested_type_and_files_produces_no_findings()
    {
        Installer a = TestManifests.CreateInstaller(installerType: InstallerType.Zip, url: "https://example.com/a.zip");
        a.NestedInstallerType = InstallerType.Portable;
        a.NestedInstallerFiles = [new NestedInstallerFile { RelativeFilePath = "app.exe", PortableCommandAlias = "app" }];
        PackageManifests manifests = TestManifests.Create(a);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Root_defaults_are_looked_through()
    {
        Installer a = TestManifests.CreateInstaller(installerType: null, url: "https://example.com/a.zip");
        PackageManifests manifests = TestManifests.Create(a);
        manifests.Installer.InstallerType = InstallerType.Zip;
        manifests.Installer.NestedInstallerType = InstallerType.Portable;
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Manifest_without_installer_entries_is_checked_at_the_root()
    {
        PackageManifests manifests = TestManifests.Create();
        manifests.Installer.Installers = null;
        manifests.Installer.InstallerType = InstallerType.Exe;
        manifests.Installer.NestedInstallerType = InstallerType.Portable;
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal("InstallerManifest.NestedInstallerType", finding.Path);
    }

    [Fact]
    public void Plain_installers_produce_no_findings()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }
}
