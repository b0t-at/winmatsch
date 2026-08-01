using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Meta1HttpsUpgradeRuleTests
{
    [Fact]
    public void Confirmed_http_url_is_upgraded_to_https()
    {
        // Motivating regression: recurring single-line http->https fixes (FOSSA #245325 et al.).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.PublisherUrl = "http://example.com/about";
        var rule = new Meta1HttpsUpgradeRule(new PolicyEvidence
        {
            HttpsUpgradeConfirmations = ["http://example.com/about"],
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal("https://example.com/about", manifests.DefaultLocale.PublisherUrl);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Unconfirmed_http_url_is_left_alone_with_a_finding()
    {
        // No speculative mutation without probe evidence.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.PublisherUrl = "http://example.com/about";
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Meta1HttpsUpgradeRule().Apply(context);

        Assert.Equal("http://example.com/about", manifests.DefaultLocale.PublisherUrl);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleCatalogueIds.Meta1, finding.RuleId);
        Assert.Contains("no HTTPS probe evidence", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Https_urls_are_untouched()
    {
        // Nonmatching control.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.PackageUrl = "https://example.com";
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Meta1HttpsUpgradeRule().Apply(context);

        Assert.Equal("https://example.com", manifests.DefaultLocale.PackageUrl);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void All_url_fields_and_extra_locales_are_covered()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.LicenseUrl = "http://example.com/license";
        manifests.Locales.Add(new LocaleManifest
        {
            PackageLocale = new LanguageTag("de-DE"),
            PublisherSupportUrl = "http://example.com/de/support",
        });
        var rule = new Meta1HttpsUpgradeRule(new PolicyEvidence
        {
            HttpsUpgradeConfirmations = ["http://example.com/license", "http://example.com/de/support"],
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal("https://example.com/license", manifests.DefaultLocale.LicenseUrl);
        Assert.Equal("https://example.com/de/support", manifests.Locales[0].PublisherSupportUrl);
    }

    [Fact]
    public void Evidence_for_a_different_url_does_not_apply()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.PublisherUrl = "http://example.com/about";
        var rule = new Meta1HttpsUpgradeRule(new PolicyEvidence
        {
            HttpsUpgradeConfirmations = ["http://other.example.com/"],
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal("http://example.com/about", manifests.DefaultLocale.PublisherUrl);
        Assert.Single(context.Findings);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.PublisherUrl = "http://example.com/about";
        var rule = new Meta1HttpsUpgradeRule(new PolicyEvidence
        {
            HttpsUpgradeConfirmations = ["http://example.com/about"],
        });

        ManifestContext context = PolicyTestSupport.RunViaPipeline(rule, manifests, RuleMode.LogOnly);

        Assert.Equal("http://example.com/about", manifests.DefaultLocale.PublisherUrl);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.PublisherUrl = "http://example.com/about";

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Meta1HttpsUpgradeRule(), manifests, RuleMode.Disabled);

        Assert.Equal("http://example.com/about", manifests.DefaultLocale.PublisherUrl);
        Assert.Empty(context.Findings);
    }
}
