using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Arp3SanitizeInstallerValuesRuleTests
{
    private static readonly Arp3SanitizeInstallerValuesRule _rule = new();

    private static PackageManifests CreateWithEntry(AppsAndFeaturesEntry entry)
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.AppsAndFeaturesEntries = [entry];
        return TestManifests.Create(installer);
    }

    [Theory]
    // Motivating regressions from winget-pkgs fix commits:
    [InlineData("Jellyfin Server $_44_")] // unexpanded NSIS variable (#216728)
    [InlineData(@"$INSTDIR\Advanced Combat Tracker.exe")] // $INSTDIR leak (#256360)
    [InlineData("ms-resource:ManifestResources/DisplayName")] // MSIX resource ref (#241407)
    [InlineData("App\u001AName")] // control characters / binary junk (#295607)
    public void Garbage_display_values_are_dropped(string garbage)
    {
        PackageManifests manifests = CreateWithEntry(new AppsAndFeaturesEntry { DisplayName = garbage, Publisher = "Good Publisher" });
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        AppsAndFeaturesEntry entry = Assert.Single(manifests.Installer.Installers![0].AppsAndFeaturesEntries!);
        Assert.Null(entry.DisplayName);
        Assert.Equal("Good Publisher", entry.Publisher);
        Assert.Single(context.Findings);
    }

    [Fact]
    public void Random_temp_install_location_is_dropped_and_empty_metadata_pruned()
    {
        // Motivating regression: DefaultInstallLocation '%Temp%\2z4plp6FQmxAQvtOB2otCZcMPuf' (#269084).
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallationMetadata = new InstallationMetadata
        {
            DefaultInstallLocation = @"%Temp%\2z4plp6FQmxAQvtOB2otCZcMPuf",
        };
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(manifests.Installer.Installers![0].InstallationMetadata);
    }

    [Fact]
    public void Entries_that_become_empty_are_removed()
    {
        // Motivating regression: "AppsAndFeaturesEntries: [- {}]" (#278914).
        PackageManifests manifests = CreateWithEntry(new AppsAndFeaturesEntry { DisplayName = "$INSTDIR" });
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(manifests.Installer.Installers![0].AppsAndFeaturesEntries);
    }

    [Fact]
    public void Pre_existing_empty_entries_are_removed()
    {
        PackageManifests manifests = CreateWithEntry(new AppsAndFeaturesEntry());
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(manifests.Installer.Installers![0].AppsAndFeaturesEntries);
    }

    [Fact]
    public void Clean_values_are_untouched()
    {
        // Nonmatching control, including a value with a legal lowercase dollar amount.
        var entry = new AppsAndFeaturesEntry
        {
            DisplayName = "My App 1.2.3 ($ale edition)",
            Publisher = "Publisher, Inc.",
            DisplayVersion = "1.2.3000",
        };
        PackageManifests manifests = CreateWithEntry(entry);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal("My App 1.2.3 ($ale edition)", entry.DisplayName);
        Assert.Equal("Publisher, Inc.", entry.Publisher);
        Assert.Equal("1.2.3000", entry.DisplayVersion);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Root_level_entries_and_metadata_are_processed()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayName = "ms-resource:Name" }];
        manifests.Installer.InstallationMetadata = new InstallationMetadata { Files = [] };
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(manifests.Installer.AppsAndFeaturesEntries);
        Assert.Null(manifests.Installer.InstallationMetadata);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = CreateWithEntry(new AppsAndFeaturesEntry { DisplayName = "$INSTDIR" });

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.LogOnly);

        Assert.Equal("$INSTDIR", manifests.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayName);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = CreateWithEntry(new AppsAndFeaturesEntry { DisplayName = "$INSTDIR" });

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.Disabled);

        Assert.Equal("$INSTDIR", manifests.Installer.Installers![0].AppsAndFeaturesEntries![0].DisplayName);
        Assert.Empty(context.Changes);
        Assert.Empty(context.Findings);
    }
}
