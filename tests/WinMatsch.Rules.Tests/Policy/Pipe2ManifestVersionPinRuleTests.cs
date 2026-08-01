using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Pipe2ManifestVersionPinRuleTests
{
    [Fact]
    public void Outdated_manifest_versions_are_pinned_to_the_default()
    {
        // Motivating regression: 52 PRs needed "Update manifest version to 1.9.0" follow-ups (#201589...).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.ManifestVersion = new ManifestVersion("1.4.0");
        manifests.Version.ManifestVersion = new ManifestVersion("1.0.0");
        manifests.DefaultLocale.ManifestVersion = new ManifestVersion("1.9.0");
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Pipe2ManifestVersionPinRule().Apply(context);

        Assert.Equal(ManifestVersion.Default, manifests.Installer.ManifestVersion);
        Assert.Equal(ManifestVersion.Default, manifests.Version.ManifestVersion);
        Assert.Equal(ManifestVersion.Default, manifests.DefaultLocale.ManifestVersion);
    }

    [Fact]
    public void Extra_locales_are_pinned_too()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Locales.Add(new LocaleManifest
        {
            PackageLocale = new LanguageTag("de-DE"),
            ManifestVersion = new ManifestVersion("1.6.0"),
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Pipe2ManifestVersionPinRule().Apply(context);

        Assert.Equal(ManifestVersion.Default, manifests.Locales[0].ManifestVersion);
    }

    [Fact]
    public void Current_manifest_versions_are_untouched()
    {
        // Nonmatching control: already at the pinned version — no changes recorded.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Pipe2ManifestVersionPinRule(), manifests, RuleMode.Apply);

        Assert.Empty(context.Changes);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Mismatching_supplied_header_comment_produces_a_finding()
    {
        // Motivating regression: EdgeTX $schema comment stuck at 1.6.0 while ManifestVersion moved (#275407).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Pipe2ManifestVersionPinRule(new PolicyEvidence
        {
            SchemaHeaderComments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["manifests/t/Test/App/1.2.3/Test.App.yaml"] =
                    "# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json",
            },
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleCatalogueIds.Pipe2, finding.RuleId);
        Assert.Contains("owned by validation", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_supplied_header_comment_is_fine()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Pipe2ManifestVersionPinRule(new PolicyEvidence
        {
            SchemaHeaderComments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["manifests/t/Test/App/1.2.3/Test.App.yaml"] =
                    $"# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.{ManifestVersion.Default.Value}.schema.json",
            },
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.ManifestVersion = new ManifestVersion("1.4.0");

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Pipe2ManifestVersionPinRule(), manifests, RuleMode.LogOnly);

        Assert.Equal(new ManifestVersion("1.4.0"), manifests.Installer.ManifestVersion);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.ManifestVersion = new ManifestVersion("1.4.0");

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Pipe2ManifestVersionPinRule(), manifests, RuleMode.Disabled);

        Assert.Equal(new ManifestVersion("1.4.0"), manifests.Installer.ManifestVersion);
        Assert.Empty(context.Changes);
    }
}
