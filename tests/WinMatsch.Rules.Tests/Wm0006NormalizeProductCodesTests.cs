using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0006NormalizeProductCodesTests
{
    private static readonly NormalizeProductCodesRule _rule = new();

    [Theory]
    [InlineData("{ab12cd34-ef56-7890-abcd-ef1234567890}", "{AB12CD34-EF56-7890-ABCD-EF1234567890}")]
    [InlineData("ab12cd34-ef56-7890-abcd-ef1234567890", "{AB12CD34-EF56-7890-ABCD-EF1234567890}")]
    [InlineData(" {AB12CD34-EF56-7890-ABCD-EF1234567890} ", "{AB12CD34-EF56-7890-ABCD-EF1234567890}")]
    [InlineData("{AB12CD34-EF56-7890-ABCD-EF1234567890}", "{AB12CD34-EF56-7890-ABCD-EF1234567890}")]
    [InlineData("MyApp_is1", "MyApp_is1")]
    [InlineData("not a guid at all", "not a guid at all")]
    public void Installer_product_codes_are_normalized_when_they_parse_as_guids(string value, string expected)
    {
        Installer a = TestManifests.CreateInstaller();
        a.ProductCode = value;
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal(expected, a.ProductCode);
    }

    [Fact]
    public void Arp_product_and_upgrade_codes_are_normalized()
    {
        Installer a = TestManifests.CreateInstaller();
        a.AppsAndFeaturesEntries =
        [
            new AppsAndFeaturesEntry
            {
                ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890",
                UpgradeCode = "{ff12cd34-ef56-7890-abcd-ef1234567890}",
            },
        ];
        PackageManifests manifests = TestManifests.Create(a);

        _rule.Apply(TestManifests.CreateContext(manifests));

        AppsAndFeaturesEntry entry = a.AppsAndFeaturesEntries[0];
        Assert.Equal("{AB12CD34-EF56-7890-ABCD-EF1234567890}", entry.ProductCode);
        Assert.Equal("{FF12CD34-EF56-7890-ABCD-EF1234567890}", entry.UpgradeCode);
    }

    [Fact]
    public void Root_level_product_code_is_normalized()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";

        _rule.Apply(TestManifests.CreateContext(manifests));

        Assert.Equal("{AB12CD34-EF56-7890-ABCD-EF1234567890}", manifests.Installer.ProductCode);
    }

    [Fact]
    public void Changes_are_traced_with_paths_when_explain_is_enabled()
    {
        Installer a = TestManifests.CreateInstaller();
        a.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
        PackageManifests manifests = TestManifests.Create(a);
        ManifestContext context = TestManifests.CreateContext(manifests, explain: true);

        _rule.Apply(context);

        RuleTraceEntry entry = Assert.Single(context.Trace);
        Assert.Equal(RuleIds.NormalizeProductCodes, entry.RuleId);
        Assert.Contains("Installers[0].ProductCode", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Already_canonical_values_produce_no_trace()
    {
        Installer a = TestManifests.CreateInstaller();
        a.ProductCode = "{AB12CD34-EF56-7890-ABCD-EF1234567890}";
        PackageManifests manifests = TestManifests.Create(a);
        ManifestContext context = TestManifests.CreateContext(manifests, explain: true);

        _rule.Apply(context);

        Assert.Empty(context.Trace);
    }
}
