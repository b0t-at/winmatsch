using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0102DuplicateInstallerEntriesTests
{
    private static readonly DuplicateInstallerEntriesRule _rule = new();

    [Fact]
    public void Colliding_installers_with_different_urls_produce_an_error()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(url: "https://example.com/a.msi"),
            TestManifests.CreateInstaller(url: "https://example.com/b.msi"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleIds.DuplicateInstallerEntries, finding.RuleId);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        Assert.Equal("Installers[1]", finding.Path);
    }

    [Fact]
    public void Each_additional_colliding_installer_is_reported()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(url: "https://example.com/a.msi"),
            TestManifests.CreateInstaller(url: "https://example.com/b.msi"),
            TestManifests.CreateInstaller(url: "https://example.com/c.msi"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(2, context.Findings.Count);
    }

    [Fact]
    public void Different_scopes_do_not_collide()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(url: "https://example.com/a.msi", scope: Scope.User),
            TestManifests.CreateInstaller(url: "https://example.com/b.msi", scope: Scope.Machine));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Root_defaults_are_looked_through_when_computing_the_key()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(installerType: null, url: "https://example.com/a.msi"),
            TestManifests.CreateInstaller(installerType: InstallerType.Msi, url: "https://example.com/b.msi"));
        manifests.Installer.InstallerType = InstallerType.Msi;
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Single(context.Findings);
    }

    [Fact]
    public void Distinct_architectures_produce_no_findings()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(Architecture.X64, url: "https://example.com/a.msi"),
            TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/b.msi"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Absent_scope_and_locale_are_wildcards()
    {
        Installer wildcard = TestManifests.CreateInstaller(url: "https://example.com/a.exe");
        wildcard.InstallerLocale = null;
        Installer known = TestManifests.CreateInstaller(
            url: "https://example.com/b.exe",
            scope: Scope.User);
        known.InstallerLocale = new LanguageTag("en-US");
        PackageManifests manifests = TestManifests.Create(wildcard, known);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Single(context.Findings);
    }

    [Fact]
    public void Known_different_locales_are_distinct()
    {
        Installer english = TestManifests.CreateInstaller(
            installerType: InstallerType.Exe,
            url: "https://example.com/en.exe");
        english.InstallerLocale = new LanguageTag("en-US");
        Installer german = TestManifests.CreateInstaller(
            installerType: InstallerType.Exe,
            url: "https://example.com/de.exe");
        german.InstallerLocale = new LanguageTag("de-DE");
        PackageManifests manifests = TestManifests.Create(english, german);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Known_different_archive_nested_types_are_distinct()
    {
        Installer portable = TestManifests.CreateInstaller(
            installerType: InstallerType.Zip,
            url: "https://example.com/portable.zip");
        portable.NestedInstallerType = InstallerType.Portable;
        Installer msi = TestManifests.CreateInstaller(
            installerType: InstallerType.Zip,
            url: "https://example.com/msi.zip");
        msi.NestedInstallerType = InstallerType.Msi;
        PackageManifests manifests = TestManifests.Create(portable, msi);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Wildcard_bridge_does_not_collapse_known_different_scopes()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(url: "https://example.com/user.exe", scope: Scope.User),
            TestManifests.CreateInstaller(url: "https://example.com/machine.exe", scope: Scope.Machine),
            TestManifests.CreateInstaller(url: "https://example.com/unknown.exe"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal("Installers[2]", finding.Path);
    }
}
