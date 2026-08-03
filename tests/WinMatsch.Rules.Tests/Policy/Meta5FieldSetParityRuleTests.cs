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
    public void Scoped_drop_does_not_suppress_another_manifest_location()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.DefaultLocale.Description = "Default locale description";
        previous.Installer.ReleaseDate = new DateOnly(2024, 1, 1);
        var overrides = new OverridePackSet(
        [
            new OverridePack
            {
                PackageIdentifier = new PackageIdentifier("Test.App"),
                DroppedFields = ["Locales[*].Description", "Installers[*].ReleaseDate"],
            },
        ]);
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule(overridePacks: overrides).Apply(context);

        Assert.Equal("Default locale description", current.DefaultLocale.Description);
        Assert.Contains(
            context.Findings,
            finding => finding.Message.Contains("ReleaseDate", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_drop_does_not_authorize_losing_per_installer_override()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        previous.Installer.InstallerSwitches = new InstallerSwitches { Silent = "/root" };
        previous.Installer.Installers![0].InstallerSwitches =
            new InstallerSwitches { Custom = "/per-installer" };
        var overrides = new OverridePackSet(
        [
            new OverridePack
            {
                PackageIdentifier = new PackageIdentifier("Test.App"),
                DroppedFields = ["Installer.InstallerSwitches"],
            },
        ]);
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule(overridePacks: overrides).Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("this entry", finding.Message, StringComparison.Ordinal);
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
    public void Switches_lost_across_a_layout_change_are_reported()
    {
        // WM0007 only carries switches on a unique Architecture+Type+Scope match; a type
        // change silently loses them without this parity finding.
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        current.Installer.Installers![0].InstallerType = InstallerType.Exe;
        previous.Installer.Installers![0].InstallerSwitches = new InstallerSwitches { Silent = "/S" };
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("InstallerSwitches", finding.Message, StringComparison.Ordinal);
        Assert.Null(current.Installer.Installers[0].InstallerSwitches);
    }

    [Fact]
    public void Present_switches_produce_no_parity_finding()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        current.Installer.Installers![0].InstallerSwitches = new InstallerSwitches { Silent = "/S" };
        previous.Installer.Installers![0].InstallerSwitches = new InstallerSwitches { Silent = "/S" };
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Hoisted_identical_switches_produce_no_per_installer_loss()
    {
        (PackageManifests current, PackageManifests previous) = CreateUpdatePair();
        current.Installer.InstallerSwitches = new InstallerSwitches { Silent = "/S" };
        previous.Installer.Installers![0].InstallerSwitches =
            new InstallerSwitches { Silent = "/S" };
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Ambiguous_layout_changes_with_previous_switches_are_reported()
    {
        // Two previous same-architecture twins (user/machine) with switches, collapsed into
        // one scope-free entry of a different type: no unique match exists, but the drop must
        // still be reported instead of silently skipped.
        PackageManifests current = TestManifests.Create(
            TestManifests.CreateInstaller(installerType: InstallerType.Exe, url: "https://example.com/new.exe"));
        Installer userTwin = TestManifests.CreateInstaller(url: "https://example.com/old.msi", scope: Scope.User);
        userTwin.InstallerSwitches = new InstallerSwitches { Custom = "/CURRENTUSER" };
        Installer machineTwin = TestManifests.CreateInstaller(url: "https://example.com/old.msi", scope: Scope.Machine);
        machineTwin.InstallerSwitches = new InstallerSwitches { Custom = "ALLUSERS=1" };
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", userTwin, machineTwin);
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("layout changed too much", finding.Message, StringComparison.Ordinal);
        Assert.Contains("InstallerSwitches", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_locale_disambiguates_previous_entries()
    {
        Installer currentInstaller = TestManifests.CreateInstaller(url: "https://example.com/app-2.0.exe");
        currentInstaller.InstallerLocale = new LanguageTag("en-US");
        PackageManifests current = TestManifests.Create(currentInstaller);

        Installer english = TestManifests.CreateInstaller(url: "https://example.com/app-1.0.exe");
        english.InstallerLocale = new LanguageTag("en-US");
        Installer french = TestManifests.CreateInstaller(url: "https://example.com/app-1.0.exe");
        french.InstallerLocale = new LanguageTag("fr-FR");
        french.InstallerSwitches = new InstallerSwitches { Silent = "/S" };
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", english, french);
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Indistinguishable_previous_entries_require_explicit_review()
    {
        Installer currentInstaller = TestManifests.CreateInstaller(url: "https://example.com/app-2.0.exe");
        currentInstaller.InstallerLocale = new LanguageTag("en-US");
        PackageManifests current = TestManifests.Create(currentInstaller);

        Installer first = TestManifests.CreateInstaller(url: "https://example.com/app-1.0.exe");
        first.InstallerLocale = new LanguageTag("en-US");
        first.InstallerSwitches = new InstallerSwitches { Silent = "/S" };
        Installer second = TestManifests.CreateInstaller(url: "https://example.com/app-1.0.exe");
        second.InstallerLocale = new LanguageTag("en-US");
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", first, second);
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        new Meta5FieldSetParityRule().Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("layout changed too much", finding.Message, StringComparison.Ordinal);
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
