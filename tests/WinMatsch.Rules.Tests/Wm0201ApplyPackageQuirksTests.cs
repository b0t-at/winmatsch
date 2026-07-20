using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0201ApplyPackageQuirksTests
{
    private static readonly ApplyPackageQuirksRule _rule = new();

    private static PackageManifests CreateChromeManifests(Installer installer)
    {
        PackageManifests manifests = TestManifests.Create(installer);
        var identifier = new PackageIdentifier("Google.Chrome");
        manifests.Installer.PackageIdentifier = identifier;
        manifests.DefaultLocale.PackageIdentifier = identifier;
        manifests.Version.PackageIdentifier = identifier;
        return manifests;
    }

    private static InstallerEvidence CreateCommentsEvidence(string url, string comments) => new()
    {
        InstallerUrl = url,
        Properties = new Dictionary<string, string>(StringComparer.Ordinal) { ["Comments"] = comments },
    };

    [Fact]
    public void Chrome_display_version_comes_from_the_msi_comments_evidence()
    {
        Installer installer = TestManifests.CreateInstaller(url: "https://dl.google.com/chrome.msi");
        installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayVersion = "66.0.3359.22" }];
        PackageManifests manifests = CreateChromeManifests(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests,
            evidence: [CreateCommentsEvidence("https://dl.google.com/chrome.msi", "138.0.7204.97")]);

        _rule.Apply(context);

        Assert.Equal("138.0.7204.97", installer.AppsAndFeaturesEntries[0].DisplayVersion);
    }

    [Fact]
    public void An_arp_entry_is_created_when_the_installer_has_none()
    {
        Installer installer = TestManifests.CreateInstaller(url: "https://dl.google.com/chrome.msi");
        PackageManifests manifests = CreateChromeManifests(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests,
            evidence: [CreateCommentsEvidence("https://dl.google.com/chrome.msi", "138.0.7204.97")]);

        _rule.Apply(context);

        AppsAndFeaturesEntry entry = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("138.0.7204.97", entry.DisplayVersion);
    }

    [Fact]
    public void Evidence_urls_match_case_insensitively()
    {
        Installer installer = TestManifests.CreateInstaller(url: "https://dl.google.com/CHROME.msi");
        PackageManifests manifests = CreateChromeManifests(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests,
            evidence: [CreateCommentsEvidence("https://dl.google.com/chrome.msi", "138.0.7204.97")]);

        _rule.Apply(context);

        Assert.Equal("138.0.7204.97", Assert.Single(installer.AppsAndFeaturesEntries!).DisplayVersion);
    }

    [Fact]
    public void Other_packages_are_not_touched()
    {
        Installer installer = TestManifests.CreateInstaller(url: "https://example.com/app.msi");
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests,
            evidence: [CreateCommentsEvidence("https://example.com/app.msi", "138.0.7204.97")]);

        _rule.Apply(context);

        Assert.Null(installer.AppsAndFeaturesEntries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Missing_or_blank_evidence_values_are_ignored(string? comments)
    {
        Installer installer = TestManifests.CreateInstaller(url: "https://dl.google.com/chrome.msi");
        PackageManifests manifests = CreateChromeManifests(installer);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (comments is not null)
        {
            properties["Comments"] = comments;
        }

        ManifestContext context = TestManifests.CreateContext(
            manifests,
            evidence: [new InstallerEvidence { InstallerUrl = "https://dl.google.com/chrome.msi", Properties = properties }]);

        _rule.Apply(context);

        Assert.Null(installer.AppsAndFeaturesEntries);
    }

    [Fact]
    public void The_quirk_application_is_traced()
    {
        Installer installer = TestManifests.CreateInstaller(url: "https://dl.google.com/chrome.msi");
        PackageManifests manifests = CreateChromeManifests(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests,
            explain: true,
            evidence: [CreateCommentsEvidence("https://dl.google.com/chrome.msi", "138.0.7204.97")]);

        _rule.Apply(context);

        RuleTraceEntry entry = Assert.Single(context.Trace);
        Assert.Equal(RuleIds.ApplyPackageQuirks, entry.RuleId);
        Assert.Contains("Comments", entry.Message, StringComparison.Ordinal);
    }
}
