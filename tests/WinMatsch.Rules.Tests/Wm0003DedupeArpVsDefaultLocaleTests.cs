using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0003DedupeArpVsDefaultLocaleTests
{
    private static readonly DedupeArpVsDefaultLocaleRule _rule = new();

    [Theory]
    [InlineData(TestManifests.DefaultPackageName, null)]
    [InlineData("Other App", "Other App")]
    public void DisplayName_is_dropped_only_when_it_equals_the_package_name(string displayName, string? expected)
    {
        Installer a = TestManifests.CreateInstaller();
        a.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayName = displayName, ProductCode = "keep" }];
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(expected, a.AppsAndFeaturesEntries![0].DisplayName);
    }

    [Theory]
    [InlineData(TestManifests.DefaultPublisher, null)]
    [InlineData("Someone Else", "Someone Else")]
    public void Publisher_is_dropped_only_when_it_equals_the_default_locale_publisher(string publisher, string? expected)
    {
        Installer a = TestManifests.CreateInstaller();
        a.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { Publisher = publisher, ProductCode = "keep" }];
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(expected, a.AppsAndFeaturesEntries![0].Publisher);
    }

    [Theory]
    [InlineData(TestManifests.DefaultVersion, null)]
    [InlineData("99.0", "99.0")]
    public void DisplayVersion_is_dropped_only_when_it_equals_the_package_version(string displayVersion, string? expected)
    {
        Installer a = TestManifests.CreateInstaller();
        a.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayVersion = displayVersion, ProductCode = "keep" }];
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(expected, a.AppsAndFeaturesEntries![0].DisplayVersion);
    }

    [Fact]
    public void Entry_and_list_are_dropped_when_everything_was_redundant()
    {
        Installer a = TestManifests.CreateInstaller();
        a.AppsAndFeaturesEntries =
        [
            new AppsAndFeaturesEntry
            {
                DisplayName = TestManifests.DefaultPackageName,
                Publisher = TestManifests.DefaultPublisher,
                DisplayVersion = TestManifests.DefaultVersion,
            },
        ];
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(a.AppsAndFeaturesEntries);
    }

    [Fact]
    public void Entry_with_a_product_code_survives_even_when_display_fields_are_dropped()
    {
        Installer a = TestManifests.CreateInstaller();
        a.AppsAndFeaturesEntries =
        [
            new AppsAndFeaturesEntry
            {
                DisplayName = TestManifests.DefaultPackageName,
                ProductCode = "{11111111-2222-3333-4444-555555555555}",
            },
        ];
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        AppsAndFeaturesEntry entry = Assert.Single(a.AppsAndFeaturesEntries!);
        Assert.Null(entry.DisplayName);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", entry.ProductCode);
    }

    [Fact]
    public void Root_level_entries_are_deduped_too()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayVersion = TestManifests.DefaultVersion }];

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Null(manifests.Installer.AppsAndFeaturesEntries);
    }
}
