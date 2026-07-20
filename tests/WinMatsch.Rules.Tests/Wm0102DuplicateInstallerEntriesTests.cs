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
    public void Each_colliding_key_is_reported_once()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(url: "https://example.com/a.msi"),
            TestManifests.CreateInstaller(url: "https://example.com/b.msi"),
            TestManifests.CreateInstaller(url: "https://example.com/c.msi"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Single(context.Findings);
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
}
