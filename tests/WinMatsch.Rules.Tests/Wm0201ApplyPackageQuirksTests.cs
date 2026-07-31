using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
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

    [Fact]
    public void Chrome_log_only_proposes_the_same_evidence_backed_change_without_mutation()
    {
        Installer installer = TestManifests.CreateInstaller(url: "https://dl.google.com/chrome.msi");
        installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayVersion = "66.0.3359.22" }];
        PackageManifests manifests = CreateChromeManifests(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests,
            evidence: [CreateCommentsEvidence("https://dl.google.com/chrome.msi", "138.0.7204.97")]);
        var configuration = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [RuleIds.ApplyPackageQuirks] = RuleMode.LogOnly,
            });

        RulePipeline.Create(
            [new ApplyPackageQuirksRule()],
            configuration,
            OverridePackSet.BuiltIn).Run(context);

        Assert.Equal("66.0.3359.22", installer.AppsAndFeaturesEntries[0].DisplayVersion);
        RuleChange change = Assert.Single(
            context.Changes,
            item => item.RuleId == RuleIds.ApplyPackageQuirks
                && item.FieldPath.EndsWith(".DisplayVersion", StringComparison.Ordinal));
        Assert.Equal("66.0.3359.22", change.Before);
        Assert.Equal("138.0.7204.97", change.After);
        Assert.Equal(RuleMode.LogOnly, change.Mode);
        Assert.Equal(RuleChangeConfidence.High, change.Confidence);
        Assert.Contains("Comments", change.SourceEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Chrome_change_evidence_redacts_signed_url_queries()
    {
        const string url = "https://dl.google.com/chrome.msi?sig=do-not-log";
        Installer installer = TestManifests.CreateInstaller(url: url);
        PackageManifests manifests = CreateChromeManifests(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests,
            evidence: [CreateCommentsEvidence(url, "138.0.7204.97")]);

        RulePipeline.Create(
            [new ApplyPackageQuirksRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.BuiltIn).Run(context);

        RuleChange change = Assert.Single(context.Changes);
        Assert.DoesNotContain("do-not-log", change.SourceEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("?sig=", change.SourceEvidence, StringComparison.Ordinal);
    }
}
