using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Pipe1SerializerInvariantsRuleTests
{
    private static readonly Pipe1SerializerInvariantsRule _rule = new();

    [Fact]
    public void Canonical_manifests_produce_no_findings()
    {
        // Core's emitter owns the LF/single-trailing-newline invariants; this rule is the
        // pipeline-level regression guard that would fire if they ever broke.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Carriage_returns_in_field_values_do_not_leak_into_serialized_output()
    {
        // Motivating regression: the whole "fix line endings" fix-commit class (PerfView #198344,
        // ResourceHacker #235308). The serializer escapes CR in quoted scalars, so the guard stays green.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.ReleaseNotes = "line1\r\nline2";
        manifests.DefaultLocale.Description = "with\rcarriage";
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Extra_locales_are_checked_too()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Locales.Add(new LocaleManifest
        {
            PackageIdentifier = manifests.Installer.PackageIdentifier,
            PackageVersion = manifests.Installer.PackageVersion,
            PackageLocale = new LanguageTag("de-DE"),
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Unserializable_manifests_stay_silent()
    {
        // Missing required identity fields is a schema problem owned by validation, not a
        // line-ending regression; the rule must not throw or double-report.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.PackageIdentifier = null;
        manifests.Version.PackageIdentifier = null;
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void The_rule_never_mutates_the_manifests()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.Apply);

        Assert.Empty(context.Changes);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.Disabled);

        Assert.Empty(context.Findings);
        RuleExecution execution = Assert.Single(context.Executions);
        Assert.Equal(RuleMode.Disabled, execution.Mode);
    }
}
