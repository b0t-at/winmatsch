using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Pipe5ContentPolicyAnnotationRuleTests
{
    private static OverridePackSet CreateOverrides(params PolicyAnnotation[] annotations)
        => new(
        [
            new OverridePack
            {
                PackageIdentifier = new PackageIdentifier("Test.App"),
                Policies = [.. annotations],
            },
        ]);

    [Fact]
    public void Blocked_installer_type_annotation_is_an_error()
    {
        // Motivating regression: PowerShell-script installer blocked as Scripted-Application (#328932).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Pipe5ContentPolicyAnnotationRule(CreateOverrides(
            new PolicyAnnotation { Id = "blocked-installer-type", Annotation = "PowerShell script installer" }));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        Assert.Contains("blocked by repository policy", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_blocked_annotation_keeps_the_submission_alive()
    {
        // Motivating regression: Oracle MySQL blocks Azure ranges -> Network-Blocker (#154168).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Pipe5ContentPolicyAnnotationRule(CreateOverrides(
            new PolicyAnnotation { Id = "network-blocked-publishers", Annotation = "publisher blocks Azure IP ranges" }));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleSeverity.Info, finding.Severity);
        Assert.Contains("do not auto-abandon", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defender_risk_annotation_produces_a_warning()
    {
        // Motivating regression: UPX-packed miniserve Defender false positives (#155335).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Pipe5ContentPolicyAnnotationRule(CreateOverrides(
            new PolicyAnnotation { Id = "defender-fp-risk", Annotation = "UPX-packed binaries" }));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("false-positive workflow", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Needs_elevation_annotation_sets_elevation_requirement()
    {
        // Motivating regression: CrowdSec service installer Error 1920 -> ElevationRequirement (#172661).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Pipe5ContentPolicyAnnotationRule(CreateOverrides(
            new PolicyAnnotation { Id = "needs-elevation", Annotation = "registers a Windows service" }));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal(ElevationRequirement.ElevationRequired, manifests.Installer.Installers![0].ElevationRequirement);
    }

    [Fact]
    public void A_contradicting_elevation_value_is_flagged_not_overwritten()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.Installers![0].ElevationRequirement = ElevationRequirement.ElevationProhibited;
        var rule = new Pipe5ContentPolicyAnnotationRule(CreateOverrides(
            new PolicyAnnotation { Id = "needs-elevation", Annotation = "registers a Windows service" }));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal(ElevationRequirement.ElevationProhibited, manifests.Installer.Installers[0].ElevationRequirement);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("not changed", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_only_pack_flag_produces_a_warning()
    {
        // Motivating non-goal: Betterbird's per-locale installer chaos -> manual-only list (#172491).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var overrides = new OverridePackSet(
        [
            new OverridePack
            {
                PackageIdentifier = new PackageIdentifier("Test.App"),
                ManualOnly = true,
            },
        ]);
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Pipe5ContentPolicyAnnotationRule(overrides).Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("manual-only", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_annotation_ids_are_surfaced()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Pipe5ContentPolicyAnnotationRule(CreateOverrides(
            new PolicyAnnotation { Id = "mystery-flag", Annotation = "???" }));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("unrecognized id 'mystery-flag'", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Packages_without_an_override_pack_are_untouched()
    {
        // Nonmatching control.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Pipe5ContentPolicyAnnotationRule().Apply(context);

        Assert.Empty(context.Findings);
        Assert.Null(manifests.Installer.Installers![0].ElevationRequirement);
    }

    [Fact]
    public void Log_only_mode_proposes_elevation_without_mutating()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Pipe5ContentPolicyAnnotationRule(CreateOverrides(
            new PolicyAnnotation { Id = "needs-elevation", Annotation = "service installer" }));

        ManifestContext context = PolicyTestSupport.RunViaPipeline(rule, manifests, RuleMode.LogOnly);

        Assert.Null(manifests.Installer.Installers![0].ElevationRequirement);
        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal(RuleMode.LogOnly, change.Mode);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Pipe5ContentPolicyAnnotationRule(CreateOverrides(
            new PolicyAnnotation { Id = "needs-elevation", Annotation = "service installer" }));

        ManifestContext context = PolicyTestSupport.RunViaPipeline(rule, manifests, RuleMode.Disabled);

        Assert.Null(manifests.Installer.Installers![0].ElevationRequirement);
        Assert.Empty(context.Changes);
        Assert.Empty(context.Findings);
    }
}
