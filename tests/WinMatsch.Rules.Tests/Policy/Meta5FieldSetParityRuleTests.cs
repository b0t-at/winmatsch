using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Meta5FieldSetParityRuleTests
{
    private static (PackageManifests Current, PackageManifests Previous) CreateUpdatePair()
    {
        PackageManifests current = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", TestManifests.CreateInstaller());
        return (current, previous);
    }

    [Fact]
    public void Missing_non_url_fields_are_carried_forward()
    {
        // Motivating regression: "Missing Properties value based on version X" (Mercurial #153496).
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.DefaultLocale.Moniker = "hg";
        previous.DefaultLocale.Tags = ["scm", "vcs"];
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        Assert.Equal("hg", current.DefaultLocale.Moniker);
        Assert.Equal(["scm", "vcs"], current.DefaultLocale.Tags);
    }

    [Fact]
    public void Missing_url_fields_need_confirmed_url_evidence()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.DefaultLocale.PublisherUrl = "https://example.com/about";
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        Assert.Null(current.DefaultLocale.PublisherUrl);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleCatalogueIds.Meta5, finding.RuleId);
        Assert.Contains("no confirmed-URL evidence", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirmed_url_fields_are_carried_forward()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.DefaultLocale.PublisherUrl = "https://example.com/about";
        var rule = new Meta5FieldSetParityRule(new PolicyEvidence
        {
            ConfirmedUrls = ["https://example.com/about"],
        });
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        rule.Apply(context);

        Assert.Equal("https://example.com/about", current.DefaultLocale.PublisherUrl);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Explicit_drop_override_allows_dropping_a_field()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.DefaultLocale.PublisherUrl = "https://example.com/about";
        previous.DefaultLocale.Moniker = "hg";
        var overrides = new OverridePackSet(
        [
            new OverridePack
            {
                PackageIdentifier = new PackageIdentifier("Test.App"),
                DroppedFields = ["PublisherUrl", "Moniker"],
            },
        ]);
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule(overridePacks: overrides).Apply(context);

        Assert.Null(current.DefaultLocale.PublisherUrl);
        Assert.Null(current.DefaultLocale.Moniker);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Existing_values_are_never_overwritten()
    {
        // Nonmatching control.
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        current.DefaultLocale.Moniker = "new-moniker";
        previous.DefaultLocale.Moniker = "old-moniker";
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        Assert.Equal("new-moniker", current.DefaultLocale.Moniker);
    }

    [Fact]
    public void Release_date_is_recomputed_from_evidence_not_copied()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.Installer.ReleaseDate = new DateOnly(2024, 1, 1);
        var rule = new Meta5FieldSetParityRule(new PolicyEvidence
        {
            ReleaseDate = new DateOnly(2026, 7, 30),
        });
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        rule.Apply(context);

        Assert.Equal(new DateOnly(2026, 7, 30), current.Installer.ReleaseDate);
    }

    [Fact]
    public void Missing_release_date_without_evidence_produces_a_finding()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.Installer.ReleaseDate = new DateOnly(2024, 1, 1);
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        Assert.Null(current.Installer.ReleaseDate);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("recomputed, not copied", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_root_fields_are_carried()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.Installer.MinimumOSVersion = new MinimumOSVersion("10.0.17763.0");
        previous.Installer.InstallModes = [InstallMode.Silent, InstallMode.SilentWithProgress];
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        Assert.Equal("10.0.17763.0", current.Installer.MinimumOSVersion?.Value);
        Assert.Equal([InstallMode.Silent, InstallMode.SilentWithProgress], current.Installer.InstallModes);
    }

    [Fact]
    public void New_packages_without_previous_are_skipped()
    {
        PackageManifests current = TestManifests.Create(TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(current);

        new Meta5FieldSetParityRule().Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.DefaultLocale.Moniker = "hg";

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Meta5FieldSetParityRule(), current, RuleMode.LogOnly, previous);

        Assert.Null(current.DefaultLocale.Moniker);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.DefaultLocale.Moniker = "hg";

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Meta5FieldSetParityRule(), current, RuleMode.Disabled, previous);

        Assert.Null(current.DefaultLocale.Moniker);
        Assert.Empty(context.Changes);
    }
}
