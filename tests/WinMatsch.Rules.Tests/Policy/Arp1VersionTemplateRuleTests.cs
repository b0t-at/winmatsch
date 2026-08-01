using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Arp1VersionTemplateRuleTests
{
    private static readonly Arp1VersionTemplateRule _rule = new();

    private static (PackageManifests Current, PackageManifests Previous) CreateUpdate(
        string previousVersion,
        string currentVersion,
        string? displayName = null,
        string? displayVersion = null)
    {
        Installer currentInstaller = TestManifests.CreateInstaller();
        if (displayName is not null || displayVersion is not null)
        {
            currentInstaller.AppsAndFeaturesEntries =
                [new AppsAndFeaturesEntry { DisplayName = displayName, DisplayVersion = displayVersion }];
        }

        PackageManifests current = TestManifests.Create(currentInstaller);
        var version = new PackageVersion(currentVersion);
        current.Installer.PackageVersion = version;
        current.DefaultLocale.PackageVersion = version;
        current.Version.PackageVersion = version;

        Installer previousInstaller = TestManifests.CreateInstaller();
        previousInstaller.AppsAndFeaturesEntries =
            [new AppsAndFeaturesEntry { DisplayName = displayName, DisplayVersion = displayVersion }];
        PackageManifests previous = PolicyTestSupport.CreatePrevious(previousVersion, previousInstaller);
        return (current, previous);
    }

    [Fact]
    public void Old_version_token_in_display_name_is_templated()
    {
        // Motivating regression: MongoDB 7.0.9 shipped "MongoDB 6.0.6" (winget-pkgs #151617).
        (PackageManifests current, PackageManifests previous) = CreateUpdate("6.0.6", "7.0.9", displayName: "MongoDB 6.0.6 (64 bit)");
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        _rule.Apply(context);

        Assert.Equal("MongoDB 7.0.9 (64 bit)", current.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayName);
    }

    [Fact]
    public void Old_version_token_in_display_version_is_templated()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdate("3.1.0", "4.2.0", displayVersion: "3.1.0");
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        _rule.Apply(context);

        Assert.Equal("4.2.0", current.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayVersion);
    }

    [Fact]
    public void Analyzer_evidence_is_preferred_over_templating()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdate("3.1.0", "4.2.0", displayName: "UHK Agent 3.1.0");
        var evidence = new InstallerEvidence
        {
            InstallerUrl = current.Installer.Installers![0].InstallerUrl!,
            Analysis = new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.Nullsoft,
                Installers =
                [
                    new Installer
                    {
                        AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayName = "UHK Agent 4.2.0-beta" }],
                    },
                ],
            },
        };
        ManifestContext context = TestManifests.CreateContext(current, previous: previous, evidence: [evidence]);

        _rule.Apply(context);

        Assert.Equal("UHK Agent 4.2.0-beta", current.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayName);
    }

    [Fact]
    public void Root_level_entries_are_templated()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdate("1.0.0", "2.0.0");
        current.Installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayName = "App 1.0.0" }];
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        _rule.Apply(context);

        Assert.Equal("App 2.0.0", current.Installer.AppsAndFeaturesEntries[0].DisplayName);
    }

    [Fact]
    public void Static_carried_value_with_foreign_version_token_is_reported_for_review()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdate("1.0.0", "2.0.0", displayVersion: "9.9.9");
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        _rule.Apply(context);

        Assert.Equal("9.9.9", current.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayVersion);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleCatalogueIds.Arp1, finding.RuleId);
        Assert.Contains("carried verbatim", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_without_the_old_version_are_untouched()
    {
        // Nonmatching control.
        (PackageManifests current, PackageManifests previous) = CreateUpdate("1.0.0", "2.0.0", displayName: "My App");
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        _rule.Apply(context);

        Assert.Equal("My App", current.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayName);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void New_packages_without_previous_are_skipped()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayName = "App 0.9" }];
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal("App 0.9", manifests.Installer.AppsAndFeaturesEntries[0].DisplayName);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Log_only_mode_records_the_proposal_without_mutating()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdate("1.0.0", "2.0.0", displayName: "App 1.0.0");

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, current, RuleMode.LogOnly, previous);

        Assert.Equal("App 1.0.0", current.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayName);
        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal(RuleMode.LogOnly, change.Mode);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdate("1.0.0", "2.0.0", displayName: "App 1.0.0");

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, current, RuleMode.Disabled, previous);

        Assert.Equal("App 1.0.0", current.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayName);
        Assert.Empty(context.Changes);
        Assert.Empty(context.Findings);
    }
}
